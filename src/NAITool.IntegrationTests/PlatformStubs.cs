// Only unavailable UI/rendering surfaces are replaced; HTTP, settings, image decoding,
// stream parsing, account selection and response handling link production source.
namespace Microsoft.Graphics.Canvas
{
    public class CanvasRenderTarget
    {
        public (uint Width, uint Height) SizeInPixels => throw new NotSupportedException("Rendering is outside this console integration test.");
        public byte[] GetPixelBytes() => throw new NotSupportedException();
    }
}
namespace Windows.Storage.Streams { }
namespace NAITool.Services
{
    public static class UpscaleService
    {
        public const double DefaultTargetScale = 4;
        public static double NormalizeTargetScale(double value) => Math.Clamp(value, 1, 8);
    }
    public sealed class LocalizationService
    {
        public static LocalizationService Instance { get; } = new();
        public static string NormalizeLanguageCode(string? value) => value ?? "en_us";
        public string GetString(string key) => key;
        public string Format(string key, params object?[] args) => key + ": " + string.Join("; ", args);
    }
}
