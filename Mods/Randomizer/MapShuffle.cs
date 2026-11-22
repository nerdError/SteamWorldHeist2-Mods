using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SWH2.Core;

namespace SWH2.Mods.Randomizer;

/// <summary>
/// The world map (WorldMap/Maps/WorldMap.tmx): whirlpool destinations, loot spots, pre-placed enemy ships and
/// turrets, hazards (spike rocks and mines), and the roaming spawns (worldmap_spawns.xml).
///
/// Map objects are Tiled tile objects: the kind is the tile (gid → Entities.tsx type and properties), layers are the
/// top-level groups "0".."9" (0 = the surface, higher = deeper). Only attribute values change (gid, width, height,
/// x, y, the Destination property), so the 8 MB map is patched in place. Only objects in "Entities" layers are
/// touched: the others (armada, kraken debris…) are switched on and off by the story.
/// </summary>
public static class MapShuffle
{
    const string MapFile = "WorldMap/Maps/WorldMap.tmx";
    const string TilesetFile = "WorldMap/Tiles/Entities.tsx";
    const string SpawnsFile = "Definitions/worldmap_spawns.xml";
    const uint FlipBits = 0xF0000000;
    static readonly string[] Seas = ["west_caribbea", "east_caribbea", "arctica", "atomic"];
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Enemies that never move, mines, and what stays (the snake is a set piece).</summary>
    static readonly Regex Stationary = new("launcher|cannon", RegexOptions.IgnoreCase);
    static readonly Regex Mine = new("_mine$", RegexOptions.IgnoreCase);
    static readonly Regex KeptEnemy = new("centipede|boss|kraken|guard_intro", RegexOptions.IgnoreCase);

    sealed class MapObject(XElement e, string layer, bool entities)
    {
        public XElement E { get; } = e;
        public string Layer { get; } = layer;
        public bool InEntities { get; } = entities;
        public string Id => E.Attribute("id")?.Value ?? "";
        public string? Name => E.Attribute("name")?.Value;
        public double X => double.Parse(E.Attribute("x")!.Value, Inv);
        public double Y => double.Parse(E.Attribute("y")!.Value, Inv);
        public uint Gid => uint.TryParse(E.Attribute("gid")?.Value, out var g) ? g : 0;
        public string? Type { get; set; }
        public string? Enemy { get; set; }
        public string? Region { get; set; }
        public string Sea => Region == null ? "?" : Seas.FirstOrDefault(s => Region.StartsWith(s, StringComparison.Ordinal)) ?? "?";
        public XAttribute? Property(string name) =>
            E.Element("properties")?.Elements("property").FirstOrDefault(p => p.Attribute("name")?.Value == name)?.Attribute("value");
    }

    sealed class Map
    {
        public readonly List<MapObject> Objects = [];
        public readonly Dictionary<string, MapObject> ById = new(StringComparer.Ordinal);
        /// <summary>When the story first reaches each region (depth in the outsets.xml timeline); unknown = last.</summary>
        public readonly Dictionary<string, int> RegionDepth = new(StringComparer.Ordinal);
        public int Depth(string? region) => region != null && RegionDepth.TryGetValue(region, out var d) ? d : int.MaxValue / 2;
        /// <summary>Strings in the Lua scripts: objects they look up by name stay.</summary>
        public HashSet<string> LuaNames = new(StringComparer.Ordinal);
        public Dictionary<uint, (string Type, string? Enemy, string Width, string Height)> Tiles = [];
    }

    static Map Load(RandomizerContext ctx)
    {
        var c = ctx.Content;
        var doc = c.Xml(MapFile).Root;
        var map = new Map();

        // Tile kinds: gid = firstgid of Entities.tsx + tile id.
        uint first = doc.Elements("tileset").Where(t => (t.Attribute("source")?.Value ?? "").EndsWith("Entities.tsx", StringComparison.Ordinal))
            .Select(t => uint.Parse(t.Attribute("firstgid")!.Value)).FirstOrDefault();
        foreach (var t in XDocument.Parse(c.ReadText(TilesetFile)).Root!.Elements("tile"))
        {
            if (t.Attribute("type")?.Value is not { } type) continue;
            var enemy = t.Element("properties")?.Elements("property").FirstOrDefault(p => p.Attribute("name")?.Value == "EnemyDefinition")?.Attribute("value")?.Value;
            var img = t.Element("image");
            map.Tiles[first + uint.Parse(t.Attribute("id")!.Value)] = (type, enemy, img?.Attribute("width")?.Value ?? "", img?.Attribute("height")?.Value ?? "");
        }

        void Walk(XElement e, string? layer, bool entities)
        {
            foreach (var child in e.Elements())
            {
                var name = child.Attribute("name")?.Value ?? "";
                if (child.Name == "group") Walk(child, layer ?? name, entities);
                else if (child.Name == "objectgroup") Walk(child, layer ?? name, name == "Entities");
                else if (child.Name == "object")
                {
                    var o = new MapObject(child, layer ?? "", entities);
                    if (o.Gid != 0 && map.Tiles.TryGetValue(o.Gid & ~FlipBits, out var tile)) { o.Type = tile.Type; o.Enemy = tile.Enemy; }
                    else o.Type = child.Attribute("type")?.Value;
                    if (o.Property("EnemyDefinition")?.Value is { Length: > 0 } own) o.Enemy = own;
                    map.Objects.Add(o);
                    map.ById[o.Id] = o;
                }
            }
        }
        Walk(doc, null, false);

        // Regions: polygons per layer.
        var polygons = new List<(string Region, string Layer, List<(double X, double Y)> Points)>();
        foreach (var o in map.Objects.Where(o => o.Type == "Region"))
            if (o.Property("WorldMapRegion")?.Value is { Length: > 0 } region && o.E.Element("polygon")?.Attribute("points")?.Value is { } pts)
                polygons.Add((region, o.Layer, pts.Split(' ').Select(p => p.Split(','))
                    .Select(p => (o.X + double.Parse(p[0], Inv), o.Y + double.Parse(p[1], Inv))).ToList()));
        foreach (var o in map.Objects.Where(o => o.Type != "Region" && o.E.Attribute("x") != null))
            o.Region = polygons.FirstOrDefault(p => p.Layer == o.Layer && Inside(p.Points, o.X, o.Y)).Region;

        // Region timeline: the debug start points name the regions visited so far (VisitedRegions, MissionRegion…).
        var outsets = c.Xml("Definitions/outsets.xml").Definitions("Outset").ToDictionary(o => o.Name(), StringComparer.Ordinal);
        foreach (var (name, o) in outsets)
        {
            if (name.StartsWith("test", StringComparison.OrdinalIgnoreCase) || name.Contains("debug")) continue;
            int depth = 0;
            for (var t = o.Attribute("Template")?.Value; t != null && depth < 64 && outsets.TryGetValue(t, out var parent); t = parent.Attribute("Template")?.Value)
                depth++;
            foreach (var r in o.Descendants().Where(e => e.Name.LocalName is "Region" or "MissionRegion" or "BountyRegion").Select(e => e.Value.Trim()).Where(r => r.Length > 0))
                if (!map.RegionDepth.TryGetValue(r, out var d) || depth < d) map.RegionDepth[r] = depth;
        }

        foreach (var lua in c.OriginalFiles.Where(f => f.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)))
            foreach (Match m in Regex.Matches(c.ReadText(lua), "\"([A-Za-z0-9_]+)\""))
                map.LuaNames.Add(m.Groups[1].Value);
        return map;
    }

    static bool Inside(List<(double X, double Y)> poly, double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            if ((poly[i].Y > y) != (poly[j].Y > y) &&
                x < (poly[j].X - poly[i].X) * (y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                inside = !inside;
        return inside;
    }

    static string Where(MapObject o) => $"#{o.Id} ({o.Region ?? "no region"}, layer {o.Layer})";

    /// <summary>
    /// Changes what an object is (its tile) and keeps where it is, how it's flipped and how much it was scaled
    /// (object size / tile image size).
    /// </summary>
    static void SetTile(Map map, MapObject o, uint tile)
    {
        var old = map.Tiles[o.Gid & ~FlipBits];
        var now = map.Tiles[tile];
        o.E.SetAttributeValue("gid", ((o.Gid & FlipBits) | tile).ToString(Inv));
        void Size(string attr, string oldImage, string newImage)
        {
            if (!double.TryParse(newImage, NumberStyles.Float, Inv, out var image)) return;
            double scale = double.TryParse(o.E.Attribute(attr)?.Value, NumberStyles.Float, Inv, out var size) &&
                           double.TryParse(oldImage, NumberStyles.Float, Inv, out var before) && before > 0 ? size / before : 1;
            o.E.SetAttributeValue(attr, Math.Round(image * scale, 2).ToString("0.##", Inv));
        }
        Size("width", old.Width, now.Width);
        Size("height", old.Height, now.Height);
    }

    // ---- whirlpools ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Whirlpools (wm_whirlpool*, property Destination = the id of a wm_whirlpool_destination) lead elsewhere. Safe
    /// for the story: a whirlpool only ever leads to a region the story reaches no later than the whirlpool itself or
    /// its own vanilla destination, and only on the same layer, so it never jumps you past a blockade or into the deep
    /// without diving gear. Whirlpools between seas or layers (the way to Atomic City) and the story's armada stay.
    /// Balanced: inside the same sea. Chaos: across seas.
    /// </summary>
    public static void ApplyWhirlpools(RandomizerContext ctx)
    {
        var map = Load(ctx);
        var rng = ctx.RngFor("whirlpools");
        bool balanced = ctx.Options.WhirlpoolMode == Mode.Balanced;
        var pools = map.Objects
            .Where(o => o.InEntities && o.Type is { } t && t.StartsWith("wm_whirlpool", StringComparison.Ordinal) && !t.Contains("destination"))
            .Select(o => (Pool: o, Dest: o.Property("Destination") is { } d && map.ById.TryGetValue(d.Value, out var dest) ? dest : null))
            .Where(x => x.Dest != null && x.Dest.InEntities && x.Dest.Layer == x.Pool.Layer && x.Dest.Region != null && x.Pool.Region != null &&
                        (x.Dest.Sea == x.Pool.Sea))
            .Select(x => (x.Pool, Dest: x.Dest!))
            .ToList();

        ctx.Spoiler.Section($"Whirlpools ({ctx.Options.WhirlpoolMode})");
        int changed = 0;
        foreach (var group in pools.GroupBy(x => x.Pool.Layer + (balanced ? " " + x.Pool.Sea : "")))
        {
            // Greedy by limit: each whirlpool's own destination is allowed, so this always finds an assignment.
            var list = group.Select(x => (x.Pool, x.Dest, Limit: Math.Max(map.Depth(x.Pool.Region), map.Depth(x.Dest.Region)))).ToList();
            rng.Shuffle(list);
            list = list.OrderBy(x => x.Limit).ToList();
            var free = list.Select(x => x.Dest).Distinct().ToList();
            var used = new Dictionary<MapObject, int>();
            foreach (var x in list) used[x.Dest] = used.GetValueOrDefault(x.Dest) + 1;
            foreach (var x in list)
            {
                var options = free.Where(d => used.GetValueOrDefault(d) > 0 && map.Depth(d.Region) <= x.Limit).ToList();
                var pick = options.Count > 0 ? rng.Pick(options) : x.Dest;
                used[pick]--;
                if (pick == x.Dest) continue;
                x.Pool.Property("Destination")!.Value = pick.Id;
                ctx.Spoiler.Line($"Whirlpool {Where(x.Pool)} now leads to {Where(pick)}    (was {Where(x.Dest)})");
                changed++;
            }
        }
        ctx.Log($"Whirlpools: {changed} of {pools.Count} lead elsewhere.");
    }

    // ---- loot spots ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Floating loot on the world map (wm_loot money, wm_loot_big chests, wm_loot_fragments) swaps places: every spot
    /// still holds something, but a big chest may wait where a few coins were. The glow rods (story) stay. Each
    /// object keeps its id (saves remember picked-up loot by it) and contents. Balanced: inside the same region and
    /// layer. Chaos: anywhere on the same layer.
    /// </summary>
    public static void ApplyChests(RandomizerContext ctx)
    {
        var map = Load(ctx);
        var rng = ctx.RngFor("mapchests");
        bool balanced = ctx.Options.ChestMode == Mode.Balanced;
        var spots = map.Objects
            .Where(o => o.InEntities && o.Type is "wm_loot" or "wm_loot_big" or "wm_loot_fragments" && (o.Name == null || !map.LuaNames.Contains(o.Name)))
            .ToList();
        ctx.Spoiler.Section($"World map loot ({ctx.Options.ChestMode})");
        int n = Slots.Shuffle(ctx, rng, spots.Select(o => new Slot($"{o.Type} #{o.Id}", o.Layer + (balanced ? " " + (o.Region ?? "?") : ""),
            () => $"{o.E.Attribute("x")!.Value}|{o.E.Attribute("y")!.Value}|{o.Region ?? "no region"}",
            v =>
            {
                var p = v.Split('|');
                o.E.SetAttributeValue("x", p[0]);
                o.E.SetAttributeValue("y", p[1]);
            })), v => v.Split('|') is var p ? $"{p[2]} at {p[0]},{p[1]}" : v);
        ctx.Log($"World map loot: {n} of {spots.Count} loot spots moved.");
    }

    // ---- enemies on the map ----------------------------------------------------------------------------------------

    /// <summary>
    /// Patrols and ambushes. Pre-placed enemy ships and turrets (wm_enemyspawn tiles) become other enemies of their
    /// kind — ships with ships (on the surface or below), turrets with turrets; mines are hazards (see
    /// <see cref="ApplyHazards"/>). Roaming spawns (worldmap_spawns.xml SpawnEntry) bring other ships. Story ships,
    /// the snake, bosses and objects that scripts look up by name stay. An enemy only moves to a region the story
    /// reaches no earlier than its own, so late-game ships never wait at the start. Balanced: inside the sea.
    /// Chaos: across seas.
    /// </summary>
    public static void ApplyPatrols(RandomizerContext ctx)
    {
        var map = Load(ctx);
        var rng = ctx.RngFor("mappatrols");
        bool balanced = ctx.Options.PatrolMode == Mode.Balanced;
        var placed = map.Objects
            .Where(o => o.InEntities && o.Type == "wm_enemyspawn" && o.Enemy != null && o.Property("EnemyDefinition") == null &&
                        !Mine.IsMatch(o.Enemy) && !KeptEnemy.IsMatch(o.Enemy) && (o.Name == null || !map.LuaNames.Contains(o.Name)))
            .ToList();

        ctx.Spoiler.Section($"World map enemies ({ctx.Options.PatrolMode})");
        int n = Slots.ShuffleNoLater(ctx, rng, placed.Select(o => (new Slot($"{Where(o)}", string.Join(" ", o.Layer, Stationary.IsMatch(o.Enemy!) ? "turret" : "ship", balanced ? o.Sea : ""),
            () => (o.Gid & ~FlipBits).ToString(Inv), v => SetTile(map, o, uint.Parse(v, Inv))), map.Depth(o.Region))),
            v => map.Tiles.TryGetValue(uint.Parse(v, Inv), out var t) ? t.Enemy ?? v : v);

        // Roaming spawns: enemy entries swap between spawn tables of the same kind (surface / underwater / Atomic City).
        static string Medium(string spawn) => spawn.StartsWith("atomic", StringComparison.Ordinal) ? "atomic" : spawn.Contains("underwater") ? "underwater" : "surface";
        string SeaOf(string spawn) => Seas.FirstOrDefault(s => spawn.StartsWith(s, StringComparison.Ordinal)) ?? "?";
        // Spawn tables are named after their region (east_caribbea_a_underwater_north): the longest region name that fits.
        int RegionDepthOf(string spawn) => map.Depth(map.RegionDepth.Keys.Where(r => spawn.StartsWith(r, StringComparison.Ordinal))
            .OrderByDescending(r => r.Length).FirstOrDefault());
        var entries = ctx.Content.Xml(SpawnsFile).Definitions("Spawn")
            .Where(s => !s.IsAbstract())
            .SelectMany(s => (s.Element("SpawnEntries")?.Elements("SpawnEntry") ?? []).Select(e => (Spawn: s.Name(), Name: e.Attribute("Name"))))
            .Where(x => x.Name?.Value is { } v && v.StartsWith("wm_enemy_", StringComparison.Ordinal) && !KeptEnemy.IsMatch(v) && !Mine.IsMatch(v))
            .Select(x => (Slot.Of($"roaming {x.Spawn}", Medium(x.Spawn) + (balanced ? " " + SeaOf(x.Spawn) : ""), x.Name!), RegionDepthOf(x.Spawn)))
            .ToList();
        int roaming = Slots.ShuffleNoLater(ctx, rng, entries);
        ctx.Log($"World map enemies: {n} of {placed.Count} placed enemies and {roaming} of {entries.Count} roaming spawn entries changed.");
    }

    // ---- hazards ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Hazards: spike rocks (wm_spike_rocks) and sea mines (wm_enemyspawn with a *_mine enemy) trade places, so mine
    /// fields and rock gardens move around. Geiger zones and rust pools (story) stay. Balanced: a permutation inside each
    /// region and layer (as many rocks and mines as before). Chaos: on layers with mines, every spot is a rock or a
    /// mine at random.
    /// </summary>
    public static void ApplyHazards(RandomizerContext ctx)
    {
        var map = Load(ctx);
        var rng = ctx.RngFor("maphazards");
        bool balanced = ctx.Options.HazardMode == Mode.Balanced;
        var rock = map.Tiles.Where(t => t.Value.Type == "wm_spike_rocks").Select(t => t.Key).FirstOrDefault();
        var spots = map.Objects
            .Where(o => o.InEntities && o.Property("EnemyDefinition") == null && (o.Name == null || !map.LuaNames.Contains(o.Name)) &&
                        (o.Type == "wm_spike_rocks" || (o.Type == "wm_enemyspawn" && o.Enemy != null && Mine.IsMatch(o.Enemy))))
            .ToList();
        string Show(uint tile) => map.Tiles.TryGetValue(tile, out var t) ? t.Enemy ?? t.Type : tile.ToString(Inv);

        ctx.Spoiler.Section($"World map hazards ({ctx.Options.HazardMode})");
        int n;
        if (balanced)
            n = Slots.Shuffle(ctx, rng, spots.Select(o => new Slot(Where(o), o.Layer + " " + (o.Region ?? "?"),
                () => (o.Gid & ~FlipBits).ToString(Inv), v => SetTile(map, o, uint.Parse(v, Inv)))), v => Show(uint.Parse(v, Inv)));
        else
        {
            n = 0;
            // Mines only where the game has them (they float on the surface); deeper layers keep their rocks.
            var mineOf = spots.Where(o => o.Type == "wm_enemyspawn").GroupBy(o => o.Layer)
                .ToDictionary(g => g.Key, g => g.Select(o => o.Gid & ~FlipBits).OrderBy(x => x).First(), StringComparer.Ordinal);
            foreach (var o in spots)
            {
                if (!mineOf.TryGetValue(o.Layer, out var mine)) continue;
                var pick = rng.Next(2) == 0 ? rock : mine;
                var was = o.Gid & ~FlipBits;
                if (pick == 0 || pick == was) continue;
                SetTile(map, o, pick);
                ctx.Spoiler.Line($"{Where(o)}: {Show(pick)}    (was {Show(was)})");
                n++;
            }
        }
        ctx.Log($"World map hazards: {n} of {spots.Count} rocks and mines changed.");
    }
}
