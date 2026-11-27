using System.Text;
using SWH2.Core;

namespace SWH2.Modding;

/// <summary>
/// Entry point of a code mod. The plugin dll (named in mod.json "plugin") must contain exactly one public
/// class implementing this interface with a parameterless constructor. It references SWH2.Core.dll from
/// the loader; don't ship a copy of it.
/// </summary>
public interface IModPlugin
{
    /// <summary>
    /// Makes the mod's changes. Runs on every install, always against the ORIGINAL game files plus the changes of
    /// mods that ran before it; nothing is written to the game until every mod has succeeded.
    /// Throw to abort the install with a message.
    /// </summary>
    void Apply(ModContext ctx);
}

/// <summary>Everything a mod gets to work with during an install.</summary>
public sealed class ModContext
{
    readonly Func<ExeImage> _exe;
    Localization? _loc;
    ItemCatalog? _items;
    LuaPatcher? _lua;

    internal ModContext(ModManifest mod, ModSettings settings, GameContent content, Func<ExeImage> exe, Action<string> log, GameOptions? options = null)
    {
        Mod = mod;
        Options = options ?? new GameOptions();
        Options.CurrentMod = mod.Id;
        Settings = settings;
        Content = content;
        _exe = exe;
        Log = log;
    }

    public ModManifest Mod { get; }
    public ModSettings Settings { get; }
    /// <summary>Game files (both paks): read, edit XML in place via <c>Content.Xml(path)</c>, write or add files.</summary>
    public GameContent Content { get; }
    /// <summary>The game exe to patch. Throws if the installed exe isn't the supported build.</summary>
    public ExeImage Exe => _exe();
    public Action<string> Log { get; }
    /// <summary>Sliders/checkboxes in the game's own Options menu (Options → Game), shared by all mods of the install.</summary>
    public GameOptions Options { get; }
    public LuaPatcher Lua => _lua ??= new LuaPatcher(Content);
    /// <summary>English strings (Language/en.csv) of the current content.</summary>
    public Localization Loc => _loc ??= Localization.Load(Content);
    public ItemCatalog Items => _items ??= new ItemCatalog(Content, Loc);

    /// <summary>Text saved next to the install (SWH2ModLoader\Reports\&lt;mod&gt;\) — e.g. a spoiler log. Empty = not saved.</summary>
    public ReportLog Report { get; } = new();
    /// <summary>File name (without extension) for <see cref="Report"/>.</summary>
    public string ReportName { get; set; } = "report";
    /// <summary>Short description of what was installed, shown in the status (defaults to the changed settings).</summary>
    public string? Summary { get; set; }

    /// <summary>Deterministic hash for seeding: the same string gives the same number on every machine and run.</summary>
    public static int StableHash(string s)
    {
        uint h = 2166136261;
        foreach (var b in Encoding.UTF8.GetBytes(s)) h = (h ^ b) * 16777619;
        return (int)(h & 0x7FFFFFFF);
    }
}

public sealed class ReportLog
{
    readonly StringBuilder _sb = new();

    public bool IsEmpty => _sb.Length == 0;

    public void Section(string title)
    {
        _sb.AppendLine();
        _sb.AppendLine("=== " + title + " " + new string('=', Math.Max(3, 70 - title.Length)));
    }

    public void Line(string text) => _sb.AppendLine(text);

    public override string ToString() => _sb.ToString();
}

/// <summary>
/// Optional, on the same class as <see cref="IModPlugin"/>: a window the loader opens from a button
/// (button text = mod.json "tool"). Runs on the GUI thread; the window may stay open while the game runs.
/// </summary>
public interface IModTool
{
    void OpenTool(ModToolContext ctx);
}

/// <summary>Optional: a command for the swh2 CLI (mod.json "command", e.g. <c>swh2 cheat win</c>).</summary>
public interface IModCommand
{
    /// <summary>Returns the process exit code (0 = ok, 2 = bad arguments).</summary>
    int RunCommand(IReadOnlyList<string> args, ModToolContext ctx);
}

/// <summary>What a tool window or CLI command gets from the loader.</summary>
public sealed class ModToolContext(ModManifest mod, GameInstall? game, ModSettings settings, Action<string> log, object? owner = null)
{
    public ModManifest Mod { get; } = mod;
    /// <summary>Null when the loader has no valid game folder.</summary>
    public GameInstall? Game { get; } = game;
    public ModSettings Settings { get; } = settings;
    public Action<string> Log { get; } = log;
    /// <summary>The loader's main window (a System.Windows.Forms.IWin32Window) in the GUI; null in the CLI.</summary>
    public object? Owner { get; } = owner;
}
