using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;

namespace NAITool.Services;

internal static class ModelIntegrity
{
    internal const string ManifestFileName = "model-integrity.json";
    internal const string PublishingFileName = ".model-publishing";
    internal sealed record FileRecord(string Name, long Length, string Sha256);
    internal sealed record Manifest(int Version, FileRecord[] Files);

    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, string> Validated = new(StringComparer.OrdinalIgnoreCase);
    private static int _sessionValidationCount;
    internal static int SessionValidationCount => _sessionValidationCount;

    // Cache stores no runtime sessions/weights, only a freshness fingerprint. Bounded for imported paths.
    private static string Stamp(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("Model asset missing.", path);
        return $"{Path.GetFullPath(path)}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{file.CreationTimeUtc.Ticks}";
    }

    private static string Fingerprint(string onnx, string csv, string? manifestText)
        => Stamp(onnx) + "\n" + Stamp(csv) + "\n" + manifestText;

    private static void Remember(string directory, string fingerprint)
    {
        if (Validated.Count >= 64) Validated.Clear();
        Validated[directory] = fingerprint;
    }

    // Called after a fully validated download is published; avoid reloading its heavy graph on the UI thread.
    internal static void RememberPublished(string directory)
    {
        lock (CacheLock)
        {
            directory = Path.GetFullPath(directory);
            Remember(directory, Fingerprint(Path.Combine(directory, "model.onnx"),
                Path.Combine(directory, "selected_tags.csv"), File.ReadAllText(Path.Combine(directory, ManifestFileName))));
        }
    }

    internal static FileRecord Describe(string path, string name)
    {
        using var stream = File.OpenRead(path);
        long length = stream.Length;
        return new FileRecord(name, length, Convert.ToHexString(SHA256.HashData(stream)));
    }

    internal static void ValidatePair(string onnxPath, string csvPath)
    {
        var tags = ReverseTaggerModelCatalog.LoadTagDefinitionsFromFile(csvPath);
        // Default CPU provider only: validation must not depend on DirectML/GPU availability.
        // Session construction checks graph/weights/operators, not numerical accuracy or inference quality.
        using var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL };
        System.Threading.Interlocked.Increment(ref _sessionValidationCount);
        using var session = new InferenceSession(onnxPath, options);
        if (session.InputMetadata.Count == 0 || session.OutputMetadata.Count == 0 || tags.Count == 0)
            throw new InvalidDataException("Model has no input, output or usable tags.");
    }

    internal static bool IsUsableDirectory(string directory)
    {
        lock (CacheLock)
        try
        {
            directory = Path.GetFullPath(directory);
            if (!Directory.Exists(directory) || File.Exists(Path.Combine(directory, PublishingFileName)))
                return false;
            string manifestPath = Path.Combine(directory, ManifestFileName);
            if (File.Exists(manifestPath))
            {
                string manifestText = File.ReadAllText(manifestPath);
                var manifest = JsonSerializer.Deserialize<Manifest>(manifestText);
                if (manifest is null || manifest.Version != 1 || manifest.Files is null || manifest.Files.Length != 2 ||
                    !manifest.Files.Select(f => f.Name).Order().SequenceEqual(new[] { "model.onnx", "selected_tags.csv" }))
                    return false;
                string fingerprint = Fingerprint(Path.Combine(directory, "model.onnx"),
                    Path.Combine(directory, "selected_tags.csv"), manifestText);
                if (Validated.TryGetValue(directory, out string? cached) && cached == fingerprint)
                    return true;
                foreach (var file in manifest.Files)
                {
                    var actual = Describe(Path.Combine(directory, file.Name), file.Name);
                    if (actual.Length != file.Length || !string.Equals(actual.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                // Manifest is a local completeness/change detector, not a trusted upstream signature.
                ValidatePair(Path.Combine(directory, "model.onnx"), Path.Combine(directory, "selected_tags.csv"));
                if (fingerprint != Fingerprint(Path.Combine(directory, "model.onnx"), Path.Combine(directory, "selected_tags.csv"), File.ReadAllText(manifestPath)))
                    return false;
                Remember(directory, fingerprint);
                return true;
            }

            // Imported/legacy models need no manifest. Match the inference service's selection order.
            var models = Directory.GetFiles(directory, "*.onnx", SearchOption.TopDirectoryOnly)
                .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).ToArray();
            if (models.Length == 0)
                models = Directory.GetFiles(directory, "*.onnx", SearchOption.AllDirectories)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
            if (models.Length == 0)
                return false;
            string csv = Path.Combine(directory, "selected_tags.csv");
            string importedFingerprint = Fingerprint(models[0], csv, null);
            if (Validated.TryGetValue(directory, out string? importedCached) && importedCached == importedFingerprint)
                return true;
            ValidatePair(models[0], csv);
            if (importedFingerprint != Fingerprint(models[0], csv, null))
                return false;
            Remember(directory, importedFingerprint);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
