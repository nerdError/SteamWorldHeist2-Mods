using System.Diagnostics;
using System.Globalization;
using SWH2.Core;
using SWH2.Modding;

namespace SWH2.ModLoader;

/// <summary>
/// Mod manager: lists Mods\*, enables/disables them, edits their settings (form generated from mod.json),
/// installs/updates mods from .zip (button or drag &amp; drop), removes them, and applies everything to the game.
/// </summary>
public sealed class MainForm : Form
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    readonly AppSettings _app = AppSettings.Load();
    ModProfile _profile = new();
    List<ModManifest> _mods = [];
    readonly Dictionary<string, ModSettings> _settings = new(StringComparer.OrdinalIgnoreCase);
    bool _loadingList;

    // Game folder
    readonly TextBox _gamePath = new() { Dock = DockStyle.Fill };
    readonly Label _pathStatus = new() { AutoSize = true, Font = new Font("Segoe UI", 10F, FontStyle.Bold), Margin = new Padding(0, 4, 12, 0) };
    readonly Label _fileStatus = new() { Dock = DockStyle.Fill, AutoEllipsis = true, UseMnemonic = false, ForeColor = SystemColors.GrayText, TextAlign = ContentAlignment.MiddleLeft };

    // Mods
    readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, MultiSelect = false,
        HideSelection = false, AllowDrop = true, HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    readonly Label _modInfo = new() { Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText, AutoEllipsis = true, UseMnemonic = false };
    readonly Button _installZip = new() { Text = "Install .zip…", AutoSize = true };
    readonly Button _remove = new() { Text = "Remove", AutoSize = true, Enabled = false };

    // Settings: one cached editor per mod (building the Randomizer's ~100 settings is the slow part)
    readonly GroupBox _settingsBox = new() { Text = "Settings", Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 8) };
    readonly Panel _settingsHost = new() { Dock = DockStyle.Fill };
    readonly TextBox _filter = new() { Width = 190, Margin = new Padding(0, 4, 6, 4) };
    readonly Button _expandAll = new() { Text = "▾ Unfold", AutoSize = true };
    readonly Button _collapseAll = new() { Text = "▸ Fold", AutoSize = true };
    readonly CheckBox _showHelp = new() { Text = "Descriptions", AutoSize = true, Margin = new Padding(6, 8, 6, 0) };
    readonly Button _reset = new() { Text = "Defaults", AutoSize = true };
    readonly ToolTip _tips = new() { AutoPopDelay = 20000 };
    readonly Dictionary<string, (SettingsEditor Editor, string Key)> _editors = new(StringComparer.OrdinalIgnoreCase);
    SettingsEditor? _editor;

    readonly SplitContainer _split = new() { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterWidth = 6 };
    readonly SplitContainer _logSplit = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel2, SplitterWidth = 6 };

    // Actions + log
    readonly Button _apply = new() { Text = "Apply to game", AutoSize = true, Font = new Font("Segoe UI", 10F, FontStyle.Bold) };
    readonly Button _restore = new() { Text = "Restore original files", AutoSize = true };
    readonly Button _extract = new() { Text = "Extract game files…", AutoSize = true };
    readonly Button _openTool = new() { Text = "Open loader folder", AutoSize = true };
    readonly Button _play = new() { Text = "▶ Play", AutoSize = true, Font = new Font("Segoe UI", 10F, FontStyle.Bold) };
    readonly Button _saveBanks = new() { Text = "Save banks…", AutoSize = true };
    /// <summary>Buttons of enabled mods' tool windows (mod.json "tool", e.g. CheatMenu's "Cheats…").</summary>
    readonly FlowLayoutPanel _tools = new() { AutoSize = true, WrapContents = false, Margin = new Padding(16, 0, 0, 0) };
    readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9.5F) };

    public MainForm()
    {
        Text = $"NE Mod Loader & Manager {ModCatalog.LoaderVersion.ToString(3)} — SteamWorld Heist 2";
        Font = new Font("Segoe UI", 10F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(1180, 860);
        MinimumSize = new Size(860, 600);
        StartPosition = FormStartPosition.CenterScreen;
        if (_app.WindowWidth > 0 && _app.WindowHeight > 0)
        {
            var area = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Min(_app.WindowWidth, area.Width), Math.Min(_app.WindowHeight, area.Height));
        }
        if (_app.Maximized) WindowState = FormWindowState.Maximized;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (ArgumentException) { }

        BuildLayout();
        WireEvents();
        _gamePath.Text = _app.GamePath is { } p && GameInstall.IsValid(p) ? p : GameInstall.TryDetect() ?? _app.GamePath ?? "";
        ValidateGamePath();
    }

    GameInstall? Game => GameInstall.IsValid(_gamePath.Text.Trim()) ? new GameInstall(_gamePath.Text.Trim()) : null;
    ModManifest? Selected => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as ModManifest : null;

    // ---- layout -----------------------------------------------------------------------

    void BuildLayout()
    {
        // Game folder / [mods | settings] over the log (both splitters draggable) / action buttons
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10, 6, 10, 8), ColumnCount = 1 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); // never wider than the window
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // Game folder: path + buttons, then one status line (details in its tooltip)
        var gameBox = new GroupBox { Text = "Game folder", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
        var gameGrid = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3 };
        gameGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        gameGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        gameGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var browse = new Button { Text = "Browse…", AutoSize = true };
        var detect = new Button { Text = "Auto-detect", AutoSize = true };
        browse.Click += (_, _) => BrowseGame();
        detect.Click += (_, _) =>
        {
            var found = GameInstall.TryDetect();
            if (found != null) _gamePath.Text = found;
            else MessageBox.Show(this, "Couldn't find the game in your Steam libraries. Use Browse….", Text);
        };
        gameGrid.Controls.Add(_gamePath, 0, 0);
        gameGrid.Controls.Add(browse, 1, 0);
        gameGrid.Controls.Add(detect, 2, 0);
        var status = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty };
        status.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        status.Controls.Add(_pathStatus, 0, 0);
        status.Controls.Add(_fileStatus, 1, 0);
        _fileStatus.Height = _pathStatus.PreferredHeight + 4;
        gameGrid.Controls.Add(status, 0, 1);
        gameGrid.SetColumnSpan(status, 3);
        gameBox.Controls.Add(gameGrid);
        root.Controls.Add(gameBox);

        // Mods (narrow, fixed width) | Settings (gets the rest)
        var modsBox = new GroupBox { Text = "Mods — tick to enable, drop a .zip here", Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 8) };
        var modsGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        modsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        modsGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        modsGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 72)); // description, 3 lines (full text in the tooltip)
        modsGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _list.Columns.Add("Mod", 200);
        _list.Columns.Add("Version", 64);
        _list.Columns.Add("", 44);
        var openMods = new Button { Text = "Open folder", AutoSize = true };
        var refresh = new Button { Text = "Refresh", AutoSize = true };
        openMods.Click += (_, _) => { if (Game is { } g) OpenFolder(g.ModsDir); };
        refresh.Click += (_, _) => RefreshMods();
        modsGrid.Controls.Add(_list);
        modsGrid.Controls.Add(_modInfo);
        var modButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        modButtons.Controls.AddRange([_installZip, _remove, openMods, refresh]);
        modsGrid.Controls.Add(modButtons);
        modsBox.Controls.Add(modsGrid);
        _split.Panel1.Controls.Add(modsBox);

        var settingsGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        settingsGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        settingsGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 4) };
        toolbar.Controls.AddRange([_filter, _expandAll, _collapseAll, _showHelp, _reset]);
        settingsGrid.Controls.Add(toolbar);
        settingsGrid.Controls.Add(_settingsHost);
        _settingsBox.Controls.Add(settingsGrid);
        _split.Panel2.Controls.Add(_settingsBox);
        _tips.SetToolTip(_filter, "Show only the settings whose name, description or options contain this text.");
        _tips.SetToolTip(_showHelp, "Show each setting's description under it. Off: descriptions only as tooltips.");
        _tips.SetToolTip(_reset, "Put every setting of this mod back to its default (the seed is kept).");
        _tips.SetToolTip(_expandAll, "Unfold every section. Click a section header to fold / unfold just that one.");
        _tips.SetToolTip(_collapseAll, "Fold every section to see the list of sections.");
        _showHelp.Checked = _app.ShowHelp;

        var logBox = new GroupBox { Text = "Log", Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 8) };
        logBox.Controls.Add(_log);
        _logSplit.Panel1.Controls.Add(_split);
        _logSplit.Panel2.Controls.Add(logBox);
        root.Controls.Add(_logSplit);

        root.Controls.Add(Row(_apply, _play, _saveBanks, _restore, _extract, _openTool, _tools));

        Controls.Add(root);
    }

    /// <summary>Splitter positions need the final window size, so they're set once the form is shown.</summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        SendMessage(_filter.Handle, 0x1501 /* EM_SETCUEBANNER */, (IntPtr)1, "Search settings…");
        _split.Panel1MinSize = 240;
        _split.Panel2MinSize = 360;
        _logSplit.Panel1MinSize = 240;
        _logSplit.Panel2MinSize = 60;
        // Defaults scale with the window: the settings get most of the space.
        int modList = _app.ModListWidth > 0 ? _app.ModListWidth : Math.Max(250, Math.Min(340, _split.Width * 28 / 100));
        int log = _app.LogHeight > 0 ? _app.LogHeight : Math.Max(70, Math.Min(150, _logSplit.Height * 18 / 100));
        SetSplitter(_split, modList, _split.Width);
        SetSplitter(_logSplit, _logSplit.Height - log - _logSplit.SplitterWidth, _logSplit.Height);
    }

    static void SetSplitter(SplitContainer s, int distance, int total)
    {
        int max = total - s.Panel2MinSize - s.SplitterWidth;
        if (max > s.Panel1MinSize) s.SplitterDistance = Math.Max(s.Panel1MinSize, Math.Min(max, distance));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

    static FlowLayoutPanel Row(params Control[] controls)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
        p.Controls.AddRange(controls);
        return p;
    }

    // ---- behaviour --------------------------------------------------------------------

    void WireEvents()
    {
        _gamePath.TextChanged += (_, _) => ValidateGamePath();
        _filter.TextChanged += (_, _) => { if (_editor != null) _editor.Filter = _filter.Text; };
        _filter.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { _filter.Clear(); e.SuppressKeyPress = true; } };
        _expandAll.Click += (_, _) => _editor?.SetAllCollapsed(false);
        _collapseAll.Click += (_, _) => _editor?.SetAllCollapsed(true);
        _showHelp.CheckedChanged += (_, _) => { _app.ShowHelp = _showHelp.Checked; if (_editor != null) _editor.ShowHelp = _showHelp.Checked; };
        _reset.Click += (_, _) => ResetSelected();
        _list.ItemChecked += (_, e) => OnModChecked(e.Item);
        _list.SelectedIndexChanged += (_, _) => ShowSelected();
        // After the list has its new size, otherwise a horizontal scrollbar from the old column width stays.
        _list.Resize += (_, _) => { if (_list.IsHandleCreated) BeginInvoke(FitModColumn); };
        _list.DragEnter += (_, e) => e.Effect = DroppedZips(e).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        _list.DragDrop += (_, e) => InstallZips(DroppedZips(e));
        _installZip.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog { Filter = "Mod archives (*.zip)|*.zip", Multiselect = true, Title = "Install SWH2 mod" };
            if (dlg.ShowDialog(this) == DialogResult.OK) InstallZips(dlg.FileNames);
        };
        _remove.Click += (_, _) => RemoveSelected();

        _apply.Click += (_, _) =>
        {
            var profile = SnapshotProfile();
            RunTask("Applying", (g, log) =>
            {
                Pipeline.Install(g, profile, log);
                profile.Save(g);
                BeginInvoke(() => { _profile = profile; ReloadSettingsFromProfile(); });
            });
        };
        _restore.Click += (_, _) => RunTask("Restoring", Pipeline.Restore);
        _extract.Click += (_, _) =>
        {
            if (Game is not { } g) return;
            var assets = MessageBox.Show(this,
                $"Extract the original game data to:\n{g.ExtractDir}\n\nAlso extract Game2.pak (sounds, textures, shaders, ~45 MB)?",
                "Extract", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (assets == DialogResult.Cancel) return;
            RunTask("Extracting", (game, log) =>
            {
                Pipeline.Extract(game, game.ExtractDir, assets == DialogResult.Yes, log);
                OpenFolder(game.ExtractDir);
            }, needsGameClosed: false);
        };
        _openTool.Click += (_, _) => { if (Game is { } g) OpenFolder(g.ToolDir); };
        _play.Click += (_, _) => LaunchGame();
        _saveBanks.Click += (_, _) =>
        {
            if (Game is not { } g) return;
            using var form = new SaveBanksForm(SnapshotProfile().SaveSlots(g));
            form.ShowDialog(this);
        };
        FormClosing += (_, _) =>
        {
            _app.Maximized = WindowState == FormWindowState.Maximized;
            var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            (_app.WindowWidth, _app.WindowHeight) = (bounds.Width, bounds.Height);
            _app.ModListWidth = _split.SplitterDistance;
            _app.LogHeight = _logSplit.Height - _logSplit.SplitterDistance - _logSplit.SplitterWidth;
            SaveAll();
        };
    }

    void FitModColumn() =>
        _list.Columns[0].Width = Math.Max(100, _list.ClientSize.Width - _list.Columns[1].Width - _list.Columns[2].Width - SystemInformation.VerticalScrollBarWidth);

    void ValidateGamePath()
    {
        var g = Game;
        _pathStatus.Text = g != null ? "✔ Game found" : "✘ Not a SteamWorld Heist 2 folder (Bundle\\Core\\Game.pak missing)";
        _pathStatus.ForeColor = g != null ? Color.FromArgb(0, 150, 0) : Color.FromArgb(192, 0, 0);
        foreach (var b in new[] { _apply, _play, _saveBanks, _restore, _extract, _openTool, _installZip }) b.Enabled = g != null;
        _profile = g != null ? ModProfile.Load(g) : new();
        _settings.Clear();
        RefreshMods();
        RefreshFileStatus();
    }

    void RefreshFileStatus()
    {
        string text;
        if (Game is not { } g) text = "";
        else try { text = new GameFiles(g).DescribeStatus(); }
            catch (Exception ex) { text = ex.Message; }
        _fileStatus.Text = text.Replace(Environment.NewLine, "   ·   ");
        _tips.SetToolTip(_fileStatus, text);
    }

    void RefreshMods()
    {
        var selected = Selected?.Id;
        _loadingList = true;
        _list.BeginUpdate();
        _list.Items.Clear();
        _mods = [];
        if (Game is { } g)
        {
            Directory.CreateDirectory(g.ModsDir);
            _mods = ModCatalog.Discover(g, e => Log("Skipped mod " + e));
            foreach (var m in _mods)
            {
                var notes = new List<string>();
                if (m.PatchesExe) notes.Add("exe");
                if (m.IsLegacy) notes.Add("files");
                var item = new ListViewItem([m.Name.Length > 0 ? m.Name : m.Id, m.Version, string.Join(", ", notes)])
                {
                    Tag = m,
                    Checked = _profile.IsEnabled(m.Id),
                    ToolTipText = m.Description,
                };
                _list.Items.Add(item);
                if (m.Id == selected) item.Selected = true;
            }
            foreach (var missing in _profile.Enabled.Where(id => !_mods.Any(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase))))
                Log($"Enabled mod '{missing}' is not installed any more.");
        }
        _list.EndUpdate();
        _loadingList = false;
        foreach (var id in _editors.Keys.Where(id => !_mods.Any(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).ToList())
            DropEditor(id);
        if (_list.SelectedItems.Count == 0 && _list.Items.Count > 0) _list.Items[0].Selected = true;
        UpdateTools();
        ShowSelected();
    }

    void OnModChecked(ListViewItem item)
    {
        if (_loadingList || item.Tag is not ModManifest mod) return;
        if (item.Checked)
        {
            var clash = _mods.Where(o => o != mod && _profile.IsEnabled(o.Id) &&
                (mod.Conflicts.Contains(o.Id, StringComparer.OrdinalIgnoreCase) || o.Conflicts.Contains(mod.Id, StringComparer.OrdinalIgnoreCase))).ToList();
            if (clash.Count > 0)
            {
                MessageBox.Show(this, $"{mod.Title} can't be used together with {string.Join(", ", clash.Select(c => c.Title))}. Disable that first.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                BeginInvoke(() => item.Checked = false);
                return;
            }
        }
        _profile.SetEnabled(mod.Id, item.Checked);
        UpdateTools();
        if (!item.Selected) item.Selected = true;
        else ShowSelected();
    }

    /// <summary>One button per enabled mod with a tool window. Tools may be used while the game runs.</summary>
    void UpdateTools()
    {
        _tools.Controls.Clear();
        foreach (var mod in _mods.Where(m => m.Tool != null && m.Plugin != null && _profile.IsEnabled(m.Id)))
        {
            var b = new Button { Text = mod.Tool, AutoSize = true, UseMnemonic = false };
            b.Click += (_, _) =>
            {
                try
                {
                    if (ModCatalog.LoadPlugin(mod) is IModTool tool)
                        tool.OpenTool(new ModToolContext(mod, Game, SettingsOf(mod), Log, this));
                    else Log($"{mod.Id}: the plugin has no tool window.");
                }
                catch (Exception ex) { Log($"{mod.Id}: {ex.Message}"); }
            };
            _tools.Controls.Add(b);
        }
    }

    ModSettings SettingsOf(ModManifest mod)
    {
        if (!_settings.TryGetValue(mod.Id, out var s)) _settings[mod.Id] = s = _profile.SettingsFor(mod);
        return s;
    }

    /// <summary>Current profile including unsaved edits in the settings panels.</summary>
    ModProfile SnapshotProfile()
    {
        var p = new ModProfile { Enabled = [.. _profile.Enabled], Settings = new(_profile.Settings, StringComparer.OrdinalIgnoreCase) };
        foreach (var m in _mods)
            if (_settings.TryGetValue(m.Id, out var s)) p.Store(m, s);
        return p;
    }

    void ReloadSettingsFromProfile()
    {
        _settings.Clear();
        ShowSelected(); // e.g. a generated seed
    }

    void SaveAll()
    {
        _app.GamePath = _gamePath.Text.Trim();
        _app.Save();
        if (Game is { } g)
            try { SnapshotProfile().Save(g); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ---- mod info + settings panel ----------------------------------------------------

    void ShowSelected()
    {
        var mod = Selected;
        _remove.Enabled = mod != null;
        SettingsEditor? editor = null;
        if (mod == null)
        {
            _modInfo.Text = _mods.Count == 0 ? "No mods installed. Use “Install .zip…” or drop a mod archive on the list." : "";
            _settingsBox.Text = "Settings";
        }
        else
        {
            _modInfo.Text = (mod.Author != null ? $"by {mod.Author}. " : "") + (mod.Description ?? "") + (mod.Url != null ? "  " + mod.Url : "");
            _tips.SetToolTip(_modInfo, _modInfo.Text);
            _settingsBox.Text = $"Settings — {mod.Title}{(_profile.IsEnabled(mod.Id) ? "" : " (disabled — tick it in the list to use it)")}";
            editor = EditorFor(mod);
            editor.Bind(SettingsOf(mod));
            editor.ShowHelp = _showHelp.Checked;
            editor.Filter = _filter.Text;
        }
        if (editor != _editor)
        {
            editor?.Show();
            _editor?.Hide();
            _editor = editor;
        }
        bool any = mod?.Settings.Count > 0;
        _filter.Enabled = _showHelp.Enabled = _reset.Enabled = any;
        _expandAll.Enabled = _collapseAll.Enabled = editor?.HasSections == true;
    }

    /// <summary>The cached editor of a mod; rebuilt only when the mod (folder, version or mod.json) changed.</summary>
    SettingsEditor EditorFor(ModManifest mod)
    {
        var manifest = Path.Combine(mod.Dir, "mod.json");
        var key = $"{mod.Dir}|{mod.Version}|{(File.Exists(manifest) ? File.GetLastWriteTimeUtc(manifest).Ticks : 0)}";
        if (_editors.TryGetValue(mod.Id, out var cached) && cached.Key == key) return cached.Editor;
        DropEditor(mod.Id);
        var editor = new SettingsEditor(mod, SettingsOf(mod), Font, _tips, Log, _app.CollapsedFor(mod.Id), _showHelp.Checked) { Visible = false };
        _settingsHost.Controls.Add(editor);
        _editors[mod.Id] = (editor, key);
        return editor;
    }

    void DropEditor(string modId)
    {
        if (!_editors.TryGetValue(modId, out var cached)) return;
        _editors.Remove(modId);
        if (_editor == cached.Editor) _editor = null;
        cached.Editor.Dispose();
    }

    void ResetSelected()
    {
        if (Selected is not { } mod || mod.Settings.Count == 0) return;
        if (MessageBox.Show(this, $"Put every setting of {mod.Title} back to its default?\n(The seed is kept.)", "Reset to defaults",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        var old = SettingsOf(mod);
        var seeds = mod.Settings.Where(d => d.Type == SettingType.Seed).ToDictionary(d => d.Id, d => old.String(d.Id));
        _settings[mod.Id] = new ModSettings(mod, seeds);
        ShowSelected();
    }

    // ---- install / remove mods --------------------------------------------------------

    static string[] DroppedZips(DragEventArgs e) =>
        e.Data?.GetData(DataFormats.FileDrop) is string[] files ? files.Where(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToArray() : [];

    void InstallZips(string[] zips)
    {
        if (Game is not { } g) return;
        foreach (var zip in zips)
        {
            try
            {
                var incoming = ModCatalog.ReadArchive(zip);
                var existing = _mods.FirstOrDefault(m => m.Id.Equals(incoming.Id, StringComparison.OrdinalIgnoreCase));
                if (existing != null && MessageBox.Show(this,
                        $"Replace {existing.Title} with {incoming.Title}?\n(The mod's settings are kept.)", "Update mod",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    continue;
                var installed = ModCatalog.InstallArchive(g, zip);
                if (existing == null) _profile.SetEnabled(installed.Id, true);
                Log($"{(existing != null ? "Updated" : "Installed")} mod {installed.Title} from {Path.GetFileName(zip)}." +
                    (existing == null ? " It's enabled — click Apply to game." : " Click Apply to game to use the new version."));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                Log($"Can't install {Path.GetFileName(zip)}: {ex.Message}");
            }
        }
        _profile = SnapshotProfile();
        RefreshMods();
    }

    void RemoveSelected()
    {
        if (Selected is not { } mod || Game is not { } g) return;
        if (MessageBox.Show(this, $"Delete the folder of {mod.Title}?\n{mod.Dir}\n\nIts changes stay in the game until you click Apply to game or Restore.",
                "Remove mod", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try
        {
            ModCatalog.Uninstall(mod);
            _profile.SetEnabled(mod.Id, false);
            _profile.Settings.Remove(mod.Id);
            _settings.Remove(mod.Id);
            SnapshotProfile().Save(g);
            Log($"Removed {mod.Title}. Click Apply to game to remove its changes from the game.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"Can't remove {mod.Id}: {ex.Message}");
        }
        RefreshMods();
    }

    // ---- helpers ----------------------------------------------------------------------

    void BrowseGame()
    {
        using var dlg = new FolderBrowserDialog { Description = "Select the SteamWorld Heist 2 folder" };
        if (Directory.Exists(_gamePath.Text)) dlg.SelectedPath = _gamePath.Text;
        if (dlg.ShowDialog(this) == DialogResult.OK) _gamePath.Text = dlg.SelectedPath;
    }

    void RunTask(string what, Action<GameInstall, Action<string>> work, bool needsGameClosed = true)
    {
        if (Game is not { } game) return;
        if (needsGameClosed && Process.GetProcessesByName("SteamWorld Heist 2").Length > 0)
        {
            MessageBox.Show(this, "Close the game first — its files are in use.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        SaveAll();
        SetBusy(true);
        Log($"=== {what}…");
        Task.Run(() =>
        {
            try { work(game, Log); }
            catch (Exception ex) { Log("ERROR: " + ex.Message); }
            finally
            {
                BeginInvoke(() =>
                {
                    SetBusy(false);
                    RefreshFileStatus();
                    RefreshMods(); // a legacy migration may have moved mod folders
                });
            }
        });
    }

    void LaunchGame()
    {
        if (Game is not { } g) return;
        if (Process.GetProcessesByName("SteamWorld Heist 2").Length > 0)
        {
            MessageBox.Show(this, "The game is already running.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        SaveAll();
        try
        {
            // The exe itself (not steam://) so exactly this install starts; Steam attaches to it on its own.
            Process.Start(new ProcessStartInfo(g.ExePath) { WorkingDirectory = g.Root, UseShellExecute = true });
            Log("=== Game started.");
        }
        catch (Exception ex) { Log("ERROR: could not start the game: " + ex.Message); }
    }

    void SetBusy(bool busy)
    {
        UseWaitCursor = busy;
        foreach (var b in new[] { _apply, _play, _restore, _extract, _installZip, _remove }) b.Enabled = !busy;
        _list.Enabled = !busy;
    }

    void Log(string message)
    {
        if (InvokeRequired) { BeginInvoke(() => Log(message)); return; }
        _log.AppendText(message.Replace("\n", Environment.NewLine) + Environment.NewLine);
    }

    static void OpenFolder(string dir)
    {
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }
}
