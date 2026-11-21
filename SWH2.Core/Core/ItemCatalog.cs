using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SWH2.Core;

public sealed record ItemDef(string Name, string Kind, string File, bool Abstract, int? Tier, string? RarityType, string? NameId);

/// <summary>Every item definition in the game (weapons, utilities, ship equipment, hats, key and special items).</summary>
public sealed class ItemCatalog
{
    public static readonly string[] SourceFiles =
    [
        "Definitions/weapons.xml",
        "Definitions/utilities.xml",
        "Definitions/ship_equipment.xml",
        "Definitions/special_items.xml",
        "Definitions/key_items.xml",
        "Definitions/hats.xml",
    ];

    readonly Dictionary<string, ItemDef> _items = new(StringComparer.OrdinalIgnoreCase);
    readonly Localization _loc;

    public ItemCatalog(GameContent content, Localization loc)
    {
        _loc = loc;
        foreach (var file in SourceFiles)
        {
            var defs = content.Xml(file).Definitions().ToDictionary(e => e.Name(), StringComparer.OrdinalIgnoreCase);
            foreach (var (name, e) in defs)
            {
                var rarity = Inherited(e, defs, "Rarity");
                int? tier = int.TryParse(rarity?.Attribute("Tier")?.Value, out var t) ? t : null;
                var tierMatch = Regex.Match(name, @"_tier(\d+)$");
                if (tier == null && tierMatch.Success) tier = int.Parse(tierMatch.Groups[1].Value);
                _items[name] = new ItemDef(name, e.Name.LocalName, file, e.IsAbstract(), tier,
                    rarity?.Attribute("Type")?.Value, e.Element("LocalizedNameId")?.Value);
            }
        }
    }

    static XElement? Inherited(XElement e, Dictionary<string, XElement> defs, string child)
    {
        for (int depth = 0; e != null && depth < 16; depth++)
        {
            if (e.Element(child) is { } found) return found;
            var tpl = e.Attribute("Template")?.Value;
            if (tpl == null || !defs.TryGetValue(tpl, out e!)) return null;
        }
        return null;
    }

    public IReadOnlyCollection<ItemDef> All => _items.Values;
    public ItemDef? Get(string name) => _items.GetValueOrDefault(name);
    public bool Exists(string name) => _items.ContainsKey(name);
    public bool IsKeyItem(string name) => Get(name)?.Kind == "KeyItem";
    public static bool IsResource(string name) => name is "loot_money" or "loot_fragments";

    public string Display(string name)
    {
        var def = Get(name);
        var id = def?.NameId ?? (def?.Kind == "Weapon" ? "weapon_" + name : name);
        var text = _loc.Get(id) ?? _loc.Get(name);
        return text != null ? $"{text} [{name}]" : name;
    }
}
