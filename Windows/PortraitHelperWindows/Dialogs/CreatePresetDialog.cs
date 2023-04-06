using System.Collections.Generic;
using System.Linq;
using Dalamud.Interface;
using Dalamud.Interface.Raii;
using Dalamud.Logging;
using HaselTweaks.ImGuiComponents;
using HaselTweaks.Records.PortraitHelper;
using HaselTweaks.Tweaks;
using ImGuiNET;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using XXHash3NET;

namespace HaselTweaks.Windows.PortraitHelperWindows.Dialogs;

public class CreatePresetDialog : ConfirmationDialog
{
    private static PortraitHelper.Configuration Config => Plugin.Config.Tweaks.PortraitHelper;

    private readonly ConfirmationButton saveButton;

    private string? name;
    private PortraitPreset? preset;
    private Image<Bgra32>? image;
    private readonly List<Guid> tags = new();

    public CreatePresetDialog() : base("Save as Preset")
    {
        AddButton(saveButton = new ConfirmationButton("Save", OnSave));
    }

    public void Open(string name, PortraitPreset? preset, Image<Bgra32>? image)
    {
        this.name = name;
        this.preset = preset;
        this.image = image;
        tags.Clear();
        Show();
    }

    public override bool DrawCondition()
        => base.DrawCondition() && preset != null && image != null;

    public override void InnerDraw()
    {
        ImGui.Text("Enter a name for the new preset:");
        ImGui.Spacing();
        ImGui.InputText("##PresetName", ref name, 100);

        var disabled = string.IsNullOrEmpty(name.Trim());
        if (!disabled && (ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter)))
        {
            OnSave();
        }

        ImGui.Spacing();

        ImGui.Text("Select Tags (optional):");
        ImGui.Spacing();

        var tagNames = tags
            .Select(id => Config.PresetTags.FirstOrDefault((t) => t.Id == id)?.Name ?? string.Empty)
            .Where(name => !string.IsNullOrEmpty(name));

        var preview = tagNames.Any() ? string.Join(", ", tagNames) : "None";

        using var tagsCombo = ImRaii.Combo("##PresetTag", preview);
        if (tagsCombo.Success)
        {
            foreach (var tag in Config.PresetTags)
            {
                var isSelected = tags.Contains(tag.Id);

                if (ImGui.TreeNodeEx($"{tag.Name}##PresetTag{tag.Id}", (isSelected ? ImGuiTreeNodeFlags.Selected : 0) | ImGuiTreeNodeFlags.FramePadding | ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.SpanAvailWidth))
                {
                    if (ImGui.IsItemClicked())
                    {
                        if (isSelected)
                        {
                            tags.Remove(tag.Id);
                        }
                        else
                        {
                            tags.Add(tag.Id);
                        }
                    }

                    if (isSelected)
                    {
                        ImGui.SameLine();
                        ImGui.SetCursorPosX(8);

                        using (ImRaii.PushFont(UiBuilder.IconFont))
                        {
                            ImGui.TextUnformatted(FontAwesomeIcon.Check.ToIconString());
                        }
                    }

                    ImGui.TreePop();
                }
            }
        }
        tagsCombo.Dispose();

        saveButton.Disabled = disabled;
    }

    private void OnSave()
    {
        if (preset == null || image == null || name == null || string.IsNullOrEmpty(name.Trim()))
        {
            PluginLog.Error("Could not save portrait: data missing"); // TODO: show error
            return;
        }

        // resize
        image.Mutate(x => x.Resize((int)PresetCard.PortraitSize.X, (int)PresetCard.PortraitSize.Y, KnownResamplers.Lanczos3));

        // generate hash
        var pixelData = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(pixelData);

        var hash = XXHash3.Hash64(pixelData).ToString("x");
        if (string.IsNullOrEmpty(hash))
        {
            PluginLog.Error("Could not save portrait: hash generation failed"); // TODO: show error
            return;
        }

        var encoder = new PngEncoder
        {
            CompressionLevel = PngCompressionLevel.BestCompression,
            ColorType = PngColorType.Rgb // no need for alpha channel
        };

        var thumbPath = Plugin.Config.GetPortraitThumbnailPath(hash);

        image.SaveAsPng(thumbPath, encoder);
        image.Dispose();

        Config.Presets.Add(new(name.Trim(), preset, tags, hash));
        Plugin.Config.Save();
    }
}
