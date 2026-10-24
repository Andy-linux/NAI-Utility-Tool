using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NAITool.Services;

int checks = 0;
void Check(bool value, string name)
{
    if (!value) throw new Exception(name);
    checks++;
}
byte[] payload = Encoding.UTF8.GetBytes("Maintenance installer fixture: unchanged binary bytes.\n");
string digest = Convert.ToHexStringLower(SHA256.HashData(payload));
const string tag = "1.3.0_fork.2.1";
string assetName = $"NAI.Utility.Tool.{tag}.Setup.exe";
object Release(string version, bool draft = false, bool prerelease = false, bool installer = true) => new
{
    tag_name = version, draft, prerelease, body = "Fix browser image imports.", published_at = "2026-10-24T00:00:00Z",
    assets = installer ? new[] { new { name = $"NAI.Utility.Tool.{version}.Setup.exe", state = "uploaded",
        browser_download_url = $"https://github.com/{ReleaseUpdateService.Repository}/releases/download/{version}/NAI.Utility.Tool.{version}.Setup.exe",
        size = payload.Length, digest = "sha256:" + digest } } : []
};
string[] orderedTags = ["1.3.0_fork.2", "1.3.0_fork.2.0", tag, "1.3.0_fork.2.2",
    "1.3.0_fork.2.10", "1.3.0_fork.3", "1.3.0_fork.10", "1.3.1_fork.1", "1.4.0_fork.1", "2.0.0_fork.1"];
for (int i = 1; i < orderedTags.Length; i++)
    Check(ReleaseUpdateService.ParseVersion(orderedTags[i])!.CompareTo(ReleaseUpdateService.ParseVersion(orderedTags[i - 1])) > 0,
        $"Numeric version ordering: {orderedTags[i - 1]} < {orderedTags[i]}");
Check(ReleaseUpdateService.ParseVersion("v1.3.0-fork.2.1") == ReleaseUpdateService.ParseVersion(tag), "Display and assembly tag forms agree");
foreach (string value in new[] { "1.3.0", "main", "dev", "1.3.0_fork.9999999999999", "1.3.0_fork.2.9999999999999",
    "1.3.0_fork.2.1.1", "1.3.0_fork.4-preview" })
    Check(ReleaseUpdateService.ParseVersion(value) == null, "Reject a non-release version: " + value);
string json = JsonSerializer.Serialize(new[] { Release(tag), Release("1.3.0_fork.2"), Release("1.3.0_fork.2.10"),
    Release("1.3.0_fork.10"), Release("1.3.0_fork.12", draft: true), Release("1.3.0_fork.11", prerelease: true),
    Release("1.3.0_fork.2.2", prerelease: true), Release("1.3.0_fork.20", installer: false) });
var parsed = ReleaseUpdateService.ParseReleases(json);
Check(parsed.Select(r => r.Tag).SequenceEqual(new[] { "1.3.0_fork.10", "1.3.0_fork.2.10", tag, "1.3.0_fork.2" }), "Only stable releases with a compatible installer are offered");
var release = parsed.Single(r => r.Tag == tag);
string sumsText = digest + "  " + assetName + Environment.NewLine;
using var handler = new FixtureHandler((request, token) =>
{
    token.ThrowIfCancellationRequested();
    string uri = request.RequestUri!.AbsoluteUri;
    byte[] data = uri.Contains("api.github.com") ? Encoding.UTF8.GetBytes(json)
        : uri.EndsWith("SHA256SUMS.txt") ? Encoding.UTF8.GetBytes(sumsText) : payload;
    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
});
using var client = new HttpClient(handler);
var service = new ReleaseUpdateService(client);
Check((await service.GetReleasesAsync(default)).Count == 4, "GitHub discovery sends and parses its release request");
string downloaded = await service.DownloadAsync(release, null, default);
Check(Path.GetFileName(downloaded) == assetName, "A patch release selects its exact installer asset");
Check(File.ReadAllBytes(downloaded).SequenceEqual(payload), "Validated installer is byte exact");
Directory.Delete(Path.GetDirectoryName(downloaded)!, true);
var sums = new ReleaseAsset("SHA256SUMS.txt", new Uri($"https://github.com/{ReleaseUpdateService.Repository}/releases/download/{tag}/SHA256SUMS.txt"), 100, null);
downloaded = await service.DownloadAsync(release with { Installer = release.Installer with { Digest = null }, Checksums = sums }, null, default);
Check(File.Exists(downloaded), "Older releases can use their SHA256SUMS file");
Directory.Delete(Path.GetDirectoryName(downloaded)!, true);
foreach (var invalid in new[]
{
    release with { Installer = release.Installer with { Digest = "sha256:" + new string('0', 64) } },
    release with { Installer = release.Installer with { Size = payload.Length + 1 } },
    release with { Installer = release.Installer with { Digest = null }, Checksums = null },
    release with { Installer = release.Installer with { DownloadUri = new Uri("https://example.com/installer.exe") } },
})
{
    bool rejected = false;
    try { await service.DownloadAsync(invalid, null, default); }
    catch (InvalidDataException) { rejected = true; }
    Check(rejected, "Unverified or unrelated installer cannot be returned for execution");
}
sumsText = digest + "  other.Setup.exe" + Environment.NewLine;
bool wrongEntry = false;
try { await service.DownloadAsync(release with { Installer = release.Installer with { Digest = null }, Checksums = sums }, null, default); }
catch (InvalidDataException) { wrongEntry = true; }
Check(wrongEntry, "A checksum for a different installer cannot authorize this download");
using (var interrupted = new CancellationTokenSource())
{
    bool stopped = false;
    try { await service.DownloadAsync(release, new CancelOnProgress(interrupted), interrupted.Token); }
    catch (OperationCanceledException) { stopped = true; }
    Check(stopped, "Cancellation after receiving bytes prevents installation");
}
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel(); bool stopped = false;
    try { await service.DownloadAsync(release, null, cancelled.Token); }
    catch (OperationCanceledException) { stopped = true; }
    Check(stopped, "Cancelled download never produces an installable result");
}
string root = Path.Combine(Path.GetTempPath(), "NAI update test " + Guid.NewGuid().ToString("N"), "便携版");
Directory.CreateDirectory(root);
try
{
    var start = ReleaseUpdateService.CreateInstallerStartInfo("fixture.Setup.exe", root, 12345);
    Check(start.ArgumentList.Contains("/DIR=" + Path.GetFullPath(root)), "Upgrade targets the root directory without shell quoting");
    Check(start.ArgumentList.Contains("/PORTABLE=1") && start.ArgumentList.Contains("/UPDATEPID=12345"), "Portable mode and parent wait are passed explicitly");
    Directory.CreateDirectory(Path.Combine(root, "uninstall"));
    File.WriteAllText(Path.Combine(root, "uninstall", "unins000.exe"), "fixture");
    Check(ReleaseUpdateService.CreateInstallerStartInfo("fixture.Setup.exe", root, 12345).ArgumentList.Contains("/PORTABLE=0"), "Registered installs retain their mode");
}
finally { Directory.Delete(Path.GetDirectoryName(root)!, true); }
Console.WriteLine($"Passed {checks} release update checks. No installer was executed.");

sealed class FixtureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
}

sealed class CancelOnProgress(CancellationTokenSource cancellation) : IProgress<double>
{
    public void Report(double value) => cancellation.Cancel();
}
