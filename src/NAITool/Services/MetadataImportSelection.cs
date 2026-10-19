using System.Collections.Generic;

namespace NAITool.Services;

internal sealed class MetadataImportSelection
{
    public bool ToI2I { get; init; }
    public bool Image { get; init; }
    public bool Prompt { get; init; } = true;
    public bool NegativePrompt { get; init; } = true;
    public bool Characters { get; init; } = true;
    public bool AppendCharacters { get; init; }
    public bool Model { get; init; } = true;
    public bool Settings { get; init; } = true;
    public bool Size { get; init; } = true;
    public bool Seed { get; init; } = true;
    public bool References { get; init; } = true;
    public bool ActualPrompt { get; init; }

    public bool HasSelection => (ToI2I && Image) || Prompt || NegativePrompt || Characters || Model || Settings || Size || Seed || References;

    public string PositivePromptFrom(ImageMetadata meta) =>
        ActualPrompt ? meta.ActualPositivePrompt ?? meta.PositivePrompt : meta.PositivePrompt;

    public string NegativePromptFrom(ImageMetadata meta) =>
        ActualPrompt ? meta.ActualNegativePrompt ?? meta.NegativePrompt : meta.NegativePrompt;

    public IReadOnlyList<string> CharacterPromptsFrom(ImageMetadata meta, bool negative = false)
    {
        var original = negative ? meta.CharacterNegativePrompts : meta.CharacterPrompts;
        var actual = negative ? meta.ActualCharacterNegativePrompts : meta.ActualCharacterPrompts;
        if (!ActualPrompt || !meta.HasActualPrompts)
            return original;

        var result = new List<string>(actual.Count);
        foreach (var prompt in actual)
            result.Add(prompt ?? "");
        return result;
    }

    public IReadOnlyList<(double X, double Y)> CharacterCentersFrom(ImageMetadata meta) =>
        ActualPrompt && meta.HasActualPrompts ? meta.ActualCharacterCenters : meta.CharacterCenters;
}
