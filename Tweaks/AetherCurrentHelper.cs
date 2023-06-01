using Dalamud.Game.ClientState.Keys;
using FFXIVClientStructs.FFXIV.Component.GUI;
using HaselTweaks.Enums;
using HaselTweaks.Structs;
using HaselTweaks.Windows;
using Lumina.Excel.GeneratedSheets;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace HaselTweaks.Tweaks;

public unsafe partial class AetherCurrentHelper : Tweak
{
    public override string Name => "Aether Current Helper";
    public override string Description => "Clicking on a zone in the Aether Currents window opens a helper window that shows where to find the aether currents or which quests unlocks them. Clicking on an aether current in the list flags the position of the aether current or the quest giver on the map.";

    public class Configuration
    {
        [ConfigField(Label = "Show distance instead of checkmark when unlocked")]
        public bool AlwaysShowDistance = false;

        [ConfigField(Label = "Center distance column", Description = "Disable this if you have problems with the window endlessly expanding to the right")]
        public bool CenterDistance = true;
    }

    private AetherCurrentHelperWindow? Window;

    public override void Disable()
    {
        if (Window != null)
            Plugin.WindowSystem.RemoveWindow(Window);
    }

    [VTableHook<AgentAetherCurrent>((int)AgentInterfaceVfs.ReceiveEvent)]
    private AtkValue* AgentAetherCurrent_ReceiveEvent(AgentAetherCurrent* agent, AtkValue* eventData, AtkValue* atkValue, uint valueCount, nint eventKind)
    {
        if (Service.KeyState[VirtualKey.SHIFT])
            goto OriginalCode;

        if (atkValue == null || atkValue->Type != ValueType.Int || atkValue->Int != 0)
            goto OriginalCode;

        var atkValue2 = (AtkValue*)((nint)atkValue + 0x10);
        if (atkValue2->Type != ValueType.Int)
            goto OriginalCode;

        var rawIndex = (uint)(atkValue2->Int + 6 * agent->TabIndex);
        var index = rawIndex + 1;
        if (index < 19)
            index = rawIndex;

        var compFlgSet = Service.Data.GetExcelSheet<AetherCurrentCompFlgSet>()?.GetRow(index + 1);
        if (compFlgSet == null)
            goto OriginalCode;

        if (Window == null)
            Plugin.WindowSystem.AddWindow(Window = new());

        Window.SetCompFlgSet(compFlgSet);

        if (!Window.IsOpen)
            Window.Toggle();

        // handled, just like in the original code
        eventData->Type = ValueType.Bool;
        eventData->Byte = 0;
        return eventData;

        OriginalCode:
        return AgentAetherCurrent_ReceiveEventHook.Original(agent, eventData, atkValue, valueCount, eventKind);
    }
}
