using System.Globalization;
using System.Runtime.InteropServices;
using SWH2.Modding;

namespace SWH2.ModLoader;

/// <summary>
/// The settings form of one mod, generated from its mod.json. Built once and kept (switching mods doesn't recreate
/// it); value changes, presets and enabledBy update the existing controls in place. Controls are positioned by hand
/// in one flat panel — no nested auto-sizing panels — so building, resizing and filtering stay fast with ~100
/// settings. Creating the native windows is the expensive part, so a row's controls stay hidden (no handles) until
/// the row comes near the visible area; the rest are created a few at a time while the app is idle.
/// Headers fold their section; the mouse wheel always scrolls the panel and never changes a value.
/// </summary>
sealed class SettingsEditor : Panel
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    const int Pad = 6, Indent = 22, RowGap = 5, CaptionGap = 10, CheckGlyph = 22, MinInput = 110;
    static readonly Color Modified = Color.FromArgb(0, 84, 166);
    static readonly Color HeaderBack = Color.FromArgb(228, 233, 241);

    sealed class Row(SettingDef def, int depth, Row? section)
    {
        public readonly SettingDef Def = def;
        public readonly int Depth = depth;
        public readonly Row? Section = section;
        public Control Main = null!;   // header label, checkbox (bool) or caption label
        public Control? Input;         // value control of non-bool settings
        public Button? Extra;          // "New seed"
        public Label? Help;
        public int InputWidth;         // preferred width of Input
        public int Top, Bottom;        // laid-out position (unscrolled), valid while Shown
        public bool Shown;             // passes the filter / not folded
        public bool Materialized;      // its controls were made visible once (= native handles exist)
        public string SearchText = "";
        public Action Refresh = () => { };
        public bool IsHeader => Def.Type == SettingType.Header;
        public bool IsBool => Def.Type == SettingType.Bool;
        public IEnumerable<Control> Controls() => new[] { Main, Input, Extra, Help }.Where(c => c != null)!;
    }

    readonly ModManifest _mod;
    readonly ToolTip _tips;
    readonly Action<string> _log;
    readonly HashSet<string> _collapsed;
    readonly List<Row> _rows = [];
    readonly Label? _exeNote;
    ModSettings _s;
    bool _syncing, _showHelp, _dirty = true, _arranging;
    string _filter = "";
    int _arrangedWidth = -1;

    /// <param name="collapsed">Ids of folded headers; updated when the user folds/unfolds (kept in AppSettings).</param>
    public SettingsEditor(ModManifest mod, ModSettings settings, Font font, ToolTip tips, Action<string> log, HashSet<string> collapsed, bool showHelp)
    {
        _mod = mod;
        _s = settings;
        _tips = tips;
        _log = log;
        _collapsed = collapsed;
        _showHelp = showHelp;
        Font = font;
        AutoScroll = true;
        DoubleBuffered = true;
        Dock = DockStyle.Fill;

        SuspendLayout();
        Row? section = null;
        foreach (var def in mod.Settings)
        {
            var row = new Row(def, Depth(def), def.Type == SettingType.Header ? null : section);
            Create(row);
            if (row.IsHeader) section = row;
            _rows.Add(row);
            foreach (var c in row.Controls())
            {
                c.Visible = false; // shown by MaterializeNear / OnIdle
                Controls.Add(c);
            }
        }
        if (mod.Settings.Count == 0)
            Controls.Add(_exeNote = NoteLabel("This mod has no settings."));
        if (mod.PatchesExe)
            Controls.Add(_exeNote = NoteLabel((_exeNote != null ? _exeNote.Text + "\n\n" : "") +
                "Patches SteamWorld Heist 2.exe (current Steam build only). The original is backed up and restored by “Restore original files” or by disabling the mod."));
        RefreshValues();
        ResumeLayout(false);
    }

    public ModManifest Mod => _mod;
    public bool HasSections => _rows.Any(r => r.IsHeader);

    /// <summary>Shows another <see cref="ModSettings"/> instance (e.g. reloaded after Apply) without rebuilding.</summary>
    public void Bind(ModSettings settings)
    {
        if (ReferenceEquals(settings, _s)) return;
        _s = settings;
        RefreshValues();
    }

    public string Filter
    {
        get => _filter;
        set { value = value.Trim(); if (value != _filter) { _filter = value; Rearrange(scrollTop: true); } }
    }

    public bool ShowHelp
    {
        get => _showHelp;
        set { if (value != _showHelp) { _showHelp = value; Rearrange(); } }
    }

    public void SetAllCollapsed(bool collapsed)
    {
        foreach (var h in _rows.Where(r => r.IsHeader))
        {
            if (collapsed) _collapsed.Add(h.Def.Id); else _collapsed.Remove(h.Def.Id);
            UpdateHeaderText(h);
        }
        Rearrange(scrollTop: true);
    }

    // ---- controls ---------------------------------------------------------------------

    int Depth(SettingDef def)
    {
        int depth = 0;
        for (SettingDef? d = def; d?.EnabledBy is { } parent && depth < 8;)
        {
            depth++;
            d = _mod.Settings.FirstOrDefault(x => x.Id.Equals(parent, StringComparison.OrdinalIgnoreCase));
        }
        return depth;
    }

    void Create(Row row)
    {
        var def = row.Def;
        row.SearchText = string.Join("\n", new[] { def.DisplayLabel, def.Help, def.Id }.Concat(def.Choices?.Select(c => c.ToString()) ?? []).Where(t => t != null));
        switch (def.Type)
        {
            case SettingType.Header:
            {
                var h = new Label
                {
                    AutoSize = false, UseMnemonic = false, Cursor = Cursors.Hand, BackColor = HeaderBack,
                    TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(4, 0, 0, 0),
                };
                h.Click += (_, _) => ToggleSection(row);
                row.Main = h;
                UpdateHeaderText(row);
                _tips.SetToolTip(h, (def.Help != null ? def.Help + "\n\n" : "") + "Click to fold / unfold this section.");
                break;
            }
            case SettingType.Bool:
            {
                var cb = new CheckBox { Text = def.DisplayLabel, UseMnemonic = false, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, CheckAlign = ContentAlignment.TopLeft };
                cb.CheckedChanged += (_, _) => Changed(def.Id, cb.Checked ? "true" : "false");
                row.Main = cb;
                row.Refresh = () => { var v = _s.Bool(def.Id); if (cb.Checked != v) cb.Checked = v; };
                break;
            }
            case SettingType.Int or SettingType.Float:
            {
                bool isInt = def.Type == SettingType.Int;
                var num = new NoWheelNumeric
                {
                    Minimum = (decimal)(def.Min ?? (isInt ? int.MinValue : -1e6)),
                    Maximum = (decimal)(def.Max ?? (isInt ? int.MaxValue : 1e6)),
                    Increment = (decimal)(def.Step ?? 1),
                    DecimalPlaces = isInt ? 0 : DecimalsOf(def.Step ?? 0.1),
                    TextAlign = HorizontalAlignment.Right,
                };
                num.ValueChanged += (_, _) => Changed(def.Id, num.Value.ToString(Inv));
                row.Input = num;
                row.InputWidth = Math.Max(90, TextRenderer.MeasureText(num.Maximum.ToString(Inv), Font).Width + 40);
                row.Refresh = () =>
                {
                    var v = Math.Max(num.Minimum, Math.Min(num.Maximum, decimal.Parse(_s.String(def.Id), Inv)));
                    if (num.Value != v) num.Value = v;
                };
                break;
            }
            case SettingType.Choice:
            {
                var combo = new NoWheelCombo { DropDownStyle = ComboBoxStyle.DropDownList };
                foreach (var c in def.Choices ?? []) combo.Items.Add(c);
                combo.SelectedIndexChanged += (_, _) =>
                {
                    if (combo.SelectedItem is ChoiceDef c) Changed(def.Id, c.Value);
                    if (def.Help == null) _tips.SetToolTip(combo, combo.Text);
                };
                int text = combo.Items.Cast<object>().Select(i => TextRenderer.MeasureText(i.ToString(), Font).Width).DefaultIfEmpty(0).Max();
                row.InputWidth = Math.Max(140, text + SystemInformation.VerticalScrollBarWidth + 14);
                combo.DropDownWidth = Math.Min(row.InputWidth, Screen.PrimaryScreen.WorkingArea.Width - 40);
                row.Input = combo;
                row.Refresh = () =>
                {
                    var v = _s.String(def.Id);
                    if ((combo.SelectedItem as ChoiceDef)?.Value != v)
                        combo.SelectedItem = def.Choices?.FirstOrDefault(c => c.Value == v);
                };
                break;
            }
            default:
            {
                bool seed = def.Type == SettingType.Seed;
                var tb = new TextBox();
                tb.TextChanged += (_, _) => Changed(def.Id, tb.Text);
                row.Input = tb;
                row.InputWidth = seed ? 170 : 280;
                row.Refresh = () => { var v = _s.String(def.Id); if (tb.Text != v) tb.Text = v; };
                if (seed)
                {
                    var b = new Button { Text = "New seed", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, UseMnemonic = false };
                    b.Click += (_, _) => tb.Text = ModSettings.NewSeed();
                    row.Extra = b;
                }
                break;
            }
        }
        if (!row.IsBool && !row.IsHeader)
            row.Main = new Label { Text = def.DisplayLabel + ":", AutoSize = false, UseMnemonic = false };
        if (def.Help != null && !row.IsHeader)
        {
            row.Help = new Label { Text = def.Help, AutoSize = false, UseMnemonic = false, ForeColor = SystemColors.GrayText };
            foreach (var c in new[] { row.Main, row.Input }) if (c != null) _tips.SetToolTip(c, def.Help);
        }
    }

    Label NoteLabel(string text) => new() { Text = text, AutoSize = false, UseMnemonic = false, ForeColor = SystemColors.GrayText };

    static int DecimalsOf(double step)
    {
        var text = step.ToString("0.####", Inv);
        int dot = text.IndexOf('.');
        return dot < 0 ? 0 : text.Length - dot - 1;
    }

    // ---- values -----------------------------------------------------------------------

    void Changed(string id, string value)
    {
        if (_syncing) return;
        var before = _s.Values.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        if (!_s.TrySet(id, value, out var error)) _log(error!);
        // A preset changed other settings (or a manual change switched a preset to custom): show the new values.
        bool others = _s.Values.Any(kv => !kv.Key.Equals(id, StringComparison.OrdinalIgnoreCase) && before[kv.Key] != kv.Value);
        if (others) BeginInvoke(new Action(RefreshValues)); // after the event: the combo that raised it is still busy
        else UpdateStates();
    }

    /// <summary>Puts the current values into the existing controls (no rebuild), then enabled/modified states.</summary>
    public void RefreshValues()
    {
        _syncing = true;
        using (new NoRedraw(this))
        {
            foreach (var r in _rows) r.Refresh();
            UpdateStates();
        }
        _syncing = false;
    }

    /// <summary>Greys out settings whose enabledBy switch is off and colours the ones changed from the default.</summary>
    void UpdateStates()
    {
        _updatingStates = true;
        try { UpdateStatesCore(); }
        finally { _updatingStates = false; }
    }

    bool _updatingStates;

    void UpdateStatesCore()
    {
        foreach (var r in _rows)
        {
            if (r.IsHeader) continue;
            bool active = _s.IsActive(r.Def.Id);
            foreach (var c in r.Controls()) if (c.Enabled != active) c.Enabled = active;
            bool changed = r.Def.Type != SettingType.Seed && _s.String(r.Def.Id) != r.Def.DefaultText;
            var color = changed ? Modified : SystemColors.ControlText;
            if (r.Main.ForeColor != color) r.Main.ForeColor = color;
        }
    }

    // ---- folding + filter -------------------------------------------------------------

    void ToggleSection(Row header)
    {
        if (_filter.Length > 0) return; // while filtering every match is shown
        if (!_collapsed.Remove(header.Def.Id)) _collapsed.Add(header.Def.Id);
        UpdateHeaderText(header);
        Rearrange();
    }

    void UpdateHeaderText(Row h) => h.Main.Text = (_collapsed.Contains(h.Def.Id) ? "▸  " : "▾  ") + h.Def.DisplayLabel;

    bool Matches(Row r) => r.SearchText.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0;

    bool IsShown(Row r)
    {
        if (_filter.Length == 0) return r.IsHeader || r.Section == null || !_collapsed.Contains(r.Section.Def.Id);
        if (r.IsHeader) return Matches(r) || _rows.Any(x => x.Section == r && Matches(x));
        return Matches(r) || (r.Section != null && Matches(r.Section));
    }

    // ---- layout -----------------------------------------------------------------------

    void Rearrange(bool scrollTop = false)
    {
        _dirty = true;
        if (scrollTop) AutoScrollPosition = Point.Empty;
        PerformLayout();
        if (scrollTop) AutoScrollPosition = Point.Empty;
        MaterializeNearNow();
        Invalidate(true);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        if (!_arranging) Arrange();
        base.OnLayout(levent);
    }

    /// <summary>
    /// Positions every control. The width doesn't depend on whether the scrollbar is shown, so showing it never
    /// causes a second pass; nothing is wider than the panel, so there is never a horizontal scrollbar.
    /// </summary>
    void Arrange()
    {
        int width = Width - SystemInformation.VerticalScrollBarWidth - 2 * Pad - 2;
        if (width < 100 || (!_dirty && width == _arrangedWidth)) return;
        _arranging = true;
        _dirty = false;
        _arrangedWidth = width;
        int scroll = AutoScrollPosition.Y;
        int captionColumn = Math.Max(150, Math.Min(340, width * 2 / 5));
        int lineHeight = TextRenderer.MeasureText("Ag", Font).Height;
        int inputHeight = PreferredInputHeight();
        int y = Pad;
        bool first = true;
        Rectangle hidden = new(Pad, Pad + scroll, 1, 1);

        using (new NoRedraw(this))
        {
            SuspendLayout();
            foreach (var r in _rows)
            {
                r.Shown = IsShown(r);
                ApplyVisibility(r);
                foreach (var c in r.Controls())
                    if (!r.Shown || (c == r.Help && !_showHelp))
                        if (c.Bounds != hidden) c.Bounds = hidden; // left-over positions must not stretch the scroll range
                if (!r.Shown) continue;

                r.Top = y;
                int x = Pad + r.Depth * Indent;
                int avail = Pad + width - x;
                if (r.IsHeader)
                {
                    if (!first) y += 8;
                    r.Main.SetBounds(Pad, y + scroll, width, lineHeight + 8);
                    y += lineHeight + 8 + RowGap;
                    r.Bottom = y;
                    first = false;
                    continue;
                }
                first = false;
                if (r.IsBool)
                {
                    int h = Math.Max(inputHeight - 2, Measure(r.Main.Text, avail - CheckGlyph) + 4);
                    r.Main.SetBounds(x, y + scroll, avail, h);
                    y += h;
                }
                else
                {
                    var input = r.Input!;
                    int extra = r.Extra != null ? r.Extra.GetPreferredSize(Size.Empty).Width + 6 : 0;
                    int inputX = Pad + captionColumn;
                    int captionWidth = inputX - CaptionGap - x;
                    bool stacked = captionWidth < 80 || Pad + width - inputX - extra < MinInput;
                    if (stacked)
                    {
                        int ch = Measure(r.Main.Text, avail);
                        r.Main.SetBounds(x, y + scroll, avail, ch);
                        y += ch + 2;
                        inputX = x;
                    }
                    int iw = Math.Max(Math.Min(MinInput, avail - extra), Math.Min(r.InputWidth, Pad + width - inputX - extra));
                    input.SetBounds(inputX, y + scroll, iw, inputHeight);
                    r.Extra?.SetBounds(inputX + iw + 6, y + scroll - 1, extra - 6, inputHeight + 2);
                    if (stacked) y += inputHeight;
                    else
                    {
                        int ch = Measure(r.Main.Text, captionWidth);
                        int top = ch <= lineHeight + 2 ? (inputHeight - ch) / 2 : 0;
                        r.Main.SetBounds(x, y + scroll + top, captionWidth, ch);
                        y += Math.Max(inputHeight, ch + top);
                    }
                }
                if (r.Help != null && _showHelp)
                {
                    int hx = x + (r.IsBool ? CheckGlyph - 4 : 0);
                    int hw = Pad + width - hx;
                    int h = Measure(r.Help.Text, hw);
                    r.Help.SetBounds(hx, y + 1 + scroll, hw, h);
                    y += h + 1;
                }
                y += RowGap;
                r.Bottom = y;
            }
            if (_exeNote != null)
            {
                bool v = _filter.Length == 0;
                if (_exeNote.Visible != v) _exeNote.Visible = v;
                int h = Measure(_exeNote.Text, width);
                _exeNote.SetBounds(Pad, (v ? y + 10 : Pad) + scroll, width, h);
                if (v) y += 10 + h;
            }
            AutoScrollMinSize = new Size(0, y + Pad);
            MaterializeNear();
            ResumeLayout(false);
        }
        _arranging = false;
        UpdateIdle();
    }

    // ---- lazy handle creation -----------------------------------------------------------

    bool _idleHooked;

    void ApplyVisibility(Row r)
    {
        foreach (var c in r.Controls())
        {
            bool v = r.Shown && r.Materialized && (c != r.Help || _showHelp);
            if (c.Visible != v) c.Visible = v;
        }
    }

    /// <summary>Shows the rows from one screen above to one screen below the visible area.</summary>
    void MaterializeNear()
    {
        int top = -AutoScrollPosition.Y - ClientSize.Height, bottom = -AutoScrollPosition.Y + 2 * ClientSize.Height;
        foreach (var r in _rows)
            if (r.Shown && !r.Materialized && r.Bottom >= top && r.Top <= bottom)
            {
                r.Materialized = true;
                ApplyVisibility(r);
            }
    }

    void MaterializeNearNow()
    {
        if (!_rows.Any(r => r.Shown && !r.Materialized)) return;
        SuspendLayout();
        MaterializeNear();
        ResumeLayout(false);
    }

    protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); MaterializeNearNow(); }
    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); MaterializeNearNow(); }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); UpdateIdle(); }

    /// <summary>While visible and rows are still hidden, create a few of them whenever the app is idle.</summary>
    void UpdateIdle()
    {
        bool want = !IsDisposed && Visible && _rows.Any(r => r.Shown && !r.Materialized);
        if (want == _idleHooked) return;
        if (want) Application.Idle += OnIdle; else Application.Idle -= OnIdle;
        _idleHooked = want;
    }

    void OnIdle(object? sender, EventArgs e)
    {
        if (IsDisposed || !Visible) { UpdateIdle(); return; }
        int n = 0;
        SuspendLayout();
        foreach (var r in _rows)
            if (r.Shown && !r.Materialized)
            {
                r.Materialized = true;
                ApplyVisibility(r);
                if (++n == 4) break;
            }
        ResumeLayout(false);
        if (n == 4) BeginInvoke(new Action(() => { })); // wake the message loop so Idle comes again
        else UpdateIdle();
    }

    protected override void Dispose(bool disposing)
    {
        if (_idleHooked) { Application.Idle -= OnIdle; _idleHooked = false; }
        base.Dispose(disposing);
    }

    int Measure(string text, int width) =>
        TextRenderer.MeasureText(text, Font, new Size(Math.Max(20, width), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;

    int _inputHeight;
    int PreferredInputHeight()
    {
        if (_inputHeight == 0)
        {
            using var probe = new ComboBox { Font = Font, DropDownStyle = ComboBoxStyle.DropDownList };
            _inputHeight = Math.Max(probe.PreferredHeight, TextRenderer.MeasureText("Ag", Font).Height + 8);
        }
        return _inputHeight;
    }

    // Focusing a control half out of view would scroll the panel; keep the position unless it's fully hidden.
    // Never while rearranging/updating: hiding or disabling the focused control moves the focus to some other
    // control, possibly far down, and the panel would jump there.
    protected override Point ScrollToControl(Control activeControl)
    {
        var r = activeControl.Bounds;
        if (_arranging || _syncing || _updatingStates || (r.Bottom > 0 && r.Top < ClientSize.Height)) return DisplayRectangle.Location;
        return base.ScrollToControl(activeControl);
    }

    // ---- wheel-safe inputs + redraw helper ------------------------------------------

    const int WM_MOUSEWHEEL = 0x020A, WM_SETREDRAW = 0x000B;
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>The wheel scrolls the settings panel instead of changing the selection (unless the list is open).</summary>
    sealed class NoWheelCombo : ComboBox
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEWHEEL && !DroppedDown && Parent is { IsHandleCreated: true } p)
            {
                SendMessage(p.Handle, m.Msg, m.WParam, m.LParam);
                return;
            }
            base.WndProc(ref m);
        }
    }

    /// <summary>The wheel scrolls the settings panel instead of changing the number (unhandled wheel goes to the parent).</summary>
    sealed class NoWheelNumeric : NumericUpDown
    {
        protected override void OnMouseWheel(MouseEventArgs e) { }
    }

    /// <summary>Stops painting a control while many of its children change, then repaints it once.</summary>
    readonly struct NoRedraw : IDisposable
    {
        readonly Control _c;
        public NoRedraw(Control c)
        {
            _c = c;
            if (c.IsHandleCreated) SendMessage(c.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        }
        public void Dispose()
        {
            if (!_c.IsHandleCreated) return;
            SendMessage(_c.Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
            _c.Invalidate(true);
        }
    }
}
