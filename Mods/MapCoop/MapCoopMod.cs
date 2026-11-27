using SWH2.Core;
using SWH2.Modding;

namespace SWH2.Mods.MapCoop;

/// <summary>
/// World map netplay (docs/MULTIPLAYER.md, "Карта мира"): a separate mod for now, next to NE Co-op (network).
/// The exe gets a dll loader (native/swh2map.dll does the rest). Coop's loader sits on the SDL_Init call; this one on the
/// one call of SDL_CreateWindow (after SDL_Init, before the game loop), so both can be on. The stub keeps the four
/// argument registers and the stack exactly as they were: SDL_CreateWindow has stack arguments.
/// </summary>
public sealed class MapCoopMod : IModPlugin, IModCommand
{
    // Steam-build RVAs (exe.Map translates them for GOG); the original call is `call [rip+rel]` = SDL_CreateWindow.
    const uint CreateWindowCall = 0x7E9DFC, IatCreateWindow = 0xB478F8, IatLoadLibraryA = 0xB47180;
    public const string DllPath = @"Mods\MapCoop\swh2map.dll";

    public void Apply(ModContext ctx)
    {
        AddDllLoader(ctx.Exe, DllPath);
        ctx.Summary = "world map netplay dll";
        ctx.Log("MapCoop: dll loader added.");
    }

    static void AddDllLoader(ExeImage exe, string dllPath)
    {
        uint call = exe.Map(CreateWindowCall), iatCreate = exe.Map(IatCreateWindow), iatLoad = exe.Map(IatLoadLibraryA);
        uint va = exe.AddSection(".mapc", 0x100);
        var path = System.Text.Encoding.ASCII.GetBytes(dllPath + "\0");
        if (path.Length > 0x40) throw new InvalidOperationException("dll path too long");
        var code = new List<byte>();
        void Add(string hex) => code.AddRange(Hex.Parse(hex));
        void Rel(uint target) => code.AddRange(BitConverter.GetBytes(checked((int)((long)target - (va + code.Count + 4)))));
        Add("51"); Add("52"); Add("4150"); Add("4151");   // push rcx, rdx, r8, r9  (rsp stays 8 mod 16)
        Add("4883EC28");                                  // sub rsp, 0x28  (shadow space, aligned)
        Add("488D0D"); Rel(va + 0x40);                    // lea rcx, [path]
        Add("FF15"); Rel(iatLoad);                        // call [LoadLibraryA]
        Add("4883C428");                                  // add rsp, 0x28
        Add("4159"); Add("4158"); Add("5A"); Add("59");   // pop r9, r8, rdx, rcx
        Add("FF25"); Rel(iatCreate);                      // jmp [SDL_CreateWindow]  (returns straight to the game)
        while (code.Count < 0x40) code.Add(0xCC);
        code.AddRange(path);
        exe.Write(va, code.ToArray());
        var patch = new byte[6];
        patch[0] = 0xE8;                                  // call stub; nop
        BitConverter.GetBytes(checked((int)((long)va - (call + 5)))).CopyTo(patch, 1);
        patch[5] = 0x90;
        exe.Patch(call, ExeImage.CallIndirect(call, iatCreate), patch);
    }

    public int RunCommand(IReadOnlyList<string> args, ModToolContext ctx) => MapTest.Run(args, ctx);
}
