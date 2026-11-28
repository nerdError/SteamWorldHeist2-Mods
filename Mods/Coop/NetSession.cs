using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using SWH2.Core;

namespace SWH2.Mods.Coop;

/// <summary>
/// Starting a network game (the loader's Network game… window, <c>swh2 nettest play</c>): the player's profile
/// (nickname, last address) and the session file the Coop dll reads at game start:
/// <code>
/// host [address:]port     (or: join address:port)
/// name nickname
/// save path             (host: the save slot file the guest's loader fetches, see FetchSave)
/// </code>
/// The game reads its save slots only at start, so a joining loader first gets the host's save from the host's
/// running game and puts it into one of the guest's slots (the old one kept as a save bank), then starts the game.
/// in %APPDATA%\Thunderful\SteamWorld Heist II\swh2_net_session.txt. The dll renames it to swh2_net_session.used.txt
/// once read, so the next start of the game is single-player again.
/// </summary>
public static class NetSession
{
    public const int DefaultPort = 27015;
    public static string SessionFile(string dir) => Path.Combine(dir, "swh2_net_session.txt");
    public static string ProfileFile => Path.Combine(NetTest.Dir, "swh2_coop_profile.txt");

    public sealed class Profile
    {
        public string Name = Environment.UserName;
        public bool Host = true;
        public int Port = DefaultPort;
        public string Address = "";
        /// <summary>Host: the save slot (0-based) the guest's loader gets; -1 = the most recently saved one.</summary>
        public int ShareSlot = -1;
        /// <summary>Guest: fetch the host's save before the game starts, into <see cref="GuestSlot"/> (0-based).</summary>
        public bool GetSave = true;
        public int GuestSlot = SaveBanks.GameSlots - 1;
        /// <summary>Host: a Steam lobby too (session "steam host", steamnet.c): friends join from the Steam friends list.</summary>
        public bool Steam = true;
        /// <summary>Host: listen only on this address ("127.0.0.1": tests on one PC, no firewall question); not kept.</summary>
        public string Bind = "";
    }

    public static Profile Load()
    {
        var p = new Profile();
        if (!File.Exists(ProfileFile)) return p;
        foreach (var line in File.ReadAllLines(ProfileFile))
        {
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
            switch (k)
            {
                case "name": p.Name = v; break;
                case "mode": p.Host = v != "join"; break;
                case "port": if (int.TryParse(v, out int port)) p.Port = port; break;
                case "address": p.Address = v; break;
                case "share": if (int.TryParse(v, out int share)) p.ShareSlot = share; break;
                case "getsave": p.GetSave = v != "0"; break;
                case "guestslot": if (int.TryParse(v, out int gs) && gs >= 0) p.GuestSlot = gs; break;
                case "steam": p.Steam = v != "0"; break;
            }
        }
        return p;
    }

    public static void Save(Profile p)
    {
        Directory.CreateDirectory(NetTest.Dir);
        File.WriteAllLines(ProfileFile, [$"name={p.Name}", $"mode={(p.Host ? "host" : "join")}", $"port={p.Port}", $"address={p.Address}",
            $"share={p.ShareSlot}", $"getsave={(p.GetSave ? 1 : 0)}", $"guestslot={p.GuestSlot}", $"steam={(p.Steam ? 1 : 0)}"]);
    }

    /// <summary>A nickname the dll and the game's text can carry: printable, no spaces at the ends, at most 20 characters.</summary>
    public static string CleanName(string name)
    {
        var clean = new string(name.Where(c => !char.IsControl(c) && c != '"').ToArray()).Trim();
        if (clean.Length > 20) clean = clean.Substring(0, 20).TrimEnd();
        if (Encoding.UTF8.GetByteCount(clean) > 60) clean = clean.Substring(0, 10);
        return clean;
    }

    /// <summary>"1.2.3.4:27015" / "host.example:27015" / "1.2.3.4" (default port).</summary>
    public static (string Host, int Port) ParseAddress(string text)
    {
        text = text.Trim();
        if (text.Length == 0) throw new InvalidOperationException("Enter the host's address (IP or name, optionally :port).");
        int colon = text.LastIndexOf(':');
        if (colon > 0 && int.TryParse(text.Substring(colon + 1), out int port) && port is > 0 and < 65536) return (text.Substring(0, colon), port);
        if (text.Contains(' ')) throw new InvalidOperationException($"Not an address: {text}");
        return (text, DefaultPort);
    }

    /// <summary>Writes the session file and (optionally) starts the game. Returns what to tell the player.</summary>
    public static string Start(GameInstall game, Profile p, bool launch, string? dataDir = null)
    {
        // Kept whatever happens next (a refused join kept nothing before: the player typed everything again).
        if (dataDir == null) Save(p);
        if (game.IsRunning) throw new InvalidOperationException("The game is running: close it first (the network game is set up at game start).");
        if (!NetTest.InstalledWithNetTest(game))
            throw new InvalidOperationException("NE Co-op is not applied to this game: tick it in the loader and click Apply to game first.");
        string name = CleanName(p.Name);
        if (name.Length == 0) throw new InvalidOperationException("Enter a nickname.");
        p.Name = name;
        string dir = dataDir ?? NetTest.Dir;
        Directory.CreateDirectory(dir);
        // A test's control file would win over the session (dll): it is only for the developer test runs.
        if (dataDir == null && File.Exists(NetTest.ControlFile)) File.Delete(NetTest.ControlFile);
        var lines = new List<string>();
        var sb = new StringBuilder();
        if (p.Host)
        {
            if (p.Port is <= 0 or >= 65536) throw new InvalidOperationException("Port: 1–65535.");
            lines.Add(p.Bind.Length > 0 ? $"host {p.Bind}:{p.Port}" : $"host {p.Port}");
            sb.AppendLine($"Hosting on port {p.Port} as {name}. Tell the other player one of your addresses:");
            foreach (var a in LocalAddresses()) sb.AppendLine($"    {a}:{p.Port}");
            sb.AppendLine("Over the internet: forward TCP port " + p.Port + " on your router to this PC, and give your public IP — or both join the same");
            sb.AppendLine("virtual LAN (Radmin VPN, ZeroTier, Tailscale…) and use its address. Windows asks once whether the game may use the network: allow it.");
            var saves = SaveBanks.ForGame(game);
            var shared = saves == null ? null : SharedSlot(saves, p.ShareSlot);
            if (shared != null)
            {
                lines.Add($"save {saves!.SlotPath(shared.Index)}");
                sb.AppendLine($"The guest's loader gets your save slot {shared.Index + 1} (saved {shared.Modified:yyyy-MM-dd HH:mm}) when it joins: play that slot.");
                sb.AppendLine("It reads the file at that moment — if you saved later, the guest joins again from its loader to get the new one.");
            }
            else sb.AppendLine("No save to give the guest (no used slot yet): the guest has to bring a copy of your save.");
            lines.Add($"info {Fingerprint(game)}");
            if (p.Steam && game.Store != "GOG")
            {
                lines.Add("steam host");
                sb.AppendLine("Through Steam too: a friend right-clicks you in the Steam friends list → Join game (or you invite them from the");
                sb.AppendLine("Steam overlay, Shift+Tab). No address, no port forwarding, no VPN. Their game must have NE Co-op applied.");
            }
        }
        else
        {
            var (host, port) = ParseAddress(p.Address);
            lines.Add($"join {host}:{port}");
            sb.AppendLine($"Joining {host}:{port} as {name}.");
            sb.AppendLine(CheckSameGame(game, FetchInfo(host, port)));
            if (p.GetSave)
            {
                var saves = SaveBanks.ForGame(game) ?? throw new InvalidOperationException(
                    "Your game has no save folder yet: start it once, make a save, quit, then join (or untick \"Get the host's save\").");
                var got = FetchSave(host, port);
                var (data, hostFile) = (got.Data, got.HostFile);
                var kept = saves.Put(p.GuestSlot, data);
                // A save from inside the host's mission: the game takes the host's battle from where it was saved.
                if (got.LogFrom > 0) lines.Add($"from {got.LogFrom} {got.TurnEnds} {(got.InBattle ? "battle" : "map")}");
                lines.Add($"slot {saves.SlotPath(p.GuestSlot)}");   // the game puts the host's next battles there itself
                sb.AppendLine($"Got the host's save ({hostFile}, {data.Length / 1024} KB) and put it into your slot {p.GuestSlot + 1}" +
                              (kept != null ? $"; what was there is kept as save bank \"{kept.Name}\" (Save banks… in the loader)." : "."));
                sb.AppendLine(got.InBattle
                    ? $"The host is in a battle: in the game, Continue / load slot {p.GuestSlot + 1} — you land in the host's battle."
                    : $"In the game: Continue / load slot {p.GuestSlot + 1}, then enter the mission the host enters.");
            }
        }
        lines.Add($"name {name}");
        File.WriteAllLines(SessionFile(dir), lines, new UTF8Encoding(false));
        if (dataDir == null) Save(p);   // the cleaned name; --data: a test copy, not the player's profile
        sb.AppendLine(p.Host
            ? "Go into the battle first; then the guest joins (its loader gets your save from inside the battle) and lands in it. Until the guest is in, your crew waits."
            : "Both: the same save and the same mission; your game follows the host's.");
        sb.AppendLine("The network game is for this one start of the game; start it again from here next time.");
        if (launch)
        {
            Process.Start(new ProcessStartInfo(game.ExePath) { WorkingDirectory = game.Root, UseShellExecute = true })?.Dispose();
            sb.AppendLine("Game started.");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>The slot the host shares: the asked one if it is used, else the most recently saved; null = no save.</summary>
    public static SaveBanks.Slot? SharedSlot(SaveBanks saves, int wanted)
    {
        var used = saves.Slots().Where(s => s.Used).ToList();
        return used.FirstOrDefault(s => s.Index == wanted) ?? used.OrderByDescending(s => s.Modified).FirstOrDefault();
    }

    /// <summary>
    /// Gets the host's save from the host's running game (nettcp.c): "getsave" on the game port, the answer is
    /// "save &lt;size&gt; &lt;file name&gt; &lt;log bytes&gt; &lt;turn ends&gt;" + the bytes, or "nosave &lt;reason&gt;". Log bytes &gt; 0:
    /// the save was written inside the host's mission, when its log was that long.
    /// </summary>
    public sealed record Fetched(byte[] Data, string HostFile, long LogFrom, int TurnEnds, bool InBattle);

    public static Fetched FetchSave(string host, int port, int timeoutMs = 8000)
    {
        using var client = Connect(host, port, timeoutMs);
        var stream = client.GetStream();
        string line = Ask(stream, "getsave", host, port);
        if (line.StartsWith("nosave ", StringComparison.Ordinal)) throw new InvalidOperationException("The host has no save to give: " + line.Substring(7));
        var parts = line.Split(' ');
        if (parts.Length < 2 || parts[0] != "save" || !int.TryParse(parts[1], out int size) || size <= 0 || size > 64 << 20)
            throw new InvalidOperationException($"Unexpected answer from the host: {line}");
        var data = new byte[size];
        try
        {
            for (int got = 0, k; got < size; got += k)
                if ((k = stream.Read(data, got, size - got)) <= 0) throw new InvalidOperationException("The connection to the host broke while getting the save.");
        }
        catch (IOException) { throw new InvalidOperationException("The connection to the host broke while getting the save."); }
        if (size < 4 || Encoding.ASCII.GetString(data, 0, 4) != "SWH2") throw new InvalidOperationException("What the host sent is not a SteamWorld Heist II save.");
        // "save <size> <file> <log bytes> <turn ends> <battle|map>"
        long from = 0;
        int ends = 0;
        bool withLog = parts.Length >= 6 && parts[parts.Length - 1] is "battle" or "map"
            && long.TryParse(parts[parts.Length - 3], out from) && int.TryParse(parts[parts.Length - 2], out ends);
        if (!withLog) { from = 0; ends = 0; }
        bool battle = withLog && parts[parts.Length - 1] == "battle" && from > 0;
        string file = string.Join(" ", parts.Skip(2).Take(parts.Length - 2 - (withLog ? 3 : 0)));
        return new Fetched(data, file.Length > 0 ? file : "save", from, ends, battle);
    }

    static TcpClient Connect(string host, int port, int timeoutMs)
    {
        var client = new TcpClient();
        try
        {
            var connect = client.BeginConnect(host, port, null, null);
            if (!connect.AsyncWaitHandle.WaitOne(timeoutMs)) throw new SocketException((int)SocketError.TimedOut);
            client.EndConnect(connect);
        }
        catch (SocketException ex)
        {
            client.Dispose();
            throw new InvalidOperationException($"Can't reach the host's game at {host}:{port} ({ex.SocketErrorCode}). " +
                "The host starts their game first (from their loader, Host a game); check the address, the port forwarding / VPN and the host's firewall.");
        }
        client.ReceiveTimeout = 30000;
        return client;
    }

    /// <summary>Sends a request line, returns the first answer line.</summary>
    static string Ask(NetworkStream stream, string request, string host, int port)
    {
        var ask = Encoding.ASCII.GetBytes(request + "\n");
        stream.Write(ask, 0, ask.Length);
        var head = new List<byte>();
        try
        {
            for (int b; (b = stream.ReadByte()) != '\n';)
            {
                if (b < 0 || head.Count > 4000) throw new InvalidOperationException($"The host at {host}:{port} didn't answer (an older Co-op version, or not the game?).");
                head.Add((byte)b);
            }
        }
        catch (IOException) { throw new InvalidOperationException($"The host at {host}:{port} didn't answer in time (an older Co-op version?)."); }
        return Encoding.UTF8.GetString(head.ToArray()).TrimEnd('\r');
    }

    /// <summary>The host's fingerprint (<see cref="Fingerprint"/>) from its running game; "-" = the host's loader gave none.</summary>
    public static string FetchInfo(string host, int port, int timeoutMs = 8000)
    {
        using var client = Connect(host, port, timeoutMs);
        client.ReceiveTimeout = 8000;
        string line = Ask(client.GetStream(), "getinfo", host, port);
        if (!line.StartsWith("info ", StringComparison.Ordinal)) throw new InvalidOperationException($"Unexpected answer from the host: {line}");
        return line.Substring(5);
    }

    /// <summary>
    /// What both games must share to stay in sync: "dll=&lt;Coop dll&gt; data=&lt;installed paks&gt; mods=&lt;installed mods&gt;".
    /// The modded paks are the same for Steam and GOG (the mods' output is deterministic), the exes are not — exe-only
    /// mod differences show in the mod list.
    /// </summary>
    public static string Fingerprint(GameInstall game)
    {
        var state = new GameFiles(game).State;
        // Per pak (the error then says which one differs). A pak no mod changed counts as "original": the Steam and GOG
        // Game2.pak differ, yet play the same.
        string data = string.Join(",", GameInstall.PakNames.Select(pak =>
            pak + ":" + (state.InstalledHashes.TryGetValue(pak, out var h) ? Short(h) : "original")));
        string mods = state.InstalledHashes.Count > 0 && state.Description != null ? state.Description : "none";
        mods = new string(mods.Where(c => !char.IsControl(c)).ToArray());
        if (Encoding.UTF8.GetByteCount(mods) > 800) mods = mods.Substring(0, 300) + "…";
        return $"dll={DllId(Path.Combine(game.ModsDir, "Coop", "swh2coop.dll"))} data={data} mods={mods}";
    }

    /// <summary>
    /// The Coop dll's source id ("src:" + the hash of its sources that build.cmd compiles in as "SWH2COOP_SRC=..."):
    /// two PCs that built the same sources with different compiler versions get different dll bytes, but the same id.
    /// An older dll without it: "file:" + the file hash. "none" = no dll.
    /// </summary>
    public static string DllId(string dll)
    {
        if (!File.Exists(dll)) return "none";
        var bytes = File.ReadAllBytes(dll);
        var marker = Encoding.ASCII.GetBytes("SWH2COOP_SRC=");
        for (int i = 0; i + marker.Length < bytes.Length; i++)
        {
            int k = 0;
            while (k < marker.Length && bytes[i + k] == marker[k]) k++;
            if (k < marker.Length) continue;
            var id = new StringBuilder();
            for (int j = i + k; j < bytes.Length && id.Length < 16 && bytes[j] != 0; j++) id.Append((char)bytes[j]);
            if (id.Length > 0 && id.ToString() != "unknown") return "src:" + id;
        }
        return "file:" + Short(GameFiles.Hash(dll));
    }

    static string Short(string hash) => hash.Length > 12 ? hash.Substring(0, 12) : hash;

    /// <summary>Throws with what differs when the host's game can't stay in sync with this one; else a line for the player.</summary>
    public static string CheckSameGame(GameInstall game, string hostInfo)
    {
        if (hostInfo == "-") return "Note: the host's loader didn't say which mods it has — make sure both applied the same mods.";
        static (string Dll, string Data, string Mods) Parse(string s)
        {
            var m = System.Text.RegularExpressions.Regex.Match(s, @"^dll=(\S+) data=(\S+) mods=(.*)$");
            return m.Success ? (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value) : ("?", "?", s);
        }
        var host = Parse(hostInfo);
        var mine = Parse(Fingerprint(game));
        string nl = Environment.NewLine;
        string both = $"{nl}  Mods, host: {host.Mods}{nl}  Mods, you:  {mine.Mods}";
        if (host.Dll != mine.Dll)
        {
            string why = host.Dll.StartsWith("src:") && mine.Dll.StartsWith("src:")
                ? "the two Co-op dlls were built from different sources (different mod pack versions, or one of you changed / didn't rebuild it)"
                : host.Dll == "none" || mine.Dll == "none"
                    ? "one of you has no Co-op dll in Mods\\Coop (apply NE Co-op in the loader)"
                    : "one of you has an older NE Co-op whose dll can't say its source version";
            throw new InvalidOperationException($"NE Co-op differs between you and the host: {why}.{nl}" +
                $"  Co-op dll, host: {host.Dll}{nl}  Co-op dll, you:  {mine.Dll}{both}{nl}" +
                "Both install the same mod pack version (the same build), Apply, then try again.");
        }
        if (host.Data != mine.Data)
        {
            var differs = DataParts(host.Data).Where(kv => DataParts(mine.Data).TryGetValue(kv.Key, out var v) ? v != kv.Value : true)
                .Select(kv => $"{kv.Key} (host {kv.Value}, you {(DataParts(mine.Data).TryGetValue(kv.Key, out var v) ? v : "?")})").ToList();
            throw new InvalidOperationException("Your game data differs from the host's — the battle would go out of sync." +
                (differs.Count > 0 ? $" Different: {string.Join(", ", differs)}." : $"{nl}  data, host: {host.Data}{nl}  data, you:  {mine.Data}") +
                $"{both}{nl}Both apply exactly the same mods with the same settings (Randomizer: the same seed), then try again.");
        }
        if (host.Mods != mine.Mods)
            return "Warning: the game data matches the host's, but the mod lists differ (exe mods, e.g. Turbo or More Save Slots):" + both;
        return $"Same mods and game data as the host (Co-op dll {mine.Dll}).";
    }

    /// <summary>"Game.pak:ABC,Game2.pak:original" -> pak -> id; an older loader's single hash -> "paks" -> it.</summary>
    static Dictionary<string, string> DataParts(string data)
    {
        var d = new Dictionary<string, string>();
        foreach (var part in data.Split(','))
        {
            int c = part.LastIndexOf(':');
            if (c > 0) d[part.Substring(0, c)] = part.Substring(c + 1);
            else d["paks"] = part;
        }
        return d;
    }

    /// <summary>IPv4 addresses of the network adapters that are up (loopback excluded).</summary>
    public static IEnumerable<string> LocalAddresses()
    {
        var list = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var a in nic.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                        list.Add($"{a.Address}  ({nic.Name})");
            }
        }
        catch (NetworkInformationException) { }
        return list.Count > 0 ? list : ["127.0.0.1  (no network adapter found)"];
    }
}
