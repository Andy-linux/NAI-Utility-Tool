using SkiaSharp;
using System.Text;
using System.Buffers.Binary;
using System.IO.Compression;

namespace NAITool.Services;

public readonly record struct ReferenceBucket(int Width, int Height, int ResizeWidth, int ResizeHeight, int Left, int Top, bool Unchanged);

public static class ReferenceImageProcessor
{
    private static readonly (int Width, int Height)[] Buckets =
    [
        (256, 4096),
        (256, 4032),
        (256, 3968),
        (256, 3904),
        (256, 3840),
        (256, 3776),
        (256, 3712),
        (256, 3648),
        (256, 3584),
        (256, 3520),
        (256, 3456),
        (256, 3392),
        (256, 3328),
        (320, 3264),
        (320, 3200),
        (320, 3136),
        (320, 3072),
        (320, 3008),
        (320, 2944),
        (320, 2880),
        (320, 2816),
        (320, 2752),
        (384, 2688),
        (384, 2624),
        (384, 2560),
        (384, 2496),
        (384, 2432),
        (384, 2368),
        (448, 2304),
        (448, 2240),
        (448, 2176),
        (448, 2112),
        (512, 2048),
        (512, 1984),
        (512, 1920),
        (512, 1856),
        (576, 1792),
        (576, 1728),
        (576, 1664),
        (640, 1600),
        (640, 1536),
        (704, 1472),
        (704, 1408),
        (768, 1344),
        (768, 1280),
        (832, 1216),
        (896, 1152),
        (960, 1088),
        (1024, 1024),
        (1088, 960),
        (1152, 896),
        (1216, 832),
        (1280, 768),
        (1344, 768),
        (1408, 704),
        (1472, 704),
        (1536, 640),
        (1600, 640),
        (1664, 576),
        (1728, 576),
        (1792, 576),
        (1856, 512),
        (1920, 512),
        (1984, 512),
        (2048, 512),
        (2112, 448),
        (2176, 448),
        (2240, 448),
        (2304, 448),
        (2368, 384),
        (2432, 384),
        (2496, 384),
        (2560, 384),
        (2624, 384),
        (2688, 384),
        (2752, 320),
        (2816, 320),
        (2880, 320),
        (2944, 320),
        (3008, 320),
        (3072, 320),
        (3136, 320),
        (3200, 320),
        (3264, 320),
        (3328, 256),
        (3392, 256),
        (3456, 256),
        (3520, 256),
        (3584, 256),
        (3648, 256),
        (3712, 256),
        (3776, 256),
        (3840, 256),
        (3904, 256),
        (3968, 256),
        (4032, 256),
        (4096, 256)
    ];

    public static ReferenceBucket SelectBucket(int width, int height)
    {
        var ratio = Math.Log((double)width / height);
        var target = Buckets[0];
        double distance = double.PositiveInfinity;
        foreach (var bucket in Buckets)
        {
            double candidate = Math.Abs(Math.Log((double)bucket.Width / bucket.Height) - ratio);
            if (candidate < distance) { target = bucket; distance = candidate; }
        }
        if (target.Width == width && target.Height == height)
            return new(width, height, width, height, 0, 0, true);
        double scale = Math.Max((double)target.Width / width, (double)target.Height / height);
        int rw = (int)Math.Floor(width * scale + .5), rh = (int)Math.Floor(height * scale + .5);
        return new(target.Width, target.Height, rw, rh, (rw-target.Width)/2, (rh-target.Height)/2, false);
    }

    public static SKBitmap Decode(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("Unable to decode reference image.");
        var bitmap = new SKBitmap(new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success)
        {
            bitmap.Dispose();
            throw new InvalidDataException("Unable to decode reference image pixels.");
        }
        return bitmap;
    }

    // PNG scanlines retain RGB even where alpha is zero; Skia's encoder zeros those colors.
    private static byte[] Encode(SKBitmap bitmap)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137,80,78,71,13,10,26,10 });
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, bitmap.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), bitmap.Height);
        header[8] = 8; header[9] = 6;
        Chunk(output, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, true))
        {
            var bytes = bitmap.Bytes;
            for (int y = 0; y < bitmap.Height; y++)
            {
                zlib.WriteByte(0);
                zlib.Write(bytes, y * bitmap.RowBytes, bitmap.Width * 4);
            }
        }
        Chunk(output, "IDAT", compressed.ToArray());
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void Chunk(Stream output, string name, byte[] bytes)
    {
        Span<byte> value = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(value, bytes.Length);
        output.Write(value);
        var type = Encoding.ASCII.GetBytes(name);
        output.Write(type); output.Write(bytes);
        uint crc = 0xffffffff;
        foreach (var data in new[] { type, bytes })
        foreach (byte item in data)
        {
            crc ^= item;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
        }
        BinaryPrimitives.WriteUInt32BigEndian(value, ~crc);
        output.Write(value);
    }

    public static byte[] Clean(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xd8) return bytes;
        using var bitmap = Decode(bytes);
        if ((long)bitmap.Width * bitmap.Height > 0x1000000) return bytes;
        var pixels = bitmap.Pixels;
        var marker = Encoding.ASCII.GetBytes("stealth_pngcomp");
        if (pixels.Length < marker.Length * 8) return bytes;
        for (int i = 0; i < marker.Length; i++)
        {
            int value = 0;
            for (int bit = 0; bit < 8; bit++)
            {
                int index = i * 8 + bit;
                int x = index / bitmap.Height, y = index % bitmap.Height;
                value = (value << 1) | (pixels[y * bitmap.Width + x].Alpha & 1);
            }
            if (value != marker[i]) return bytes;
        }
        for (int i = 0; i < pixels.Length; i++)
        {
            var pixel = pixels[i];
            if (pixel.Alpha is 254 or 1)
                pixels[i] = pixel.WithAlpha(pixel.Alpha == 254 ? (byte)255 : (byte)0);
        }
        bitmap.Pixels = pixels;
        return Encode(bitmap);
    }

    public static byte[] Bucket(byte[] prepared)
    {
        using var source = Decode(prepared);
        var bucket = SelectBucket(source.Width, source.Height);
        if (bucket.Unchanged) return prepared;
        using var resized = LanczosImageResize.Resize(source, bucket.ResizeWidth, bucket.ResizeHeight);
        using var output = new SKBitmap(bucket.Width, bucket.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var pixels = new byte[bucket.Width * bucket.Height * 4];
        var input = resized.Bytes;
        for (int y=0; y<bucket.Height; y++)
            Array.Copy(input, (y+bucket.Top)*resized.RowBytes+bucket.Left*4, pixels, y*bucket.Width*4, bucket.Width*4);
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, output.GetPixels(), pixels.Length);
        return Encode(output);
    }

    public static byte[] PrepareV5(byte[] original) => Bucket(Clean(original));
}
