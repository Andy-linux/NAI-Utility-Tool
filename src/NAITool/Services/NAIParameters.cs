using System.Text.Json.Serialization;

namespace NAITool.Services;

public class NAIParameters
{
    public const string DefaultGenerationModel = "nai-diffusion-5-full";
    public const string DefaultInpaintModel = "nai-diffusion-4-5-full-inpainting";
    public const string DefaultI2IDenoiseModel = "nai-diffusion-4-5-full";

    public string Model { get; set; } = DefaultGenerationModel;
    public string Sampler { get; set; } = "k_euler_ancestral";
    private string? _schedule;
    public string Schedule
    {
        get => _schedule ?? (ModelCapabilities.BaseModel(Model) is "nai-diffusion" or "safe-diffusion" or "nai-diffusion-furry" or "nai-diffusion-2" ? "native" : "karras");
        set => _schedule = value;
    }
    private double? _scale;
    public double Scale
    {
        get => _scale ?? ModelCapabilities.BaseModel(Model) switch
        {
            "nai-diffusion-5-full" or "nai-diffusion-5-full-medium" or "nai-diffusion-5-curated" => 7,
            "nai-diffusion-4-5-full" or "nai-diffusion-4-5-curated" or "nai-diffusion-3" => 5,
            "nai-diffusion-4-full" or "nai-diffusion-4-curated" or "nai-diffusion-4-curated-preview" => 5.5,
            "nai-diffusion-furry-3" => 6.2,
            "nai-diffusion" or "safe-diffusion" or "nai-diffusion-furry" or "nai-diffusion-2" => 10,
            _ => 3,
        };
        set => _scale = value;
    }
    public double CfgRescale { get; set; } = 0;
    public bool DynamicThresholding { get; set; } = false;
    public bool LegacyUc { get; set; }
    public bool Sm { get; set; }
    public bool SmDyn { get; set; }
    // Effort is a client-side choice; wire representation is resolved by ModelCapabilities.
    // High fields remain a draft while Medium is active.
    public string Effort { get; set; } = "high";
    public bool Variety { get; set; } = false;
    public bool QualityToggle { get; set; } = true;
    public string QualityPreset { get; set; } = "standard";
    [JsonIgnore]
    public string QualityPresetId { get => QualityToggle ? QualityPreset : "none"; set { QualityPreset = value == "none" ? "standard" : value; QualityToggle = value != "none"; } }
    public bool TagHintTransparentBackground { get; set; } = false;
    public bool StraightAlpha { get; set; } = true;
    private int? _steps;
    public int Steps
    {
        get => _steps ?? ModelCapabilities.BaseModel(Model) switch
        {
            "nai-diffusion-5-full-medium" => 14,
            "nai-diffusion-5-full" or "nai-diffusion-5-curated" or
            "nai-diffusion-4-5-full" or "nai-diffusion-4-5-curated" or
            "nai-diffusion-4-full" or "nai-diffusion-4-curated" or "nai-diffusion-4-curated-preview" or
            "nai-diffusion-3" or "nai-diffusion-furry-3" => 23,
            _ => 28,
        };
        set => _steps = value;
    }
    [JsonConverter(typeof(SeedTextJsonConverter))]
    public string Seed { get; set; } = "0";
    public int UcPreset { get; set; } = 0;
    public string? UcPresetId { get; set; }
    public double InpaintStrength { get; set; } = 1.0;
    public double InpaintNoise { get; set; }
    public double DenoiseStrength { get; set; } = 0.7;
    public double DenoiseNoise { get; set; } = 0;
}
