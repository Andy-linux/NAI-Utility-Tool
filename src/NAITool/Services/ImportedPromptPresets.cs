using System;
using System.Collections.Generic;
using System.Linq;

namespace NAITool.Services;

internal sealed class ImportedPromptPresetMatch
{
    public string PositivePrompt { get; init; } = "";
    public string NegativePrompt { get; init; } = "";
    public bool QualityMatched { get; init; }
    public string QualityPreset { get; init; } = "standard";
    public bool TransparentBackground { get; init; }
    public int? UcPresetMatched { get; init; }
    public string? UcPresetId { get; init; }
}

internal static class ImportedPromptPresets
{
    private sealed record QualityCandidate(string Id, string Text, bool Transparent);

    internal static ImportedPromptPresetMatch Read(
        string positive, string negative, string sourceModel, string targetModel, ImageMetadata metadata)
    {
        sourceModel = ModelCapabilities.BaseModel(sourceModel);
        targetModel = ModelCapabilities.BaseModel(targetModel);
        var candidates = QualityCandidates(sourceModel, metadata);
        var mix = NovelAiPromptPresets.SplitPromptMix(positive);
        bool firstOnly = NovelAIService.IsV4PlusModel(targetModel);
        var marker = firstOnly ? NovelAiPromptPresets.TextMarker.Match(mix[0]) : null;
        string text = marker?.Success == true ? mix[0][marker.Index..] : "";
        if (text.Length > 0) mix[0] = mix[0][..marker!.Index];
        QualityCandidate? selected = null;
        foreach (var candidate in candidates)
        {
            var stripped = mix.Select((part, index) => firstOnly && index > 0 ? part
                : Detach(part, candidate.Text, sourceModel == "nai-diffusion-2")).ToArray();
            if (stripped.Any(part => part == null)) continue;
            mix = stripped.Select(part => part!).ToArray();
            selected = candidate;
            break;
        }
        mix[0] += text;

        string nsfw = "";
        if (negative == "lowres") negative = "";
        else if (negative.StartsWith("nsfw", StringComparison.Ordinal))
        {
            nsfw = "nsfw";
            negative = negative[4..];
            if (negative.StartsWith(", ", StringComparison.Ordinal))
            {
                nsfw += ", ";
                negative = negative[2..];
            }
        }
        var ucIds = NovelAiPromptPresets.UcIds(sourceModel);
        var orderedUc = ucIds.Where(id => id != "none")
            .OrderByDescending(id => NovelAiPromptPresets.UcTags(sourceModel, id).Length).ToList();
        if (metadata.UcPresetId == "none") orderedUc.Clear();
        else if (metadata.UcPresetId is { } hint && ucIds.Contains(hint))
            orderedUc.Insert(0, hint);
        string uc = "none";
        foreach (string id in orderedUc)
        {
            string prefix = NovelAiPromptPresets.UcTags(sourceModel, id);
            if (prefix.Length == 0 || !negative.StartsWith(prefix, StringComparison.Ordinal)) continue;
            negative = negative[prefix.Length..];
            if (negative.StartsWith(", ", StringComparison.Ordinal)) negative = negative[2..];
            uc = id;
            break;
        }
        uc = MapUc(sourceModel, uc, targetModel);
        if (uc == "none") negative = nsfw + negative;
        return new ImportedPromptPresetMatch
        {
            PositivePrompt = string.Join("|", mix),
            NegativePrompt = negative,
            QualityMatched = selected is { Id: not "none" },
            QualityPreset = selected is { Id: not "none" } ? selected.Id : "standard",
            TransparentBackground = selected?.Transparent ?? false,
            UcPresetId = uc,
            UcPresetMatched = uc == "heavy" ? 0 : uc == "light" ? 1 : 2,
        };
    }

    private static List<QualityCandidate> QualityCandidates(string model, ImageMetadata metadata)
    {
        string[] ids = NovelAiPromptPresets.SupportsLightQuality(model) ? ["standard", "light"] : ["standard"];
        var normal = ids.Select(id => new QualityCandidate(id, NovelAiPromptPresets.QualityTags(model, id), false))
            .Where(candidate => candidate.Text.Length > 0).ToList();
        bool transparent = metadata.TagHintTransparentBackground == true;
        var result = new List<QualityCandidate>();
        if (transparent)
        {
            result.AddRange(normal.Select(WithTransparency));
            result.Add(WithTransparency(new QualityCandidate("none", "", false)));
        }
        result.AddRange(normal);
        if (metadata.QualityToggle == false)
            return transparent ? [WithTransparency(new QualityCandidate("none", "", false))] : [];
        var hinted = normal.FirstOrDefault(candidate => candidate.Id == metadata.QualityPreset);
        if (hinted != null)
        {
            result.Insert(0, hinted);
            if (transparent) result.Insert(0, WithTransparency(hinted));
        }
        return result;
    }

    private static QualityCandidate WithTransparency(QualityCandidate candidate) =>
        candidate with { Text = "transparent background" + (candidate.Text.Length > 0 ? ", " + candidate.Text : ""), Transparent = true };

    private static string? Detach(string prompt, string preset, bool prefix)
    {
        if (prompt == preset) return "";
        if (prefix && prompt.StartsWith(preset + ", ", StringComparison.Ordinal)) return prompt[(preset.Length + 2)..];
        if (!prefix && prompt.EndsWith(", " + preset, StringComparison.Ordinal)) return prompt[..^(preset.Length + 2)];
        return null;
    }

    private static string MapUc(string sourceModel, string id, string targetModel)
    {
        if (sourceModel == targetModel) return id;
        var available = NovelAiPromptPresets.UcIds(targetModel);
        if (available.Contains(id)) return id;
        string[] fallback = id switch
        {
            "humanFocus" or "furryFocus" or "heavy" => ["heavy", "light", "none"],
            "light" => ["light", "none", "heavy"],
            _ => ["none", "light", "heavy"],
        };
        return fallback.FirstOrDefault(available.Contains) ?? "none";
    }
}
