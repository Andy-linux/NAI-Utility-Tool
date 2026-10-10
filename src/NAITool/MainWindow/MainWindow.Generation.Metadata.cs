using System.Text.Json;
using System.Linq;
using System.Text.Json.Nodes;
using NAITool.Services;

namespace NAITool;

public sealed partial class MainWindow
{
    private void RestoreImportedHighDraft(ImageMetadata meta, NAIParameters target)
    {
        if (meta.HighDraft is not NAIParameters draft) return;
        if (!IsAssetProtectionStepLimitEnabled() || draft.Steps <= 28)
            target.Steps = draft.Steps;
        target.Sampler = draft.Sampler;
        target.CfgRescale = draft.CfgRescale;
        target.UcPreset = draft.UcPreset;
        target.UcPresetId = draft.UcPresetId;
    }

    private sealed record ClientParameterDraftSnapshot(NAIParameters Parameters, string Negative, string[] CharacterNegatives);

    private ClientParameterDraftSnapshot CaptureClientParameterDraft(NAIParameters parameters) => new(
        JsonSerializer.Deserialize<NAIParameters>(JsonSerializer.Serialize(parameters))!,
        _currentMode == AppMode.ImageGeneration ? _genNegativePrompt : _i2iNegativePrompt,
        HighDraftRules.CaptureActiveNegatives(CurrentCharacterEntries.Select(entry => (entry.IsDisabled, entry.NegativePrompt))));

    private byte[] AddClientParameterDraftMetadata(byte[] image, ClientParameterDraftSnapshot snapshot)
    {
        if (_settings.Settings.PrivacyMode) return image;
        var draft = snapshot.Parameters;
        var meta = ImageMetadataService.ReadFromBytes(image);
        // Stream finals may be WebP. Convert locally before PNG chunk editing;
        // never feed another format to the PNG-only metadata writer.
        image = EnsurePngEncoded(image);
        if (!HasPngSignature(image)) return image;
        JsonObject root;
        try { root = JsonNode.Parse(meta?.RawJson ?? "{}") as JsonObject ?? new JsonObject(); }
        catch (JsonException) { root = new JsonObject(); }
        // Client-only fields are added after generation, never sent upstream.
        root["nai_tool_effort"] = draft.Effort;
        root["nai_tool_quality_preset"] = draft.QualityPreset;
        root["nai_tool_high_parameters"] = JsonSerializer.SerializeToNode(draft);
        root["nai_tool_high_character_negatives"] = JsonSerializer.SerializeToNode(snapshot.CharacterNegatives);
        root["nai_tool_high_negative"] = snapshot.Negative;
        return ImageMetadataService.ReplacePngComment(image, root.ToJsonString());
    }
}
