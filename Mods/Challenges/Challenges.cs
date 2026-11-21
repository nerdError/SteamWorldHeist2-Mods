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

        if (s.Bool("crewOneHp")) { Mutators.CrewOneHp(ctx); on.Add("crew 1 HP"); }
        if (s.Bool("enemiesOneHp")) { Mutators.EnemiesOneHp(ctx); on.Add("enemies 1 HP"); }
        if (s.Bool("explosiveShots")) { Mutators.ExplosiveShots(ctx, s.Int("explosionRadius")); on.Add("explosive shots"); }
        if (s.Bool("deathBlasts")) { Mutators.DeathBlasts(ctx, s.Int("deathBlastDamage")); on.Add("death blasts"); }

        bool quit = s.Bool("quitForfeits"), permadeath = s.Bool("permadeath"), oneLife = s.Bool("oneLife");
        if (quit || permadeath || oneLife)
        {
            // Procedural combat missions have no script; give them a shared one so the hooks run there too.
            var missions = new MissionScripts(ctx, addGeneric: true);
            if (quit) { QuitForfeits.Apply(ctx, missions); on.Add("quitting forfeits"); }
            if (permadeath || oneLife) Hardcore.Apply(ctx, missions, permadeath, oneLife);
            if (permadeath) on.Add("permadeath");
            if (oneLife) on.Add("one life");
        }

        if (on.Count == 0) ctx.Log("No challenge selected — nothing to do.");
        ctx.Summary = string.Join(", ", on);
    }
}
