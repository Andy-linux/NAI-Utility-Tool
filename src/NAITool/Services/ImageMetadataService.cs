using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using SkiaSharp;

namespace NAITool.Services;

public class ImageMetadata
{
    public string Model { get; set; } = "";
    public string ModelHash { get; set; } = "";
    public string ModelDisplayName => !string.IsNullOrWhiteSpace(Model) ? Model : Source ?? "";
    public bool HasPositivePrompt { get; set; }
    public bool HasNegativePrompt { get; set; }
    public bool HasCharacterPrompts { get; set; }
    public string PositivePrompt { get; set; } = "";
    public string NegativePrompt { get; set; } = "";
    public string? ActualPositivePrompt { get; set; }
    public string? ActualNegativePrompt { get; set; }
    public List<string?> ActualCharacterPrompts { get; set; } = new();
    public List<string?> ActualCharacterNegativePrompts { get; set; } = new();
    public List<string> CharacterPrompts { get; set; } = new();
    public List<string> CharacterNegativePrompts { get; set; } = new();
    public List<(double X, double Y)> CharacterCenters { get; set; } = new();
    public bool? UseCharacterCoordinates { get; set; }
    public List<VibeTransferInfo> VibeTransfers { get; set; } = new();
    public List<PreciseReferenceInfo> PreciseReferences { get; set; } = new();
    public int Width { get; set; }
    public int Height { get; set; }
    public int Steps { get; set; }
    public double Scale { get; set; }
    public bool HasScale { get; set; }
    public double CfgRescale { get; set; }
    public bool HasCfgRescale { get; set; }
    public bool DynamicThresholding { get; set; }
    public bool? LegacyUc { get; set; }
    public string? Seed { get; set; }
    public string Sampler { get; set; } = "";
    public string NoiseSchedule { get; set; } = "";
    public bool Sm { get; set; }
    public bool SmDyn { get; set; }
    public double? SkipCfgAboveSigma { get; set; }
    public bool Variety => SkipCfgAboveSigma is > 0;
    public string? Software { get; set; }
    public string? Source { get; set; }
    public Dictionary<string, string> TextChunks { get; set; } = new(StringComparer.Ordinal);
    public bool? QualityToggle { get; set; }
    public string? QualityPreset { get; set; }
    public int? UcPreset { get; set; }
    public string? UcPresetId { get; set; }
    public string? Effort { get; set; }
    public bool? TagHintTransparentBackground { get; set; }
    public bool? StraightAlpha { get; set; }
    public string RawJson { get; set; } = "";
    public bool IsNaiParsed { get; set; }
    public bool IsSdFormat { get; set; }
    public bool IsModelInference { get; set; }
}

public static class ImageMetadataService
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private const string StealthMagicCompressed = "stealth_pngcomp";
    private const string StealthMagicPlain = "stealth_pnginfo";

    public static ImageMetadata? ReadFromFile(string filePath)
    {
        if (!File.Exists(filePath)) return null;
        try
        {
            var bytes = File.ReadAllBytes(filePath);
            return ReadFromBytes(bytes);
        }
        catch { return null; }
    }

    public static ImageMetadata? ReadFromBytes(byte[] data)
    {
        var textChunks = ReadGenerationTextChunks(data);
        if (textChunks.TryGetValue("Comment", out var json))
        {
            var meta = ParseMetadataJson(json);
            if (meta != null)
            {
                ApplyMetadataContext(meta, textChunks);
                return meta;
            }
        }
        if (textChunks.TryGetValue("parameters", out var sdText))
        {
            var sdMeta = TryParseSdFormat(sdText);
            if (sdMeta != null)
            {
                sdMeta.Source = textChunks.GetValueOrDefault("Source");
                sdMeta.TextChunks = new Dictionary<string, string>(textChunks, StringComparer.Ordinal);
                return sdMeta;
            }
        }
        if (json != null)
        {
            return new ImageMetadata
            {
                RawJson = json,
                IsNaiParsed = false,
                Software = textChunks.GetValueOrDefault("Software"),
                Source = textChunks.GetValueOrDefault("Source"),
                TextChunks = new Dictionary<string, string>(textChunks, StringComparer.Ordinal),
            };
        }
        if (textChunks.Count > 0)
        {
            var sb = new StringBuilder();
            foreach (var kvp in textChunks)
                sb.AppendLine($"{kvp.Key}: {kvp.Value}");
            return new ImageMetadata
            {
                RawJson = sb.ToString().TrimEnd(),
                IsNaiParsed = false,
                Source = textChunks.GetValueOrDefault("Source"),
                TextChunks = new Dictionary<string, string>(textChunks, StringComparer.Ordinal),
            };
        }
        return null;
    }

    public static Dictionary<string, string> ReadGenerationTextChunks(byte[] data)
    {
        var textChunks = ReadPngTextChunks(data);
        if (textChunks.Count > 0) return textChunks;
        textChunks = ReadExifTextChunks(data);
        return textChunks.Count > 0 ? textChunks : TryReadStealthTextChunks(data);
    }

    public static Dictionary<string, string> ReadConversionTextChunks(byte[] data)
    {
        var textChunks = TryReadStealthTextChunks(data);
        foreach (var pair in ReadExifTextChunks(data)) textChunks[pair.Key] = pair.Value;
        return textChunks;
    }

    private static Dictionary<string, string> ReadExifTextChunks(byte[] data)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var exif = FindExifData(data);
            if (exif == null) return result;
            int start = exif.Length >= 6 && Encoding.ASCII.GetString(exif, 0, 6) == "Exif\0\0" ? 6 : 0;
            if (exif.Length - start < 8) return result;
            bool little = exif[start] == 'I' && exif[start + 1] == 'I';
            if (!little && !(exif[start] == 'M' && exif[start + 1] == 'M')) return result;
            ushort U16(int p) => little ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(exif.AsSpan(p, 2)) : System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(exif.AsSpan(p, 2));
            uint U32(int p) => little ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(exif.AsSpan(p, 4)) : System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(exif.AsSpan(p, 4));
            void ReadIfd(int position, bool nested)
            {
                int count = U16(position);
                for (int i = 0; i < count; i++)
                {
                    int p = checked(position + 2 + 12 * i);
                    ushort tag = U16(p);
                    if (tag == 0x8769 && !nested) ReadIfd(checked(start + (int)U32(p + 8)), true);
                    if (tag != 0x9286) continue;
                    int size = checked((int)U32(p + 4));
                    int value = size <= 4 ? p + 8 : checked(start + (int)U32(p + 8));
                    if (size < 8 || value < 0 || size > exif.Length - value) continue;
                    string header = Encoding.ASCII.GetString(exif, value, 8);
                    string json;
                    if (header == "UNICODE\0")
                    {
                        bool unicodeLittle = size >= 10 && exif[value + 8] == 255 && exif[value + 9] == 254;
                        json = (unicodeLittle ? Encoding.Unicode : Encoding.BigEndianUnicode)
                            .GetString(exif, value + 8, size - 8).TrimStart('\uFEFF');
                    }
                    else
                    {
                        int skip = header is "ASCII\0\0\0" or "\0\0\0\0\0\0\0\0" ? 8 : 0;
                        json = Encoding.UTF8.GetString(exif, value + skip, size - skip);
                    }
                    using var doc = JsonDocument.Parse(json.TrimEnd('\0'));
                    foreach (var field in doc.RootElement.EnumerateObject())
                        result[field.Name] = field.Value.ValueKind == JsonValueKind.String
                            ? field.Value.GetString()!.Replace("\0", "", StringComparison.Ordinal)
                            : field.Value.ValueKind == JsonValueKind.Null ? "" : field.Value.GetRawText();
                }
            }
            ReadIfd(checked(start + (int)U32(start + 4)), false);
        }
        catch { /* Unreadable EXIF falls back to the image's stealth carrier. */ }
        return result;
    }

    private static byte[]? FindExifData(byte[] data)
    {
        if (data.Length >= 12 && Encoding.ASCII.GetString(data, 0, 4) == "RIFF" &&
            Encoding.ASCII.GetString(data, 8, 4) == "WEBP")
        {
            for (int offset = 12; offset + 8 <= data.Length;)
            {
                int length = checked((int)BitConverter.ToUInt32(data, offset + 4));
                if (length > data.Length - offset - 8) break;
                if (Encoding.ASCII.GetString(data, offset, 4) == "EXIF")
                    return data.AsSpan(offset + 8, length).ToArray();
                offset = checked(offset + 8 + length + (length & 1));
            }
        }
        else if (data.Length >= 8 && data.AsSpan(0, 8).SequenceEqual(PngSignature))
        {
            for (int offset = 8; offset + 12 <= data.Length;)
            {
                int length = ReadInt32BE(data, offset);
                if (length < 0 || length > data.Length - offset - 12) break;
                if (Encoding.ASCII.GetString(data, offset + 4, 4) == "eXIf")
                    return data.AsSpan(offset + 8, length).ToArray();
                offset += 12 + length;
            }
        }
        else if (data.Length >= 2 && data[0] == 255 && data[1] == 216)
        {
            int offset = 2;
            while (offset + 4 <= data.Length && data[offset] == 255)
            {
                byte marker = data[offset + 1];
                if (marker is 0xDA or 0xD9) break;
                int length = (data[offset + 2] << 8) | data[offset + 3];
                if (length < 2 || length > data.Length - offset - 2) break;
                if (marker == 0xE1 && length >= 8 && Encoding.ASCII.GetString(data, offset + 4, 6) == "Exif\0\0")
                    return data.AsSpan(offset + 4, length - 2).ToArray();
                offset += 2 + length;
            }
        }
        return null;
    }

    public static Dictionary<string, string> ReadRoundTripTextChunks(byte[] data)
    {
        return new Dictionary<string, string>(ReadGenerationTextChunks(data), StringComparer.Ordinal);
    }

    public static Dictionary<string, string> ReadPngTextChunks(byte[] data)
    {
        var result = new Dictionary<string, string>();
        if (data.Length < 8) return result;

        for (int i = 0; i < 8; i++)
            if (data[i] != PngSignature[i]) return result;

        int offset = 8;
        while (offset + 12 <= data.Length)
        {
            int length = ReadInt32BE(data, offset);
            if (length < 0 || offset + 12 + length > data.Length) break;

            string type = Encoding.ASCII.GetString(data, offset + 4, 4);
            int dataStart = offset + 8;

            if (type == "tEXt" && length > 0)
            {
                int nullPos = Array.IndexOf(data, (byte)0, dataStart, length);
                if (nullPos >= 0)
                {
                    string key = Encoding.Latin1.GetString(data, dataStart, nullPos - dataStart);
                    string value = Encoding.Latin1.GetString(data, nullPos + 1, length - (nullPos - dataStart) - 1);
                    result[key] = value;
                }
            }
            else if (type == "iTXt" && length > 0)
            {
                int nullPos = Array.IndexOf(data, (byte)0, dataStart, length);
                if (nullPos >= 0 && nullPos + 2 < dataStart + length)
                {
                    string key = Encoding.UTF8.GetString(data, dataStart, nullPos - dataStart);
                    byte compFlag = data[nullPos + 1];
                    int cur = nullPos + 3;
                    int langEnd = Array.IndexOf(data, (byte)0, cur, dataStart + length - cur);
                    if (langEnd >= 0)
                    {
                        cur = langEnd + 1;
                        int tkEnd = Array.IndexOf(data, (byte)0, cur, dataStart + length - cur);
                        if (tkEnd >= 0)
                        {
                            cur = tkEnd + 1;
                            int textLen = dataStart + length - cur;
                            if (textLen > 0 && compFlag == 0)
                                result[key] = Encoding.UTF8.GetString(data, cur, textLen);
                        }
                    }
                }
            }

            if (type == "IEND") break;
            offset += 12 + length;
        }

        return result;
    }

    private static int ReadInt32BE(byte[] data, int offset)
        => (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];

    // ─── SD WebUI format parsing ───

    private static readonly Dictionary<string, string> SdSamplerMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Euler"] = "k_euler",
        ["Euler a"] = "k_euler_ancestral",
        ["DPM++ 2M"] = "k_dpmpp_2m",
        ["DPM++ SDE"] = "k_dpmpp_sde",
        ["DPM++ 2M SDE"] = "k_dpmpp_2m_sde",
        ["DPM++ 2S a"] = "k_dpmpp_2s_ancestral",
        ["DDIM"] = "ddim_v3",
    };

    private static readonly Dictionary<string, string> SdScheduleMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Karras"] = "karras",
        ["Normal"] = "native",
        ["Simple"] = "native",
        ["Exponential"] = "exponential",
        ["SGM Uniform"] = "native",
    };

    public static ImageMetadata? TryParseSdFormat(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var meta = new ImageMetadata { IsSdFormat = true, IsNaiParsed = false, RawJson = text };

        int negIdx = text.IndexOf("\nNegative prompt:", StringComparison.Ordinal);
        if (negIdx < 0) negIdx = text.IndexOf("Negative prompt:", StringComparison.Ordinal);

        int paramsLineIdx = text.LastIndexOf("\nSteps:", StringComparison.Ordinal);
        if (paramsLineIdx < 0) paramsLineIdx = text.LastIndexOf("Steps:", StringComparison.Ordinal);
        if (paramsLineIdx < 0) return null;

        if (negIdx >= 0 && negIdx < paramsLineIdx)
        {
            meta.HasPositivePrompt = true;
            meta.HasNegativePrompt = true;
            meta.PositivePrompt = text[..negIdx].Trim();
            int negStart = text.IndexOf(':', negIdx) + 1;
            meta.NegativePrompt = text[negStart..paramsLineIdx].Trim();
        }
        else
        {
            meta.HasPositivePrompt = true;
            meta.PositivePrompt = text[..paramsLineIdx].Trim();
            meta.NegativePrompt = "";
        }

        string paramsLine = text[paramsLineIdx..].Trim();
        meta.Model = ExtractSdString(paramsLine, @"(?:^|,\s*)Model:\s*([^,\r\n]+)");
        meta.Steps = ExtractSdInt(paramsLine, @"Steps:\s*(\d+)");
        meta.Seed = ExtractSdString(paramsLine, @"(?:^|,\s*)Seed:\s*([^,\r\n]+)");
        meta.Scale = ExtractSdDouble(paramsLine, @"CFG scale:\s*([\d.]+)");
        meta.HasScale = System.Text.RegularExpressions.Regex.IsMatch(paramsLine, @"CFG scale:\s*([\d.]+)");

        var sizeMatch = System.Text.RegularExpressions.Regex.Match(paramsLine, @"Size:\s*(\d+)x(\d+)");
        if (sizeMatch.Success)
        {
            meta.Width = int.Parse(sizeMatch.Groups[1].Value);
            meta.Height = int.Parse(sizeMatch.Groups[2].Value);
        }

        string sdSampler = ExtractSdString(paramsLine, @"Sampler:\s*([^,]+)");
        string sdSchedule = ExtractSdString(paramsLine, @"Schedule type:\s*([^,]+)");

        if (string.IsNullOrEmpty(sdSchedule))
        {
            foreach (var suffix in new[] { " Karras", " Exponential" })
            {
                if (sdSampler.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    sdSchedule = suffix.Trim();
                    sdSampler = sdSampler[..^suffix.Length];
                    break;
                }
            }
        }

        meta.Sampler = SdSamplerMap.GetValueOrDefault(sdSampler, sdSampler);
        meta.NoiseSchedule = SdScheduleMap.GetValueOrDefault(sdSchedule, sdSchedule);

        return meta;
    }

    private static int ExtractSdInt(string s, string pattern)
    {
        var m = System.Text.RegularExpressions.Regex.Match(s, pattern);
        return m.Success && int.TryParse(m.Groups[1].Value, out int v) ? v : 0;
    }

    private static double ExtractSdDouble(string s, string pattern)
    {
        var m = System.Text.RegularExpressions.Regex.Match(s, pattern);
        return m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 0;
    }

    private static string ExtractSdString(string s, string pattern)
    {
        var m = System.Text.RegularExpressions.Regex.Match(s, pattern);
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }

    /// <summary>
    /// Converts an SD-format prompt for use in NAI:
    /// - Strips lora tags: &lt;lora:name:weight&gt;
    /// - Converts SD weight (prompt:weight) to NAI v4 format weight::prompt::
    /// </summary>
    public static string ConvertSdPromptToNai(string prompt)
    {
        string result = System.Text.RegularExpressions.Regex.Replace(prompt, @"<lora:[^>]+>", "");
        result = System.Text.RegularExpressions.Regex.Replace(result, @",\s*,", ",");
        result = result.Trim(' ', ',');

        result = System.Text.RegularExpressions.Regex.Replace(result, @"\(([^()]+):([\d.]+)\)", m =>
        {
            string inner = m.Groups[1].Value;
            string weight = m.Groups[2].Value;
            return $"{weight}::{inner}::";
        });

        return result.Trim();
    }

    public static ImageMetadata? TryParseJson(string json, Dictionary<string, string>? textChunks = null)
    {
        var meta = ParseMetadataJson(json);
        if (meta != null && textChunks != null)
            ApplyMetadataContext(meta, textChunks);
        return meta;
    }

    private static void ApplyMetadataContext(ImageMetadata meta, Dictionary<string, string> textChunks)
    {
        meta.IsNaiParsed = true;
        meta.Software = textChunks.GetValueOrDefault("Software");
        meta.Source = textChunks.GetValueOrDefault("Source") ?? meta.Source;
        string hash = meta.ModelHash;
        if (string.IsNullOrEmpty(hash) &&
            ImportedImageModel.ResolveForTarget(meta.Source, new[] { "nai-diffusion-5-full", "nai-diffusion-5-curated" }) != null)
            hash = meta.Source!.Split(' ')[^1];
        switch (hash.ToUpperInvariant())
        {
            // Official V5 image import maps these hashes to their model variants.
            case "70AB5786":
            case "93F4BD30":
                meta.Model = "nai-diffusion-5-full";
                meta.Effort = "medium";
                break;
            case "0ADF9AB7":
            case "657484A5":
                meta.Model = "nai-diffusion-5-full";
                meta.Effort = "high";
                break;
            case "DB276663":
                meta.Model = "nai-diffusion-5-curated";
                meta.Effort = "high";
                break;
        }
        foreach (var vibe in meta.VibeTransfers)
        {
            vibe.EncodingModel = VibeTransferInfo.EncodingFamily(meta.ModelDisplayName);
            vibe.IsEncoded = NovelAIService.IsV4PlusModel(vibe.EncodingModel);
        }
        meta.TextChunks = new Dictionary<string, string>(textChunks, StringComparer.Ordinal);
        meta.TextChunks["Comment"] = meta.RawJson;
    }

    public static string PrettyPrintJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                WriteSorted(writer, doc.RootElement);
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch { return json; }
    }

    public static string CompactJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                doc.RootElement.WriteTo(writer);
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch { return json; }
    }

    private static void WriteSorted(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(prop.Name);
                    WriteSorted(writer, prop.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteSorted(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    public static byte[] ReplacePngComment(byte[] pngData, string? newComment)
    {
        var textChunks = ReadGenerationTextChunks(pngData);
        if (newComment == null)
            textChunks.Remove("Comment");
        else
        {
            textChunks["Comment"] = newComment;
            if (TryParseJson(newComment) is { HasPositivePrompt: true } metadata)
                textChunks["Description"] = metadata.PositivePrompt;
        }
        using var bitmap = DecodeRgbaUnpremulBitmap(pngData);
        if (bitmap == null) throw new ArgumentException("Invalid PNG image.", nameof(pngData));
        return WritePngTextChunks(RemoveNovelAiStealthMetadata(pngData), textChunks, transplant: false);
    }

    public static byte[] ReplacePngTextChunks(byte[] pngData, IReadOnlyDictionary<string, string>? textChunks) =>
        WritePngTextChunks(pngData, textChunks, transplant: false);

    private static byte[] WritePngTextChunks(byte[] pngData, IReadOnlyDictionary<string, string>? textChunks, bool transplant)
    {
        if (pngData.Length < PngSignature.Length || !pngData.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
            throw new ArgumentException("PNG metadata editing requires a PNG image.", nameof(pngData));
        using var output = new MemoryStream();
        output.Write(PngSignature, 0, 8);
        int offset = 8;
        while (offset + 12 <= pngData.Length)
        {
            int length = ReadInt32BE(pngData, offset);
            if (length < 0 || length > pngData.Length - offset - 12) break;
            string type = Encoding.ASCII.GetString(pngData, offset + 4, 4);
            int chunkTotalLen = 12 + length;
            bool remove = !transplant && type is "tEXt" or "iTXt" or "zTXt" or "eXIf";
            if (transplant && type is "tEXt" or "iTXt")
            {
                int end = Array.IndexOf(pngData, (byte)0, offset + 8, length);
                string? key = end >= 0 ? Encoding.Latin1.GetString(pngData, offset + 8, end - offset - 8) : null;
                remove = key != null && textChunks?.ContainsKey(key) == true;
            }
            if (!remove)
            {
                output.Write(pngData, offset, chunkTotalLen);
                if (type == "IHDR" && textChunks != null)
                    foreach (var pair in textChunks) WriteTextChunk(output, pair.Key, pair.Value, transplant);
                if (type == "IEND") break;
            }
            offset += chunkTotalLen;
        }
        return output.ToArray();
    }

    public static byte[] StripPngMetadata(byte[] pngData)
    {
        using var output = new MemoryStream();
        output.Write(PngSignature, 0, 8);

        int offset = 8;
        while (offset + 12 <= pngData.Length)
        {
            int length = ReadInt32BE(pngData, offset);
            if (length < 0 || offset + 12 + length > pngData.Length) break;

            string type = Encoding.ASCII.GetString(pngData, offset + 4, 4);
            int chunkTotalLen = 12 + length;

            // Strip common metadata-bearing ancillary chunks.
            bool shouldStrip = type is "tEXt" or "iTXt" or "zTXt" or "eXIf";
            if (!shouldStrip)
                output.Write(pngData, offset, chunkTotalLen);

            if (type == "IEND") break;
            offset += chunkTotalLen;
        }

        byte[] stripped = output.ToArray();
        return RemoveNovelAiStealthMetadata(stripped);
    }

    public static byte[] ReapplyNovelAiMetadata(byte[] pngData, byte[]? metadataSourceBytes, int imageWidth, int imageHeight)
    {
        if (pngData.Length == 0 || metadataSourceBytes == null || metadataSourceBytes.Length == 0)
            return pngData;
        byte[] result = TransplantStealthAlpha(pngData, metadataSourceBytes);
        var textChunks = ReadPngTextChunks(metadataSourceBytes);
        return textChunks.Count == 0 ? result : WritePngTextChunks(result, textChunks, transplant: true);
    }

    public static byte[] ReapplyNovelAiMetadata(byte[] pngData, IReadOnlyDictionary<string, string>? textChunks) =>
        textChunks == null || textChunks.Count == 0 ? pngData : WritePngTextChunks(pngData, textChunks, transplant: true);

    public static byte[] ReapplyNovelAiMetadata(byte[] pngData, IReadOnlyDictionary<string, string>? textChunks,
        int imageWidth, int imageHeight) => ReapplyNovelAiMetadata(pngData, textChunks);

    private static byte[] TransplantStealthAlpha(byte[] pngData, byte[] sourceBytes)
    {
        if (TryExtractStealthJsonText(sourceBytes) == null) return pngData;
        using var source = DecodeRgbaUnpremulBitmap(sourceBytes);
        using var target = DecodeRgbaUnpremulBitmap(pngData);
        if (source == null || target == null) return pngData;
        var sourcePixels = source.Pixels;
        var targetPixels = target.Pixels;
        int count = Math.Min(sourcePixels.Length, targetPixels.Length);
        for (int i = 0; i < count; i++)
        {
            int from = (i % source.Height) * source.Width + i / source.Height;
            int to = (i % target.Height) * target.Width + i / target.Height;
            var color = targetPixels[to];
            targetPixels[to] = color.WithAlpha((byte)((color.Alpha & 254) | (sourcePixels[from].Alpha & 1)));
        }
        target.Pixels = targetPixels;
        using var image = SKImage.FromBitmap(target);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private static byte[] RemoveNovelAiStealthMetadata(byte[] pngData)
    {
        if (TryExtractStealthJsonText(pngData) == null)
            return pngData;

        try
        {
            using var bitmap = DecodeRgbaUnpremulBitmap(pngData);
            if (bitmap == null) return pngData;

            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    var color = bitmap.GetPixel(x, y);
                    bitmap.SetPixel(x, y, color.WithAlpha(color.Alpha == 254 ? (byte)255 : color.Alpha == 1 ? (byte)0 : color.Alpha));
                }
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data?.ToArray() ?? pngData;
        }
        catch
        {
            return pngData;
        }
    }

    private static void WriteTextChunk(Stream output, string keyword, string text, bool international = false)
    {
        byte[] keyBytes = (international ? Encoding.UTF8 : Encoding.Latin1).GetBytes(keyword.Length > 79 ? keyword[..79] : keyword);
        if (!international && CanEncodeLatin1(text))
        {
            byte[] textBytes = Encoding.Latin1.GetBytes(text);
            int dataLen = keyBytes.Length + 1 + textBytes.Length;

            WriteInt32BE(output, dataLen);
            byte[] typeBytes = Encoding.ASCII.GetBytes("tEXt");
            output.Write(typeBytes, 0, 4);
            output.Write(keyBytes, 0, keyBytes.Length);
            output.WriteByte(0);
            output.Write(textBytes, 0, textBytes.Length);

            byte[] chunkData = new byte[4 + dataLen];
            Array.Copy(typeBytes, 0, chunkData, 0, 4);
            Array.Copy(keyBytes, 0, chunkData, 4, keyBytes.Length);
            chunkData[4 + keyBytes.Length] = 0;
            Array.Copy(textBytes, 0, chunkData, 5 + keyBytes.Length, textBytes.Length);
            uint crc = Crc32(chunkData);
            WriteInt32BE(output, (int)crc);
            return;
        }

        byte[] utf8Bytes = Encoding.UTF8.GetBytes(text);
        using var payload = new MemoryStream();
        payload.Write(keyBytes, 0, keyBytes.Length);
        payload.WriteByte(0);
        payload.WriteByte(0);
        payload.WriteByte(0);
        payload.WriteByte(0);
        payload.WriteByte(0);
        payload.Write(utf8Bytes, 0, utf8Bytes.Length);

        byte[] payloadBytes = payload.ToArray();
        WriteInt32BE(output, payloadBytes.Length);
        byte[] type = Encoding.ASCII.GetBytes("iTXt");
        output.Write(type, 0, 4);
        output.Write(payloadBytes, 0, payloadBytes.Length);

        byte[] crcInput = new byte[4 + payloadBytes.Length];
        Array.Copy(type, 0, crcInput, 0, 4);
        Array.Copy(payloadBytes, 0, crcInput, 4, payloadBytes.Length);
        WriteInt32BE(output, (int)Crc32(crcInput));
    }

    private static void WriteInt32BE(Stream s, int value)
    {
        s.WriteByte((byte)(value >> 24));
        s.WriteByte((byte)(value >> 16));
        s.WriteByte((byte)(value >> 8));
        s.WriteByte((byte)value);
    }

    private static readonly uint[] Crc32Table = InitCrc32Table();
    private static uint[] InitCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int j = 0; j < 8; j++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
            crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    private static ImageMetadata? ParseMetadataJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var meta = new ImageMetadata
            {
                RawJson = json,
                Model = TryReadString(root, "model", "model_name", "Model"),
                ModelHash = TryReadString(root, "model_hash"),
                Source = TryReadString(root, "Source", "source"),
            };

            if (root.TryGetProperty("prompt", out var prompt) && prompt.ValueKind == JsonValueKind.String)
            {
                meta.HasPositivePrompt = true;
                meta.PositivePrompt = prompt.GetString() ?? "";
            }
            if (root.TryGetProperty("uc", out var uc) && uc.ValueKind == JsonValueKind.String)
            {
                meta.HasNegativePrompt = true;
                meta.NegativePrompt = uc.GetString() ?? "";
            }
            if (root.TryGetProperty("width", out var w))
                meta.Width = w.GetInt32();
            if (root.TryGetProperty("height", out var h))
                meta.Height = h.GetInt32();
            if (root.TryGetProperty("steps", out var steps))
                meta.Steps = steps.GetInt32();
            if (root.TryGetProperty("scale", out var scale))
            {
                meta.Scale = scale.GetDouble();
                meta.HasScale = true;
            }
            if (root.TryGetProperty("cfg_rescale", out var cfgRescale))
            {
                meta.CfgRescale = cfgRescale.GetDouble();
                meta.HasCfgRescale = true;
            }
            if (root.TryGetProperty("dynamic_thresholding", out var dynamicThresholding) &&
                dynamicThresholding.ValueKind is JsonValueKind.True or JsonValueKind.False)
                meta.DynamicThresholding = dynamicThresholding.GetBoolean();
            if (root.TryGetProperty("seed", out var seed))
                meta.Seed = SeedValue.ReadJson(seed);
            if (root.TryGetProperty("sampler", out var sampler))
                meta.Sampler = sampler.GetString() ?? "";
            if (root.TryGetProperty("noise_schedule", out var schedule))
                meta.NoiseSchedule = schedule.GetString() ?? "";
            if (root.TryGetProperty("sm", out var sm))
                meta.Sm = sm.GetBoolean();
            if (root.TryGetProperty("sm_dyn", out var smDyn))
                meta.SmDyn = smDyn.GetBoolean();
            if (root.TryGetProperty("skip_cfg_above_sigma", out var skipCfg) && skipCfg.ValueKind == JsonValueKind.Number)
                meta.SkipCfgAboveSigma = skipCfg.GetDouble();
            if (root.TryGetProperty("quality_toggle", out var qt))
                meta.QualityToggle = qt.GetBoolean();
            if (root.TryGetProperty("tag_hint_uc_preset", out var ucHint) && ucHint.ValueKind == JsonValueKind.Number)
                meta.UcPresetId = ucHint.GetInt32() switch { 2 => "heavy", 3 => "light", 4 => "humanFocus", 5 => "furryFocus", 0 => "none", _ => null };
            if (root.TryGetProperty("tag_hint_qt", out var qtHint) && qtHint.ValueKind == JsonValueKind.Number)
            {
                meta.QualityPreset = qtHint.GetInt32() == 3 ? "light" : "standard";
                meta.QualityToggle = qtHint.GetInt32() != 0;
            }
            if (root.TryGetProperty("ucPreset", out var ucPreset))
                meta.UcPreset = ucPreset.GetInt32();
            if (root.TryGetProperty("tag_hint_transparent_background", out var transparent) &&
                transparent.ValueKind is JsonValueKind.True or JsonValueKind.False)
                meta.TagHintTransparentBackground = transparent.GetBoolean();
            if (root.TryGetProperty("straight_alpha", out var straightAlpha) &&
                straightAlpha.ValueKind is JsonValueKind.True or JsonValueKind.False)
                meta.StraightAlpha = straightAlpha.GetBoolean();

            if (root.TryGetProperty("actual_prompts", out var actualPrompts) &&
                actualPrompts.ValueKind == JsonValueKind.Object)
            {
                meta.ActualPositivePrompt = ReadActualCaption(actualPrompts, "prompt", meta.ActualCharacterPrompts);
                meta.ActualNegativePrompt = ReadActualCaption(actualPrompts, "negative_prompt", meta.ActualCharacterNegativePrompts);
            }

            if (root.TryGetProperty("use_coords", out var useCoords) &&
                useCoords.ValueKind is JsonValueKind.True or JsonValueKind.False)
                meta.UseCharacterCoordinates = useCoords.GetBoolean();

            if (root.TryGetProperty("v4_prompt", out var v4p) && v4p.ValueKind == JsonValueKind.Object)
            {
                // The caption's explicit flag takes precedence, including false.
                if (v4p.TryGetProperty("use_coords", out var promptUseCoords) &&
                    promptUseCoords.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    meta.UseCharacterCoordinates = promptUseCoords.GetBoolean();
                meta.HasCharacterPrompts = v4p.TryGetProperty("caption", out var characterCaption) &&
                    characterCaption.ValueKind == JsonValueKind.Object &&
                    characterCaption.TryGetProperty("char_captions", out var declaredCharacters) &&
                    declaredCharacters.ValueKind == JsonValueKind.Array;
                ReadCharacterCaptions(v4p, meta.CharacterPrompts, meta.CharacterCenters);
            }

            if (root.TryGetProperty("legacy_uc", out var legacyUc) &&
                legacyUc.ValueKind is JsonValueKind.True or JsonValueKind.False)
                meta.LegacyUc = legacyUc.GetBoolean();
            if (root.TryGetProperty("v4_negative_prompt", out var v4np) && v4np.ValueKind == JsonValueKind.Object)
            {
                ReadCharacterCaptions(v4np, meta.CharacterNegativePrompts);
                if (v4np.TryGetProperty("legacy_uc", out var negativeLegacyUc) &&
                    negativeLegacyUc.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    meta.LegacyUc = negativeLegacyUc.GetBoolean();
            }

            ParseVibeTransfers(root, meta);
            ParsePreciseReferences(root, meta);

            return meta;
        }
        catch { return null; }
    }

    private static string? ReadActualCaption(JsonElement actualPrompts, string key, List<string?> characters)
    {
        if (!actualPrompts.TryGetProperty(key, out var caption)) return null;
        if (caption.ValueKind == JsonValueKind.String) return caption.GetString();
        if (caption.ValueKind != JsonValueKind.Object) return null;

        if (caption.TryGetProperty("char_captions", out var characterCaptions) &&
            characterCaptions.ValueKind == JsonValueKind.Array)
        {
            foreach (var character in characterCaptions.EnumerateArray())
                characters.Add(character.ValueKind == JsonValueKind.Object &&
                    character.TryGetProperty("char_caption", out var prompt) && prompt.ValueKind == JsonValueKind.String
                        ? prompt.GetString() : null);
        }
        return caption.TryGetProperty("base_caption", out var baseCaption) && baseCaption.ValueKind == JsonValueKind.String
            ? baseCaption.GetString() : null;
    }

    private static void ReadCharacterCaptions(JsonElement prompt, List<string> captions,
        List<(double X, double Y)>? centers = null)
    {
        if (prompt.ValueKind != JsonValueKind.Object ||
            !prompt.TryGetProperty("caption", out var caption) || caption.ValueKind != JsonValueKind.Object ||
            !caption.TryGetProperty("char_captions", out var characters) || characters.ValueKind != JsonValueKind.Array)
            return;

        foreach (var character in characters.EnumerateArray())
        {
            // Keep empty entries so prompts, negatives and positions retain the same indexes.
            bool isObject = character.ValueKind == JsonValueKind.Object;
            captions.Add(isObject ? TryReadString(character, "char_caption") : "");
            if (centers == null)
                continue;

            double x = 0.5, y = 0.5;
            if (isObject && character.TryGetProperty("centers", out var positions) &&
                positions.ValueKind == JsonValueKind.Array && positions.GetArrayLength() > 0 &&
                positions[0].ValueKind == JsonValueKind.Object)
            {
                x = ReadCharacterCoordinate(positions[0], "x");
                y = ReadCharacterCoordinate(positions[0], "y");
            }
            centers.Add((x, y));
        }
    }

    private static double ReadCharacterCoordinate(JsonElement position, string axis) =>
        position.TryGetProperty(axis, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out double coordinate)
            ? CharacterPromptRules.NormalizeCoordinate(coordinate) : 0.5;

    private static bool CanEncodeLatin1(string text)
    {
        foreach (char ch in text)
        {
            if (ch > 0xFF)
                return false;
        }
        return true;
    }

    private static Dictionary<string, string> TryReadStealthTextChunks(byte[] pngData)
    {
        string? jsonText = TryExtractStealthJsonText(pngData);
        if (string.IsNullOrWhiteSpace(jsonText))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                var result = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                        result[prop.Name] = prop.Value.GetString() ?? "";
                }

                if (result.ContainsKey("Comment") || result.ContainsKey("parameters") ||
                    result.ContainsKey("Description") || result.ContainsKey("Software") || result.ContainsKey("Source"))
                {
                    return result;
                }
            }
        }
        catch
        {
        }

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Comment"] = jsonText,
        };
    }

    private static string? TryExtractStealthJsonText(byte[] pngData)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(pngData);
            if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0)
                return null;

            int byteCount = (bitmap.Width * bitmap.Height) / 8;
            if (byteCount <= 0)
                return null;

            byte[] packedBytes = new byte[byteCount];
            int bitCount = 0;
            int byteIndex = 0;
            byte current = 0;

            for (int x = 0; x < bitmap.Width; x++)
            {
                for (int y = 0; y < bitmap.Height; y++)
                {
                    byte bit = (byte)(bitmap.GetPixel(x, y).Alpha & 1);
                    current = (byte)((current << 1) | bit);
                    bitCount++;
                    if ((bitCount & 7) == 0)
                    {
                        packedBytes[byteIndex++] = current;
                        current = 0;
                    }
                }
            }

            ReadOnlySpan<byte> probe = packedBytes;
            byte[] magicCompressed = Encoding.UTF8.GetBytes(StealthMagicCompressed);
            byte[] magicPlain = Encoding.UTF8.GetBytes(StealthMagicPlain);
            int payloadOffset;
            bool compressed;

            if (probe.Length >= magicCompressed.Length &&
                probe[..magicCompressed.Length].SequenceEqual(magicCompressed))
            {
                payloadOffset = magicCompressed.Length;
                compressed = true;
            }
            else if (probe.Length >= magicPlain.Length &&
                     probe[..magicPlain.Length].SequenceEqual(magicPlain))
            {
                payloadOffset = magicPlain.Length;
                compressed = false;
            }
            else
            {
                return null;
            }

            if (payloadOffset + 4 > packedBytes.Length)
                return null;

            int bitLength = ReadInt32BE(packedBytes, payloadOffset);
            if (bitLength <= 0 || (bitLength & 7) != 0)
                return null;

            int payloadLength = bitLength / 8;
            int payloadStart = payloadOffset + 4;
            if (payloadStart + payloadLength > packedBytes.Length)
                return null;

            byte[] payload = new byte[payloadLength];
            Array.Copy(packedBytes, payloadStart, payload, 0, payloadLength);
            byte[] rawBytes = compressed ? DecompressGzip(payload) : payload;
            return Encoding.UTF8.GetString(rawBytes);
        }
        catch
        {
            return null;
        }
    }

    private static byte[] DecompressGzip(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static SKBitmap? DecodeRgbaUnpremulBitmap(byte[] pngData)
    {
        using var stream = new SKMemoryStream(pngData);
        using var codec = SKCodec.Create(stream);
        if (codec == null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
            return null;

        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput)
            return bitmap;

        bitmap.Dispose();
        return null;
    }

    private static void WriteInt32BE(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static void ParseVibeTransfers(JsonElement root, ImageMetadata meta)
    {
        if (root.TryGetProperty("reference_image_multiple", out var imageArray) &&
            imageArray.ValueKind == JsonValueKind.Array)
        {
            root.TryGetProperty("reference_information_extracted_multiple", out var infoArray);
            var strengthValues = TryReadDoubleArray(root, "reference_strength_multiple");
            int index = 0;
            foreach (var item in imageArray.EnumerateArray())
            {
                string imageBase64 = item.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(imageBase64))
                {
                    index++;
                    continue;
                }

                double info = TryReadDoubleAt(infoArray, index, 1.0);
                double strength = index < strengthValues.Count ? strengthValues[index] : 0.6;
                meta.VibeTransfers.Add(new VibeTransferInfo
                {
                    ImageBase64 = imageBase64,
                    Strength = strength,
                    InformationExtracted = info,
                    HasInformationExtracted = infoArray.ValueKind == JsonValueKind.Array && index < infoArray.GetArrayLength() && infoArray[index].ValueKind == JsonValueKind.Number,
                    IsEncoded = NovelAIService.IsV4PlusModel(VibeTransferInfo.EncodingFamily(meta.ModelDisplayName)),
                    EncodingModel = meta.ModelDisplayName,
                    FileName = LocalizationService.Instance.Format("references.imported.vibe_numbered", index + 1),
                });
                index++;
            }

            return;
        }

        if (root.TryGetProperty("reference_image", out var singleImage))
        {
            string imageBase64 = singleImage.GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(imageBase64))
            {
                meta.VibeTransfers.Add(new VibeTransferInfo
                {
                    ImageBase64 = imageBase64,
                    Strength = TryReadDouble(root, "reference_strength", 0.6),
                    InformationExtracted = TryReadDouble(root, "reference_information_extracted", 1.0),
                    HasInformationExtracted = root.TryGetProperty("reference_information_extracted", out var singleInfo) && singleInfo.ValueKind == JsonValueKind.Number,
                    IsEncoded = NovelAIService.IsV4PlusModel(VibeTransferInfo.EncodingFamily(meta.ModelDisplayName)),
                    EncodingModel = meta.ModelDisplayName,
                    FileName = LocalizationService.Instance.Format("references.imported.vibe_numbered", 1),
                });
            }
        }
    }

    private static void ParsePreciseReferences(JsonElement root, ImageMetadata meta)
    {
        if (root.TryGetProperty("director_reference_images", out var dirImageArray) &&
            dirImageArray.ValueKind == JsonValueKind.Array)
        {
            var strengthValues = TryReadDoubleArray(root, "director_reference_strength_values");
            var secondaryValues = TryReadDoubleArray(root, "director_reference_secondary_strength_values");
            bool hasDescriptions = root.TryGetProperty("director_reference_descriptions", out var descArray)
                && descArray.ValueKind == JsonValueKind.Array;

            int index = 0;
            foreach (var item in dirImageArray.EnumerateArray())
            {
                string imageBase64 = item.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(imageBase64))
                {
                    index++;
                    continue;
                }

                string typeValue = "";
                if (hasDescriptions)
                {
                    var descElements = descArray.EnumerateArray().ToList();
                    if (index < descElements.Count &&
                        descElements[index].TryGetProperty("caption", out var caption) &&
                        caption.TryGetProperty("base_caption", out var baseCap))
                    {
                        typeValue = baseCap.GetString() ?? "";
                    }
                }

                double secondaryStrength = index < secondaryValues.Count ? secondaryValues[index] : 0.0;
                meta.PreciseReferences.Add(new PreciseReferenceInfo
                {
                    ImageBase64 = imageBase64,
                    FileName = LocalizationService.Instance.Format("references.imported.precise_numbered", index + 1),
                    ReferenceType = ParsePreciseReferenceType(typeValue),
                    Strength = Math.Clamp(index < strengthValues.Count ? strengthValues[index] : 1.0, 0, 1),
                    Fidelity = Math.Clamp(Math.Round(1.0 - secondaryStrength, 2), 0, 1),
                });
                index++;
            }
        }

        if (meta.PreciseReferences.Count > 0)
            return;

        if (root.TryGetProperty("precise_references", out var objectArray) &&
            objectArray.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (var item in objectArray.EnumerateArray())
            {
                string imageBase64 = TryReadString(item, "imageBase64", "image", "reference_image");
                if (string.IsNullOrWhiteSpace(imageBase64))
                {
                    index++;
                    continue;
                }

                meta.PreciseReferences.Add(new PreciseReferenceInfo
                {
                    ImageBase64 = imageBase64,
                    FileName = TryReadString(item, "fileName", "filename", "name", "label"),
                    ReferenceType = ParsePreciseReferenceType(TryReadString(item, "referenceType", "reference_type", "type")),
                    Strength = Math.Clamp(TryReadDouble(item, "strength", 1.0, "reference_strength"), 0, 1),
                    Fidelity = Math.Clamp(TryReadDouble(item, "fidelity", 0.0, "reference_fidelity"), 0, 1),
                });
                index++;
            }
        }

        if (meta.PreciseReferences.Count > 0)
            return;

        bool hasTypeArray = root.TryGetProperty("reference_type_multiple", out var typeArray);
        bool hasFidelityArray = root.TryGetProperty("reference_fidelity_multiple", out _);
        if (!hasTypeArray && !hasFidelityArray)
        {
            return;
        }

        if (!root.TryGetProperty("reference_image_multiple", out var imageArray) ||
            imageArray.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var legacyStrengthValues = TryReadDoubleArray(root, "reference_strength_multiple");
        var fidelityValues = TryReadDoubleArray(root, "reference_fidelity_multiple");
        int currentIndex = 0;
        foreach (var item in imageArray.EnumerateArray())
        {
            string imageBase64 = item.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(imageBase64))
            {
                currentIndex++;
                continue;
            }

            string fileName = LocalizationService.Instance.Format("references.imported.precise_numbered", currentIndex + 1);
            string typeValue = hasTypeArray && typeArray.ValueKind == JsonValueKind.Array
                ? TryReadStringAt(typeArray, currentIndex)
                : "";
            meta.PreciseReferences.Add(new PreciseReferenceInfo
            {
                ImageBase64 = imageBase64,
                FileName = fileName,
                ReferenceType = ParsePreciseReferenceType(typeValue),
                Strength = Math.Clamp(currentIndex < legacyStrengthValues.Count ? legacyStrengthValues[currentIndex] : 1.0, 0, 1),
                Fidelity = Math.Clamp(currentIndex < fidelityValues.Count ? fidelityValues[currentIndex] : 0.0, 0, 1),
            });
            currentIndex++;
        }
    }

    private static double TryReadDouble(JsonElement element, string propertyName, double fallback, params string[] aliases)
    {
        if (element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number)
            return value.GetDouble();

        foreach (var alias in aliases)
        {
            if (element.TryGetProperty(alias, out value) && value.ValueKind == JsonValueKind.Number)
                return value.GetDouble();
        }

        return fallback;
    }

    private static string TryReadString(JsonElement element, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? "";
        }

        return "";
    }

    private static List<double> TryReadDoubleArray(JsonElement element, string propertyName)
    {
        var result = new List<double>();
        if (!element.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number)
                result.Add(item.GetDouble());
        }

        return result;
    }

    private static double TryReadDoubleAt(JsonElement array, int index, double fallback)
    {
        if (array.ValueKind != JsonValueKind.Array)
            return fallback;

        int current = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (current == index)
                return item.ValueKind == JsonValueKind.Number ? item.GetDouble() : fallback;
            current++;
        }

        return fallback;
    }

    private static string TryReadStringAt(JsonElement array, int index)
    {
        if (array.ValueKind != JsonValueKind.Array)
            return "";

        int current = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (current == index)
                return item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
            current++;
        }

        return "";
    }

    private static PreciseReferenceType ParsePreciseReferenceType(string rawType) =>
        rawType.Trim().ToLowerInvariant() switch
        {
            "character" => PreciseReferenceType.Character,
            "style" => PreciseReferenceType.Style,
            "character_style" => PreciseReferenceType.CharacterAndStyle,
            "character&style" => PreciseReferenceType.CharacterAndStyle,
            "character+style" => PreciseReferenceType.CharacterAndStyle,
            _ => PreciseReferenceType.CharacterAndStyle,
        };
}
