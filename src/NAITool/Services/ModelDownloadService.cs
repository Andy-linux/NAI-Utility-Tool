using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NAITool.Services;

public class ModelDownloadProgress
{
    public string StatusMessage { get; set; } = "";
    public string FileName { get; set; } = "";
    public int FileIndex { get; set; }
    public int TotalFiles { get; set; }
    public long BytesReceived { get; set; }
    public long? TotalBytes { get; set; }
    public double ProgressPercent { get; set; }
    public bool IsCompleted { get; set; }
    public bool IsError { get; set; }
}

public static class ModelDownloadService
{
    private const string HuggingFaceRepo = "https://huggingface.co/deepghs/pixai-tagger-v0.9-onnx/resolve/main";
    private static readonly string[] RequiredFiles = ["model.onnx", "selected_tags.csv"];
    public static string DefaultDownloadPath => Path.Combine(AppPathResolver.AppRootDir, "models", "tagger", "pixai-tagger-v0.9-onnx");
    public static bool IsModelDownloaded(string directory) => ModelIntegrity.IsUsableDirectory(directory);

    public static async Task<string> DownloadModelAsync(
        string? targetDir = null,
        Action<ModelDownloadProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        return await DownloadModelAsync(client, new Uri(HuggingFaceRepo + "/"), targetDir ?? DefaultDownloadPath,
            onProgress, cancellationToken);
    }

    internal static async Task<string> DownloadModelAsync(HttpClient client, Uri baseUri, string targetDir,
        Action<ModelDownloadProgress>? onProgress = null, CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? expectedSha256 = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(targetDir);
        // Cross-process download lock.
        using var downloadLock = new FileStream(Path.Combine(targetDir, ".model-download.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        string suffix = "." + Guid.NewGuid().ToString("N") + ".tmp";
        string[] temps = RequiredFiles.Select(f => Path.Combine(targetDir, f + suffix)).ToArray();
        string manifestTemp = Path.Combine(targetDir, ModelIntegrity.ManifestFileName + suffix);
        long completedBytes = 0;
        int index = 0;
        try
        {
            var records = new ModelIntegrity.FileRecord[RequiredFiles.Length];
            for (; index < RequiredFiles.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string file = RequiredFiles[index];
                using var response = await client.GetAsync(new Uri(baseUri, file), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                long? expectedLength = response.Content.Headers.ContentLength;
                string? responseHash = ReadResponseSha256(response);
                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var destination = new FileStream(temps[index], FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 81920, FileOptions.Asynchronous))
                {
                    var buffer = new byte[81920];
                    long received = 0;
                    var timer = Stopwatch.StartNew();
                    while (true)
                    {
                        int count = await source.ReadAsync(buffer, cancellationToken);
                        if (count == 0) break;
                        await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                        received += count;
                        if (expectedLength.HasValue && received > expectedLength.Value)
                            throw new InvalidDataException("Response exceeded expected length.");
                        if (timer.ElapsedMilliseconds >= 1000)
                        {
                            onProgress?.Invoke(new ModelDownloadProgress
                            {
                                StatusMessage = $"Downloading {file}", FileName = file, FileIndex = index + 1,
                                TotalFiles = RequiredFiles.Length, BytesReceived = completedBytes + received,
                                ProgressPercent = Math.Min(99, (index + (expectedLength is > 0 ? received / (double)expectedLength.Value : 0)) * 50)
                            });
                            timer.Restart();
                        }
                    }
                    if (received == 0 || (expectedLength.HasValue && received != expectedLength.Value))
                        throw new InvalidDataException("Incomplete model response.");
                    await destination.FlushAsync(cancellationToken);
                    destination.Flush(flushToDisk: true);
                    completedBytes += received;
                }
                records[index] = ModelIntegrity.Describe(temps[index], file);
                if (responseHash != null && !string.Equals(responseHash, records[index].Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Response checksum mismatch.");
                if (expectedSha256 != null && expectedSha256.TryGetValue(file, out string? hash) &&
                    !string.Equals(hash, records[index].Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Model checksum mismatch.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(() => ModelIntegrity.ValidatePair(temps[0], temps[1]), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await using (var manifestStream = new FileStream(manifestTemp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(manifestStream, new ModelIntegrity.Manifest(1, records), cancellationToken: cancellationToken);
                await manifestStream.FlushAsync(cancellationToken);
                manifestStream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            Publish(targetDir, temps, manifestTemp);
            ModelIntegrity.RememberPublished(targetDir);
            // Publishing is a short non-cancellable commit: cancellation cannot strand half a pair.
            onProgress?.Invoke(new ModelDownloadProgress
            {
                StatusMessage = "Download complete.", FileIndex = RequiredFiles.Length, TotalFiles = RequiredFiles.Length,
                BytesReceived = completedBytes, TotalBytes = completedBytes, ProgressPercent = 100, IsCompleted = true
            });
            return targetDir;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            onProgress?.Invoke(new ModelDownloadProgress
            {
                StatusMessage = "Model download or validation failed.", TotalFiles = RequiredFiles.Length,
                FileIndex = Math.Min(index + 1, RequiredFiles.Length), IsError = true
            });
            throw new IOException("Model download or validation failed.", ex);
        }
        finally
        {
            foreach (string path in temps.Append(manifestTemp))
                if (File.Exists(path)) File.Delete(path);
        }
    }

    private static string? ReadResponseSha256(HttpResponseMessage response)
    {
        // RFC Digest/Content-Digest supplied by the server.
        foreach (string header in new[] { "Content-Digest", "Digest" })
        {
            if (!response.Headers.TryGetValues(header, out var values) &&
                !response.Content.Headers.TryGetValues(header, out values))
                continue;
            foreach (string entry in string.Join(",", values).Split(','))
            {
                string[] parts = entry.Trim().Split('=', 2);
                if (parts.Length != 2 || !parts[0].Equals("sha-256", StringComparison.OrdinalIgnoreCase))
                    continue;
                byte[] digest = Convert.FromBase64String(parts[1].Trim().Trim(':', '"'));
                if (digest.Length != 32)
                    throw new InvalidDataException("Invalid response SHA-256 digest.");
                return Convert.ToHexString(digest);
            }
        }
        return null;
    }

    private static void Publish(string directory, string[] temps, string manifestTemp)
    {
        string marker = Path.Combine(directory, ModelIntegrity.PublishingFileName);
        // A crash between individual atomic renames leaves the marker, so a mixed pair is never usable.
        using (var stream = new FileStream(marker, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.WriteByte(1);
            stream.Flush(flushToDisk: true);
        }
        for (int i = 0; i < temps.Length; i++)
            PublishFile(temps[i], Path.Combine(directory, RequiredFiles[i]));
        PublishFile(manifestTemp, Path.Combine(directory, ModelIntegrity.ManifestFileName));
        File.Delete(marker);
    }

    private static void PublishFile(string temporary, string final)
    {
        if (File.Exists(final))
            File.Replace(temporary, final, destinationBackupFileName: null);
        else
            File.Move(temporary, final);
    }
}
