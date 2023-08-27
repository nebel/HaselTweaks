using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using HaselCommon.Utils;
using HaselTweaks.Enums.PortraitHelper;
using HaselTweaks.Records.PortraitHelper;
using HaselTweaks.Structs;
using HaselTweaks.Utils;
using HaselTweaks.Windows.PortraitHelperWindows;
using HaselTweaks.Windows.PortraitHelperWindows.Overlays;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Ole;
using DalamudFramework = Dalamud.Game.Framework;

namespace HaselTweaks.Tweaks;

[Tweak]
public partial class PortraitHelper : Tweak
{
    public static Configuration Config => Plugin.Config.Tweaks.PortraitHelper;

    public class Configuration
    {
        public List<SavedPreset> Presets = new();
        public List<SavedPresetTag> PresetTags = new();
        public bool ShowAlignmentTool = false;
        public int AlignmentToolVerticalLines = 2;
        public Vector4 AlignmentToolVerticalColor = new(0, 0, 0, 1f);
        public int AlignmentToolHorizontalLines = 2;
        public Vector4 AlignmentToolHorizontalColor = new(0, 0, 0, 1f);

        [BoolConfig]
        public bool NotifyGearChecksumMismatch = true;

        [BoolConfig]
        public bool ReequipGearsetOnUpdate = false;

        [BoolConfig]
        public bool AutoUpdatePotraitOnGearUpdate = false;
    }

    private static readonly TimeSpan CheckDelay = TimeSpan.FromMilliseconds(500);

    private DateTime _lastClipboardCheck = default;
    private uint _lastClipboardSequenceNumber;
    private CancellationTokenSource? _jobChangedOrGearsetUpdatedCTS;
    private uint _lastJob = 0;
    private DalamudLinkPayload? _openPortraitEditPayload;

    public static ImportFlags CurrentImportFlags { get; set; } = ImportFlags.All;
    public static PortraitPreset? ClipboardPreset { get; set; }

    public override unsafe void Enable()
    {
        _lastJob = Service.ClientState.LocalPlayer?.ClassJob.Id ?? 0;

        _openPortraitEditPayload = Service.PluginInterface.AddChatLinkHandler(1000, OpenPortraitEditChatHandler);

        if (IsAddonOpen(AgentId.BannerEditor))
            OnAddonOpen("BannerEditor");
    }

    public override void Disable()
    {
        Service.PluginInterface.RemoveChatLinkHandler(1000);

        Service.WindowManager.CloseWindow<MenuBar>();
        CloseOverlays();
    }

    public override void OnLogin()
    {
        _lastJob = Service.ClientState.LocalPlayer?.ClassJob.Id ?? 0;
    }

    public override void OnLogout()
    {
        _lastJob = 0;
    }

    private unsafe void OpenPortraitEditChatHandler(uint commandId, SeString message)
    {
        var raptureGearsetModule = RaptureGearsetModule.Instance();
        var gearsetId = raptureGearsetModule->CurrentGearsetIndex;
        if (raptureGearsetModule->IsValidGearset(gearsetId) == 0)
            return;

        GetAgent<AgentBannerEditor>()->OpenForGearset(gearsetId);
    }

    public override unsafe void OnAddonOpen(string addonName)
    {
        if (addonName != "BannerEditor")
            return;

        if (!IsAddonOpen(addonName))
            return;

        Service.WindowManager.OpenWindow<MenuBar>();
    }

    public override unsafe void OnAddonClose(string addonName)
    {
        if (addonName != "BannerEditor")
            return;

        Service.WindowManager.CloseWindow<MenuBar>();
        CloseOverlays();
    }

    public static void CloseOverlays()
    {
        Service.WindowManager.CloseWindow<AdvancedImportOverlay>();
        Service.WindowManager.CloseWindow<AdvancedEditOverlay>();
        Service.WindowManager.CloseWindow<PresetBrowserOverlay>();
        Service.WindowManager.CloseWindow<AlignmentToolSettingsOverlay>();
    }

    public override unsafe void OnFrameworkUpdate(DalamudFramework framework)
    {
        var currentJob = Service.ClientState.LocalPlayer?.ClassJob.Id ?? 0;
        if (currentJob != 0 && currentJob != _lastJob)
        {
            _jobChangedOrGearsetUpdatedCTS?.Cancel();
            _jobChangedOrGearsetUpdatedCTS = new();

            _lastJob = currentJob;

            Service.Framework.RunOnTick(() =>
            {
                CheckForGearChecksumMismatch(RaptureGearsetModule.Instance()->CurrentGearsetIndex, true);
            }, CheckDelay, cancellationToken: _jobChangedOrGearsetUpdatedCTS.Token);
        }

        if (Service.WindowManager.GetWindow<MenuBar>() is null)
            return;

        CheckClipboard();
    }

    public void CheckClipboard()
    {
        if (DateTime.Now - _lastClipboardCheck <= TimeSpan.FromMilliseconds(100))
            return;

        var clipboardSequenceNumber = PInvoke.GetClipboardSequenceNumber();

        if (_lastClipboardSequenceNumber == clipboardSequenceNumber)
            return;

        if (!PInvoke.IsClipboardFormatAvailable((uint)CLIPBOARD_FORMAT.CF_TEXT))
            return;

        if (!PInvoke.OpenClipboard(HWND.Null))
            return;

        try
        {
            _lastClipboardSequenceNumber = clipboardSequenceNumber;

            var data = PInvoke.GetClipboardData((uint)CLIPBOARD_FORMAT.CF_TEXT);
            if (!data.IsNull)
            {
                var clipboardText = MemoryHelper.ReadString(data, 1024);
                ClipboardPreset = PortraitPreset.FromExportedString(clipboardText);

                if (ClipboardPreset != null)
                    Debug($"Parsed ClipboardPreset: {ClipboardPreset}");
            }
        }
        catch (Exception e)
        {
            Error(e, "Error during CheckClipboard");
        }
        finally
        {
            PInvoke.CloseClipboard();

            _lastClipboardCheck = DateTime.Now;
        }
    }
    [AddressHook<RaptureGearsetModule>(nameof(RaptureGearsetModule.Addresses.UpdateGearset))]
    public unsafe int RaptureGearsetModule_UpdateGearset(RaptureGearsetModule* raptureGearsetModule, int gearsetId)
    {
        var ret = RaptureGearsetModule_UpdateGearsetHook.OriginalDisposeSafe(raptureGearsetModule, gearsetId);

        _jobChangedOrGearsetUpdatedCTS?.Cancel();
        _jobChangedOrGearsetUpdatedCTS = new();

        Service.Framework.RunOnTick(() =>
        {
            CheckForGearChecksumMismatch(gearsetId);
        }, delay: CheckDelay, cancellationToken: _jobChangedOrGearsetUpdatedCTS.Token);

        return ret;
    }

    private unsafe void CheckForGearChecksumMismatch(int gearsetId, bool isJobChange = false)
    {
        var raptureGearsetModule = RaptureGearsetModule.Instance();

        if (raptureGearsetModule->IsValidGearset(gearsetId) == 0)
            return;

        var gearset = raptureGearsetModule->GetGearset(gearsetId);
        if (gearset == null)
            return;

        var bannerIndex = gearset->BannerIndex;
        if (bannerIndex == 0) // no banner linked
            return;

        var bannerModule = BannerModule.Instance();
        var bannerId = bannerModule->GetBannerIdByBannerIndex(bannerIndex - 1);
        if (bannerId < 0) // banner not found
            return;

        var banner = bannerModule->GetBannerById(bannerId);
        if (banner == null) // banner not found
            return;

        if (banner->Checksum == GetEquippedGearChecksum())
        {
            Log($"Gear checksum matches! (Portrait: {banner->Checksum:X}, Equipped: {GetEquippedGearChecksum():X})");
            return;
        }

        Log($"Gear checksum mismatch detected! (Portrait: {banner->Checksum:X}, Equipped: {GetEquippedGearChecksum():X})");

        if (!isJobChange && Config.ReequipGearsetOnUpdate && gearset->GlamourSetLink > 0 && GameMain.IsInSanctuary())
        {
            Log($"Re-equipping Gearset #{gearset->ID + 1} to reapply glamour plate");
            raptureGearsetModule->EquipGearset(gearset->ID, gearset->GlamourSetLink);
            RecheckGearChecksum(banner);
        }
        else if (!isJobChange && Config.AutoUpdatePotraitOnGearUpdate && gearset->GlamourSetLink == 0)
        {
            Log("Trying to send portrait update...");
            if (SendPortraitUpdate(banner))
                RecheckGearChecksum(banner);
        }
        else if (Config.NotifyGearChecksumMismatch)
        {
            NotifyMismatch();
        }
    }

    private unsafe void RecheckGearChecksum(BannerModuleEntry* banner)
    {
        _jobChangedOrGearsetUpdatedCTS?.Cancel();
        _jobChangedOrGearsetUpdatedCTS = new();

        Service.Framework.RunOnTick(() =>
        {
            if (banner->Checksum != GetEquippedGearChecksum())
            {
                Log($"Gear checksum still mismatching (Portrait: {banner->Checksum:X}, Equipped: {GetEquippedGearChecksum():X}), opening Banner Editor");
                NotifyMismatch();
            }
            else
            {
                Log($"Gear checksum matches now (Portrait: {banner->Checksum:X}, Equipped: {GetEquippedGearChecksum():X})");
            }
        }, delay: CheckDelay, cancellationToken: _jobChangedOrGearsetUpdatedCTS.Token); // TODO: find out when it's safe to check again instead of randomly picking a delay. ping may vary
    }

    private unsafe void NotifyMismatch()
    {
        var text = t("PortraitHelper.GearChecksumMismatch"); // based on LogMessage#5876

        var sb = new SeStringBuilder()
            .AddUiForeground("\uE078 ", 32);

        var raptureGearsetModule = RaptureGearsetModule.Instance();
        if (raptureGearsetModule->IsValidGearset(raptureGearsetModule->CurrentGearsetIndex) == 1)
        {
            if (_openPortraitEditPayload != null)
            {
                sb.Add(_openPortraitEditPayload)
                  .AddText(text)
                  .Add(RawPayload.LinkTerminator);
            }
            else
            {
                sb.AddText(text);
            }
        }
        else
        {
            sb.AddText(text);
        }

        UIModule.Instance()->ShowErrorText(text, false);

        Service.ChatGui.PrintError(sb.Build());
    }

    private unsafe uint GetEquippedGearChecksum()
    {
        using var checksumData = new DisposableStruct<GearsetChecksumData>();
        var container = InventoryManager.Instance()->GetInventoryContainer(InventoryType.EquippedItems);

        for (var i = 0; i < 14; i++)
        {
            var item = container->Items[i];
            checksumData.Ptr->ItemIds[i] = item.GlamourID != 0 ? item.GlamourID : item.ItemID;
            checksumData.Ptr->StainIds[i] = item.Stain;
        }

        GetAgent<AgentStatus>()->UpdateGearVisibilityInNumberArray();

        var numberArray = AtkStage.GetSingleton()->GetNumberArrayData()[62];

        var gearVisibilityFlag = BannerGearVisibilityFlag.None;

        if (numberArray->IntArray[268] == 0)
            gearVisibilityFlag |= BannerGearVisibilityFlag.HeadgearHidden;

        if (numberArray->IntArray[269] == 0)
            gearVisibilityFlag |= BannerGearVisibilityFlag.WeaponHidden;

        if (numberArray->IntArray[270] == 1)
            gearVisibilityFlag |= BannerGearVisibilityFlag.VisorClosed;

        return GearsetChecksumData.GenerateChecksum(checksumData.Ptr->ItemIds, checksumData.Ptr->StainIds, gearVisibilityFlag);
    }

    private unsafe bool SendPortraitUpdate(BannerModuleEntry* banner)
    {
        var raptureGearsetModule = RaptureGearsetModule.Instance();

        var gearsetId = raptureGearsetModule->CurrentGearsetIndex;
        if (raptureGearsetModule->IsValidGearset(gearsetId) == 0)
        {
            Warning("No Portrait Update: Gearset invalid");
            return false;
        }

        var gearset = raptureGearsetModule->GetGearset(gearsetId);
        if (gearset == null)
        {
            Warning("No Portrait Update: Gearset is null");
            return false;
        }

        var bannerIndex = gearset->BannerIndex;
        if (bannerIndex == 0) // no banner linked
        {
            Information("No Portrait Update: Gearset not linked to Banner");
            return false;
        }

        if (banner->BannerIndex != bannerIndex - 1)
        {
            Warning($"No Portrait Update: Banner index mismatch (Banner: {banner->BannerIndex}, Gearset Banner Link: {bannerIndex - 1})");
            return false;
        }

        var currentChecksum = GetEquippedGearChecksum();
        if (banner->Checksum == currentChecksum)
        {
            Information("No Portrait Update: Checksum still matches");
            return false;
        }

        var localPlayer = (Character*)(Service.ClientState.LocalPlayer?.Address ?? 0);
        if (localPlayer == null)
        {
            Warning("No Portrait Update: LocalPlayer is null");
            return false;
        }

        var helper = HaselUIModule.Instance()->GetVf35Struct()->BannerModuleHelper;

        // TODO: check E8 ?? ?? ?? ?? 84 C0 74 4A 48 8D 4C 24

        if (!helper->IsBannerNotExpired(banner, 1))
        {
            Warning("No Portrait Update: Banner expired");
            return false;
        }

        if (!helper->IsBannerCharacterDataNotExpired(banner, 1))
        {
            Warning("No Portrait Update: Banner character data expired");
            return false;
        }

        using var bannerUpdateData = new DisposableCreatable<BannerUpdateData>();

        if (!helper->InitializeBannerUpdateData(bannerUpdateData))
        {
            Warning("No Portrait Update: InitializeBannerUpdateData failed");
            return false;
        }

        // update Banner
        banner->LastUpdated = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        banner->Checksum = currentChecksum;
        helper->CopyRaceGenderHeightTribe(banner, localPlayer);
        BannerModule.Instance()->UserFileEvent.HasChanges = true;

        if (!helper->CopyBannerEntryToBannerUpdateData(bannerUpdateData, banner))
        {
            Warning("No Portrait Update: CopyBannerEntryToBannerUpdateData failed");
            return false;
        }

        var result = helper->SendBannerUpdateData(bannerUpdateData);

        if (result)
        {
            Log("Portrait Update sent");
        }
        else
        {
            Warning("Portrait Update failed to send");
        }

        return result;
    }

    public static unsafe Image<Bgra32>? GetCurrentCharaViewImage()
    {
        var charaViewTexture = RenderTargetManager.Instance()->GetCharaViewTexture(GetAgent<AgentBannerEditor>()->EditorState->CharaView->Base.ClientObjectIndex);
        if (charaViewTexture == null || charaViewTexture->D3D11Texture2D == null)
            return null;

        var device = Service.PluginInterface.UiBuilder.Device;
        var texture = CppObject.FromPointer<Texture2D>((nint)charaViewTexture->D3D11Texture2D);

        // thanks to ChatGPT
        // Get the texture description
        var desc = texture.Description;

        // Create a staging texture with the same description
        using var stagingTexture = new Texture2D(device, new Texture2DDescription()
        {
            ArraySize = 1,
            BindFlags = BindFlags.None,
            CpuAccessFlags = CpuAccessFlags.Read,
            Format = desc.Format,
            Height = desc.Height,
            Width = desc.Width,
            MipLevels = 1,
            OptionFlags = desc.OptionFlags,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging
        });

        // Copy the texture data to the staging texture
        device.ImmediateContext.CopyResource(texture, stagingTexture);

        // Map the staging texture
        device.ImmediateContext.MapSubresource(stagingTexture, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None, out var dataStream);

        using var pixelDataStream = new MemoryStream();
        dataStream.CopyTo(pixelDataStream);

        // Unmap the staging texture
        device.ImmediateContext.UnmapSubresource(stagingTexture, 0);

        return Image.LoadPixelData<Bgra32>(pixelDataStream.ToArray(), desc.Width, desc.Height);
    }

    public static string GetPortraitThumbnailPath(string hash)
    {
        var portraitsPath = Path.Join(Service.PluginInterface.ConfigDirectory.FullName, "Portraits");

        if (!Directory.Exists(portraitsPath))
            Directory.CreateDirectory(portraitsPath);

        return Path.Join(portraitsPath, $"{hash}.png");
    }
}