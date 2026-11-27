using SWH2.Modding;

namespace SWH2.Mods.Challenges;

/// <summary>
/// "No checkpoints": no "Load Checkpoint" in missions (pause menu and the objective-failed popup).
/// Can't be done in XML — SaveRestorePoints=0 crashes the game, 1 still allows going back a turn
/// (docs/EXE_NOTES.md, "Точки отката") — so bool HasRestorePoint() is patched to always return false.
/// Its only callers are the restore itself, the pause-menu button and the fail popup.
/// </summary>
public static class NoCheckpoints
{
    const uint HasRestorePoint = 0xC0F80;
    static readonly byte[] Original = Hex.Parse("4883B9B000000000"); // cmp qword ptr [rcx+0xb0], 0
    static readonly byte[] ReturnFalse = Hex.Parse("30C0C3");        // xor al, al; ret

    public static void Apply(ModContext ctx)
    {
        ctx.Exe.Patch(ctx.Exe.Map(HasRestorePoint), Original, ReturnFalse);
        ctx.Report.Section("No checkpoints");
        ctx.Report.Line("Load Checkpoint is disabled in missions.");
        ctx.Log("No checkpoints: HasRestorePoint() patched to false.");
    }
}
