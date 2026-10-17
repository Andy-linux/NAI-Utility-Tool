namespace NAITool.Services;

public enum PreciseReferenceType
{
    CharacterAndStyle,
    Character,
    Style,
}

public sealed class VibeTransferInfo
{
    public string ImageBase64 { get; set; } = "";
    public double Strength { get; set; } = 0.6;
    public double InformationExtracted { get; set; } = 1.0;
    public string FileName { get; set; } = "";
    public bool IsEncoded { get; set; }
    public bool NormalizeStrength { get; set; } = true;
    public bool HasInformationExtracted { get; set; } = true;
    public string? EncodingModel { get; set; }

    public static string EncodingFamily(string model) => ImportedImageModel.ResolveForTarget(model,
        new[] { "nai-diffusion-4-full", "nai-diffusion-4-curated-preview", "nai-diffusion-4-5-full", "nai-diffusion-4-5-curated", "nai-diffusion-3", "nai-diffusion-furry-3" })
        ?? ModelCapabilities.HighModel(model).Replace("-inpainting", "");
    public static bool IsCompatibleEncoding(string? encodedModel, string model) =>
        encodedModel != null && EncodingFamily(encodedModel) == EncodingFamily(model);

    public static double DefaultInformationExtracted(string model) =>
        EncodingFamily(model) == "nai-diffusion-4-5-full" ? 0.7 : 1.0;
}

public sealed class PreciseReferenceInfo
{
    public string ImageBase64 { get; set; } = "";
    public PreciseReferenceType ReferenceType { get; set; } = PreciseReferenceType.CharacterAndStyle;
    public double Strength { get; set; } = 1.0;
    public double Fidelity { get; set; }
    public string FileName { get; set; } = "";
}
