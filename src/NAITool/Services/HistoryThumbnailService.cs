using System.Runtime.InteropServices.WindowsRuntime;
using SkiaSharp;
using Windows.Graphics.Imaging;

namespace NAITool.Services;

public sealed record HistoryThumbnailPixels(int Width, int Height, byte[] Pixels);

public static class HistoryThumbnailService
{
    public static Task<HistoryThumbnailPixels> DecodeAsync(string path, int maxWidth, int maxHeight, CancellationToken token) => Task.Run(async () =>
    {
        token.ThrowIfCancellationRequested();
        if (Path.GetExtension(path).Equals(".webp", StringComparison.OrdinalIgnoreCase))
        {
            using var decoded = SKBitmap.Decode(path) ?? throw new InvalidDataException("WebP thumbnail decode failed");
            double factor = Math.Min(1, Math.Min(maxWidth / (double)decoded.Width, maxHeight / (double)decoded.Height));
            int w = Math.Max(1, (int)Math.Round(decoded.Width * factor));
            int h = Math.Max(1, (int)Math.Round(decoded.Height * factor));
            using var scaled = decoded.Resize(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            if (scaled == null) throw new InvalidDataException("WebP thumbnail resize failed");
            token.ThrowIfCancellationRequested();
            return new HistoryThumbnailPixels(w, h, scaled.GetPixelSpan().ToArray());
        }
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var stream = file.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token).ConfigureAwait(false);
        double scale = Math.Min(1, Math.Min(maxWidth / (double)decoder.PixelWidth, maxHeight / (double)decoder.PixelHeight));
        uint width = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale));
        uint height = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale));
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            new BitmapTransform { ScaledWidth = width, ScaledHeight = height, InterpolationMode = BitmapInterpolationMode.Fant },
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return new HistoryThumbnailPixels((int)width, (int)height, pixels.DetachPixelData());
    }, token);
}
