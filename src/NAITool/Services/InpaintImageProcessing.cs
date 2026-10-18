using System;
using SkiaSharp;

namespace NAITool.Services;

internal static class InpaintImageProcessing
{
    internal static SKBitmap? DecodeClean(byte[] bytes) => DecodeClean(bytes, out _);

    private static SKBitmap? DecodeClean(byte[] bytes, out bool hasStealth)
    {
        hasStealth = false;
        using var codec = SKCodec.Create(SKData.CreateCopy(bytes));
        if (codec == null) return null;
        var bitmap = new SKBitmap(codec.Info.WithColorType(SKColorType.Rgba8888).WithAlphaType(SKAlphaType.Unpremul));
        codec.GetPixels(bitmap.Info, bitmap.GetPixels());
        const string marker = "stealth_pngcomp";
        if ((long)bitmap.Width * bitmap.Height < marker.Length * 8) return bitmap;
        for (int i = 0; i < marker.Length; i++)
        {
            int value = 0;
            for (int bit = 0; bit < 8; bit++)
            {
                int position = i * 8 + bit;
                value = (value << 1) | (bitmap.GetPixel(position / bitmap.Height, position % bitmap.Height).Alpha & 1);
            }
            if (value != marker[i]) return bitmap;
        }
        hasStealth = true;
        var pixels = bitmap.Pixels;
        for (int i = 0; i < pixels.Length; i++)
        {
            var p = pixels[i];
            pixels[i] = p.WithAlpha(p.Alpha == 254 ? (byte)255 : p.Alpha == 1 ? (byte)0 : p.Alpha);
        }
        bitmap.Pixels = pixels;
        return bitmap;
    }

    internal static byte[] PrepareDisplayBytes(byte[] bytes)
    {
        // Info's alpha bits are data, not transparency. Remove them before WinUI
        // premultiplies pixels so repeated canvas edits do not darken RGB values.
        using var bitmap = DecodeClean(bytes, out bool hasStealth);
        bool isWebp = ImageOutputNaming.Extension(bytes) == ".webp";
        if (bitmap == null || (!hasStealth && !isWebp)) return bytes;
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    internal static byte[] EncodeMask(SKBitmap source)
    {
        int width = source.Width / 8, height = source.Height / 8;
        using var mask = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var pixels = new SKColor[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            var pixel = source.GetPixel(x * 8 + 4, y * 8 + 4);
            pixels[y * width + x] = pixel.Alpha > 155 ? SKColors.White : SKColors.Transparent;
        }
        mask.Pixels = pixels;
        using var image = SKImage.FromBitmap(mask);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    internal static string PrepareApiMask(string reducedMaskBase64, int width, int height)
    {
        using var reduced = SKBitmap.Decode(Convert.FromBase64String(reducedMaskBase64));
        using var expanded = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var pixels = new SKColor[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int sourceX = Math.Min(reduced.Width - 1, (int)((x + .5) * reduced.Width / width));
            int sourceY = Math.Min(reduced.Height - 1, (int)((y + .5) * reduced.Height / height));
            var pixel = reduced.GetPixel(sourceX, sourceY);
            pixels[y * width + x] = pixel.Alpha == 0 ? SKColors.Black : SKColors.White;
        }
        expanded.Pixels = pixels;
        using var image = SKImage.FromBitmap(expanded);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return Convert.ToBase64String(png.ToArray());
    }

    internal static byte[] Composite(byte[] generated, string sourceBase64, string maskBase64)
    {
        using var source = DecodeClean(Convert.FromBase64String(sourceBase64));
        using var raw = DecodeClean(generated);
        using var mask = SKBitmap.Decode(Convert.FromBase64String(maskBase64));
        int width = source.Width, height = source.Height;
        var dilated = new byte[mask.Width * mask.Height];
        for (int y = 0; y < mask.Height; y++)
        for (int x = 0; x < mask.Width; x++)
        {
            var pixel = mask.GetPixel(x, y);
            if (pixel.Alpha == 0 || pixel.Red != 255) continue;
            for (int yy = Math.Max(0, y - 4); yy <= Math.Min(mask.Height - 1, y + 4); yy++)
            for (int xx = Math.Max(0, x - 4); xx <= Math.Min(mask.Width - 1, x + 4); xx++)
                dilated[yy * mask.Width + xx] = 255;
        }
        var feather = new byte[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            feather[y * width + x] = dilated[(y / 8) * mask.Width + x / 8];
        // Website worker uses two radius-20 box passes with fixed-point normalization.
        var horizontal = new int[feather.Length];
        for (int iteration = 0; iteration < 2; iteration++)
        {
            for (int y = 0; y < height; y++)
            {
                int sum = 0;
                for (int dx = -20; dx <= 20; dx++) sum += feather[y * width + Math.Clamp(dx, 0, width - 1)];
                for (int x = 0; x < width; x++)
                {
                    horizontal[y * width + x] = sum;
                    sum += feather[y * width + Math.Min(width - 1, x + 21)] - feather[y * width + Math.Max(0, x - 20)];
                }
            }
            for (int x = 0; x < width; x++)
            {
                int sum = 0;
                for (int dy = -20; dy <= 20; dy++) sum += horizontal[Math.Clamp(dy, 0, height - 1) * width + x];
                for (int y = 0; y < height; y++)
                {
                    feather[y * width + x] = (byte)((sum * 39) >> 16);
                    sum += horizontal[Math.Min(height - 1, y + 21) * width + x] - horizontal[Math.Max(0, y - 20) * width + x];
                }
            }
        }
        using var output = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var pixels = new SKColor[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            var foreground = raw.GetPixel(x, y);
            var background = source.GetPixel(x, y);
            double maskAlpha = feather[y * width + x] / 255.0;
            double alpha = Math.Floor(foreground.Alpha * maskAlpha + .5) / 255.0;
            double backgroundAlpha = Math.Floor(background.Alpha * (1 - maskAlpha) + .5) / 255.0;
            double combinedAlpha = alpha + backgroundAlpha;
            byte Channel(byte f, byte b) => combinedAlpha == 0 ? (byte)0 : (byte)Math.Clamp((int)Math.Round((f * alpha + b * backgroundAlpha) / combinedAlpha), 0, 255);
            pixels[y * width + x] = new SKColor(Channel(foreground.Red, background.Red), Channel(foreground.Green, background.Green),
                Channel(foreground.Blue, background.Blue), (byte)Math.Round(combinedAlpha * 255));
        }
        output.Pixels = pixels;
        using var image = SKImage.FromBitmap(output);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        // Pixel edits invalidate the raw result's signed_hash; the existing edited-image writer marks it unofficial.
        return ImageMetadataService.ReapplyNovelAiMetadata(png.ToArray(), generated, width, height);
    }
}
