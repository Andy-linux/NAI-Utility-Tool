using SkiaSharp;

internal static class TestImage
{
    public static byte[] CreatePng()
    {
        using var bitmap = new SKBitmap(64, 64, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        bitmap.Erase(SKColors.White);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }
}
