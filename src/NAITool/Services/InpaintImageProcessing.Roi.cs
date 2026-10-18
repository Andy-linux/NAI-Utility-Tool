using System;
using System.Collections.Generic;
using NAITool.Models;
using SkiaSharp;

namespace NAITool.Services;

internal sealed record InpaintRequestImage(
    string SourceBase64, string ImageBase64, string MaskBase64,
    int CanvasWidth, int CanvasHeight, int Width, int Height, InpaintRegion? Region);

internal static partial class InpaintImageProcessing
{
    internal static InpaintRequestImage PrepareRequest(string sourceBase64, string maskBase64, InpaintRegion? selection)
    {
        using var source = DecodeClean(Convert.FromBase64String(sourceBase64))
            ?? throw new ArgumentException("Invalid inpaint source image.");
        if (selection == null)
            return new(sourceBase64, sourceBase64, maskBase64,
                source.Width, source.Height, source.Width, source.Height, null);

        var selected = selection.Value;
        var region = new InpaintRegion(Math.Max(0, selected.X), Math.Max(0, selected.Y),
            Math.Min(selected.Width, source.Width - selected.X) - (selected.X > 0 ? 0 : selected.X),
            Math.Min(selected.Height, source.Height - selected.Y) - (selected.Y > 0 ? 0 : selected.Y));
        if (region.Width <= 0 || region.Height <= 0)
            throw new ArgumentException("The inpaint region does not overlap the canvas.");
        var size = region.RequestSize();
        using var crop = Crop(source, region.X, region.Y, region.Width, region.Height);
        using var resized = LanczosImageResize.Resize(crop, size.Width, size.Height);
        using var fullMask = DecodeClean(Convert.FromBase64String(maskBase64))
            ?? throw new ArgumentException("Invalid inpaint mask.");
        using var maskCrop = Crop(fullMask, region.X / 8, region.Y / 8, region.Width / 8, region.Height / 8);
        using var mask = ResizeNearest(maskCrop, size.Width / 8, size.Height / 8);
        var pixels = mask.Pixels;
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = pixels[i].Alpha > 155 ? SKColors.White : SKColors.Transparent;
        mask.Pixels = pixels;
        return new(sourceBase64, Convert.ToBase64String(EncodePng(resized)), Convert.ToBase64String(EncodePng(mask)),
            source.Width, source.Height, size.Width, size.Height, region);
    }

    internal static List<CharacterPromptInfo>? RemapCharacters(List<CharacterPromptInfo>? characters, InpaintRequestImage request)
    {
        if (characters == null || request.Region is not { } region) return characters;
        var result = new List<CharacterPromptInfo>(characters.Count);
        foreach (var character in characters)
            result.Add(new CharacterPromptInfo
            {
                PositivePrompt = character.PositivePrompt,
                NegativePrompt = character.NegativePrompt,
                UseCustomPosition = character.UseCustomPosition,
                CenterX = Math.Clamp((character.CenterX * request.CanvasWidth - region.X) / region.Width, 0, 1),
                CenterY = Math.Clamp((character.CenterY * request.CanvasHeight - region.Y) / region.Height, 0, 1),
            });
        return result;
    }

    internal static byte[] Composite(byte[] generated, InpaintRequestImage request)
    {
        if (request.Region is not { } region)
            return Composite(generated, request.SourceBase64, request.MaskBase64);
        using var source = DecodeClean(Convert.FromBase64String(request.SourceBase64))!;
        using var raw = DecodeClean(generated)!;
        using var mask = DecodeClean(Convert.FromBase64String(request.MaskBase64))!;
        var feather = CreateFeatherMask(mask, request.Width, request.Height);
        using var alphaMask = new SKBitmap(request.Width, request.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var maskPixels = new SKColor[feather.Length];
        var patchPixels = raw.Pixels;
        for (int i = 0; i < feather.Length; i++)
        {
            byte value = feather[i];
            maskPixels[i] = new SKColor(value, value, value, value);
            patchPixels[i] = patchPixels[i].WithAlpha((byte)InpaintRegion.Round(patchPixels[i].Alpha * value / 255.0));
        }
        raw.Pixels = patchPixels;
        alphaMask.Pixels = maskPixels;
        int width = Math.Max(1, InpaintRegion.Round(region.Width)), height = Math.Max(1, InpaintRegion.Round(region.Height));
        using var patch = LanczosImageResize.Resize(raw, width, height);
        using var resizedMask = LanczosImageResize.Resize(alphaMask, width, height);
        PlacePatch(source, patch, region, resizedMask);
        return ImageMetadataService.ReapplyNovelAiMetadata(EncodePng(source), generated, source.Width, source.Height);
    }

    internal static byte[] PlaceRawResult(byte[] generated, string sourceBase64, InpaintRegion region)
    {
        using var source = DecodeClean(Convert.FromBase64String(sourceBase64))!;
        using var raw = DecodeClean(generated)!;
        using var patch = LanczosImageResize.Resize(raw,
            Math.Max(1, InpaintRegion.Round(region.Width)), Math.Max(1, InpaintRegion.Round(region.Height)));
        PlacePatch(source, patch, region, null);
        return ImageMetadataService.ReapplyNovelAiMetadata(EncodePng(source), generated, source.Width, source.Height);
    }

    private static void PlacePatch(SKBitmap source, SKBitmap patch, InpaintRegion region, SKBitmap? mask)
    {
        var background = source.Pixels;
        var foreground = patch.Pixels;
        var maskPixels = mask?.Pixels;
        int left = InpaintRegion.Round(region.X), top = InpaintRegion.Round(region.Y);
        for (int y = 0; y < patch.Height; y++)
        for (int x = 0; x < patch.Width; x++)
        {
            int dx = left + x, dy = top + y;
            if (dx < 0 || dy < 0 || dx >= source.Width || dy >= source.Height) continue;
            int index = dy * source.Width + dx, patchIndex = y * patch.Width + x;
            var f = foreground[patchIndex];
            var b = background[index];
            double alpha = f.Alpha / 255.0;
            double backgroundAlpha = maskPixels == null
                ? b.Alpha / 255.0 * (1 - alpha)
                : InpaintRegion.Round(b.Alpha * (255 - maskPixels[patchIndex].Alpha) / 255.0) / 255.0;
            double combined = Math.Min(1, alpha + backgroundAlpha);
            if (combined == 0) continue;
            byte Channel(byte fc, byte bc) =>
                (byte)Math.Clamp(Math.Round((fc * alpha + bc * backgroundAlpha) / combined), 0, 255);
            background[index] = new SKColor(Channel(f.Red, b.Red), Channel(f.Green, b.Green), Channel(f.Blue, b.Blue),
                (byte)InpaintRegion.Round(255 * combined));
        }
        source.Pixels = background;
    }

    private static SKBitmap Crop(SKBitmap source, double x, double y, double width, double height)
    {
        int left = InpaintRegion.Round(x), top = InpaintRegion.Round(y);
        int w = Math.Max(1, InpaintRegion.Round(width)), h = Math.Max(1, InpaintRegion.Round(height));
        var result = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var pixels = new SKColor[w * h];
        for (int row = 0; row < h; row++)
        for (int col = 0; col < w; col++)
            if (left + col >= 0 && top + row >= 0 && left + col < source.Width && top + row < source.Height)
                pixels[row * w + col] = source.GetPixel(left + col, top + row);
        result.Pixels = pixels;
        return result;
    }

    private static SKBitmap ResizeNearest(SKBitmap source, int width, int height)
    {
        var result = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var pixels = new SKColor[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            pixels[y * width + x] = source.GetPixel(
                Math.Min(source.Width - 1, (int)((x + .5) * source.Width / width)),
                Math.Min(source.Height - 1, (int)((y + .5) * source.Height / height)));
        result.Pixels = pixels;
        return result;
    }

    private static byte[] EncodePng(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }
}
