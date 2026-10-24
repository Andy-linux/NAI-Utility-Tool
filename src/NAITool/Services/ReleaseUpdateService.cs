using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NAITool.Services;

public sealed record ReleaseAsset(string Name, Uri DownloadUri, long Size, string? Digest);
public sealed record ClientRelease(string Tag, Version Version, string Notes, DateTimeOffset PublishedAt,
    Uri PageUri, ReleaseAsset Installer, ReleaseAsset? Checksums)
{
    public override string ToString() => Tag;
}

public sealed class ReleaseUpdateService
{
    public const string Repository = "Andy-linux/NAI-Utility-Tool";
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases";
    public static string CurrentVersion => typeof(ReleaseUpdateService).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
    private readonly HttpClient _client;

    public ReleaseUpdateService(HttpClient client) => _client = client;

    // The fork's tag is not SemVer: compare its numeric revision (fork.10 > fork.2).
    public static Version? ParseVersion(string tag)
    {
        var match = Regex.Match(tag, @"^v?(\d+)\.(\d+)\.(\d+)[_-]fork\.(\d+)$", RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        var parts = new int[4];
        for (int i = 0; i < parts.Length; i++)
            if (!int.TryParse(match.Groups[i + 1].Value, out parts[i])) return null;
        return new Version(parts[0], parts[1], parts[2], parts[3]);
    }

    private static HttpRequestMessage Request(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("NAI-Utility-Tool/" + CurrentVersion.Replace('_', '-'));
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        return request;
    }

    public async Task<IReadOnlyList<ClientRelease>> GetReleasesAsync(CancellationToken cancellationToken)
    {
        using var request = Request(new Uri("https://api.github.com/repos/" + Repository + "/releases?per_page=100"));
        using var response = await _client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return ParseReleases(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    public static IReadOnlyList<ClientRelease> ParseReleases(string json)
    {
        using var document = JsonDocument.Parse(json);
        var releases = new List<ClientRelease>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.GetProperty("draft").GetBoolean() || item.GetProperty("prerelease").GetBoolean()) continue;
            string tag = item.GetProperty("tag_name").GetString()!;
            var version = ParseVersion(tag);
            if (version == null) continue;
            var assets = item.GetProperty("assets").EnumerateArray()
                .Where(a => a.GetProperty("state").GetString() == "uploaded")
                .Select(a => new ReleaseAsset(a.GetProperty("name").GetString()!,
                    new Uri(a.GetProperty("browser_download_url").GetString()!), a.GetProperty("size").GetInt64(),
                    a.TryGetProperty("digest", out var digest) ? digest.GetString() : null)).ToList();
            var installer = assets.FirstOrDefault(a => a.Name == $"NAI.Utility.Tool.{tag}.Setup.exe");
            if (installer == null || installer.Size <= 0) continue;
            releases.Add(new ClientRelease(tag, version, item.GetProperty("body").GetString() ?? "",
                item.GetProperty("published_at").GetDateTimeOffset(), new Uri(ReleasesPage + "/tag/" + tag),
                installer, assets.FirstOrDefault(a => a.Name == "SHA256SUMS.txt")));
        }
        return releases.OrderByDescending(r => r.Version).ToList();
    }

    private static void CheckAssetUri(ReleaseAsset asset, string tag)
    {
        string prefix = ReleasesPage + "/download/" + tag + "/";
        if (!asset.DownloadUri.AbsoluteUri.StartsWith(prefix, StringComparison.Ordinal) ||
            Path.GetFileName(asset.Name) != asset.Name)
            throw new InvalidDataException("The update asset does not belong to this fork's release.");
    }

    private async Task<string> GetChecksumAsync(ClientRelease release, CancellationToken cancellationToken)
    {
        string? digest = release.Installer.Digest;
        if (digest != null && Regex.IsMatch(digest, "^sha256:[0-9a-fA-F]{64}$")) return digest[7..];
        if (release.Checksums == null) throw new InvalidDataException("This release has no SHA-256 checksum.");
        CheckAssetUri(release.Checksums, release.Tag);
        using var request = Request(release.Checksums.DownloadUri);
        using var response = await _client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        string text = await response.Content.ReadAsStringAsync(cancellationToken);
        foreach (string line in text.Split('\n'))
        {
            var fields = line.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 2 && fields[1].TrimStart('*') == release.Installer.Name &&
                Regex.IsMatch(fields[0], "^[0-9a-fA-F]{64}$")) return fields[0];
        }
        throw new InvalidDataException("The installer is missing from SHA256SUMS.txt.");
    }

    public async Task<string> DownloadAsync(ClientRelease release, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        CheckAssetUri(release.Installer, release.Tag);
        string checksum = await GetChecksumAsync(release, cancellationToken);
        string directory = Path.Combine(Path.GetTempPath(), "NAI-Utility-Tool", "updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, release.Installer.Name);
        string partial = path + ".partial";
        try
        {
            using var request = Request(release.Installer.DownloadUri);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                byte[] buffer = new byte[81920];
                long received = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    hash.AppendData(buffer, 0, count);
                    received += count;
                    progress?.Report(Math.Min(100, received * 100.0 / release.Installer.Size));
                }
                if (received != release.Installer.Size) throw new InvalidDataException("The installer download is incomplete.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(checksum, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The installer SHA-256 checksum does not match.");
            File.Move(partial, path);
            return path;
        }
        catch
        {
            if (File.Exists(partial)) File.Delete(partial);
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            throw;
        }
    }

    public static ProcessStartInfo CreateInstallerStartInfo(string installer, string appRoot, int processId)
    {
        var start = new ProcessStartInfo(installer) { UseShellExecute = true };
        start.ArgumentList.Add("/DIR=" + Path.GetFullPath(appRoot));
        start.ArgumentList.Add("/PORTABLE=" + (File.Exists(Path.Combine(appRoot, "uninstall", "unins000.exe")) ? "0" : "1"));
        start.ArgumentList.Add("/UPDATEPID=" + processId);
        start.ArgumentList.Add("/NORESTART");
        return start;
    }
}
