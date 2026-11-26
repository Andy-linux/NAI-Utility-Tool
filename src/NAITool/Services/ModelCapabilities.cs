namespace NAITool.Services;

/// <summary>Shared UI/request model capabilities. Unknown models are not opt-in.</summary>
internal static class ModelCapabilities
{
    internal static string BaseModel(string model) => ModelCapabilityRules.BaseModel(model);
    internal static bool SupportsEffort(string model) => ModelCapabilityRules.SupportsEffort(model);
    internal static bool IsMedium(NAIParameters p) => SupportsEffort(p.Model) && (p.Effort == "medium" || BaseModel(p.Model) == "nai-diffusion-5-full-medium");
    internal static string HighModel(string model) => ModelCapabilityRules.HighModel(model);
    internal static NAIParameters CreateRequestSnapshot(NAIParameters p, int preciseCardCount = 0, bool inpaint = false)
    {
        var result = System.Text.Json.JsonSerializer.Deserialize<NAIParameters>(System.Text.Json.JsonSerializer.Serialize(p))!;
        string preferred = IsMedium(p)
            ? HighModel(p.Model).Replace("nai-diffusion-5-full", "nai-diffusion-5-full-medium", System.StringComparison.Ordinal)
            : p.Model;
        inpaint |= preferred.EndsWith("-inpainting", System.StringComparison.Ordinal);
        var models = EffectiveGenerationResolver.Resolve(inpaint ? BaseModel(preferred) : preferred, preciseCardCount, inpaint);
        result.Model = models.RequestModel;
        if (SupportsEffort(result.Model))
            result.Effort = BaseModel(result.Model) == "nai-diffusion-5-full-medium" ? "medium" : "high";
        if (IsMedium(result))
        {
            result.Steps = 14;
            result.Sampler = "k_euler_ancestral";
            result.CfgRescale = 0;
            result.UcPresetId = "heavy";
        }
        // The website resolves legacy inpaint DDIM to Euler before applying capabilities.
        if (result.Model.EndsWith("-inpainting", System.StringComparison.Ordinal) &&
            SupportsSmea(result.Model) && result.Sampler is ("ddim" or "ddim_v3"))
            result.Sampler = "k_euler_ancestral";
        if (NovelAiAnlasCalculator.IsV5Model(result.Model)) result.Schedule = "karras";
        else
        {
            string[] schedules = result.Sampler switch
            {
                "k_dpm_2" => ["exponential", "polyexponential"],
                "k_euler" or "k_euler_ancestral" or "k_dpmpp_2s_ancestral" or
                "k_dpmpp_2m" or "k_dpmpp_2m_sde" or "k_dpmpp_sde" =>
                    NovelAIService.IsV4PlusModel(result.Model)
                        ? ["karras", "exponential", "polyexponential"]
                        : ["native", "karras", "exponential", "polyexponential"],
                _ => [],
            };
            if (!System.Linq.Enumerable.Contains(schedules, result.Schedule))
                result.Schedule = result.Sampler is "k_dpm_2" or "k_dpmpp_2m" ? "exponential"
                    : schedules.Length > 0 ? "karras" : "native";
        }
        result.UcPresetId = NovelAiPromptPresets.ResolveUcId(result);
        if (result.QualityPreset is not ("standard" or "light" or "none"))
            result.QualityPreset = "standard";
        if (!NovelAiPromptPresets.SupportsLightQuality(result.Model) && result.QualityPreset == "light")
            result.QualityPreset = "standard";
        if (!SupportsSmea(result.Model) || !SupportsSmeaSampler(result.Sampler)) { result.Sm = false; result.SmDyn = false; }
        if (!SupportsVariety(result.Model)) result.Variety = false;
        return result;
    }

    internal static double? GetSkipCfgAboveSigma(string model, int width, int height, bool variety) =>
        ModelCapabilityRules.GetSkipCfgAboveSigma(model, width, height, variety);
    internal static bool SupportsSmea(string model) => ModelCapabilityRules.SupportsSmea(model);
    internal static bool SupportsLegacyUc(string model) => ModelCapabilityRules.SupportsLegacyUc(model);
    internal static bool SupportsSmeaSampler(string sampler) => ModelCapabilityRules.SupportsSmeaSampler(sampler);
    internal static bool SupportsVariety(string model) => ModelCapabilityRules.SupportsVariety(model);
}
