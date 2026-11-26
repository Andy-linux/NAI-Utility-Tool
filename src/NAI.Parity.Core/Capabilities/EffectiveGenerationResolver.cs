namespace NAITool.Services;

public readonly record struct GenerationModels(string PreferredModel, string EffectiveModel, string RequestModel)
{
    public bool EffortOverriddenByPreciseReference => PreferredModel != EffectiveModel;
}

public static class EffectiveGenerationResolver
{
    public static GenerationModels Resolve(string preferredModel, int preciseReferenceCardCount, bool inpaint)
    {
        string effective = preferredModel == "nai-diffusion-5-full-medium" && preciseReferenceCardCount > 0 && !inpaint
            ? "nai-diffusion-5-full" : preferredModel;
        string request = inpaint ? InpaintingModel(effective) : effective;
        return new(preferredModel, effective, request);
    }

    public static string InpaintingModel(string model) => model switch
    {
            "nai-diffusion-3" => "nai-diffusion-3-inpainting",
            "nai-diffusion-furry-3" => "nai-diffusion-furry-3-inpainting",
            "nai-diffusion-4-curated-preview" => "nai-diffusion-4-curated-inpainting",
            "nai-diffusion-4-full" => "nai-diffusion-4-full-inpainting",
            "nai-diffusion-4-5-curated" => "nai-diffusion-4-5-curated-inpainting",
            "nai-diffusion-4-5-full" => "nai-diffusion-4-5-full-inpainting",
            "nai-diffusion-5-curated" => "nai-diffusion-4-5-curated-inpainting",
            "nai-diffusion-5-full" => "nai-diffusion-5-full-inpainting",
            "nai-diffusion-5-full-medium" => "nai-diffusion-5-full-medium-inpainting",
            _ => model,
    };
}
