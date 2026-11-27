using System.Globalization;
using System.Security;
using SWH2.Core;

namespace SWH2.Modding;

/// <summary>
/// Settings mods add to the game's own Options menu (category "Game", after the game's items): sliders and checkboxes
/// bound to new keys in Definitions/settings.xml. The game saves them with its other settings
/// (%APPDATA%\Thunderful\SteamWorld Heist II\settings.xml), so a mod's code reads them in the game — see
/// <see cref="EmitReadFloat"/> — and the loader's value is only the starting value. docs/EXE_NOTES.md, "Меню настроек".
///
/// How the menu is built (Steam RVAs): OptionsMenu::BuildCategory(menu, category) <see cref="BuildRva"/> switches on the
/// category (0 = Game, 1 = Audio, 2 = Graphics, 3 = Keys, 4 = Language) and adds items with
/// AddSlider(menu, group, &amp;key, &amp;label, &amp;hint, optional&lt;float&gt; step) <see cref="AddSliderRva"/> and
/// AddCheckbox(menu, group, &amp;key, &amp;label, &amp;hint) <see cref="AddCheckboxRva"/> (string_views; label and hint
/// are text ids). A slider's value is 0..1. Two hooks: the category switch remembers "Game", the common exit adds our items.
/// </summary>
public sealed class GameOptions
{
    public const uint BuildRva = 0x58FAE0;
    const uint DispatchRva = 0x58FB69;   // test edi, edi; je game  (8 bytes)
    const uint GameRva = 0x5904A4, NextCategoryRva = 0x58FB71;
    const uint ExitRva = 0x590E63;       // lea rcx, [rbp-0x30]; call dtor  (9 bytes), then 0x590e6c
    const uint ExitDtorRva = 0x97AF0, ExitContinueRva = 0x590E6C;
    public const uint AddSliderRva = 0x592720, AddCheckboxRva = 0x5913F0;
    /// <summary>Settings::GetFloat(settings, &amp;string_view key) → xmm0; the settings object is the pointer at <see cref="SettingsPtrRva"/>.</summary>
    public const uint GetFloatRva = 0xEFAA0, SettingsPtrRva = 0xE9FBF0;

    sealed record Item(string Mod, string Key, bool Slider, float Default, float Step, string Label, string? Hint, string? LabelRu, string? HintRu);
    readonly List<Item> _items = [];

    public bool IsEmpty => _items.Count == 0;
    internal string CurrentMod { get; set; } = "";

    /// <summary>Setting key for a mod's option: Mod.&lt;ModId&gt;.&lt;name&gt;.</summary>
    public string KeyFor(string name) => $"Mod.{CurrentMod}.{name}";

    /// <summary>
    /// A slider (value 0..1, <paramref name="defaultValue"/> too) in Options → Game. <paramref name="step"/> &gt; 0 makes it
    /// snap. Texts: English, optional Russian. Returns the setting key.
    /// </summary>
    public string Slider(string name, float defaultValue, float step, string label, string? hint = null, string? labelRu = null, string? hintRu = null)
    {
        if (defaultValue is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(defaultValue), "Slider values are 0..1.");
        return Add(new(CurrentMod, KeyFor(name), true, defaultValue, step, label, hint, labelRu, hintRu));
    }

    /// <summary>A checkbox in Options → Game. Returns the setting key (a bool setting).</summary>
    public string Checkbox(string name, bool defaultValue, string label, string? hint = null, string? labelRu = null, string? hintRu = null) =>
        Add(new(CurrentMod, KeyFor(name), false, defaultValue ? 1 : 0, 0, label, hint, labelRu, hintRu));

    string Add(Item item)
    {
        if (_items.Any(i => i.Key == item.Key)) throw new InvalidOperationException($"Game option {item.Key} is added twice.");
        _items.Add(item);
        return item.Key;
    }

    /// <summary>
    /// Code that loads a setting's float into xmm0 (calls Settings::GetFloat). Clobbers the volatile registers
    /// (rax rcx rdx r8–r11, xmm0–xmm5): save them around it. Needs rsp 16-byte aligned with 0x30 bytes free at [rsp]
    /// (shadow space + the key's string_view at [rsp+0x20]); <paramref name="keyLabel"/> = the key's text in the blob.
    /// </summary>
    public static void EmitReadFloat(X64 a, ExeImage exe, string keyLabel, string key)
    {
        a.LeaRax(keyLabel).MovRspRax(0x20).MovRspImm(0x28, System.Text.Encoding.UTF8.GetByteCount(key))
         .MovRcxRipQword(exe.Map(SettingsPtrRva)).LeaRsp(X64.Reg.Rdx, 0x20).Call(exe.Map(GetFloatRva));
    }

    /// <summary>Called by the pipeline after every mod ran: settings, texts and the menu hooks.</summary>
    internal void Install(GameContent content, Func<ExeImage> getExe, Action<string> log)
    {
        if (IsEmpty) return;
        AddSettings(content);
        AddTexts(content);
        PatchMenu(getExe());
        log($"Game options: {_items.Count} item(s) in Options → Game ({string.Join(", ", _items.Select(i => i.Key))}).");
    }

    static string LabelId(Item i) => "ui_option_" + i.Key.Replace('.', '_').ToLowerInvariant();

    void AddSettings(GameContent content)
    {
        const string file = "Definitions/settings.xml";
        var xml = content.ReadText(file);
        int end = xml.LastIndexOf("</Settings>", StringComparison.Ordinal);
        if (end < 0) throw new InvalidOperationException($"{file}: </Settings> not found.");
        var lines = string.Concat(_items.Select(i =>
            $"\t<Setting Key=\"{i.Key}\" DefaultValue=\"{(i.Slider ? i.Default.ToString("0.######", CultureInfo.InvariantCulture) : i.Default != 0 ? "true" : "false")}\" " +
            $"Type=\"{(i.Slider ? "float" : "bool")}\" Persistence=\"persistent\" Visibility=\"public\" Description=\"{SecurityElement.Escape(i.Label)} (mod {i.Mod})\"/>\r\n"));
        content.WriteText(file, xml.Insert(end, "\t<!-- Mod options (SWH2 mod loader) -->\r\n" + lines), bom: content.ReadOriginal(file) is [0xEF, 0xBB, 0xBF, ..]);
    }

    /// <summary>Language/&lt;lang&gt;_modoptions.csv for every game language (the game loads all &lt;lang&gt;_*.csv): Russian texts in ru, English elsewhere.</summary>
    void AddTexts(GameContent content)
    {
        static string Clean(string s) => s.Replace('\t', ' ').Replace("\r", "").Replace('\n', ' ');
        foreach (var lang in content.OriginalFiles.Where(f => f.StartsWith("Language/") && f.EndsWith(".csv") && !Path.GetFileName(f).Contains('_'))
                                                  .Select(f => Path.GetFileNameWithoutExtension(f)).OrderBy(l => l, StringComparer.Ordinal))
        {
            bool ru = lang == "ru";
            var sb = new System.Text.StringBuilder("# Mod options (SWH2 mod loader)\r\n");
            foreach (var i in _items)
            {
                sb.Append($"{LabelId(i)}\t{Clean(ru && i.LabelRu != null ? i.LabelRu : i.Label)}\r\n");
                sb.Append($"{LabelId(i)}_hint\t{Clean((ru && i.HintRu != null ? i.HintRu : i.Hint) ?? "")}\r\n");
            }
            content.WriteText($"Language/{lang}_modoptions.csv", sb.ToString());
        }
    }

    void PatchMenu(ExeImage exe)
    {
        uint dispatch = exe.Map(DispatchRva), exit = exe.Map(ExitRva);
        uint va = exe.Alloc(Stubs(exe, 0).Length);   // the length doesn't depend on the address
        var a = Stubs(exe, va);
        exe.Write(va, a.ToArray());

        // test edi, edi; je game  →  jmp dispatch; nop×3
        exe.Patch(dispatch, [0x85, 0xFF, 0x0F, 0x84, .. BitConverter.GetBytes(checked((int)((long)exe.Map(GameRva) - (dispatch + 8))))],
                  [.. ExeImage.Jmp(dispatch, a.Address("dispatch")), 0x90, 0x90, 0x90]);
        // lea rcx, [rbp-0x30]; call dtor  →  jmp exit; nop×4
        exe.Patch(exit, [0x48, 0x8D, 0x4D, 0xD0, 0xE8, .. BitConverter.GetBytes(checked((int)((long)exe.Map(ExitDtorRva) - (exit + 9))))],
                  [.. ExeImage.Jmp(exit, a.Address("exit")), 0x90, 0x90, 0x90, 0x90]);
    }

    X64 Stubs(ExeImage exe, uint va)
    {
        var a = new X64(va);

        // Category switch: remember whether this is "Game", then go on as the original.
        a.Label("dispatch").Bytes(0x85, 0xFF).SeteByte("isGame").Jne("next").Jmp(exe.Map(GameRva)).Label("next").Jmp(exe.Map(NextCategoryRva));

        // Common exit (rsi = menu, rsp = the function's own frame: [rsp+0x20..0x80) are its free temporaries).
        a.Label("exit").CmpByte("isGame", 0).Je("done").MovByte("isGame", 0);
        int n = 0;
        foreach (var i in _items)
        {
            string id = LabelId(i);
            a.LeaRax($"k{n}").MovRspRax(0x30).MovRspImm(0x38, Utf8(i.Key))
             .LeaRax($"l{n}").MovRspRax(0x60).MovRspImm(0x68, Utf8(id))
             .LeaRax($"h{n}").MovRspRax(0x70).MovRspImm(0x78, Utf8(id + "_hint"))
             .LeaRsp(X64.Reg.Rax, 0x70).MovRspRax(0x20);
            if (i.Slider)   // optional<float> step by value: float, then the has-value byte
                a.MovRaxImm64(i.Step > 0 ? BitConverter.ToUInt32(BitConverter.GetBytes(i.Step), 0) | 1UL << 32 : 0).MovRspRax(0x28);
            a.LeaRsp(X64.Reg.R9, 0x60).LeaRsp(X64.Reg.R8, 0x30).XorEdxEdx().MovRcxRsi()
             .Call(exe.Map(i.Slider ? AddSliderRva : AddCheckboxRva));
            n++;
        }
        a.Label("done").Bytes(0x48, 0x8D, 0x4D, 0xD0).Call(exe.Map(ExitDtorRva)).Jmp(exe.Map(ExitContinueRva));

        a.Label("isGame").Bytes(0).Align(8);
        n = 0;
        foreach (var i in _items)
        {
            a.Label($"k{n}").Ascii(i.Key).Label($"l{n}").Ascii(LabelId(i)).Label($"h{n}").Ascii(LabelId(i) + "_hint");
            n++;
        }
        return a;
    }

    static int Utf8(string s) => System.Text.Encoding.UTF8.GetByteCount(s);
}
