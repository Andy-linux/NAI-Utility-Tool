using System;
using System.Collections.Generic;
using System.Linq;

namespace NAITool.Services;

internal static class EnhanceRules
{
    internal const long MaxPixels = 3L * 1024 * 1024;

    internal static (double Strength, double Noise) GetMagnitudeValues(int magnitude) =>
        Math.Clamp(magnitude, 1, 5) switch
        {
            1 => (0.2, 0.0),
            2 => (0.4, 0.0),
            3 => (0.5, 0.0),
            4 => (0.6, 0.0),
            _ => (0.7, 0.1),
        };

    internal static bool CanUseMax(string model, int width, int height) =>
        NovelAiAnlasCalculator.IsV5Model(model) && width > 0 && height > 0 &&
        (long)width * height < MaxPixels * 0.8;

    internal static (int Width, int Height) GetOutputDimensions(
        int width, int height, double amount, bool useMax = false)
    {
        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
            throw new ArgumentOutOfRangeException(nameof(width), "Enhance dimensions exceed the supported pixel range.");
        if (!double.IsFinite(amount) || amount is not (1.0 or 1.5 or 2.0))
            throw new ArgumentOutOfRangeException(nameof(amount));

        double scale = useMax
            ? Math.Min(2, Math.Sqrt((double)MaxPixels / ((long)width * height)))
            : amount;
        int outputWidth = (int)Math.Floor(width * scale);
        int outputHeight = (int)Math.Floor(height * scale);
        // The official generateEnhance dispatcher rounds ordinary dimensions to
        // the nearest 64 before resizing the input; ties round upward (85097.If).
        // Max sends the source dimensions with upscaled_enhance instead.
        return useMax ? (outputWidth, outputHeight) :
            (Math.Max(64, ((outputWidth + 32) / 64) * 64), Math.Max(64, ((outputHeight + 32) / 64) * 64));
    }

    internal static List<double> GetAvailableAmounts(int width, int height)
    {
        var amounts = new List<double>();
        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
            return amounts;

        foreach (double amount in new[] { 1.0, 1.5, 2.0 })
        {
            var dims = GetOutputDimensions(width, height, amount);
            // Imported source images need not be aligned; apply the official
            // request rounding before enforcing the actual output pixel cap.
            if ((long)dims.Width * dims.Height <= MaxPixels)
                amounts.Add(amount);
        }
        return amounts;
    }

    internal static double GetPreferredAmount(IReadOnlyList<double> available, double requested) =>
        available.OrderBy(value => Math.Abs(value - (double.IsFinite(requested) ? requested : 1.5))).First();
}
