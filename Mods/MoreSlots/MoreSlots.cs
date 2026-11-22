using System.Xml.Linq;
using SWH2.Core;
using SWH2.Modding;

namespace SWH2.Mods.MoreSlots;

/// <summary>
/// More save slots (docs/EXE_NOTES.md, "Слоты сохранений"). The game keeps its slots in a std::vector of 0x108-byte
/// profiles that is allocated once with 3 elements; save files are named by index (savegame_{:03d}.dat), the
/// current slot is the session int Session.CurrentProfile, and everything else indexes into the vector. Only the
/// allocation and three loops hard-code the 3. The main menu instantiates one main_menu_profile_slot template per
/// slot into save_slots_list, so the UI follows the loop count on its own.
/// </summary>
public sealed class MoreSlotsMod : IModPlugin
{
    const int Original = 3, ProfileSize = 0x108;

    public void Apply(ModContext ctx)
    {
        int n = ctx.Settings.Int("slots");
        if (n is < 1 or > 100) throw new InvalidOperationException($"Save slots must be 1..100, got {n}.");
        var exe = ctx.Exe;
        byte[] Imm32(int v) => BitConverter.GetBytes(v);

        // Allocation of the profile vector (0x563760): new(3*0x108), end = begin+3*0x108, construct 3 elements.
        exe.Patch(0x56379D, [0xB9, .. Imm32(Original * ProfileSize)], [0xB9, .. Imm32(n * ProfileSize)]);             // mov ecx, size
        exe.Patch(0x5637B5, [0x48, 0x05, .. Imm32(Original * ProfileSize)], [0x48, 0x05, .. Imm32(n * ProfileSize)]); // add rax, size
        exe.Patch(0x5637C9, [0xBA, .. Imm32(Original)], [0xBA, .. Imm32(n)]);                                          // mov edx, count
        // Loops over the slots: reading the save files at startup, "is there any save" (main menu), menu buttons.
        exe.Patch(0x561D20, [0x41, 0x83, 0xFE, Original], [0x41, 0x83, 0xFE, (byte)n]); // cmp r14d, count
        exe.Patch(0x5ED3CC, [0x48, 0x83, 0xF9, Original], [0x48, 0x83, 0xF9, (byte)n]); // cmp rcx, count
        exe.Patch(0x5F0D4E, [0x83, 0xFB, Original], [0x83, 0xFB, (byte)n]);             // cmp ebx, count
        // The current slot getter (0x561e10) clamps Session.CurrentProfile to 0..2: without this, slots 4+ start
        // fine but every save goes to slot 3.
        exe.Patch(0x561F23, [0xB8, .. Imm32(Original - 1)], [0xB8, .. Imm32(n - 1)]);  // mov eax, count-1

        if (n > 6) CompactButtons(ctx);
        ctx.Summary = $"{n} save slots";
        ctx.Log($"MoreSlots: {n} save slots.");
    }

    /// <summary>Slot buttons are 140 high + 50 gap (UI is laid out for 2160 lines); shrink them so 7–10 fit.</summary>
    static void CompactButtons(ModContext ctx)
    {
        var ui = ctx.Content.Xml("UI/ui.me");
        var template = ui.Root.Descendants("Template").FirstOrDefault(t => t.Name() == "main_menu_profile_slot")
            ?? throw new InvalidOperationException("UI/ui.me: template main_menu_profile_slot not found.");
        var button = template.Element("Button") ?? throw new InvalidOperationException("main_menu_profile_slot has no Button.");
        Set(button, "Height", "140", "100");
        Set(button, "Margins", "0 0 0 50", "0 0 0 20");
        var delete = button.Elements("Button").FirstOrDefault(b => b.Name() == "delete_button")
            ?? throw new InvalidOperationException("main_menu_profile_slot has no delete_button.");
        Set(delete, "Height", "140", "100");
    }

    static void Set(XElement e, string attr, string expected, string value)
    {
        var a = e.Attribute(attr);
        if (a?.Value != expected) throw new InvalidOperationException($"UI/ui.me: unexpected {e.Name}@{attr}='{a?.Value}' (expected '{expected}').");
        a.Value = value;
    }
}
