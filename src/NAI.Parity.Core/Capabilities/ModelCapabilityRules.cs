namespace NAITool.Services;

/// <summary>Shared UI/request model capabilities. Unknown models are not opt-in.</summary>
public static class ModelCapabilityRules
{
    public static string BaseModel(string model) => model.EndsWith("-inpainting", System.StringComparison.Ordinal)
        ? model[..^"-inpainting".Length] : model;

    public static bool SupportsEffort(string model) => BaseModel(model) is "nai-diffusion-5-full" or "nai-diffusion-5-full-medium";
    public static string HighModel(string model) => model switch
    {
        "nai-diffusion-5-full-medium" => "nai-diffusion-5-full",
        "nai-diffusion-5-full-medium-inpainting" => "nai-diffusion-5-full-inpainting",
        _ => model,
    };
    public static double? GetSkipCfgAboveSigma(string model, int width, int height, bool variety)
    {
        if (!variety || !SupportsVariety(model) || width < 8 || height < 8) return null;
        double baseSigma = BaseModel(model).StartsWith("nai-diffusion-4-5", System.StringComparison.Ordinal) ? 58 : 19;
        return baseSigma * System.Math.Sqrt((double)(width / 8) * (height / 8) / (104 * 152));
    }

    public static bool SupportsSmea(string model) => BaseModel(model) is
        "nai-diffusion-3" or "nai-diffusion-furry-3";

    public static bool SupportsLegacyUc(string model) => BaseModel(model) is
        "nai-diffusion-4-full" or "nai-diffusion-4-curated" or "nai-diffusion-4-curated-preview";

    public static bool SupportsSmeaSampler(string sampler) => sampler is not
        ("ddim" or "plms" or "k_lms" or "nai_smea" or "nai_smea_dyn" or "ddim_v3");

    // V5 explicitly does not support Variety+. Exact request scaling is resolved
    // separately from the verified official request implementation.
    public static bool SupportsVariety(string model) => BaseModel(model) is
        "nai-diffusion-3" or "nai-diffusion-furry-3" or
        "nai-diffusion-4-full" or "nai-diffusion-4-curated" or
        "nai-diffusion-4-curated-preview" or "nai-diffusion-4-5-full" or
        "nai-diffusion-4-5-curated";
}
