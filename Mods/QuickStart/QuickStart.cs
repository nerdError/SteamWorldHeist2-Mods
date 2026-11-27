using SWH2.Core;
using SWH2.Modding;

namespace SWH2.Mods.QuickStart;

/// <summary>
/// Faster start (docs/EXE_NOTES.md, "Быстрый старт"):
/// - Logo movie: LogosSystemState::Update (0x279840) plays the movies of a 1-entry table (Movies/logo_thunderful) and,
///   once the index reaches the count, creates the MainMenuState (0x2799c3). The count compare <c>cmp rax, 1</c> becomes
///   <c>cmp rax, 0</c>: the table is done before it starts.
/// Loading the last save at start is not done yet: see docs/EXE_NOTES.md, "Быстрый старт" (MainMenuState method 8
/// runs once on entering the menu; calling the load there hangs on a black screen).
/// </summary>
public sealed class QuickStartMod : IModPlugin
{
    const uint LogoCountRva = 0x279894;           // 48 83 F8 01  cmp rax, 1

    public void Apply(ModContext ctx)
    {
        var exe = ctx.Exe;
        var done = new List<string>();
        if (ctx.Settings.Bool("skipIntro"))
        {
            exe.Patch(exe.Map(LogoCountRva), [0x48, 0x83, 0xF8, 0x01], [0x48, 0x83, 0xF8, 0x00]);
            done.Add("no logo movie");
        }
        ctx.Summary = done.Count > 0 ? string.Join(", ", done) : "nothing selected";
        ctx.Log($"QuickStart: {ctx.Summary}.");
    }
}
