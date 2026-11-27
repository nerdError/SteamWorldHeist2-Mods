using System.Windows.Forms;
using SWH2.Core;
using SWH2.Modding;

namespace SWH2.Mods.Coop;

/// <summary>Loader window (Network game…: <see cref="NetPlayForm"/>) and <c>swh2 nettest</c> (network game + developer tests, <see cref="NetTest"/>).</summary>
public sealed partial class CoopMod : IModTool, IModCommand
{
    static NetPlayForm? _form;

    public void OpenTool(ModToolContext ctx)
    {
        if (_form is null || _form.IsDisposed) _form = new NetPlayForm(ctx.Game);
        if (ctx.Owner is Form owner) _form.Icon = owner.Icon;
        _form.Show(ctx.Owner as IWin32Window);
        _form.Activate();
    }

    const string Usage = "swh2 nettest play host [[BIND:]PORT] [--share SLOT] | play join ADDRESS[:PORT] [--slot N | --no-save]   [--name NICK] [--no-launch] [--data DIR]\n" +
        "    (network game; host --share: the save slot the guest gets (default: most recent); join: the host's save goes into slot N first;\n" +
        "    --data: another game copy's data folder) |\n" +
        "  getsave ADDRESS[:PORT] FILE   (only fetch the host's save from the host's running game) |\n" +
        "  info [ADDRESS[:PORT]]   (this game's mods/data fingerprint; with an address: compare with the host's running game) |\n" +
        "  status | store SLOT | record SLOT [--no-launch] | replay SLOT [--no-launch] | compare [--all] |\n" +
        "  run SLOT [--record | --autoplay] [--timeout SEC] [--stall SEC] [--script FILE]   (unattended: autopilot into the mission, replay, close, compare) |\n" +
        "  net SLOT [--guest DIR] [--port N] [--timeout SEC] [--stall SEC] [--guest-plays] [--shots] [--names HOST,GUEST] [--drop SEC] [--late] [--rejoin SEC]   (network test: this copy hosts, the GOG copy replays live over TCP;\n" +
        "    --guest-plays: the crew is split host/guest, each plays its own from the record; --drop: the guest's connection breaks once, SEC s in;\n" +
        "    --late: the guest joins inside the host's battle, from the host's save there;\n" +
        "    --rejoin SEC: the guest quits to the main menu SEC s into the battle and must come back into it by itself)";

    public int RunCommand(IReadOnlyList<string> a, ModToolContext ctx)
    {
        try
        {
            string cmd = a.Count > 0 ? a[0] : "status";
            bool launch = !a.Contains("--no-launch");
            int Slot() => a.Count > 1 && int.TryParse(a[1], out int s) && s >= 1 ? s - 1 : throw new ArgumentException(Usage);
            GameInstall Game() => ctx.Game ?? throw new InvalidOperationException("No game folder.");
            switch (cmd)
            {
                case "status": ctx.Log(NetTest.Status(ctx.Game)); return 0;
                case "play":
                {
                    string? Opt(string name) { int i = a.ToList().IndexOf(name); return i >= 0 && i + 1 < a.Count ? a[i + 1] : null; }
                    var p = NetSession.Load();
                    if (a.Count < 2 || a[1] is not ("host" or "join")) throw new ArgumentException(Usage);
                    p.Host = a[1] == "host";
                    string? arg = a.Count > 2 && !a[2].StartsWith("--", StringComparison.Ordinal) ? a[2] : null;
                    if (p.Host && arg != null)
                    {
                        if (arg.Contains(':')) { var (bind, bport) = NetSession.ParseAddress(arg); p.Bind = bind; p.Port = bport; }
                        else p.Port = int.TryParse(arg, out int port) ? port : throw new ArgumentException(Usage);
                    }
                    if (!p.Host) p.Address = arg ?? throw new ArgumentException(Usage);
                    if (Opt("--name") is { } nick) p.Name = nick;
                    if (Opt("--share") is { } share) p.ShareSlot = int.TryParse(share, out int sh) && sh >= 1 ? sh - 1 : throw new ArgumentException(Usage);
                    if (Opt("--slot") is { } slot) { p.GuestSlot = int.TryParse(slot, out int gs) && gs >= 1 ? gs - 1 : throw new ArgumentException(Usage); p.GetSave = true; }
                    if (a.Contains("--no-save")) p.GetSave = false;
                    ctx.Log(NetSession.Start(Game(), p, launch, Opt("--data")));
                    return 0;
                }
                case "info":
                    ctx.Log("This game: " + NetSession.Fingerprint(Game()));
                    if (a.Count > 1)
                    {
                        var (ih, ip) = NetSession.ParseAddress(a[1]);
                        string hostInfo = NetSession.FetchInfo(ih, ip);
                        ctx.Log("Host:      " + hostInfo);
                        ctx.Log(NetSession.CheckSameGame(Game(), hostInfo));
                    }
                    return 0;
                case "getsave":
                {
                    if (a.Count < 3) throw new ArgumentException(Usage);
                    var (host, port) = NetSession.ParseAddress(a[1]);
                    var got = NetSession.FetchSave(host, port);
                    File.WriteAllBytes(a[2], got.Data);
                    ctx.Log($"Got {got.HostFile} ({got.Data.Length} bytes) from {host}:{port} -> {a[2]}; saved at host log byte {got.LogFrom}, {got.TurnEnds} turn ends before");
                    return 0;
                }
                case "store": ctx.Log(NetTest.StoreStart(Slot())); return 0;
                case "record": ctx.Log(NetTest.Prepare(Game(), Slot(), replay: false, launch)); return 0;
                case "replay": ctx.Log(NetTest.Prepare(Game(), Slot(), replay: true, launch)); return 0;
                case "run":
                {
                    string? Opt(string name) { int i = a.ToList().IndexOf(name); return i >= 0 && i + 1 < a.Count ? a[i + 1] : null; }
                    int timeout = int.TryParse(Opt("--timeout"), out int t) ? t : 900, stall = int.TryParse(Opt("--stall"), out int st) ? st : 90;
                    var script = Opt("--script") is { } file ? File.ReadAllLines(file) : null;
                    ctx.Log(NetTest.Run(Game(), Slot(), replay: !a.Contains("--record"), timeout, stall, script, out bool runDiffers, autoplay: a.Contains("--autoplay")));
                    return runDiffers ? 1 : 0;
                }
                case "net":
                {
                    string? Opt(string name) { int i = a.ToList().IndexOf(name); return i >= 0 && i + 1 < a.Count ? a[i + 1] : null; }
                    int timeout = int.TryParse(Opt("--timeout"), out int t) ? t : 900, stall = int.TryParse(Opt("--stall"), out int st) ? st : 120;
                    int port = int.TryParse(Opt("--port"), out int pt) ? pt : 27015;
                    string guestDir = Opt("--guest") ?? GameInstall.DetectAll().FirstOrDefault(d => !string.Equals(d, Game().Root, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException("No second game copy found: pass --guest DIR.");
                    ctx.Log(NetTest.RunNet(Game(), new GameInstall(guestDir), Slot(), port, timeout, stall, out bool netDiffers, guestPlays: a.Contains("--guest-plays"), shots: a.Contains("--shots"), names: Opt("--names")?.Split(','), dropSec: int.TryParse(Opt("--drop"), out int drop) ? drop : 0, late: a.Contains("--late"), rejoinSec: int.TryParse(Opt("--rejoin"), out int rejoin) ? rejoin : 0));
                    return netDiffers ? 1 : 0;
                }
                case "compare":
                    ctx.Log(NetTest.Compare(out bool differs, a.Contains("--all")));
                    return differs ? 1 : 0;
                default: throw new ArgumentException(Usage);
            }
        }
        catch (ArgumentException ex) { ctx.Log(ex.Message); return 2; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { ctx.Log("ERROR: " + ex.Message); return 1; }
    }
}
