using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using NAITool.Services;

internal static class Program
{
    private static readonly byte[] Onnx = Convert.FromBase64String("CAg6XwoQCgFYEgFZIghJZGVudGl0eRIRU3ludGhldGljSWRlbnRpdHlaGwoBWBIWChQIARIQCgIIAQoCCAEKAggBCgIIAWIbCgFZEhYKFAgBEhAKAggBCgIIAQoCCAEKAggBQgIQDQ==");
    private static readonly byte[] Csv = Encoding.UTF8.GetBytes("tag_id,name,category\n1,synthetic,0\n");
    private static int passed;
    private static async Task Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "nai-download-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var server = new FixtureServer();
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            string directory = Path.Combine(root, "download");
            List<ModelDownloadProgress> progress = [];
            await ModelDownloadService.DownloadModelAsync(client, server.Uri, directory, progress.Add);
            int validationsAfterDownload = ModelIntegrity.SessionValidationCount;
            Check(ModelDownloadService.IsModelDownloaded(directory), "valid localhost download usable");
            for (int check = 0; check < 10; check++)
                Check(ReverseTaggerModelCatalog.HasUsableModelDirectory(directory), "cached downloaded usability");
            Check(ModelIntegrity.SessionValidationCount == validationsAfterDownload, "published model checks do not reconstruct session");
            Check(progress.Count(p => p.IsCompleted) == 1 && progress.Last().ProgressPercent == 100, "completion after validation");
            Check(File.Exists(Path.Combine(directory, ModelIntegrity.ManifestFileName)), "complete manifest published");
            Check(!Directory.GetFiles(directory, "*.tmp").Any(), "success temporary cleanup");
            var originalManifest = File.ReadAllBytes(Path.Combine(directory, ModelIntegrity.ManifestFileName));

            // Same-length, valid CSV mutation must be detected by the recorded hash.
            File.WriteAllText(Path.Combine(directory, "selected_tags.csv"), "tag_id,name,category\n1,Synthetic,0\n");
            Check(!ModelDownloadService.IsModelDownloaded(directory), "changed final detected");
            Check(new FileInfo(Path.Combine(directory, "selected_tags.csv")).Length == Csv.Length, "same-length corruption invalidates cached fingerprint");
            File.WriteAllBytes(Path.Combine(directory, "selected_tags.csv"), Csv);
            File.WriteAllBytes(Path.Combine(directory, "model.onnx"), [8]);
            Check(!ModelDownloadService.IsModelDownloaded(directory), "truncated final detected");
            File.WriteAllBytes(Path.Combine(directory, "model.onnx"), Onnx);

            await Failure("checksum", () => ModelDownloadService.DownloadModelAsync(client, server.Uri, directory, progress.Add,
                expectedSha256: new Dictionary<string, string> { ["model.onnx"] = new string('0', 64) }), progress, directory);
            Check(File.ReadAllBytes(Path.Combine(directory, ModelIntegrity.ManifestFileName)).SequenceEqual(originalManifest) &&
                ModelDownloadService.IsModelDownloaded(directory), "failed download preserves valid finals");
            await ModelDownloadService.DownloadModelAsync(client, server.Uri, directory, expectedSha256:
                new Dictionary<string, string> { ["model.onnx"] = Convert.ToHexString(SHA256.HashData(Onnx)) });
            Check(ModelDownloadService.IsModelDownloaded(directory), "expected checksum accepted");

            server.BadDigest = true;
            await Failure("response digest mismatch", () => ModelDownloadService.DownloadModelAsync(client, server.Uri, directory, progress.Add), progress, directory);
            server.BadDigest = false;
            await ModelDownloadService.DownloadModelAsync(client, server.Uri, directory);
            Check(ModelDownloadService.IsModelDownloaded(directory), "response digest accepted");
            server.Model = [8];
            await Failure("invalid ONNX", () => ModelDownloadService.DownloadModelAsync(client, server.Uri, directory, progress.Add), progress, directory);
            server.Model = Onnx;
            server.Tags = Encoding.UTF8.GetBytes("tag_id,name,category\n");
            await Failure("empty CSV", () => ModelDownloadService.DownloadModelAsync(client, server.Uri, directory, progress.Add), progress, directory);
            server.Tags = Encoding.UTF8.GetBytes("tag_id,name,category\n1,\"broken,0\n");
            await Failure("malformed CSV", () => ModelDownloadService.DownloadModelAsync(client, server.Uri, directory, progress.Add), progress, directory);
            server.Tags = Csv;
            server.FailTags = true;
            await Failure("HTTP failure on second file", () => ModelDownloadService.DownloadModelAsync(client, server.Uri, directory, progress.Add), progress, directory);
            server.FailTags = false;

            using (var truncatedClient = new HttpClient(new TruncatedHandler()))
                await Failure("declared length mismatch", () => ModelDownloadService.DownloadModelAsync(truncatedClient, server.Uri, directory, progress.Add), progress, directory);
            server.Chunked = true;
            await ModelDownloadService.DownloadModelAsync(client, server.Uri, directory);
            Check(ModelDownloadService.IsModelDownloaded(directory), "unknown length chunked validated");
            server.Chunked = false;

            using (var cancel = new CancellationTokenSource())
            {
                server.Slow = true;
                var task = ModelDownloadService.DownloadModelAsync(client, server.Uri, directory, progress.Add, cancel.Token);
                await server.SlowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cancel.Cancel();
                await Failure("cancellation", () => task, progress, directory, cancelled: true);
                server.Slow = false;
            }
            Check(ModelDownloadService.IsModelDownloaded(directory), "cancellation preserves prior finals");
            Check(!File.Exists(Path.Combine(directory, ".model-download.lock")), "writer lock cleanup");

            string blockedPublish = Path.Combine(root, "blocked-publish");
            Directory.CreateDirectory(Path.Combine(blockedPublish, "selected_tags.csv"));
            await Failure("publication failure", () => ModelDownloadService.DownloadModelAsync(client, server.Uri, blockedPublish, progress.Add), progress, blockedPublish);
            Check(File.Exists(Path.Combine(blockedPublish, ModelIntegrity.PublishingFileName)), "failed publication retains safety marker");
            Check(!ModelDownloadService.IsModelDownloaded(blockedPublish), "failed mixed publication not usable");
            Directory.Delete(Path.Combine(blockedPublish, "selected_tags.csv"));
            await ModelDownloadService.DownloadModelAsync(client, server.Uri, blockedPublish);
            Check(ModelDownloadService.IsModelDownloaded(blockedPublish) && !File.Exists(Path.Combine(blockedPublish, ModelIntegrity.PublishingFileName)),
                "successful retry repairs failed publication");

            string imported = Path.Combine(root, "imported");
            Directory.CreateDirectory(Path.Combine(imported, "nested"));
            File.WriteAllBytes(Path.Combine(imported, "nested", "custom.onnx"), Onnx);
            File.WriteAllBytes(Path.Combine(imported, "selected_tags.csv"), Csv);
            Check(ReverseTaggerModelCatalog.HasUsableModelDirectory(imported), "external nested model without manifest compatible");
            int importedValidations = ModelIntegrity.SessionValidationCount;
            Check(ReverseTaggerModelCatalog.HasUsableModelDirectory(imported) && ModelIntegrity.SessionValidationCount == importedValidations,
                "unchanged imported check reuses validation without session");
            File.WriteAllBytes(Path.Combine(imported, "nested", "custom.onnx"), [8]);
            Check(!ReverseTaggerModelCatalog.HasUsableModelDirectory(imported) && !ModelDownloadService.IsModelDownloaded(imported), "one-byte imported ONNX rejected");
            File.WriteAllBytes(Path.Combine(imported, "nested", "custom.onnx"), Onnx);
            File.WriteAllText(Path.Combine(imported, ModelIntegrity.PublishingFileName), "1");
            Check(!ModelDownloadService.IsModelDownloaded(imported), "interrupted publish not usable");
            Console.WriteLine($"PASS: {passed} assertions; CPU ONNX session validation, synthetic localhost HTTP only.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task Failure(string name, Func<Task> action, List<ModelDownloadProgress> progress, string directory, bool cancelled = false)
    {
        progress.Clear();
        try { await action(); throw new Exception("Expected failure: " + name); }
        catch (OperationCanceledException) when (cancelled) { }
        catch (IOException) when (!cancelled) { }
        Check(!progress.Any(p => p.IsCompleted), name + " no false completion");
        Check(!Directory.GetFiles(directory, "*.tmp").Any(), name + " temporary cleanup");
    }
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        passed++;
        Console.WriteLine("PASS: " + name);
    }

    private sealed class TruncatedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(Onnx);
            content.Headers.ContentLength = Onnx.Length + 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
    private sealed class FixtureServer : IDisposable
    {
        private readonly HttpListener listener = new();
        private readonly CancellationTokenSource stop = new();
        private readonly Task loop;
        public Uri Uri { get; }
        public byte[] Model { get; set; } = Onnx;
        public byte[] Tags { get; set; } = Csv;
        public bool FailTags { get; set; }
        public bool Chunked { get; set; }
        public bool Slow { get; set; }
        public bool BadDigest { get; set; }
        public TaskCompletionSource SlowStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FixtureServer()
        {
            var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            int port = ((IPEndPoint)socket.LocalEndpoint).Port;
            socket.Stop();
            Uri = new Uri($"http://localhost:{port}/");
            listener.Prefixes.Add(Uri.ToString());
            listener.Start();
            loop = Run();
        }
        private async Task Run()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var context = await listener.GetContextAsync().WaitAsync(stop.Token);
                    try
                    {
                        bool tags = context.Request.Url!.AbsolutePath.EndsWith(".csv");
                        if (tags && FailTags) { context.Response.StatusCode = 503; continue; }
                        byte[] body = tags ? Tags : Model;
                        byte[] digest = BadDigest ? new byte[32] : SHA256.HashData(body);
                        context.Response.Headers["Content-Digest"] = "sha-256=:" + Convert.ToBase64String(digest) + ":";
                        if (Chunked) context.Response.SendChunked = true;
                        else context.Response.ContentLength64 = body.Length;
                        if (Slow)
                        {
                            await context.Response.OutputStream.WriteAsync(body.AsMemory(0, 1));
                            await context.Response.OutputStream.FlushAsync();
                            SlowStarted.TrySetResult();
                            await Task.Delay(300, stop.Token);
                            context.Response.Abort();
                        }
                        else await context.Response.OutputStream.WriteAsync(body);
                    }
                    catch (HttpListenerException) { context.Response.Abort(); }
                    finally
                    {
                        try { context.Response.Close(); }
                        catch (InvalidOperationException) { context.Response.Abort(); }
                    }
                }
            }
            catch (Exception) when (stop.IsCancellationRequested) { }
        }
        public void Dispose()
        {
            stop.Cancel(); listener.Close();
            loop.GetAwaiter().GetResult(); stop.Dispose();
        }
    }
}

namespace NAITool.Services
{
    // Isolated test stand-ins for UI localization/app-root services. No user configuration is read.
    internal static class AppPathResolver { internal static string AppRootDir => AppContext.BaseDirectory; }
    internal sealed class LocalizationService
    {
        internal static LocalizationService Instance { get; } = new();
        internal string GetString(string key) => key;
        internal string Format(string key, params object?[] args) => key;
    }
}
