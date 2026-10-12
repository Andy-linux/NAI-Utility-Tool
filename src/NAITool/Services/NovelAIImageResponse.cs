using System.IO.Compression;

namespace NAITool.Services;

/// <summary>Single-result contract: no arbitrary first-entry selection and no silently discarded results.</summary>
internal static class NovelAIImageResponse
{
    internal const int MaximumImageBytes = 64 * 1024 * 1024;
    public static async Task<byte[]> ReadBoundedAsync(Stream source, CancellationToken ct, int limit = MaximumImageBytes)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[81920];
        while (true)
        {
            int read = await source.ReadAsync(buffer, ct);
            if (read == 0) break;
            if (output.Length + read > limit) throw new InvalidDataException("Response exceeds the size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    public static async Task<byte[]> ReadSingleAsync(byte[] bytes, Func<byte[], bool> decodable, CancellationToken ct, Func<string, bool>? resultEntry = null)
    {
        if (bytes.Length > MaximumImageBytes) throw new InvalidDataException("Image response exceeds the size limit.");
        if (decodable(bytes)) return bytes;
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        if (archive.Entries.Count > 128) throw new InvalidDataException("ZIP contains too many entries.");
        long decompressedBytes = 0;
        byte[]? result = null;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name) || entry.Length == 0) continue;
            if (resultEntry != null && !resultEntry(entry.Name)) continue;
            if (!entry.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
                !entry.Name.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) &&
                !entry.Name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) &&
                !entry.Name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)) continue;
            decompressedBytes += entry.Length;
            if (entry.Length > MaximumImageBytes || decompressedBytes > MaximumImageBytes)
                throw new InvalidDataException("ZIP image data exceeds the total size limit.");
            using var source = entry.Open();
            var candidate = await ReadBoundedAsync(source, ct);
            if (!decodable(candidate)) continue;
            if (result != null) throw new InvalidDataException("Multiple image results are unsupported by this single-result operation.");
            result = candidate;
        }
        return result ?? throw new InvalidDataException("Response contains no decodable result image.");
    }
}
