using System.Text.Json;
using System.Text.Json.Serialization;

namespace SWH2.Modding;

/// <summary>
/// Mods\&lt;Id&gt;\mod.json. A mod is any combination of
///  - loose files: <see cref="Files"/> folder mirroring the in-pak layout (e.g. files\Definitions\weapons.xml);
///  - a plugin: <see cref="Plugin"/> dll with an <see cref="IModPlugin"/> that edits content / patches the exe in code.
/// A folder without mod.json is treated as a legacy loose-file mod (the whole folder is the pak layout).
/// </summary>
public sealed class ModManifest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string? Author { get; set; }
    public string? Description { get; set; }
    public string? Url { get; set; }

    /// <summary>Plugin assembly file name, relative to the mod folder.</summary>
    public string? Plugin { get; set; }
    /// <summary>Folder (relative to the mod folder) with loose files in pak layout; applied before the plugin.</summary>
    public string Files { get; set; } = "files";

    /// <summary>Mods run in ascending order (then by id). Data/file mods ~0, generators ~100, rule changes ~200.</summary>
    public int Order { get; set; }
    public string? MinLoaderVersion { get; set; }
    /// <summary>True if the mod patches the game exe (shown in the UI; exe mods need the supported game build).</summary>
    public bool PatchesExe { get; set; }
    /// <summary>Button text for the mod's tool window in the loader (plugin implements <see cref="IModTool"/>).</summary>
    public string? Tool { get; set; }
    /// <summary>Command name for <c>swh2 &lt;command&gt; ...</c> (plugin implements <see cref="IModCommand"/>).</summary>
    public string? Command { get; set; }
    /// <summary>Usage line(s) for the command, shown by <c>swh2 help</c>.</summary>
    public string? CommandHelp { get; set; }
    /// <summary>Ids of mods that can't be enabled together with this one.</summary>
    public List<string> Conflicts { get; set; } = [];
    public List<SettingDef> Settings { get; set; } = [];

    [JsonIgnore] public string Dir { get; set; } = "";
    [JsonIgnore] public bool IsLegacy { get; set; }
    [JsonIgnore] public string Title => $"{(string.IsNullOrWhiteSpace(Name) ? Id : Name)} {Version}".TrimEnd();

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}

public enum SettingType
{
    Bool,
    Int,
    Float,
    /// <summary>One of <see cref="SettingDef.Choices"/>.</summary>
    Choice,
    String,
    /// <summary>A string with a "New seed" button; an empty seed is generated at install time.</summary>
    Seed,
    /// <summary>Not a value: a heading that groups the settings below it.</summary>
    Header,
}

public sealed class SettingDef
{
    public string Id { get; set; } = "";
    public SettingType Type { get; set; }
    public string? Label { get; set; }
    public string? Help { get; set; }
    public JsonElement? Default { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? Step { get; set; }
    public List<ChoiceDef>? Choices { get; set; }
    /// <summary>Id of a bool setting that must be on for this one to be editable.</summary>
    public string? EnabledBy { get; set; }

    [JsonIgnore] public string DisplayLabel => Label ?? Id;

    public string DefaultText => Default is not { } d ? Type switch
    {
        SettingType.Bool => "false",
        SettingType.Int or SettingType.Float => (Min ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
        SettingType.Choice => Choices?.FirstOrDefault()?.Value ?? "",
        _ => "",
    } : JsonText(d);

    /// <summary>True for a preset: a choice whose options set other settings (<see cref="ChoiceDef.Sets"/>).</summary>
    [JsonIgnore] public bool IsPreset => Type == SettingType.Choice && Choices?.Any(c => c.Sets != null) == true;

    public static string JsonText(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.String => e.GetString() ?? "",
        _ => e.GetRawText(),
    };
}

public sealed class ChoiceDef
{
    public string Value { get; set; } = "";
    public string? Label { get; set; }
    /// <summary>
    /// Preset: picking this option sets these other settings (id → value). Changing one of them by hand
    /// afterwards switches the preset to its first option without <c>sets</c> (e.g. "Custom").
    /// </summary>
    public Dictionary<string, JsonElement>? Sets { get; set; }
    public override string ToString() => Label ?? Value;
}
