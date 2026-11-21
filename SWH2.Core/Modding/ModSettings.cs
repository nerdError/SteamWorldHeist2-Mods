using System.Globalization;

namespace SWH2.Modding;

/// <summary>
/// The values of one mod's settings, stored as invariant strings (as in profile.json and on the command line)
/// and validated against the manifest's <see cref="SettingDef"/>s.
/// </summary>
public sealed class ModSettings
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    readonly ModManifest _mod;
    readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public ModSettings(ModManifest mod, IReadOnlyDictionary<string, string>? values = null)
    {
        _mod = mod;
        foreach (var def in mod.Settings.Where(d => d.Type != SettingType.Header))
            _values[def.Id] = def.DefaultText;
        if (values != null)
            foreach (var (k, v) in values)
                if (Find(k) is { } def && Validate(def, v, out var value, out _)) _values[def.Id] = value; // unknown keys (renamed settings) and bad values are dropped
    }

    public IReadOnlyDictionary<string, string> Values => _values;

    SettingDef? Find(string id) => _mod.Settings.FirstOrDefault(d => d.Type != SettingType.Header && d.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    SettingDef Def(string id) => Find(id) ?? throw new KeyNotFoundException($"{_mod.Id}: no setting '{id}' in mod.json.");

    public string String(string id) => _values[Def(id).Id];
    public bool Bool(string id) => String(id) == "true";
    public int Int(string id) => int.Parse(String(id), Inv);
    public double Double(string id) => double.Parse(String(id), Inv);
    public float Float(string id) => (float)Double(id);

    /// <summary>True when the setting is editable: its <see cref="SettingDef.EnabledBy"/> chain is switched on.</summary>
    public bool IsActive(string id)
    {
        for (var def = Find(id); def?.EnabledBy is { } parent; def = Find(parent))
            if (!Bool(parent)) return false;
        return true;
    }

    /// <summary>
    /// Validates and stores a value. Numbers are clamped to min/max. Picking a preset option also sets the
    /// settings it lists; changing one of those by hand switches the preset to its "custom" option.
    /// </summary>
    public bool TrySet(string id, string value, out string? error)
    {
        var def = Find(id);
        if (def == null) { error = $"{_mod.Id} has no setting '{id}'. Known: {string.Join(", ", _values.Keys)}"; return false; }
        if (!Validate(def, value, out value, out error)) return false;
        _values[def.Id] = value;

        if (def.IsPreset)
        {
            foreach (var (k, v) in def.Choices!.First(c => c.Value == value).Sets ?? [])
                if (Find(k) is { } target && !target.IsPreset && Validate(target, SettingDef.JsonText(v), out var set, out _))
                    _values[target.Id] = set;
        }
        else
            foreach (var preset in _mod.Settings.Where(d => d.IsPreset))
            {
                var sets = preset.Choices!.First(c => c.Value == _values[preset.Id]).Sets;
                if (sets == null || !sets.TryGetValue(def.Id, out var wanted)) continue;
                if (Validate(def, SettingDef.JsonText(wanted), out var w, out _) && w == value) continue;
                if (preset.Choices!.FirstOrDefault(c => c.Sets == null) is { } custom) _values[preset.Id] = custom.Value;
            }
        return true;
    }

    static bool Validate(SettingDef def, string value, out string result, out string? error)
    {
        error = null;
        result = "";
        string id = def.Id;
        value = value.Trim();
        switch (def.Type)
        {
            case SettingType.Bool:
                value = value.ToLowerInvariant() switch
                {
                    "true" or "1" or "on" or "yes" => "true",
                    "false" or "0" or "off" or "no" => "false",
                    _ => "",
                };
                if (value == "") { error = $"{id}: expected on/off"; return false; }
                break;
            case SettingType.Int or SettingType.Float:
                if (!double.TryParse(value.Replace(',', '.'), NumberStyles.Float, Inv, out var d)) { error = $"{id}: expected a number"; return false; }
                if (def.Min is { } min) d = Math.Max(d, min);
                if (def.Max is { } max) d = Math.Min(d, max);
                value = def.Type == SettingType.Int ? ((int)Math.Round(d)).ToString(Inv) : d.ToString("0.###", Inv);
                break;
            case SettingType.Choice:
                var c = def.Choices?.FirstOrDefault(x => x.Value.Equals(value, StringComparison.OrdinalIgnoreCase));
                if (c == null) { error = $"{id}: expected one of {string.Join(", ", def.Choices?.Select(x => x.Value) ?? [])}"; return false; }
                value = c.Value;
                break;
        }
        result = value;
        return true;
    }

    public void Set(string id, string value)
    {
        if (!TrySet(id, value, out var error)) throw new ArgumentException(error);
    }

    /// <summary>Fills empty seeds with a random one; returns true if anything changed.</summary>
    public bool FillSeeds()
    {
        bool changed = false;
        foreach (var def in _mod.Settings.Where(d => d.Type == SettingType.Seed))
            if (string.IsNullOrWhiteSpace(_values[def.Id]))
            {
                _values[def.Id] = NewSeed();
                changed = true;
            }
        return changed;
    }

    public static string NewSeed() => Rng.Next(100000, 999999999).ToString(Inv);

    /// <summary>Settings that differ from the defaults, e.g. "seed=123, damage=20".</summary>
    public string Describe() => string.Join(", ", _mod.Settings
        .Where(d => d.Type != SettingType.Header && (d.Type == SettingType.Seed || _values[d.Id] != d.DefaultText) && IsActive(d.Id))
        .Select(d => $"{d.Id}={_values[d.Id]}"));
}
