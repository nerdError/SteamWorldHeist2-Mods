using SWH2.Core;
using SWH2.Modding;

namespace SWH2.Mods.Turbo;

/// <summary>
/// Faster battles: speeds up enemy turns and enemy/turret/reinforcement intros (and, if set, your own turn), while
/// cutscenes and dialogue stay at normal speed (docs/EXE_NOTES.md, "Скорость боя").
///
/// The tactics world writes its time scale to game+0x1b70 every frame; the whole simulation's dt is multiplied
/// by it. The hook replaces that store (`movss [rdx+0x1b70], xmm1`) with a jump to code in a new section that
/// picks a multiplier from the current sequence (cutscene/dialogue → ×1, enemy/turret intro, reinforcements →
/// ×intro) and the current faction (not "cowbots" → ×enemy, "cowbots" → ×player).
/// The player multiplier is a stub after the floats: the "cowbots" branch (short je at <see cref="PlayerJe"/>, which
/// jumped straight to the store = ×1) is pointed at it; it multiplies, stores and jumps back like the main code.
/// </summary>
public sealed class TurboMod : IModPlugin
{
    const uint HookRva = 0x480B77;
    static readonly byte[] HookOriginal = Hex.Parse("F30F118A701B0000"); // movss [rdx+0x1b70], xmm1

    /// <summary>
    /// Assembled by tools/re/exe_patch.py (turbo_code) at <see cref="AssembledAt"/>; followed in the section by two
    /// floats (enemy, intro multiplier) that the code reads rip-relative right after its last instruction.
    /// </summary>
    const uint AssembledAt = 0xF2A000;
    const int PlayerJe = 0x112;   // 74 12: je to the store when the faction is "cowbots"
    static readonly byte[] Code = Hex.Parse(
        "486305B55BF7FF488B44C6084885C00F84AA000000488B40104885C00F849D000000488B08483B48080F849000000048" +
        "8B014885C00F8484000000F7400C120000000F85DE000000488B00488D0D764DC7FF4839C80F84C3000000488D0D2693" +
        "C7FF4839C80F84B3000000488D0DFE47C7FF4839C80F84A3000000488D0D2E62C5FF4839C80F849B000000488D0DAE45" +
        "C7FF4839C80F848B000000488D0D1663C5FF4839C8747F488D0D3248C7FF4839C87473488D0D9E44C7FF4839C8746748" +
        "6382F81E00004885C0785B488B8AE81E0000482B8AE01E000048C1F9054839C8734448C1E005480382E01E0000488378" +
        "1007752048837818107203488B00488B0848C1E10848B800636F77626F74734839C17412F30F590D17000000EB08F30F" +
        "590D11000000F30F118A701B0000E94C6A55FF");

    /// <summary>rel32 fields pointing outside the code: SequenceSimulator slot, 8 sequence vtables, jmp back.</summary>
    static readonly int[] Fixups = [0x03, 0x4E, 0x5E, 0x6E, 0x7E, 0x8E, 0x9E, 0xAA, 0xB6, 0x12F];

    public void Apply(ModContext ctx)
    {
        float enemy = ctx.Settings.Float("enemySpeed"), intro = ctx.Settings.Float("introSpeed"), player = ctx.Settings.Float("playerSpeed");
        bool inGame = ctx.Settings.Bool("inGame");
        var exe = ctx.Exe;
        uint va = exe.Alloc(0x400);   // shared code section (writable: the prologue writes the floats); ~0x2c4 used
        uint hook = exe.Map(HookRva);

        // Code, then the floats enemy / intro / player, then the player stub.
        int stub = Code.Length + 12;
        var blob = new byte[stub + 21];
        exe.RelocateMapped(Code, AssembledAt, va, Fixups).CopyTo(blob, 0); // targets are Steam-build RVAs
        BitConverter.GetBytes(enemy).CopyTo(blob, Code.Length);      // x86: little-endian
        BitConverter.GetBytes(intro).CopyTo(blob, Code.Length + 4);
        BitConverter.GetBytes(player).CopyTo(blob, Code.Length + 8);
        if (blob[PlayerJe] != 0x74 || blob[PlayerJe + 1] != 0x12) throw new InvalidOperationException("Turbo: unexpected code at the player branch.");
        blob[PlayerJe + 1] = (byte)(stub - (PlayerJe + 2));
        byte[] mulss = [0xF3, 0x0F, 0x59, 0x0D, .. BitConverter.GetBytes(Code.Length + 8 - (stub + 8))]; // mulss xmm1, [player]
        mulss.CopyTo(blob, stub);
        HookOriginal.CopyTo(blob, stub + 8);                                                         // movss [rdx+0x1b70], xmm1
        ExeImage.Jmp(va + (uint)stub + 16, hook + 8).CopyTo(blob, stub + 16);                         // back after the hook
        exe.Write(va, blob);

        // In-game sliders (Options → Game): a prologue reads them every frame into the three floats, then runs the code.
        uint entry = va;
        if (inGame)
        {
            var speeds = new[] { ("EnemySpeed", enemy, 8f, "Turbo: enemy turn speed", "Turbo: скорость хода врагов"),
                                 ("IntroSpeed", intro, 10f, "Turbo: enemy/turret intro speed", "Turbo: скорость показа врагов и турелей"),
                                 ("PlayerSpeed", player, 8f, "Turbo: your turn speed", "Turbo: скорость своего хода") };
            var a = new X64(va + (uint)blob.Length);
            a.PushVolatile().SubRsp(0x98);                   // the hook site's rsp is 16-aligned: 7 pushes + 0x98 keep it so
            for (int x = 0; x < 6; x++) a.SaveXmm(x, 0x30 + 16 * x);
            for (int s = 0; s < speeds.Length; s++)
            {
                var (name, value, max, label, labelRu) = speeds[s];
                string key = ctx.Options.Slider(name, (Math.Min(value, max) - 1) / (max - 1), 0.5f / (max - 1), label,
                    $"Left = normal speed (×1), right = ×{max:0}. Steps of ×0.5.", labelRu, $"Слева — обычная скорость (×1), справа — ×{max:0}. Шаг ×0,5.");
                GameOptions.EmitReadFloat(a, exe, $"key{s}", key);
                a.MulssXmm0($"scale{s}").AddssXmm0("one").StoreXmm0(va + (uint)(Code.Length + 4 * s));   // 1 + v·(max−1)
            }
            for (int x = 0; x < 6; x++) a.LoadXmm(x, 0x30 + 16 * x);
            a.AddRsp(0x98).PopVolatile().Jmp(va);
            a.Align(4).Label("one").Float(1);
            for (int s = 0; s < speeds.Length; s++) a.Label($"scale{s}").Float(speeds[s].Item3 - 1);
            for (int s = 0; s < speeds.Length; s++) a.Label($"key{s}").Ascii(ctx.Options.KeyFor(speeds[s].Item1));
            entry = a.Origin;
            exe.Write(a.Origin, a.ToArray());
        }
        exe.Patch(hook, HookOriginal, [.. ExeImage.Jmp(hook, entry), 0x90, 0x90, 0x90]);

        ctx.Summary = $"enemy turns ×{enemy:0.##}, intros ×{intro:0.##}" + (player != 1 ? $", your turn ×{player:0.##}" : "") + (inGame ? ", sliders in the game's Options" : "");
        ctx.Log($"Turbo: {ctx.Summary} (code at {va:X}).");
    }
}
