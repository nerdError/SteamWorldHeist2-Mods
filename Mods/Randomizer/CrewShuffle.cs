using System.Xml.Linq;
using SWH2.Core;

namespace SWH2.Mods.Randomizer;

/// <summary>Who your crew are: starting jobs, personal abilities, and what a new game starts with.</summary>
public static class CrewShuffle
{
    const string PersonasFile = "Definitions/personas.xml";
    const string OutsetsFile = "Definitions/outsets.xml";
    static readonly string[] NewGameOutsets = ["new_game", "new_game_skip_tutorial"];

    static List<XElement> Crew(GameContent c) => c.Xml(PersonasFile).Definitions("Persona")
        .Where(p => !p.IsAbstract() && p.Element("DefaultWeapon") is { } w && w.Value.Trim().Length > 0)
        .ToList();

    /// <summary>
    /// Starting jobs. In this game the job is the weapon class, so each crew member's default weapon (personas.xml
    /// DefaultWeapon) decides the job they join with. Balanced: the default weapons swap, so every job is still there as
    /// often as before. Chaos: each crew member gets the starter weapon of a random job (some jobs may be missing until
    /// you can switch jobs). The skip-tutorial start follows (outsets.xml Crew/Jobs/Job/JobName).
    /// </summary>
    public static void ApplyJobs(RandomizerContext ctx)
    {
        var c = ctx.Content;
        var rng = ctx.RngFor("crewjobs");
        var jobs = SfxShuffle.JobsOfPlayerWeapons(c);
        var crew = Crew(c);
        var before = crew.ToDictionary(p => p.Name(), p => p.Element("DefaultWeapon")!.Value.Trim());

        ctx.Spoiler.Section($"Starting jobs ({ctx.Options.CrewJobMode})");
        if (ctx.Options.CrewJobMode == Mode.Balanced)
            Slots.Shuffle(ctx, rng, crew.Select(p => Slot.Of(p.Name(), "", p.Element("DefaultWeapon")!)), w => $"{jobs.GetValueOrDefault(w, "?")} ({w})");
        else
        {
            var starters = before.Values.Distinct().OrderBy(w => w, StringComparer.Ordinal).ToList();
            foreach (var p in crew)
            {
                var pick = rng.Pick(starters);
                if (pick == before[p.Name()]) continue;
                p.Element("DefaultWeapon")!.Value = pick;
                ctx.Spoiler.Line($"{p.Name()}: {jobs.GetValueOrDefault(pick, "?")} ({pick})    (was {jobs.GetValueOrDefault(before[p.Name()], "?")})");
            }
        }

        int outsetJobs = 0;
        var now = crew.ToDictionary(p => p.Name(), p => p.Element("DefaultWeapon")!.Value.Trim());
        foreach (var outset in c.Xml(OutsetsFile).Definitions("Outset").Where(o => NewGameOutsets.Contains(o.Name())))
            foreach (var member in outset.Element("Crew")?.Elements("Crew") ?? [])
                if (member.Element("CrewName")?.Value.Trim() is { } name && now.TryGetValue(name, out var weapon) && weapon != before[name])
                    foreach (var job in member.Element("Jobs")?.Elements("Job") ?? [])
                        if (job.Element("JobName") is { } jn && jn.Value.Trim() == jobs.GetValueOrDefault(before[name]) && jobs.TryGetValue(weapon, out var newJob))
                        {
                            jn.Value = newJob;
                            outsetJobs++;
                        }
        ctx.Log($"Starting jobs: {crew.Count(p => now[p.Name()] != before[p.Name()])} of {crew.Count} crew members changed ({outsetJobs} outset entries).");
    }

    /// <summary>
    /// Personal abilities (personas.xml PersonalUpgrade1/2, see job_upgrades.personal.xml), e.g. Daisy's stun gun.
    /// Balanced: the pairs move together (with their ReplacePersonalUpgrade flag, so an upgrade still upgrades its own
    /// ability). Chaos: first and second abilities shuffle on their own, and the second one never replaces the first.
    /// </summary>
    public static void ApplyPersonalAbilities(RandomizerContext ctx)
    {
        var rng = ctx.RngFor("personal");
        var crew = ctx.Content.Xml(PersonasFile).Definitions("Persona")
            .Where(p => !p.IsAbstract() && p.Element("PersonalUpgrade1") is { } u && u.Value.Trim().Length > 0 && p.Element("PersonalUpgrade2") != null)
            .ToList();
        string Show(string u) => ctx.Loc.Get("jobupgrade_" + u) is { } n ? $"{n} [{u}]" : u;

        ctx.Spoiler.Section($"Personal abilities ({ctx.Options.PersonalMode})");
        int n;
        if (ctx.Options.PersonalMode == Mode.Balanced)
            n = Slots.Shuffle(ctx, rng, crew.Select(p => new Slot(p.Name(), "",
                () => string.Join("|", p.Element("PersonalUpgrade1")!.Value.Trim(), p.Element("PersonalUpgrade2")!.Value.Trim(), p.Element("ReplacePersonalUpgrade")?.Value.Trim() ?? "false"),
                v =>
                {
                    var parts = v.Split('|');
                    p.Element("PersonalUpgrade1")!.Value = parts[0];
                    p.Element("PersonalUpgrade2")!.Value = parts[1];
                    if (p.Element("ReplacePersonalUpgrade") is { } r) r.Value = parts[2];
                    else p.Add(new XElement("ReplacePersonalUpgrade", parts[2]));
                })), v => string.Join(" + ", v.Split('|').Take(2).Select(Show)));
        else
        {
            n = Slots.Shuffle(ctx, rng, crew.Select(p => Slot.Of(p.Name() + " ability 1", "1", p.Element("PersonalUpgrade1")!)), Show)
              + Slots.Shuffle(ctx, rng, crew.Select(p => Slot.Of(p.Name() + " ability 2", "2", p.Element("PersonalUpgrade2")!)), Show);
            foreach (var r in crew.Select(p => p.Element("ReplacePersonalUpgrade")).Where(r => r != null && r.Value.Trim() != "false"))
                r!.Value = "false";
        }
        ctx.Log($"Personal abilities: {n} changed.");
    }

    /// <summary>
    /// A new game (both starts, outsets.xml new_game / new_game_skip_tutorial) begins with random money, fragments and
    /// a random extra weapon and utility. Balanced: up to 60 money, a tier 1-2 weapon and a common utility. Chaos: up to
    /// 400 money and 20 fragments, any weapon and utility. The story items of the start stay.
    /// </summary>
    public static void ApplyStart(RandomizerContext ctx)
    {
        var c = ctx.Content;
        var rng = ctx.RngFor("start");
        bool balanced = ctx.Options.StartMode == Mode.Balanced;
        var outsets = c.Xml(OutsetsFile).Definitions("Outset").ToDictionary(o => o.Name(), StringComparer.Ordinal);

        bool Fits(ItemDef i, string kind) => i.Kind == kind && !i.Abstract && i.Tier is { } t && i.RarityType is "common" or "rare"
            && (kind != "Weapon" || i.File == "Definitions/weapons.xml")
            && (!balanced || (kind == "Weapon" ? t is 1 or 2 : i.RarityType == "common" && t <= 2));
        var weapons = ctx.Items.All.Where(i => Fits(i, "Weapon")).Select(i => i.Name).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var utilities = ctx.Items.All.Where(i => Fits(i, "Utility")).Select(i => i.Name).OrderBy(x => x, StringComparer.Ordinal).ToList();

        int money = rng.Next(balanced ? 61 : 401);
        int fragments = balanced ? 0 : rng.Next(21);
        var weapon = weapons.Count > 0 ? rng.Pick(weapons) : null;
        var utility = utilities.Count > 0 ? rng.Pick(utilities) : null;

        ctx.Spoiler.Section($"New game start ({ctx.Options.StartMode})");
        ctx.Spoiler.Line($"+{money} money, +{fragments} fragments, {(weapon != null ? ctx.Items.Display(weapon) : "-")}, {(utility != null ? ctx.Items.Display(utility) : "-")}");
        foreach (var name in NewGameOutsets)
        {
            if (!outsets.TryGetValue(name, out var o)) continue;
            var res = o.Element("Resources");
            if (res == null) o.Add(res = new XElement("Resources"));
            void Add(string tag, int amount)
            {
                var e = res.Element(tag);
                int old = int.TryParse(e?.Value.Trim(), out var v) ? v : 0;
                if (e != null) e.Value = (old + amount).ToString();
                else res.Add(new XElement(tag, amount));
            }
            Add("Money", money);
            Add("Fragments", fragments);

            // A new list keeps the template's items (the story's communicators) with Inherit, like outsets' Ship lists.
            var items = o.Element("Items");
            if (items == null) o.Add(items = new XElement("Items", new XAttribute("Inherit", "true")));
            if (weapon != null) items.Add(new XElement("Item", weapon));
            if (utility != null) items.Add(new XElement("Item", utility));
        }
        ctx.Log($"New game start: +{money} money, +{fragments} fragments, {weapon}, {utility}.");
    }
}
