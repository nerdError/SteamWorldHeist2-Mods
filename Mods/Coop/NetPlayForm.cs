using System.Drawing;
using System.Windows.Forms;
using SWH2.Core;

namespace SWH2.Mods.Coop;

/// <summary>Loader window: start a network game (nickname, host or join), see <see cref="NetSession"/>.</summary>
public sealed class NetPlayForm : Form
{
    readonly GameInstall? _game;
    readonly TextBox _name = new() { Width = 220, MaxLength = 20 };
    readonly RadioButton _host = new() { Text = "Host a game", AutoSize = true };
    readonly RadioButton _join = new() { Text = "Join a game", AutoSize = true };
    readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = NetSession.DefaultPort, Width = 90 };
    readonly TextBox _address = new() { Width = 260 };
    readonly ComboBox _share = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    readonly CheckBox _getSave = new() { Text = "Get the host's save first, into my slot", AutoSize = true, Anchor = AnchorStyles.Left };
    readonly NumericUpDown _guestSlot = new() { Minimum = 1, Maximum = SaveBanks.GameSlots, Width = 60 };
    readonly TextBox _out = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9F) };

    public NetPlayForm(GameInstall? game)
    {
        _game = game;
        Text = "NE Co-op — network game";
        Font = new Font("Segoe UI", 10F);
        ClientSize = new Size(760, 560);
        MinimumSize = new Size(620, 460);
        StartPosition = FormStartPosition.CenterParent;

        var p = NetSession.Load();
        _name.Text = p.Name;
        _host.Checked = p.Host;
        _join.Checked = !p.Host;
        _port.Value = Math.Max(1, Math.Min(65535, p.Port));
        _address.Text = p.Address;
        List<SaveBanks.Slot> slots = _game == null ? [] : SaveBanks.ForGame(_game)?.Slots() ?? [];
        _share.Items.Add(new ShareItem(-1, "the most recently saved slot"));
        foreach (var s in slots.Where(s => s.Used)) _share.Items.Add(new ShareItem(s.Index, $"slot {s.Index + 1} — saved {s.Modified:yyyy-MM-dd HH:mm}"));
        _share.SelectedItem = _share.Items.Cast<ShareItem>().FirstOrDefault(i => i.Slot == p.ShareSlot) ?? _share.Items[0];
        _getSave.Checked = p.GetSave;
        _guestSlot.Maximum = Math.Max(SaveBanks.GameSlots, Math.Max(slots.Count, p.GuestSlot + 1));
        _guestSlot.Value = p.GuestSlot + 1;
        // The two sit in different rows (containers), so WinForms doesn't group them: each one unticks the other.
        _host.CheckedChanged += (_, _) => { if (_host.Checked) _join.Checked = false; UpdateEnabled(); };
        _join.CheckedChanged += (_, _) => { if (_join.Checked) _host.Checked = false; UpdateEnabled(); };
        _getSave.CheckedChanged += (_, _) => UpdateEnabled();
        UpdateEnabled();

        var top = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(10), WrapContents = false };
        top.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(730, 0),
            Text = "Two players, each with their own game (Steam or GOG) and NE Co-op applied. One hosts, the other joins. " +
                   "The crew is split: 1st crewmate host, 2nd guest, 3rd host… — each controls only their own.",
        });
        top.Controls.Add(Row(Caption("Your nickname:"), _name));
        top.Controls.Add(Row(_host, Caption("port:"), _port));
        top.Controls.Add(Row(Indent(), Caption("give the guest my save:"), _share));
        top.Controls.Add(Row(_join, Caption("host's address:"), _address, Hint("e.g. 192.168.1.20 or 26.1.2.3:27015")));
        top.Controls.Add(Row(Indent(), _getSave, _guestSlot, Hint("(what is there now is kept as a save bank)")));
        var start = new Button { Text = "Start the game", AutoSize = true, Font = new Font(Font, FontStyle.Bold) };
        start.Click += (_, _) => Run(() => NetSession.Start(RequireGame(), Current(), launch: true));
        var prepare = new Button { Text = "Only prepare (start the game yourself)", AutoSize = true };
        prepare.Click += (_, _) => Run(() => NetSession.Start(RequireGame(), Current(), launch: false));
        var dev = new Button { Text = "Developer tests…", AutoSize = true };
        dev.Click += (_, _) => { var f = new NetTestForm(_game) { Icon = Icon }; f.Show(this); };
        top.Controls.Add(Row(start, prepare, dev));
        Controls.Add(_out);
        Controls.Add(top);

        Show(p.Host
            ? "Your addresses (give one to the other player):" + Environment.NewLine + string.Join(Environment.NewLine, NetSession.LocalAddresses().Select(a => "    " + a))
            : "The host starts their game first. Then enter the host's address and click Start the game: your loader gets the host's save, then starts your game.");
    }

    void UpdateEnabled()
    {
        _port.Enabled = _host.Checked;
        _address.Enabled = _join.Checked;
        _share.Enabled = _host.Checked;
        _getSave.Enabled = _join.Checked;
        _guestSlot.Enabled = _join.Checked && _getSave.Checked;
    }

    sealed record ShareItem(int Slot, string Text)
    {
        public override string ToString() => Text;
    }

    NetSession.Profile Current() => new()
    {
        Name = _name.Text, Host = _host.Checked, Port = (int)_port.Value, Address = _address.Text.Trim(),
        ShareSlot = (_share.SelectedItem as ShareItem)?.Slot ?? -1, GetSave = _getSave.Checked, GuestSlot = (int)_guestSlot.Value - 1,
    };

    GameInstall RequireGame() => _game ?? throw new InvalidOperationException("No game folder set in the loader.");

    void Run(Func<string> action)
    {
        try { Show(action()); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) { Show("ERROR: " + ex.Message); }
    }

    void Show(string text) => _out.Text = text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);

    static Label Caption(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 0) };
    static Label Indent() => new() { AutoSize = false, Width = 24 };
    static Label Hint(string text) => new() { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 0) };

    static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
        row.Controls.AddRange(controls);
        return row;
    }
}
