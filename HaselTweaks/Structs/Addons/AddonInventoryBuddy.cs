using FFXIVClientStructs.FFXIV.Component.GUI;

namespace HaselTweaks.Structs;

// ctor "40 53 48 83 EC 20 48 8B D9 E8 ?? ?? ?? ?? 80 8B ?? ?? ?? ?? ?? 48 8D 05 ?? ?? ?? ?? 81 8B ?? ?? ?? ?? ?? ?? ?? ?? 48 8D 8B ?? ?? ?? ?? 48 89 03 33 D2 B8"
[StructLayout(LayoutKind.Explicit, Size = 0x498)]
public partial struct AddonInventoryBuddy
{
    [FieldOffset(0)] public AtkUnitBase AtkUnitBase;

    [FieldOffset(0x488)] public byte TabIndex;

    [MemberFunction("E9 ?? ?? ?? ?? BA ?? ?? ?? ?? 48 8B 5C 24 ?? 48 83 C4 20")]
    public readonly partial void SetTab(byte tab);
}
