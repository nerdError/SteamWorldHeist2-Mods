using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using SWH2.Core;

namespace SWH2.Mods.Coop;

/// <summary>Loader window for the netplay record/replay test: the four steps of <see cref="NetTest"/> as buttons.</summary>
public sealed class NetTestForm : Form
{
    readonly GameInstall? _game;
    readonly Label _status = new() { AutoSize = true, Font = new Font("Consolas", 9F) };
    readonly NumericUpDown _slot = new() { Minimum = 1, Maximum = 99, Value = 1, Width = 60 };
    readonly CheckBox _launch = new() { Text = "Start the game", Checked = true, AutoSize = true };
    readonly CheckBox _all = new() { Text = "All differences", AutoSize = true };
    readonly TextBox _out = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font("Consolas", 9F) };
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };

    public NetTestForm(GameInstall? game)
    {
        _game = game;
        Text = "Netplay test (record / replay)";
        Font = new Font("Segoe UI", 10F);
        ClientSize = new Size(820, 640);
        MinimumSize = new Size(640, 480);
        StartPosition = FormStartPosition.CenterParent;
        BuildLayout();
        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();
        RefreshStatus();
        FormClosed += (_, _) => _timer.Dispose();
    }

    void BuildLayout()
    {
        var steps = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(8), WrapContents = false };
        steps.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(790, 0),
            Text = "Checks that the same commands from the same save give the same battle (docs/MULTIPLAYER.md). " +
                   "Needs Co-op → Netplay test ticked and applied. Close the game between the steps.",
        });
        steps.Controls.Add(_status);

        steps.Controls.Add(Row(new Label { Text = "Save slot:", AutoSize = true, Anchor = AnchorStyles.Left }, _slot, _launch));
        steps.Controls.Add(Row(
            Button("1. Store slot as test start", () => NetTest.StoreStart(Slot)),
            new Label { Text = "save in the middle of a mission first (enemies alive)", AutoSize = true, ForeColor = SystemColors.GrayText, Anchor = AnchorStyles.Left }));
        steps.Controls.Add(Row(
            Button("2. Record run", () => NetTest.Prepare(RequireGame(), Slot, replay: false, _launch.Checked)),
            Button("3. Replay run", () => NetTest.Prepare(RequireGame(), Slot, replay: true, _launch.Checked)),
            Button("4. Compare", () =>
            {
                var report = NetTest.Compare(out bool differs, _all.Checked);
                return report + Environment.NewLine + (differs ? "=> DIFFERENT" : "=> SAME");
            }),
            _all));
        steps.Controls.Add(Row(
            Button("Open logs folder", () =>
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{NetTest.Dir}\"") { UseShellExecute = true });
                return "";
            })));
        Controls.Add(_out);
        Controls.Add(steps);
    }

    int Slot => (int)_slot.Value - 1;

    GameInstall RequireGame() => _game ?? throw new InvalidOperationException("No game folder set in the loader.");

    static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
        row.Controls.AddRange(controls);
        return row;
    }

    Button Button(string text, Func<string> action)
    {
        var b = new Button { Text = text, AutoSize = true };
        b.Click += (_, _) =>
        {
            try
            {
                var result = action();
                if (result.Length > 0) Show(result);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                Show("ERROR: " + ex.Message);
            }
            RefreshStatus();
        };
        return b;
    }

    void Show(string text)
    {
        _out.Text = $"[{DateTime.Now:HH:mm:ss}]{Environment.NewLine}{text.Replace("\n", Environment.NewLine).Replace("\r\r", "\r")}";
    }

    void RefreshStatus()
    {
        try { _status.Text = NetTest.Status(_game); }
        catch (IOException) { }
    }
}
