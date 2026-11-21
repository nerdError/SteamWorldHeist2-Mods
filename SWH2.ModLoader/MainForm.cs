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
    readonly Label _pathStatus = new() { AutoSize = true, Font = new Font("Segoe UI", 10F, FontStyle.Bold) };
    readonly Label _fileStatus = new() { AutoSize = true, ForeColor = SystemColors.GrayText };

    // Mods
    readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, MultiSelect = false,
        HideSelection = false, AllowDrop = true, HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    readonly Label _modInfo = new() { Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText, AutoEllipsis = true, UseMnemonic = false };
    readonly Button _installZip = new() { Text = "Install .zip…", AutoSize = true };
    readonly Button _remove = new() { Text = "Remove", AutoSize = true, Enabled = false };

    // Settings
    readonly GroupBox _settingsBox = new() { Text = "Settings", Dock = DockStyle.Fill, Padding = new Padding(8) };
    readonly FlowLayoutPanel _settingsPanel = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
    readonly ToolTip _tips = new() { AutoPopDelay = 20000 };
    readonly List<(string Id, Control Control)> _settingControls = [];

    // Actions + log
    readonly Button _apply = new() { Text = "Apply to game", AutoSize = true, Font = new Font("Segoe UI", 10F, FontStyle.Bold) };
    readonly Button _restore = new() { Text = "Restore original files", AutoSize = true };
    readonly Button _extract = new() { Text = "Extract game files…", AutoSize = true };
    readonly Button _openTool = new() { Text = "Open loader folder", AutoSize = true };
    /// <summary>Buttons of enabled mods' tool windows (mod.json "tool", e.g. CheatMenu's "Cheats…").</summary>
    readonly FlowLayoutPanel _tools = new() { AutoSize = true, WrapContents = false, Margin = new Padding(16, 0, 0, 0) };
    readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9.5F) };

    public MainForm()
    {
        Text = $"SWH2 Mod Loader {ModCatalog.LoaderVersion.ToString(3)} — SteamWorld Heist 2";
        Font = new Font("Segoe UI", 10F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(1100, 860);
        MinimumSize = new Size(860, 640);
        StartPosition = FormStartPosition.CenterScreen;

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
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); // never wider than the window
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 72));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 28));

        // Game folder
        var gameBox = new GroupBox { Text = "Game folder", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8) };
        var gameGrid = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3 };
        _fileStatus.MaximumSize = new Size(ClientSize.Width - 60, 0);
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
        gameGrid.Controls.Add(_pathStatus, 0, 1);
        gameGrid.SetColumnSpan(_pathStatus, 3);
        gameGrid.Controls.Add(_fileStatus, 0, 2);
        gameGrid.SetColumnSpan(_fileStatus, 3);
        gameBox.Controls.Add(gameGrid);
        root.Controls.Add(gameBox);

        // Mods | Settings
        var middle = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        middle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        middle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        var modsBox = new GroupBox { Text = "Mods — tick to enable, drop a .zip to install", Dock = DockStyle.Fill, Padding = new Padding(8) };
        var modsGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        modsGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        modsGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
        modsGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _list.Columns.Add("Mod", 220);
        _list.Columns.Add("Version", 70);
        _list.Columns.Add("Author", 100);
        _list.Columns.Add("", 90);
        var openMods = new Button { Text = "Open folder", AutoSize = true };
        var refresh = new Button { Text = "Refresh", AutoSize = true };
        openMods.Click += (_, _) => { if (Game is { } g) OpenFolder(g.ModsDir); };
        refresh.Click += (_, _) => RefreshMods();
        modsGrid.Controls.Add(_list);
        modsGrid.Controls.Add(_modInfo);
        modsGrid.Controls.Add(Row(_installZip, _remove, openMods, refresh));
        modsBox.Controls.Add(modsGrid);
        middle.Controls.Add(modsBox, 0, 0);

        _settingsBox.Controls.Add(_settingsPanel);
        middle.Controls.Add(_settingsBox, 1, 0);
        root.Controls.Add(middle);

        root.Controls.Add(Row(_apply, _restore, _extract, _openTool, _tools));

        var logBox = new GroupBox { Text = "Log", Dock = DockStyle.Fill, Padding = new Padding(8) };
        logBox.Controls.Add(_log);
        root.Controls.Add(logBox);

        Controls.Add(root);
    }

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
        Resize += (_, _) => _fileStatus.MaximumSize = new Size(Math.Max(200, ClientSize.Width - 60), 0);
        _list.ItemChecked += (_, e) => OnModChecked(e.Item);
        _list.SelectedIndexChanged += (_, _) => ShowSelected();
        _list.Resize += (_, _) => _list.Columns[0].Width = Math.Max(120, _list.ClientSize.Width - 70 - 100 - 90 - 4);
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
        FormClosing += (_, _) => SaveAll();
    }

    void ValidateGamePath()
    {
        var g = Game;
        _pathStatus.Text = g != null ? "✔ Game found" : "✘ Not a SteamWorld Heist 2 folder (Bundle\\Core\\Game.pak missing)";
        _pathStatus.ForeColor = g != null ? Color.FromArgb(0, 150, 0) : Color.FromArgb(192, 0, 0);
        foreach (var b in new[] { _apply, _restore, _extract, _openTool, _installZip }) b.Enabled = g != null;
        _profile = g != null ? ModProfile.Load(g) : new();
        _settings.Clear();
        RefreshMods();
        RefreshFileStatus();
    }

    void RefreshFileStatus()
    {
        if (Game is not { } g) { _fileStatus.Text = ""; return; }
        try { _fileStatus.Text = new GameFiles(g).DescribeStatus(); }
        catch (Exception ex) { _fileStatus.Text = ex.Message; }
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
                var item = new ListViewItem([m.Name.Length > 0 ? m.Name : m.Id, m.Version, m.Author ?? "", string.Join(", ", notes)])
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
        _settingsPanel.SuspendLayout();
        _settingsPanel.Controls.Clear();
        _settingControls.Clear();
        if (mod == null)
        {
            _modInfo.Text = _mods.Count == 0 ? "No mods installed. Use “Install mod (.zip)…” or drop a mod archive on the list." : "";
            _settingsBox.Text = "Settings";
            _settingsPanel.ResumeLayout();
            return;
        }

        _modInfo.Text = (mod.Description ?? "") + (mod.Url != null ? "  " + mod.Url : "");
        _settingsBox.Text = $"Settings — {mod.Title}{(_profile.IsEnabled(mod.Id) ? "" : " (disabled)")}";
        var s = SettingsOf(mod);
        int width = Math.Max(300, _settingsPanel.ClientSize.Width - 30);

        if (mod.Settings.Count == 0)
            _settingsPanel.Controls.Add(new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Text = "This mod has no settings." });

        foreach (var def in mod.Settings)
        {
            Control? input = null;
            Control row;
            switch (def.Type)
            {
                case SettingType.Header:
                    row = new Label { Text = def.DisplayLabel, AutoSize = true, UseMnemonic = false, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, _settingsPanel.Controls.Count > 0 ? 12 : 0, 0, 2) };
                    break;
                case SettingType.Bool:
                {
                    var cb = new CheckBox { Text = def.DisplayLabel, AutoSize = true, Checked = s.Bool(def.Id), UseMnemonic = false };
                    cb.CheckedChanged += (_, _) => Changed(s, def.Id, cb.Checked ? "true" : "false");
                    row = input = cb;
                    break;
                }
                case SettingType.Int or SettingType.Float:
                {
                    bool isInt = def.Type == SettingType.Int;
                    var num = new NumericUpDown
                    {
                        Minimum = (decimal)(def.Min ?? (isInt ? int.MinValue : -1e6)),
                        Maximum = (decimal)(def.Max ?? (isInt ? int.MaxValue : 1e6)),
                        Increment = (decimal)(def.Step ?? 1),
                        DecimalPlaces = isInt ? 0 : DecimalsOf(def.Step ?? 0.1),
                        Width = 90,
                    };
                    num.Value = Math.Max(num.Minimum, Math.Min(num.Maximum, decimal.Parse(s.String(def.Id), Inv)));
                    num.ValueChanged += (_, _) => Changed(s, def.Id, num.Value.ToString(Inv));
                    input = num;
                    row = Row(Caption(def), num);
                    break;
                }
                case SettingType.Choice:
                {
                    var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
                    foreach (var c in def.Choices ?? []) combo.Items.Add(c);
                    combo.SelectedItem = def.Choices?.FirstOrDefault(c => c.Value == s.String(def.Id));
                    combo.SelectedIndexChanged += (_, _) => { if (combo.SelectedItem is ChoiceDef c) Changed(s, def.Id, c.Value); };
                    input = combo;
                    row = Row(Caption(def), combo);
                    break;
                }
                default:
                {
                    var tb = new TextBox { Width = def.Type == SettingType.Seed ? 160 : 260, Text = s.String(def.Id) };
                    tb.TextChanged += (_, _) => Changed(s, def.Id, tb.Text);
                    input = tb;
                    if (def.Type == SettingType.Seed)
                    {
                        var newSeed = new Button { Text = "New seed", AutoSize = true };
                        newSeed.Click += (_, _) => tb.Text = ModSettings.NewSeed();
                        row = Row(Caption(def), tb, newSeed);
                    }
                    else row = Row(Caption(def), tb);
                    break;
                }
            }
            if (def.EnabledBy != null) row.Margin = new Padding(20, row.Margin.Top, 0, row.Margin.Bottom);
            if (def.Help != null)
            {
                _tips.SetToolTip(row, def.Help);
                if (input != null) _tips.SetToolTip(input, def.Help);
            }
            _settingsPanel.Controls.Add(row);
            if (input != null) _settingControls.Add((def.Id, row));
            if (def.Help != null && def.Type != SettingType.Header)
            {
                var help = new Label { Text = def.Help, AutoSize = true, UseMnemonic = false, ForeColor = SystemColors.GrayText, MaximumSize = new Size(width - 20, 0), Margin = new Padding(row.Margin.Left + 20, 0, 0, 4) };
                _settingsPanel.Controls.Add(help);
                _settingControls.Add((def.Id, help));
            }
        }
        if (mod.PatchesExe)
            _settingsPanel.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(width, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 14, 0, 0),
                Text = "Patches SteamWorld Heist 2.exe (current Steam build only). The original is backed up and restored by “Restore original files” or by disabling the mod.",
            });
        UpdateActive(s);
        _settingsPanel.ResumeLayout();
    }

    static Label Caption(SettingDef def) => new() { Text = def.DisplayLabel + ":", AutoSize = true, UseMnemonic = false, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 0) };

    static int DecimalsOf(double step)
    {
        var text = step.ToString("0.####", Inv);
        int dot = text.IndexOf('.');
        return dot < 0 ? 0 : text.Length - dot - 1;
    }

    void Changed(ModSettings s, string id, string value)
    {
        var before = s.Values.ToDictionary(kv => kv.Key, kv => kv.Value);
        if (!s.TrySet(id, value, out var error)) Log(error!);
        // A preset changed other settings (or a manual change switched the preset to custom): show the new values.
        if (s.Values.Any(kv => !kv.Key.Equals(id, StringComparison.OrdinalIgnoreCase) && before[kv.Key] != kv.Value))
            BeginInvoke(new Action(() =>
            {
                var scroll = _settingsPanel.VerticalScroll.Value;
                ShowSelected();
                _settingsPanel.AutoScrollPosition = new Point(0, scroll);
            }));
        else UpdateActive(s);
    }

    void UpdateActive(ModSettings s)
    {
        foreach (var (id, control) in _settingControls) control.Enabled = s.IsActive(id);
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

    void SetBusy(bool busy)
    {
        UseWaitCursor = busy;
        foreach (var b in new[] { _apply, _restore, _extract, _installZip, _remove }) b.Enabled = !busy;
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
