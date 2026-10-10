using System.Text.Json;
using NAITool.Services;
using SkiaSharp;

if (args.Length != 3 || !Uri.TryCreate(args[0], UriKind.Absolute, out var origin) || origin.Host != "127.0.0.1" || !Uri.TryCreate(args[2], UriKind.Absolute, out var mockOrigin) || mockOrigin.Host != "127.0.0.1")
    throw new ArgumentException("Usage: IntegrationTests http://127.0.0.1:port temporary-config-directory http://127.0.0.1:mock-port");
Directory.CreateDirectory(args[1]);
var checks = new List<string>();
void Check(bool condition, string name, string? detail = null)
{
    if (!condition) throw new Exception(name + " failed: " + detail);
    checks.Add(name);
    Console.WriteLine("PASS " + name);
}
using var image = new SKBitmap(16, 16);
image.Erase(SKColors.Blue);
using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
byte[] png = encoded.ToArray();
using var encodedWebp = image.Encode(SKEncodedImageFormat.Webp, 100);
byte[] webp = encodedWebp.ToArray();
using (var fixtureClient = new HttpClient(new HttpClientHandler { UseProxy = false }))
{
    using var fixtureResponse = await fixtureClient.PostAsync(args[2] + "/test-fixture", new ByteArrayContent(webp));
    fixtureResponse.EnsureSuccessStatusCode();
}
foreach (bool legacy in new[] { false, true })
{
    var settings = new SettingsService(Path.Combine(args[1], legacy ? "legacy" : "root"));
    settings.Settings.ApiBaseUrl = args[0] + "/api/novelai" + (legacy ? "/ai/generate-image" : "");
    settings.Settings.AccountAssetProtectionMode = false;
    settings.Settings.DevLogEnabled = false;
    settings.Settings.UseProxy = false;
    settings.Settings.GenParameters.Model = "nai-diffusion-4-5-full";
    settings.Settings.GenParameters.QualityToggle = false;
    settings.Settings.ApiWaitingTimeoutSeconds = 2;
    settings.Settings.ApiGenerationTimeoutSeconds = 5;
    settings.Settings.ApiReadOnlyTimeoutSeconds = 2;
    settings.SetApiTokens(["client-synthetic"]);
    using var service = new NovelAIService(settings);
    string prefix = legacy ? "legacy URL: " : "root URL: ";
    var endpoints = new ApiEndpointResolver(settings.Settings.ApiBaseUrl);
    foreach (string path in new[] { "/ai/generate-image", "/ai/generate-image-stream", "/ai/encode-vibe", "/ai/upscale", "/ai/augment-image", "/user/information", "/user/data", "/user/subscription", "/oa/v1/chat/completions" })
        Check(endpoints.Resolve(path) == args[0] + "/api/novelai" + path, prefix + "localhost credential boundary " + path);
    var connection = await service.TestConnectionAsync("client-synthetic");
    Check(connection.Success, prefix + "account information", connection.Message);
    var account = await service.GetAccountInfoAsync("client-synthetic");
    Check(account?.IsAccountInfoAvailable == true && account.AnlasBalance == 1100, prefix + "account data");
    Check(account?.HasActiveSubscription == true && account.IsOpus && account.ExpiresAt != null && account.V5UsagePercent == 0 && account.V5UsageIsNegative == false,
        prefix + "active unexpired account: zero V5 percent is not exhaustion");
    var generated = await service.GenerateAsync(64, 64, "zip", "");
    Check(generated.ImageBytes is { Length: > 0 } && generated.Error == null, prefix + "ZIP metadata-first result", generated.Error);
    var webpResult = await service.GenerateAsync(64, 64, "webp-response", "");
    Check(webpResult.ImageBytes?.SequenceEqual(webp) == true, prefix + "actual WebP response decoded", webpResult.Error);
    var vibe = await service.EncodeVibeAsync(Convert.ToBase64String(png), "nai-diffusion-4-5-full", 1);
    Check(vibe.VibeData?.SequenceEqual(new byte[] { 1, 2, 3, 4 }) == true, prefix + "vibe", vibe.Error);
    var upscale = await service.UpscaleImageAsync(png);
    Check(upscale.ImageBytes is { Length: > 0 } && upscale.Error == null, prefix + "upscale", upscale.Error);
    var text = await service.GeneratePromptTextAsync("test", "test-model");
    Check(text.Text == "synthetic prompt", prefix + "text SSE", text.Error);
    settings.Settings.StreamGeneration = true;
    foreach (string mode in new[] { "msgpack-final", "sse-final", "msgpack-error", "sse-error", "msgpack-eof", "msgpack-truncated", "sse-eof", "sse-truncated" })
    {
        int previews = 0;
        var progress = new ImmediateProgress(_ => previews++);
        var result = await service.GenerateAsync(64, 64, mode, "", progress: progress);
        bool expectedSuccess = mode.EndsWith("final");
        Check(expectedSuccess ? result.ImageBytes is { Length: > 0 } && result.Error == null : result.ImageBytes == null && result.Error != null,
            prefix + mode, result.Error);
        Check(previews == (expectedSuccess ? 2 : 1), prefix + mode + " progress (preview, plus final only on success)", previews.ToString());
    }
    settings.Settings.StreamGeneration = false;
    var multiple = await service.GenerateAsync(64, 64, "zip-multiple", "");
    Check(multiple.ImageBytes == null && multiple.Error != null, prefix + "multiple ZIP images rejected");
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        var cancellation = await service.GenerateAsync(64, 64, "cancelled-before-send", "", ct: cancelled.Token);
        Check(cancellation.ImageBytes == null && cancellation.Error != null, prefix + "cancel before dispatch");
    }
    var retry = await service.GenerateAsync(64, 64, "no-retry", "");
    Check(retry.ImageBytes == null && service.LastGenerationErrorStatusCode == 429, prefix + "429 failure", retry.Error);
    var hold = service.GenerateAsync(64, 64, "hold", "");
    using var control = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
    var deadline = System.Diagnostics.Stopwatch.StartNew();
    bool active = false;
    while (!active && deadline.Elapsed < TimeSpan.FromSeconds(3))
    {
        using var state = JsonDocument.Parse(await control.GetStringAsync(args[2] + "/test-state"));
        active = state.RootElement.GetProperty("holdActive").GetBoolean();
        if (!active) await Task.Delay(20);
    }
    Check(active, prefix + "generation confirmed active at upstream barrier");
    try
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        account = await service.GetAccountInfoAsync("client-synthetic");
        Check(account?.IsAccountInfoAvailable == true && watch.Elapsed < TimeSpan.FromSeconds(2) && !hold.IsCompleted, prefix + "read bypasses active generation");
    }
    finally { await control.GetStringAsync(args[2] + "/test-release"); }
    var held = await hold;
    Check(held.ImageBytes != null, prefix + "held generation completes", held.Error);
    settings.Settings.GenParameters.Model = "nai-diffusion-5-full";
    settings.Settings.GenParameters.Effort = "medium";
    settings.Settings.GenParameters.Steps = 28;
    settings.Settings.GenParameters.Sampler = "k_dpmpp_2m";
    settings.Settings.GenParameters.Scale = 4.5;
    settings.Settings.GenParameters.CfgRescale = 0.35;
    var medium = await service.GenerateAsync(64, 64, "medium-wire", "high draft negative");
    Check(medium.ImageBytes != null, prefix + "Medium request", medium.Error);
    Check(settings.Settings.GenParameters.Steps == 28 && settings.Settings.GenParameters.Sampler == "k_dpmpp_2m" && settings.Settings.GenParameters.CfgRescale == 0.35,
        prefix + "Medium preserves High parameter draft");
}
Console.WriteLine("RESULT_JSON " + JsonSerializer.Serialize(checks));
sealed class ImmediateProgress(Action<byte[]> callback) : IProgress<byte[]> { public void Report(byte[] value) => callback(value); }
