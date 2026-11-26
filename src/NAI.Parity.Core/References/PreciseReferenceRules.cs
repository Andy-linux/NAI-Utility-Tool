namespace NAITool.Services;

public static class PreciseReferenceRules
{
    public static bool IsV45(string model) => ModelCapabilityRules.BaseModel(model) is
        "nai-diffusion-4-5-full" or "nai-diffusion-4-5-curated";
    public static bool IsV5(string model) => ModelCapabilityRules.BaseModel(model) is
        "nai-diffusion-5-full" or "nai-diffusion-5-full-medium" or "nai-diffusion-5-curated";
    public static bool SupportsRequest(string model) => IsV45(model) ||
        (IsV5(model) && !model.EndsWith("-inpainting", StringComparison.Ordinal));

    public static string Description(PreciseReferenceType type) => type switch
    {
        PreciseReferenceType.Character => "character",
        PreciseReferenceType.CharacterMainOutfit => "character_mainoutfit",
        PreciseReferenceType.Style => "style",
        PreciseReferenceType.Outfit => "costume",
        PreciseReferenceType.CharacterAndStyleAnyOutfit => "character&style_anyoutfit",
        _ => "character&style"
    };

    public static PreciseReferenceType Parse(string? description) => description?.ToLowerInvariant() switch
    {
        "character" => PreciseReferenceType.Character,
        "character_mainoutfit" or "charactermainoutfit" => PreciseReferenceType.CharacterMainOutfit,
        "style" => PreciseReferenceType.Style,
        "costume" or "outfit" => PreciseReferenceType.Outfit,
        "character&style_anyoutfit" or "characterandstyleanyoutfit" => PreciseReferenceType.CharacterAndStyleAnyOutfit,
        _ => PreciseReferenceType.CharacterAndStyle,
    };

    public static List<PreciseReferenceInfo> Filter(string model, IEnumerable<PreciseReferenceInfo>? cards)
    {
        var result = new List<PreciseReferenceInfo>();
        bool outfit = IsV5(model), fidelity = IsV45(model);
        foreach (var card in cards ?? [])
        {
            if (!card.Enabled || (!outfit && card.ReferenceType == PreciseReferenceType.Outfit)) continue;
            var type = card.ReferenceType;
            if (!outfit) type = type switch
            {
                PreciseReferenceType.CharacterMainOutfit => PreciseReferenceType.Character,
                PreciseReferenceType.CharacterAndStyleAnyOutfit => PreciseReferenceType.CharacterAndStyle,
                _ => type
            };
            result.Add(new PreciseReferenceInfo
            {
                ImageBase64 = card.ImageBase64, FileName = card.FileName,
                ReferenceType = type, Strength = card.Strength,
                Fidelity = fidelity ? card.Fidelity : 1.0
            });
        }
        return result;
    }

    public static List<PreciseReferenceInfo> ForRequest(string model, IEnumerable<PreciseReferenceInfo>? cards)
    {
        var effective = SupportsRequest(model) ? Filter(model, cards) : [];
        if (effective.Count > 16) throw new ArgumentException("A request supports at most 16 active precise references.");
        if (effective.Any(r => !double.IsFinite(r.Strength) || !double.IsFinite(r.Fidelity)))
            throw new ArgumentException("Reference strength and fidelity must be finite.");
        return effective;
    }
}
