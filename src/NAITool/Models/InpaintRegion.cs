using System;

namespace NAITool.Models;

// Canvas-pixel bounds; the website's mask selection uses these coordinates divided by eight.
public readonly record struct InpaintRegion(double X, double Y, double Width, double Height)
{
    public InpaintRegion? Clip(int canvasWidth, int canvasHeight)
    {
        double left = Math.Max(0, X), top = Math.Max(0, Y);
        double right = Math.Min(canvasWidth, X + Width), bottom = Math.Min(canvasHeight, Y + Height);
        return right > left && bottom > top ? new(left, top, right - left, bottom - top) : null;
    }

    public (int Width, int Height) RequestSize()
    {
        double pixels = Width * Height;
        if (pixels >= 0.8 * 1048576)
            return (Round(Width), Round(Height));
        double scale = Math.Sqrt(1048576 / pixels);
        return (Math.Max(64, (int)Math.Floor(Width * scale / 64) * 64),
            Math.Max(64, (int)Math.Floor(Height * scale / 64) * 64));
    }

    internal static int Round(double value) => (int)Math.Floor(value + .5);
}
