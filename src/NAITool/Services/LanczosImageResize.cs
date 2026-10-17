using System;
using SkiaSharp;

namespace NAITool.Services;

// Fixed-point Lanczos3 port of nodeca/pica 9.0.1 mm_resize.
// Copyright (C) 2014-2017 Vitaly Puzrin. MIT license:
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
internal static class LanczosImageResize
{
    private static (int Start, short[] Weights)[] Filters(int source, int target)
    {
        var result = new (int, short[])[target];
        double scale = (double)target / source, clamped = Math.Min(1, scale), window = 3 / clamped;
        for (int x = 0; x < target; x++)
        {
            double center = (x + .5) / scale;
            int first = Math.Max(0, (int)Math.Floor(center - window));
            int last = Math.Min(source - 1, (int)Math.Ceiling(center + window));
            var values = new float[last - first + 1];
            double total = 0;
            for (int j = 0; j < values.Length; j++)
            {
                double distance = Math.Abs((first + j + .5 - center) * clamped);
                double value = distance >= 3 ? 0 : distance < 1e-16 ? 1 :
                    Math.Sin(Math.PI * distance) / (Math.PI * distance) *
                    Math.Sin(Math.PI * distance / 3) / (Math.PI * distance / 3);
                values[j] = (float)value;
                total += value;
            }
            var weights = new short[values.Length];
            double normalizedTotal = 0;
            for (int j = 0; j < values.Length; j++)
            {
                double value = values[j] / total;
                normalizedTotal += value;
                weights[j] = (short)Math.Floor(value * 16383 + .5);
            }
            // Pica's typed-array compensation is ignored when this index is out of range.
            if ((target >> 1) < weights.Length)
                weights[target >> 1] += (short)Math.Floor((1 - normalizedTotal) * 16383 + .5);
            result[x] = (first, weights);
        }
        return result;
    }

    internal static SKBitmap Resize(SKBitmap source, int width, int height)
    {
        var output = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        if (source.Width == width && source.Height == height)
        {
            output.Pixels = source.Pixels;
            return output;
        }
        var pixels = source.Pixels;
        bool alpha = Array.Exists(pixels, p => p.Alpha != 255);
        var horizontal = Filters(source.Width, width);
        var vertical = Filters(source.Height, height);
        var intermediate = new ushort[width * source.Height * 4];
        for (int y = 0; y < source.Height; y++)
        for (int x = 0; x < width; x++)
        {
            var filter = horizontal[x];
            int r = 0, g = 0, b = 0, a = 0;
            for (int j = 0; j < filter.Weights.Length; j++)
            {
                var p = pixels[y * source.Width + filter.Start + j];
                int w = filter.Weights[j], multiplier = alpha ? p.Alpha : 1;
                unchecked
                {
                    r += w * p.Red * multiplier; g += w * p.Green * multiplier;
                    b += w * p.Blue * multiplier; a += w * p.Alpha;
                }
            }
            if (alpha) { r /= 255; g /= 255; b /= 255; }
            int index = (y * width + x) * 4;
            intermediate[index] = (ushort)Math.Max(0, r >> 7);
            intermediate[index + 1] = (ushort)Math.Max(0, g >> 7);
            intermediate[index + 2] = (ushort)Math.Max(0, b >> 7);
            intermediate[index + 3] = (ushort)Math.Max(0, a >> 7);
        }
        var destination = new SKColor[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            var filter = vertical[y];
            int r = 0, g = 0, b = 0, a = 0;
            for (int j = 0; j < filter.Weights.Length; j++)
            {
                int index = ((filter.Start + j) * width + x) * 4, w = filter.Weights[j];
                unchecked
                {
                    r += w * intermediate[index]; g += w * intermediate[index + 1];
                    b += w * intermediate[index + 2]; a += w * intermediate[index + 3];
                }
            }
            r >>= 7; g >>= 7; b >>= 7; a >>= 7;
            byte finalAlpha = Clamp((a + 8192) >> 14);
            if (alpha && finalAlpha > 0)
            {
                r = (int)((double)r * 255 / finalAlpha);
                g = (int)((double)g * 255 / finalAlpha);
                b = (int)((double)b * 255 / finalAlpha);
            }
            destination[y * width + x] = new SKColor(Clamp((r + 8192) >> 14),
                Clamp((g + 8192) >> 14), Clamp((b + 8192) >> 14), finalAlpha);
        }
        output.Pixels = destination;
        return output;
    }

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);
}
