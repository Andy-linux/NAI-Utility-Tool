// Only platform/UI dependencies are stubbed. Settings, encryption, parameters and
// atomic file implementation are linked from production source and execute unchanged.
namespace NAITool.Services;

public static class LocalizationService
{
    public static string NormalizeLanguageCode(string value) => value.Trim().ToLowerInvariant();
}

public static class UpscaleService
{
    public const double DefaultTargetScale = 2;
    public static double NormalizeTargetScale(double value) => Math.Clamp(value, 1, 4);
}
