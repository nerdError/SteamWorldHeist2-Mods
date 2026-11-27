using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SWH2.Core;

namespace SWH2.Mods.CheatMenu;

/// <summary>
/// Replacement for the cheat menu stripped from the release game: sends commands to the CheatMenu Lua mod
/// (see CheatClient). Global hotkeys Ctrl+Alt+1…8 work while the game has focus.
/// </summary>
public sealed class CheatForm : Form
{
    record Cheat(string Label, string Command, Keys Key, string Tip);

    static readonly Cheat[] ActionCheats =
    [
        new("Win mission", "win", Keys.D1, "Ends the mission in victory"),
        new("Heal crew", "heal", Keys.D2, "Restores all crew to full health"),
        new("Kill all enemies", "kill", Keys.D3, "May break scripted boss fights"),
        new("Enemies to 1 HP", "weaken", Keys.D4, "Every enemy on the map drops to 1 HP"),
        new("Alarm off", "alarm_off", Keys.D5, "Turns the alarm off"),
    ];

    static readonly Cheat[] ToggleCheats =
    [
        new("Invulnerable crew", "god", Keys.D6, "Crew takes no damage and can't drop below 1 HP"),
        new("Freeze enemies", "freeze", Keys.D7, "Enemy AI just passes its turn"),
        new("Attacks don't end turn", "spinup", Keys.D8, "Crew can keep shooting after an attack"),
    ];

    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    readonly GameInstall? _game;
    readonly Dictionary<string, CheckBox> _toggles = [];
    readonly Label _status = new() { AutoSize = true, Font = new Font("Segoe UI", 10F, FontStyle.Bold) };
    readonly ComboBox _item = new() { Width = 360, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
    readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9F) };
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    readonly Dictionary<string, DateTime> _lastClick = [];
    bool _syncing;

    public CheatForm(GameInstall? game)
    {
        _game = game;
        Text = "Cheats";
        Font = new Font("Segoe UI", 10F);
        ClientSize = new Size(560, 600);
        MinimumSize = new Size(480, 480);
        StartPosition = FormStartPosition.CenterParent;
        BuildLayout();
        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();
        RefreshStatus();
        LoadItemsAsync();
    }

    void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1 };
        for (int i = 0; i < 5; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        root.Controls.Add(_status);
        root.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            ForeColor = SystemColors.GrayText,
            Text = "The NE Cheat Menu mod must be enabled and applied to the game (Apply to game). "
                 + "Cheats work inside missions only. Hotkeys Ctrl+Alt+1…8 work while the game has focus.",
        });

        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 4) };
        var tips = new ToolTip();
        foreach (var c in ActionCheats)
        {
            var b = new Button { Text = $"{c.Label}  ({HotkeyText(c.Key)})", AutoSize = true };
            tips.SetToolTip(b, c.Tip);
            b.Click += (_, _) => Send(c.Command);
            actions.Controls.Add(b);
        }
        root.Controls.Add(actions);

        var toggles = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        foreach (var c in ToggleCheats)
        {
            var cb = new CheckBox { Text = $"{c.Label}  ({HotkeyText(c.Key)})", AutoSize = true };
            tips.SetToolTip(cb, c.Tip);
            cb.CheckedChanged += (_, _) =>
            {
                if (_syncing) return;
                _lastClick[c.Command] = DateTime.UtcNow;
                Send(c.Command, cb.Checked ? "1" : "0");
            };
            _toggles[c.Command] = cb;
            toggles.Controls.Add(cb);
        }
        root.Controls.Add(toggles);

        var give = new Button { Text = "Give item", AutoSize = true };
        give.Click += (_, _) => GiveSelected();
        var giveRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 8, 0, 4) };
        giveRow.Controls.AddRange([_item, give]);
        root.Controls.Add(giveRow);

        var logBox = new GroupBox { Text = "Results (swh2_cheats.log)", Dock = DockStyle.Fill, Padding = new Padding(8) };
        logBox.Controls.Add(_log);
        root.Controls.Add(logBox);
        Controls.Add(root);
    }

    static string HotkeyText(Keys k) => "Ctrl+Alt+" + (char)k;

    void Send(string cmd, string? arg = null)
    {
        try { CheatClient.Send(cmd, arg); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void GiveSelected()
    {
        var text = _item.Text.Trim();
        // Entries look like "Display name [item_id]"; a raw id typed by hand works too.
        var open = text.LastIndexOf('[');
        var id = open >= 0 && text.EndsWith(']') ? text[(open + 1)..^1] : text;
        if (id.Length > 0) Send("give", id);
    }

    void LoadItemsAsync()
    {
        if (_game == null) return;
        var game = _game;
        Task.Run(() =>
        {
            using var content = GameContent.OpenOriginal(new GameFiles(game));
            var items = new ItemCatalog(content, Localization.Load(content));
            return items.All.Where(i => !i.Abstract).Select(i => items.Display(i.Name)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }).ContinueWith(t =>
        {
            if (IsDisposed || t.IsFaulted) return;
            BeginInvoke(() => _item.Items.AddRange(t.Result));
        });
    }

    void RefreshStatus()
    {
        var st = CheatClient.ReadStatus();
        if (st == null)
        {
            _status.Text = "No contact yet — start a mission with the CheatMenu mod installed.";
            _status.ForeColor = SystemColors.GrayText;
        }
        else
        {
            _status.Text = st.InMission ? $"✔ In mission: {st.Script}" : $"Not in a mission (last: {st.Script}, {st.LastSeen.ToLocalTime():HH:mm:ss})";
            _status.ForeColor = st.InMission ? Color.FromArgb(0, 150, 0) : SystemColors.GrayText;
            // Only trust the mod's state while it is alive, and give a fresh click time to be picked up.
            if (st.InMission)
            {
                _syncing = true;
                foreach (var (name, on) in st.Toggles)
                    if (_toggles.TryGetValue(name, out var cb) && DateTime.UtcNow - _lastClick.GetValueOrDefault(name) > TimeSpan.FromSeconds(4))
                        cb.Checked = on;
                _syncing = false;
            }
        }
        var log = string.Join(Environment.NewLine, CheatClient.ReadLogTail(12));
        if (_log.Text != log) { _log.Text = log; _log.SelectionStart = log.Length; _log.ScrollToCaret(); }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        foreach (var (c, i) in ActionCheats.Concat(ToggleCheats).Select((c, i) => (c, i)))
            RegisterHotKey(Handle, i + 1, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)c.Key);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        for (int i = 1; i <= ActionCheats.Length + ToggleCheats.Length; i++) UnregisterHotKey(Handle, i);
        _timer.Stop();
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            int i = (int)m.WParam - 1;
            if (i >= 0 && i < ActionCheats.Length) Send(ActionCheats[i].Command);
            else if ((i -= ActionCheats.Length) >= 0 && i < ToggleCheats.Length)
            {
                var cb = _toggles[ToggleCheats[i].Command];
                cb.Checked = !cb.Checked;   // sends the command via CheckedChanged
            }
            return;
        }
        base.WndProc(ref m);
    }
}
