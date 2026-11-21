using SWH2.Modding;

namespace SWH2.Mods.Challenges;

/// <summary>Challenge manager: independent rule changes for harder runs, each with its own switch.</summary>
public sealed class ChallengesMod : IModPlugin
{
    public void Apply(ModContext ctx)
    {
        var s = ctx.Settings;
        ctx.ReportName = "challenges";
        var on = new List<string>();

        bool replays = s.Bool("lockReplays"), respawns = s.Bool("noRespawns"), ships = s.Bool("noShipLoot");
        if (replays || respawns || ships)
        {
            NoGrind.Apply(ctx, replays, respawns, ships);
            if (replays) on.Add("no replays");
            if (respawns) on.Add("no respawning loot");
            if (ships) on.Add("no ship loot");
        }
        if (s.Bool("noCheckpoints"))
        {
            NoCheckpoints.Apply(ctx);
            on.Add("no checkpoints");
        }
        if (s.Bool("quitForfeits"))
        {
            QuitForfeits.Apply(ctx);
            on.Add("quitting forfeits");
        }

        if (on.Count == 0) ctx.Log("No challenge selected — nothing to do.");
        ctx.Summary = string.Join(", ", on);
    }
}
