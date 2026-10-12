namespace NAITool.Services;

/// <summary>Shared UI/request model capabilities. Unknown models are not opt-in.</summary>
internal static class ModelCapabilities
{
    internal static string BaseModel(string model) => model.EndsWith("-inpainting", System.StringComparison.Ordinal)
        ? model[..^"-inpainting".Length] : model;

    internal static bool SupportsEffort(string model) => BaseModel(model) is "nai-diffusion-5-full" or "nai-diffusion-5-full-medium";
    internal static bool IsMedium(NAIParameters p) => SupportsEffort(p.Model) && (p.Effort == "medium" || BaseModel(p.Model) == "nai-diffusion-5-full-medium");
    internal static string HighModel(string model) => model switch
    {
        "nai-diffusion-5-full-medium" => "nai-diffusion-5-full",
        "nai-diffusion-5-full-medium-inpainting" => "nai-diffusion-5-full-inpainting",
        _ => model,
    };
    internal static NAIParameters CreateRequestSnapshot(NAIParameters p)
    {
        var result = System.Text.Json.JsonSerializer.Deserialize<NAIParameters>(System.Text.Json.JsonSerializer.Serialize(p))!;
        if (IsMedium(p))
        {
            result.Model = HighModel(p.Model).Replace("nai-diffusion-5-full", "nai-diffusion-5-full-medium", System.StringComparison.Ordinal);
            result.Steps = 14;
            result.Sampler = "k_euler_ancestral";
            result.CfgRescale = 0;
            result.UcPresetId = "heavy";
        }
        if (NovelAiAnlasCalculator.IsV5Model(result.Model)) result.Schedule = "karras";
        result.UcPresetId = NovelAiPromptPresets.ResolveUcId(result);
        if (result.QualityPreset is not ("standard" or "light" or "none"))
            result.QualityPreset = "standard";
        if (!NovelAiPromptPresets.SupportsLightQuality(result.Model) && result.QualityPreset == "light")
            result.QualityPreset = "standard";
        if (!SupportsSmea(result.Model)) { result.Sm = false; result.SmDyn = false; }
        if (!SupportsVariety(result.Model)) result.Variety = false;
        return result;
    }

    internal static double? GetSkipCfgAboveSigma(string model, int width, int height, bool variety)
    {
        if (!variety || !SupportsVariety(model) || width < 8 || height < 8) return null;
        double baseSigma = BaseModel(model).StartsWith("nai-diffusion-4-5", System.StringComparison.Ordinal) ? 58 : 19;
        return baseSigma * System.Math.Sqrt((double)(width / 8) * (height / 8) / (104 * 152));
    }

    internal static bool SupportsSmea(string model) => BaseModel(model) is
        "nai-diffusion-3" or "nai-diffusion-furry-3";

    // V5 explicitly does not support Variety+. Exact request scaling is resolved
    // separately from the verified official request implementation.
    internal static bool SupportsVariety(string model) => BaseModel(model) is
        "nai-diffusion-3" or "nai-diffusion-furry-3" or
        "nai-diffusion-4-full" or "nai-diffusion-4-curated" or
        "nai-diffusion-4-curated-preview" or "nai-diffusion-4-5-full" or
        "nai-diffusion-4-5-curated";
}
