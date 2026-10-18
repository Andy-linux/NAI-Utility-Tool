using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using SkiaSharp;
using Windows.Storage.Streams;

namespace NAITool.Services;

public class CharacterPromptInfo
{
    public string PositivePrompt { get; set; } = "";
    public string NegativePrompt { get; set; } = "";
    public double CenterX { get; set; } = 0.5;
    public double CenterY { get; set; } = 0.5;
    public bool UseCustomPosition { get; set; }
}

/// <summary>
/// NovelAI API 服务。
/// </summary>
public partial class NovelAIService : IDisposable
{
    private TimeSpan AccountInfoRequestTimeout => NovelAIRequestPolicy.Budget(0, _settings.Settings.ApiReadOnlyTimeoutSeconds, readOnly: true);
    private TimeSpan ImageGenerationRequestTimeout => NovelAIRequestPolicy.Budget(_settings.Settings.ApiWaitingTimeoutSeconds, _settings.Settings.ApiGenerationTimeoutSeconds);
    private TimeSpan TextRequestTimeout => NovelAIRequestPolicy.Budget(_settings.Settings.ApiWaitingTimeoutSeconds, _settings.Settings.ApiTextTimeoutSeconds);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
    };
    private static readonly JsonSerializerOptions LogJsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = true,
    };

    private readonly object _httpClientLock = new();
    private readonly List<HttpClient> _retiredHttpClients = [];
    private HttpClient? _httpClient;
    private string? _httpClientProxyKey;
    private readonly SettingsService _settings;

    public NovelAIService(SettingsService settings) { _settings = settings; }

    public int? LastGenerationErrorStatusCode { get; private set; }

    private static string L(string key) => LocalizationService.Instance.GetString(key);
    private static string Lf(string key, params object?[] args) => LocalizationService.Instance.Format(key, args);
    private static string LocalizedSafeMessage(string key, string fallback)
    {
        string translated = L(key);
        return string.IsNullOrWhiteSpace(translated) || translated == key ? fallback : translated;
    }
    private static string SafeStatus(int status) => LocalizedSafeMessage(NovelAIRequestPolicy.StatusKey(status), NovelAIRequestPolicy.Status(status));
    private static string SafeException(Exception error) => LocalizedSafeMessage(NovelAIRequestPolicy.ExceptionKey(error), NovelAIRequestPolicy.Exception(error));

    private bool UsesCustomApiBaseUrl => _settings.Settings.UsesCustomApiBaseUrl;

    private static CancellationTokenSource CreateRequestTimeoutTokenSource(TimeSpan timeout, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        return cts;
    }

    private static NovelAiAccountInfo UnavailableAccountInfo() => new()
    {
        IsAccountInfoAvailable = false,
    };

    private ApiEndpointResolver GetApiEndpoints() => new(_settings.Settings.ApiBaseUrl);

    private HttpClient GetOrCreateClient()
    {
        string proxyKey = _settings.Settings.UseProxy && !string.IsNullOrEmpty(_settings.Settings.ProxyPort)
            ? _settings.Settings.ProxyPort
            : "";

        lock (_httpClientLock)
        {
            if (_httpClient == null || _httpClientProxyKey != proxyKey)
            {
                if (_httpClient != null)
                    _retiredHttpClients.Add(_httpClient);

                var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
                if (!string.IsNullOrEmpty(proxyKey))
                {
                    handler.Proxy = new WebProxy($"http://127.0.0.1:{proxyKey}");
                    handler.UseProxy = true;
                }

                _httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
                _httpClientProxyKey = proxyKey;
            }

            return _httpClient;
        }
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(
        string token, CancellationToken ct = default)
    {
        try
        {
            var client = GetOrCreateClient();
            using var timeout = CreateRequestTimeoutTokenSource(AccountInfoRequestTimeout, ct);
            string endpointIdentity = GetApiEndpoints().Identity;
            using var request = CreateAuthorizedRequest(HttpMethod.Get, GetApiEndpoints().UserInfoUrl, token);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (endpointIdentity != GetApiEndpoints().Identity) return (false, L("settings.api.configuration_changed"));
            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Unauthorized)
                foreach (var account in _settings.Accounts.Where(a => a.Token == token))
                {
                    account.IsTokenValid = response.StatusCode == HttpStatusCode.OK;
                    if (account.IsTokenValid == false) account.Info = null;
                }
            return response.StatusCode switch
            {
                HttpStatusCode.OK => (true, L("settings.network.test.success")),
                HttpStatusCode.Unauthorized => (false, L("settings.network.test.unauthorized")),
                _ => (false, Lf("settings.network.test.api_error", (int)response.StatusCode, SafeStatus((int)response.StatusCode)))
            };
        }
        catch (HttpRequestException ex) when (SafeException(ex).Contains("proxy", StringComparison.OrdinalIgnoreCase))
        {
            return (false, Lf("settings.network.test.proxy_error", SafeException(ex)));
        }
        catch (Exception ex)
        {
            return (false, Lf("settings.network.test.connection_failed", SafeException(ex)));
        }
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryGetNestedElement(JsonElement element, out JsonElement result, params string[] path)
    {
        result = element;
        foreach (string segment in path)
        {
            if (!TryGetPropertyIgnoreCase(result, segment, out result))
                return false;
        }
        return true;
    }

    private static int? ReadNullableInt(JsonElement element)
    {
        try
        {
            return element.ValueKind switch
            {
                JsonValueKind.Number when element.TryGetInt32(out int intValue) => intValue,
                JsonValueKind.Number when element.TryGetDouble(out double doubleValue) &&
                    double.IsFinite(doubleValue) && doubleValue >= int.MinValue && doubleValue <= int.MaxValue => (int)Math.Round(doubleValue),
                JsonValueKind.String when int.TryParse(element.GetString(), out int stringValue) => stringValue,
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 根据官方 Account API 规范解析账户信息。
    /// /user/data 返回 UserAccountDataResponse：
    ///   { subscription: { tier: int(0-3), active: bool, trainingStepsLeft: int|object, ... }, ... }
    /// tier: 0=Paper, 1=Tablet, 2=Scroll, 3=Opus
    /// trainingStepsLeft: Anlas 余额 (整数 或 { fixedTrainingStepsLeft, purchasedTrainingSteps })
    /// </summary>
    public async Task<NovelAiAccountInfo?> GetAccountInfoAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        try
        {
            using var requestTimeoutCts = CreateRequestTimeoutTokenSource(AccountInfoRequestTimeout, ct);
            var requestCt = requestTimeoutCts.Token;
            var client = GetOrCreateClient();
            string endpointIdentity = GetApiEndpoints().Identity;
            using var request = CreateAuthorizedRequest(HttpMethod.Get, GetApiEndpoints().UserDataUrl, token);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestCt);
            if (endpointIdentity != GetApiEndpoints().Identity) return UnavailableAccountInfo();
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    foreach (var account in _settings.Accounts.Where(a => a.Token == token))
                    {
                        account.IsTokenValid = false;
                        account.Info = null;
                    }
                Debug.WriteLine($"[NAI] /user/data request failed: {(int)response.StatusCode}");
                return UsesCustomApiBaseUrl && response.StatusCode != HttpStatusCode.Unauthorized
                    ? UnavailableAccountInfo()
                    : null;
            }

            using var accountStream = await response.Content.ReadAsStreamAsync(requestCt);
            string json = Encoding.UTF8.GetString(await NovelAIImageResponse.ReadBoundedAsync(accountStream, requestCt, 64 * 1024));
            if (endpointIdentity != GetApiEndpoints().Identity) return UnavailableAccountInfo();
            // Omit account response bodies from logs.

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!TryGetPropertyIgnoreCase(root, "subscription", out var sub))
            {
                Debug.WriteLine("[NAI] subscription field was not found in the response");
                return UsesCustomApiBaseUrl ? UnavailableAccountInfo() : null;
            }

            // ── 订阅等级 ──
            // 官方字段: subscription.tier (int: 0=Paper, 1=Tablet, 2=Scroll, 3=Opus)
            int? tier = null;
            if (TryGetPropertyIgnoreCase(sub, "tier", out var tierEl))
                tier = ReadNullableInt(tierEl);



            bool isOpus = tier.HasValue && tier.Value >= 3;
            bool hasActiveField = TryGetPropertyIgnoreCase(sub, "active", out var activeEl);
            bool active = hasActiveField && (activeEl.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(activeEl.GetString(), out bool value) && value,
                _ => false,
            });
            string tierName = tier switch
            {
                0 => "Paper",
                1 => "Tablet",
                2 => "Scroll",
                3 => "Opus",
                _ => tier.HasValue ? $"Tier{tier}" : "",
            };

            // ── Anlas 余额 ──
            // 官方字段: subscription.trainingStepsLeft
            // 实际可能是整数 (参考 ComfyUI-NAIDGenerator) 或对象 (参考 Swagger spec)
            int? anlas = null;
            if (TryGetPropertyIgnoreCase(sub, "trainingStepsLeft", out var stepsEl))
            {
                if (stepsEl.ValueKind == JsonValueKind.Number)
                {
                    anlas = ReadNullableInt(stepsEl);

                }
                else if (stepsEl.ValueKind == JsonValueKind.Object)
                {
                    int? fixedSteps = null, purchasedSteps = null;
                    if (TryGetPropertyIgnoreCase(stepsEl, "fixedTrainingStepsLeft", out var fixedEl))
                        fixedSteps = ReadNullableInt(fixedEl);
                    if (TryGetPropertyIgnoreCase(stepsEl, "purchasedTrainingSteps", out var purchasedEl))
                        purchasedSteps = ReadNullableInt(purchasedEl);
                    anlas = fixedSteps.HasValue && purchasedSteps.HasValue
                        ? (int)Math.Clamp((long)fixedSteps.Value + purchasedSteps.Value, 0, int.MaxValue) : null;

                }
                else
                {
                    Debug.WriteLine($"[NAI] trainingStepsLeft has an unknown value kind: {stepsEl.ValueKind}");
                }
            }
            else
            {
                Debug.WriteLine("[NAI] trainingStepsLeft field was not found in subscription");
            }

            // NovelAI Diffusion V5 uses a separate, continuously refilling Opus allowance.
            // Unknown allowance remains unknown; percent alone does not establish exhaustion.
            int? v5UsagePercent = null;
            bool? v5UsageIsNegative = null;
            int? v5UsageTimeUntilNextPercentSeconds = null;
            if (TryGetPropertyIgnoreCase(sub, "usage", out var usageEl) &&
                usageEl.ValueKind == JsonValueKind.Object)
            {
                if (TryGetPropertyIgnoreCase(usageEl, "percent", out var percentEl))
                {
                    int? parsedPercent = ReadNullableInt(percentEl);
                    if (parsedPercent.HasValue)
                        v5UsagePercent = Math.Max(parsedPercent.Value, 0);
                }

                if (TryGetPropertyIgnoreCase(usageEl, "isNegative", out var negativeEl) &&
                    negativeEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    v5UsageIsNegative = negativeEl.GetBoolean();
                    // Preserve percent independently of the exhaustion flag.
                }

                if (TryGetPropertyIgnoreCase(usageEl, "timeUntilNextPercent", out var nextPercentTimeEl))
                {
                    int? parsedSeconds = ReadNullableInt(nextPercentTimeEl);
                    if (parsedSeconds.HasValue)
                        v5UsageTimeUntilNextPercentSeconds = Math.Max(parsedSeconds.Value, 0);
                }
            }

            string? expiresAt = null;
            bool expired = true; // Missing expiry does not establish an active, free-eligible subscription.
            if (TryGetPropertyIgnoreCase(sub, "expiresAt", out var expiresEl) && expiresEl.ValueKind == JsonValueKind.Number)
            {
                long ts = expiresEl.GetInt64();
                if (ts > 10_000_000_000L)
                    ts /= 1000;

                var expiresAtOffset = DateTimeOffset.FromUnixTimeSeconds(ts);
                expired = expiresAtOffset <= DateTimeOffset.UtcNow;
                expiresAt = expiresAtOffset.ToLocalTime().ToString("yyyy-MM-dd");
            }

            bool hasActiveSubscription = tier.HasValue && tier.Value > 0 && active && !expired;



            return new NovelAiAccountInfo
            {
                AnlasBalance = anlas,
                V5UsagePercent = v5UsagePercent,
                V5UsageIsNegative = v5UsageIsNegative,
                V5UsageTimeUntilNextPercentSeconds = v5UsageTimeUntilNextPercentSeconds,
                TierName = tierName,
                IsOpus = isOpus,
                HasActiveSubscription = hasActiveSubscription,
                TierLevel = tier,
                ExpiresAt = expiresAt,
                IsAccountInfoAvailable = true,
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NAI] Account query failed: {ex.GetType().Name}");
            return UsesCustomApiBaseUrl ? UnavailableAccountInfo() : null;
        }
    }

    internal static bool IsV4PlusModel(string model) =>
        model.Contains("-4", StringComparison.Ordinal) ||
        model.Contains("-5", StringComparison.Ordinal);

    private static bool IsV45Model(string model) =>
        model.Contains("4-5", StringComparison.Ordinal);

    internal static bool IsV5Model(string model) => NovelAiAnlasCalculator.IsV5Model(model);

    private static bool SupportsVibeTransferModel(string model) => ModelCapabilities.BaseModel(model) is
        "nai-diffusion-3" or "nai-diffusion-furry-3" or "nai-diffusion-4-full" or
        "nai-diffusion-4-curated" or "nai-diffusion-4-curated-preview" or
        "nai-diffusion-4-5-full" or "nai-diffusion-4-5-curated";

    private static bool SupportsPreciseReferenceModel(string model) =>
        IsV45Model(model);

    private static string ApplyQualityTags(string prompt, NAIParameters parameters)
    {
        return NovelAiPromptPresets.AddQuality(prompt, parameters);
    }

    private static void ApplyVerifiedModelParameters(Dictionary<string, object?> parameters, NAIParameters p, int width, int height)
    {
        parameters["image_format"] = "webp";
        parameters["tag_hint_qt"] = NovelAiPromptPresets.HintId(p.QualityToggle ? p.QualityPresetId ?? "standard" : "none");
        parameters["tag_hint_uc_preset"] = NovelAiPromptPresets.HintId(NovelAiPromptPresets.ResolveUcId(p));
        parameters.Remove("ucPreset"); parameters.Remove("uc_preset");
        parameters.Remove("qualityToggle"); parameters.Remove("quality_toggle");
        parameters.Remove("uc");
        if (!IsV5Model(p.Model) || !p.TagHintTransparentBackground)
            parameters.Remove("tag_hint_transparent_background");
        if (!IsV5Model(p.Model)) parameters.Remove("straight_alpha");
        var sigma = ModelCapabilities.GetSkipCfgAboveSigma(p.Model, width, height, p.Variety);
        if (sigma.HasValue) parameters["skip_cfg_above_sigma"] = sigma.Value;
        else parameters.Remove("skip_cfg_above_sigma");
        // Official V4+ capabilities disable Decrisper independently of CFG Rescale.
        if (IsV4PlusModel(p.Model)) parameters["dynamic_thresholding"] = false;
        if (ModelCapabilities.IsMedium(p)) parameters.Remove("cfg_rescale");
        if (IsV5Model(p.Model))
        {
            parameters.Remove("sm"); parameters.Remove("sm_dyn");
            parameters["noise_schedule"] = "karras";
        }
        else if (p.Sampler is "ddim" or "plms" or "k_lms" or "nai_smea" or "nai_smea_dyn" or "ddim_v3" or "k_dpm_fast")
            parameters.Remove("noise_schedule");
    }

    private static string ToApiPreciseReferenceType(PreciseReferenceType type) => type switch
    {
        PreciseReferenceType.Character => "character",
        PreciseReferenceType.Style => "style",
        _ => "character&style",
    };

    private static string PrepareDirectorReferenceImage(string base64Image)
    {
        byte[] bytes = Convert.FromBase64String(base64Image);
        // Strip recognized stealth alpha bits before filtering, as for the website's reference input.
        using var bitmap = InpaintImageProcessing.DecodeClean(bytes);
        if (bitmap == null)
            return base64Image;

        double srcRatio = (double)bitmap.Width / bitmap.Height;
        var target = new[] { (Width: 1024, Height: 1536), (Width: 1536, Height: 1024), (Width: 1472, Height: 1472) }
            .OrderBy(size => Math.Abs((double)size.Width / size.Height - srcRatio)).First();
        int TargetW = target.Width, TargetH = target.Height;
        double tgtRatio = (double)TargetW / TargetH;

        int newW, newH;
        if (srcRatio > tgtRatio)
        {
            newW = TargetW;
            newH = Math.Max(1, (int)Math.Floor(TargetW / srcRatio + .5));
        }
        else
        {
            newH = TargetH;
            newW = Math.Max(1, (int)Math.Floor(TargetH * srcRatio + .5));
        }

        using var resized = LanczosImageResize.Resize(bitmap, newW, newH);

        using var canvas = new SKBitmap(TargetW, TargetH, SKColorType.Rgb888x, SKAlphaType.Opaque);
        canvas.Erase(SKColors.Black);
        using var c = new SKCanvas(canvas);
        int offsetX = (int)Math.Floor((TargetW - newW) / 2.0 + .5);
        int offsetY = (int)Math.Floor((TargetH - newH) / 2.0 + .5);
        c.DrawBitmap(resized, offsetX, offsetY);

        using var image = SKImage.FromBitmap(canvas);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return Convert.ToBase64String(data.ToArray());
    }

    private static string PrepareV3ReferenceImage(string base64Image)
    {
        const int size = 448;
        using var source = InpaintImageProcessing.DecodeClean(Convert.FromBase64String(base64Image));
        if (source == null) throw new InvalidDataException("Unable to decode the V3 reference image.");
        double scale = Math.Min((double)size / source.Width, (double)size / source.Height);
        int width = Math.Max(1, (int)Math.Floor(source.Width * scale + .5));
        int height = Math.Max(1, (int)Math.Floor(source.Height * scale + .5));
        using var resized = LanczosImageResize.Resize(source, width, height);
        int offsetX = (int)Math.Floor((size - width) / 2.0 + .5);
        int offsetY = (int)Math.Floor((size - height) / 2.0 + .5);
        var pixels = new SKColor[size * size];
        Array.Fill(pixels, SKColors.Black);
        var input = resized.Pixels;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            var color = input[y * width + x];
            double alpha = color.Alpha / 255.0;
            pixels[(y + offsetY) * size + x + offsetX] = new SKColor(
                (byte)Math.Round(color.Red * alpha), (byte)Math.Round(color.Green * alpha),
                (byte)Math.Round(color.Blue * alpha), 255);
        }
        using var canvas = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        canvas.Pixels = pixels;
        using var image = SKImage.FromBitmap(canvas);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return Convert.ToBase64String(data.ToArray());
    }

    private async Task EncodeUnencodedVibesAsync(
        List<VibeTransferInfo> vibeTransfers, string model, CancellationToken ct)
    {
        for (int i = 0; i < vibeTransfers.Count; i++)
        {
            var v = vibeTransfers[i];
            if (v.IsEncoded)
            {
                if (!VibeTransferInfo.IsCompatibleEncoding(v.EncodingModel, model))
                    throw new InvalidOperationException($"Vibe encoding is unavailable for {model}: {v.FileName}");
                continue;
            }

            var (vibeData, error) = await EncodeVibeAsync(v.ImageBase64, model, v.InformationExtracted, ct);
            if (vibeData != null)
            {
                v.ImageBase64 = Convert.ToBase64String(vibeData);
                v.IsEncoded = true;
                v.EncodingModel = model;
            }
            else
            {
                throw new InvalidOperationException(
                    $"Vibe encoding failed for '{v.FileName}': {error}");
            }
        }
    }

    private static void ApplyVibeTransferParameters(
        Dictionary<string, object?> parameters,
        List<VibeTransferInfo> vibeTransfers,
        string model)
    {
        if (vibeTransfers.Count == 0)
            return;

        parameters["reference_image_multiple"] = vibeTransfers.Select(x =>
            ModelCapabilities.SupportsSmea(model) ? PrepareV3ReferenceImage(x.ImageBase64) : x.ImageBase64).ToList();
        var strengths = vibeTransfers.Select(x => x.Strength).ToList();
        if (vibeTransfers.All(x => x.IsEncoded))
        {
            bool normalize = vibeTransfers[0].NormalizeStrength;
            double total = strengths.Sum(x => Math.Abs(x));
            if (normalize && strengths.Count > 1 && total > 1)
                strengths = strengths.Select(x => x / total).ToList();
            parameters["normalize_reference_strength_multiple"] = normalize;
        }
        parameters["reference_strength_multiple"] = strengths;
        if (!vibeTransfers.All(x => x.IsEncoded))
            parameters["reference_information_extracted_multiple"] = vibeTransfers.Select(x => x.InformationExtracted).ToList();
    }

    private static void ApplyPreciseReferenceParameters(
        Dictionary<string, object?> parameters,
        List<PreciseReferenceInfo> preciseReferences)
    {
        if (preciseReferences.Count == 0)
            return;

        parameters["director_reference_images"] = preciseReferences
            .Select(x => PrepareDirectorReferenceImage(x.ImageBase64)).ToList();
        parameters["director_reference_strength_values"] = preciseReferences
            .Select(x => Math.Clamp(x.Strength, 0, 1)).ToList();
        parameters["director_reference_secondary_strength_values"] = preciseReferences
            .Select(x => 1.0 - Math.Clamp(x.Fidelity, 0, 1)).ToList();
        parameters["director_reference_information_extracted"] = preciseReferences
            .Select(_ => 1.0).ToList();
        parameters["director_reference_descriptions"] = preciseReferences
            .Select(x => new Dictionary<string, object?>
            {
                ["caption"] = new Dictionary<string, object?>
                {
                    ["base_caption"] = ToApiPreciseReferenceType(x.ReferenceType),
                    ["char_captions"] = new List<object>(),
                },
                ["legacy_uc"] = false,
            }).ToList();
    }

    internal BusinessLog BeginBusinessLog(object input, bool unassociatedUiReport = false) => new(
        _settings.Settings.DevLogEnabled, _settings.Settings.DevLogMode == "Debug",
        Path.Combine(AppPathResolver.AppRootDir, "logs"), _settings.Accounts.Select(a => a.Token), input, unassociatedUiReport);

    private void WriteRequestLog(
        string requestName,
        Dictionary<string, object> payload,
        long elapsedMs,
        HttpResponseMessage? response = null,
        string? responseText = null,
        byte[]? imageBytes = null,
        Exception? exception = null)
    {
        BusinessLog.Current?.Stage(exception != null ? "request_failure" : "parsed_result",
            new { requestName, preparation_and_request_elapsed_ms = elapsedMs,
                parse_outcome = exception != null ? "failed" : imageBytes != null || responseText != null ? "result" : "no_result",
                responseText, exception = exception?.ToString() });
    }

    private static bool IsPngOrWebp(byte[] bytes)
    {
        bool isPng = bytes.Length > 4 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;
        bool isWebp = bytes.Length > 12 &&
            bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
            bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50;

        return isPng || isWebp;
    }

    private static bool IsDecodableImage(byte[] bytes)
    {
        try
        {
            using var codec = SKCodec.Create(new MemoryStream(bytes));
            if (codec == null || codec.Info.Width <= 0 || codec.Info.Height <= 0 ||
                (long)codec.Info.Width * codec.Info.Height > 16_777_216) return false;
            using var bitmap = SKBitmap.Decode(bytes);
            return bitmap != null && bitmap.Width > 0 && bitmap.Height > 0;
        }
        catch { return false; }
    }

    private static async Task<byte[]?> ReadGeneratedImageBytesAsync(HttpContent content, CancellationToken ct, bool findImageEntry = false)
    {
        using var stream = await content.ReadAsStreamAsync(ct);
        return await NovelAIImageResponse.ReadSingleAsync(await NovelAIImageResponse.ReadBoundedAsync(stream, ct), IsDecodableImage, ct,
            findImageEntry ? name => name.StartsWith("image", StringComparison.Ordinal) && name.EndsWith(".png", StringComparison.Ordinal) : null);
    }

    private async Task<byte[]?> ReadGeneratedImageStreamAsync(
        HttpContent content,
        IProgress<byte[]>? progress,
        CancellationToken ct)
    {
        using var stream = await content.ReadAsStreamAsync(ct);
        return await NovelAIImageStream.ReadAsync(stream, content.Headers.ContentType?.MediaType,
            IsDecodableImage, progress, ct);
    }

    public async Task<(byte[]? ImageBytes, string? Error)> InpaintAsync(
        string imageBase64, string maskBase64,
        int width, int height,
        string prompt, string negativePrompt,
        List<CharacterPromptInfo>? characters = null,
        List<VibeTransferInfo>? vibeTransfers = null,
        List<PreciseReferenceInfo>? preciseReferences = null,
        IProgress<byte[]>? progress = null,
        CancellationToken ct = default)
    {
        using var diagnostic = BeginBusinessLog(new { operation = "inpaint", imageBase64, maskBase64, width, height, prompt, negativePrompt, characters, vibeTransfers, preciseReferences, parameters = _settings.Settings.InpaintParameters });
        if (!_settings.HasApiTokens)
        {
            diagnostic.Stage("request_rejected", new { reason = "missing_api_token" });
            return (null, L("api.error.token_missing_network"));
        }
        var naiParams = ModelCapabilities.CreateRequestSnapshot(_settings.Settings.InpaintParameters);
        bool streamGeneration = _settings.Settings.StreamGeneration && IsV4PlusModel(naiParams.Model);
        if (!_settings.Settings.UsesCustomApiBaseUrl &&
            NovelAiAnlasCalculator.PaidBaseCost(naiParams.Model, width, height, naiParams.Steps,
                sm: false, strength: IsV4PlusModel(naiParams.Model) ? naiParams.InpaintStrength : 1) > NovelAiAnlasCalculator.MaxBaseCost)
        {
            diagnostic.Stage("request_rejected", new { reason = "generation_cost_limit", maximum_base_cost = NovelAiAnlasCalculator.MaxBaseCost });
            return (null, Lf("api.error.generation_cost_limit", NovelAiAnlasCalculator.MaxBaseCost));
        }

        string resolvedSeed = SeedValue.Resolve(naiParams.Seed);
        object seed = SeedValue.ToRequestValue(resolvedSeed);
        bool isV4Plus = IsV4PlusModel(naiParams.Model);
        bool isV45 = IsV45Model(naiParams.Model);
        string effectivePrompt = ApplyQualityTags(prompt, naiParams);
        string effectiveNegativePrompt = NovelAiPromptPresets.ResolveUc(naiParams.Model, NovelAiPromptPresets.ResolveUcId(naiParams), effectivePrompt, ModelCapabilities.IsMedium(naiParams) ? "" : negativePrompt);

        if (IsV5Model(naiParams.Model)) effectivePrompt = NovelAiPromptPresets.AddAutoText(effectivePrompt, characters);

        var parameters = new Dictionary<string, object?>
        {
            ["params_version"] = 4,
            ["width"] = width,
            ["height"] = height,
            ["scale"] = naiParams.Scale,
            ["sampler"] = naiParams.Sampler,
            ["steps"] = naiParams.Steps,
            ["n_samples"] = 1,
            ["seed"] = seed,
            ["image"] = imageBase64,
            // The reduced mask is for feathering; transport uses full-size nearest-expanded black/white pixels.
            ["mask"] = InpaintImageProcessing.PrepareApiMask(maskBase64, width, height),
            ["noise_schedule"] = naiParams.Schedule,
            ["uc"] = effectiveNegativePrompt,
            ["negative_prompt"] = effectiveNegativePrompt,
            ["ucPreset"] = naiParams.UcPreset,
            ["uc_preset"] = naiParams.UcPreset,
            ["add_original_image"] = false,
            ["cfg_rescale"] = naiParams.CfgRescale,
            ["legacy"] = false,
            ["legacy_v3_extend"] = false,
            ["dynamic_thresholding"] = naiParams.DynamicThresholding,
            ["skip_cfg_above_sigma"] = null,
            ["strength"] = Math.Clamp(naiParams.DenoiseStrength, 0, 1),
            ["noise"] = Math.Clamp(naiParams.DenoiseNoise, 0, 1),
            ["qualityToggle"] = naiParams.QualityToggle,
            ["quality_toggle"] = naiParams.QualityToggle,
        };
        if (BigInteger.TryParse(resolvedSeed, NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out var numericSeed))
            parameters["extra_noise_seed"] = SeedValue.ToRequestValue(
                (numericSeed - 1).ToString(CultureInfo.InvariantCulture));
        if (isV4Plus && naiParams.InpaintStrength != 1)
            parameters["img2img"] = new Dictionary<string, object>
            {
                ["strength"] = Math.Clamp(naiParams.InpaintStrength, 0, 1),
                ["color_correct"] = true,
            };
        if (IsV5Model(naiParams.Model))
        {
            parameters["tag_hint_transparent_background"] = naiParams.TagHintTransparentBackground;
            parameters["straight_alpha"] = naiParams.StraightAlpha;
        }

        ApplyVerifiedModelParameters(parameters, naiParams, width, height);
        if (streamGeneration) parameters["stream"] = "msgpack";

        if (naiParams.Sampler == "k_euler_ancestral" && naiParams.Schedule != "native")
        {
            parameters["deliberate_euler_ancestral_bug"] = false;
            parameters["prefer_brownian"] = true;
        }

        if (isV4Plus)
        {
            var posCharCaptions = new List<object>();
            var negCharCaptions = new List<object>();
            bool useCoords = false;

            if (characters is { Count: > 0 })
            {
                foreach (var ch in characters.Where(ch => ch.PositivePrompt.Length > 0))
                {
                    var center = new Dictionary<string, double>
                    {
                        ["x"] = IsV5Model(naiParams.Model) ? ch.CenterX : 0.1 + 0.2 * Math.Clamp((int)Math.Floor(5 * ch.CenterX), 0, 4),
                        ["y"] = IsV5Model(naiParams.Model) ? ch.CenterY : 0.1 + 0.2 * Math.Clamp((int)Math.Floor(5 * ch.CenterY), 0, 4),
                    };
                    posCharCaptions.Add(new Dictionary<string, object>
                    {
                        ["char_caption"] = ch.PositivePrompt,
                        ["centers"] = new[] { center },
                    });
                    negCharCaptions.Add(new Dictionary<string, object>
                    {
                        ["char_caption"] = ModelCapabilities.IsMedium(naiParams) ? "" : ch.NegativePrompt,
                        ["centers"] = new[] { center },
                    });
                    if (ch.UseCustomPosition)
                        useCoords = true;
                }
            }

            parameters["use_coords"] = useCoords;
            parameters["v4_prompt"] = new Dictionary<string, object>
            {
                ["caption"] = new Dictionary<string, object>
                {
                    ["base_caption"] = effectivePrompt,
                    ["char_captions"] = posCharCaptions,
                },
                ["use_coords"] = useCoords,
                ["use_order"] = true,
            };
            parameters["v4_negative_prompt"] = new Dictionary<string, object>
            {
                ["caption"] = new Dictionary<string, object>
                {
                    ["base_caption"] = effectiveNegativePrompt,
                    ["char_captions"] = negCharCaptions,
                },
                ["use_coords"] = useCoords,
                ["use_order"] = false,
                ["legacy_uc"] = naiParams.LegacyUc && ModelCapabilities.SupportsLegacyUc(naiParams.Model),
            };
        }
        else
        {
            // SMEA is unsupported for image-based requests.
            parameters["sm"] = false;
            parameters["sm_dyn"] = false;
        }

        if (preciseReferences is { Count: > 0 } && SupportsPreciseReferenceModel(naiParams.Model))
            ApplyPreciseReferenceParameters(parameters, preciseReferences);
        else if (vibeTransfers is { Count: > 0 } && SupportsVibeTransferModel(naiParams.Model) && !isV4Plus)
        {
            ApplyVibeTransferParameters(parameters, vibeTransfers, naiParams.Model);
        }

        var payload = new Dictionary<string, object>
        {
            ["input"] = effectivePrompt,
            ["model"] = naiParams.Model,
            ["action"] = "infill",
            ["parameters"] = parameters,
        };
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var content = diagnostic.Content(payload);
            var endpoints = GetApiEndpoints();
            var url = streamGeneration ? endpoints.GenerateStreamUrl : endpoints.GenerateUrl;
            using var requestTimeoutCts = CreateRequestTimeoutTokenSource(ImageGenerationRequestTimeout, ct);
            var requestCt = requestTimeoutCts.Token;
            var account = await SelectAccountAsync(ApiRequestCost.Image(naiParams.Model, width, height, naiParams.Steps,
                    strength: IsV4PlusModel(naiParams.Model) ? naiParams.InpaintStrength : 1,
                    referenceAnlas: GenerationReferenceAnlas(naiParams.Model, vibeTransfers, preciseReferences),
                    characterReference: SupportsPreciseReferenceModel(naiParams.Model) && preciseReferences is { Count: > 0 }, smDyn: false), requestCt);
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            if (streamGeneration)
                request.Headers.Accept.ParseAdd("application/octet-stream");
            using var response = await SendAccountRequestAsync(request,
                account,
                HttpCompletionOption.ResponseHeadersRead,
                requestCt);

            if (!response.IsSuccessStatusCode)
            {
                var errorText = SafeStatus((int)response.StatusCode);
                stopwatch.Stop();
                WriteRequestLog("Inpaint generation", payload, stopwatch.ElapsedMilliseconds, response, errorText);
                return (null, Lf("api.error.status", (int)response.StatusCode, errorText));
            }

            byte[]? imageBytes = streamGeneration
                ? await ReadGeneratedImageStreamAsync(response.Content, progress, requestCt)
                : await ReadGeneratedImageBytesAsync(response.Content, requestCt);
            if (imageBytes == null)
            {
                stopwatch.Stop();
                WriteRequestLog("Inpaint generation", payload, stopwatch.ElapsedMilliseconds, response);
                return (null, L("api.error.empty_zip"));
            }

            progress?.Report(imageBytes);
            stopwatch.Stop();
            WriteRequestLog("Inpaint generation", payload, stopwatch.ElapsedMilliseconds, response, imageBytes: imageBytes);
            return (imageBytes, null);
        }
        catch (TaskCanceledException ex)
        {
            stopwatch.Stop();
            WriteRequestLog("Inpaint generation", payload, stopwatch.ElapsedMilliseconds, exception: ex);
            return (null, SafeException(ex));
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            WriteRequestLog("Inpaint generation", payload, stopwatch.ElapsedMilliseconds, exception: ex);
            var msg = Lf("api.error.network_failed", SafeException(ex));
            if (SafeException(ex).Contains("proxy", StringComparison.OrdinalIgnoreCase))
                msg += L("api.error.proxy_hint");
            return (null, msg);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            WriteRequestLog("Inpaint generation", payload, stopwatch.ElapsedMilliseconds, exception: ex);
            return (null, Lf("api.error.request_failed", SafeException(ex)));
        }
    }

    internal static string PrepareEnhancedImageBase64(byte[] imageBytes, int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > EnhanceRules.MaxPixels)
            throw new ArgumentOutOfRangeException(nameof(width), "Enhance dimensions exceed the supported pixel range.");

        // Enhance sends the selected result itself; target dimensions belong to request parameters.
        return Convert.ToBase64String(imageBytes);
    }

    private static string AddEnhancePromptHint(string prompt)
    {
        if (prompt.Contains("upscaled, blurry", StringComparison.Ordinal))
            return prompt;
        const string hint = ", -2::upscaled, blurry::,";
        var marker = NovelAiPromptPresets.TextMarker.Match(prompt);
        return marker.Success ? prompt.Insert(marker.Index, hint) : prompt + hint;
    }

    public async Task<(byte[]? ImageBytes, string? Error)> ImageToImageAsync(
        string imageBase64,
        int width, int height,
        string prompt, string negativePrompt,
        List<CharacterPromptInfo>? characters = null,
        List<VibeTransferInfo>? vibeTransfers = null,
        List<PreciseReferenceInfo>? preciseReferences = null,
        IProgress<byte[]>? progress = null,
        CancellationToken ct = default,
        NAIParameters? parametersOverride = null,
        bool isEnhance = false,
        bool upscaledEnhance = false)
    {
        using var diagnostic = BeginBusinessLog(new { operation = isEnhance ? "enhance" : "i2i", imageBase64, width, height, prompt, negativePrompt, characters, vibeTransfers, preciseReferences, parameters = parametersOverride ?? _settings.Settings.I2IDenoiseParameters });
        if (!_settings.HasApiTokens)
        {
            diagnostic.Stage("request_rejected", new { reason = "missing_api_token" });
            return (null, L("api.error.token_missing_network"));
        }
        var naiParams = ModelCapabilities.CreateRequestSnapshot(parametersOverride ?? _settings.Settings.I2IDenoiseParameters);
        bool streamGeneration = _settings.Settings.StreamGeneration && IsV4PlusModel(naiParams.Model);
        if ((isEnhance && (width <= 0 || height <= 0 || (long)width * height > EnhanceRules.MaxPixels)) ||
            (upscaledEnhance && (!isEnhance || !EnhanceRules.CanUseMax(naiParams.Model, width, height))))
        {
            diagnostic.Stage("request_rejected", new { reason = "enhance_size_or_max_upscale_constraint" });
            return (null, L("enhance.size_limit"));
        }

        var pricedSize = upscaledEnhance
            ? EnhanceRules.GetOutputDimensions(width, height, 1.0, useMax: true)
            : (Width: width, Height: height);
        if (!_settings.Settings.UsesCustomApiBaseUrl &&
            NovelAiAnlasCalculator.PaidBaseCost(naiParams.Model, pricedSize.Width, pricedSize.Height, naiParams.Steps,
                sm: false, strength: naiParams.DenoiseStrength) > NovelAiAnlasCalculator.MaxBaseCost)
        {
            diagnostic.Stage("request_rejected", new { reason = "generation_cost_limit", maximum_base_cost = NovelAiAnlasCalculator.MaxBaseCost });
            return (null, Lf("api.error.generation_cost_limit", NovelAiAnlasCalculator.MaxBaseCost));
        }

        string resolvedSeed = SeedValue.Resolve(naiParams.Seed);
        object seed = SeedValue.ToRequestValue(resolvedSeed);
        bool isV4Plus = IsV4PlusModel(naiParams.Model);
        bool isV45 = IsV45Model(naiParams.Model);
        string effectivePrompt = ApplyQualityTags(prompt, naiParams);
        if (isEnhance && !upscaledEnhance && (isV45 || IsV5Model(naiParams.Model)))
            effectivePrompt = AddEnhancePromptHint(effectivePrompt);
        string effectiveNegativePrompt = NovelAiPromptPresets.ResolveUc(naiParams.Model, NovelAiPromptPresets.ResolveUcId(naiParams), effectivePrompt, ModelCapabilities.IsMedium(naiParams) ? "" : negativePrompt);

        if (IsV5Model(naiParams.Model)) effectivePrompt = NovelAiPromptPresets.AddAutoText(effectivePrompt, characters);

        var parameters = new Dictionary<string, object?>
        {
            ["params_version"] = 4,
            ["width"] = width,
            ["height"] = height,
            ["scale"] = naiParams.Scale,
            ["sampler"] = naiParams.Sampler,
            ["steps"] = naiParams.Steps,
            ["n_samples"] = 1,
            ["seed"] = seed,
            ["image"] = imageBase64,
            ["noise_schedule"] = naiParams.Schedule,
            ["uc"] = effectiveNegativePrompt,
            ["negative_prompt"] = effectiveNegativePrompt,
            ["ucPreset"] = naiParams.UcPreset,
            ["uc_preset"] = naiParams.UcPreset,
            ["cfg_rescale"] = naiParams.CfgRescale,
            ["legacy"] = false,
            ["legacy_v3_extend"] = false,
            ["dynamic_thresholding"] = naiParams.DynamicThresholding,
            ["skip_cfg_above_sigma"] = null,
            ["strength"] = Math.Clamp(naiParams.DenoiseStrength, 0, 1),
            ["noise"] = Math.Clamp(naiParams.DenoiseNoise, 0, 1),
            ["qualityToggle"] = naiParams.QualityToggle,
            ["quality_toggle"] = naiParams.QualityToggle,
        };
        parameters["sm"] = false;
        parameters["sm_dyn"] = false;
        if (BigInteger.TryParse(resolvedSeed, NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out var numericSeed))
            parameters["extra_noise_seed"] = SeedValue.ToRequestValue(
                (numericSeed - 1).ToString(CultureInfo.InvariantCulture));
        if (isEnhance)
        {
            parameters["params_version"] = 4;
            parameters["add_original_image"] = true;
            parameters["color_correct"] = false;
            parameters["sm"] = false;
            parameters["sm_dyn"] = false;
            if (upscaledEnhance) parameters["upscaled_enhance"] = true;
        }
        if (IsV5Model(naiParams.Model))
        {
            parameters["tag_hint_transparent_background"] = naiParams.TagHintTransparentBackground;
            parameters["straight_alpha"] = naiParams.StraightAlpha;
        }

        ApplyVerifiedModelParameters(parameters, naiParams, width, height);
        if (streamGeneration) parameters["stream"] = "msgpack";

        if (naiParams.Sampler == "k_euler_ancestral" && naiParams.Schedule != "native")
        {
            parameters["deliberate_euler_ancestral_bug"] = false;
            parameters["prefer_brownian"] = true;
        }

        if (isV4Plus)
        {
            var posCharCaptions = new List<object>();
            var negCharCaptions = new List<object>();
            bool useCoords = false;

            if (characters is { Count: > 0 })
            {
                foreach (var ch in characters.Where(ch => ch.PositivePrompt.Length > 0))
                {
                    var center = new Dictionary<string, double>
                    {
                        ["x"] = IsV5Model(naiParams.Model) ? ch.CenterX : 0.1 + 0.2 * Math.Clamp((int)Math.Floor(5 * ch.CenterX), 0, 4),
                        ["y"] = IsV5Model(naiParams.Model) ? ch.CenterY : 0.1 + 0.2 * Math.Clamp((int)Math.Floor(5 * ch.CenterY), 0, 4),
                    };
                    posCharCaptions.Add(new Dictionary<string, object>
                    {
                        ["char_caption"] = ch.PositivePrompt,
                        ["centers"] = new[] { center },
                    });
                    negCharCaptions.Add(new Dictionary<string, object>
                    {
                        ["char_caption"] = ModelCapabilities.IsMedium(naiParams) ? "" : ch.NegativePrompt,
                        ["centers"] = new[] { center },
                    });
                    if (ch.UseCustomPosition)
                        useCoords = true;
                }
            }

            parameters["use_coords"] = useCoords;
            parameters["v4_prompt"] = new Dictionary<string, object>
            {
                ["caption"] = new Dictionary<string, object>
                {
                    ["base_caption"] = effectivePrompt,
                    ["char_captions"] = posCharCaptions,
                },
                ["use_coords"] = useCoords,
                ["use_order"] = true,
            };
            parameters["v4_negative_prompt"] = new Dictionary<string, object>
            {
                ["caption"] = new Dictionary<string, object>
                {
                    ["base_caption"] = effectiveNegativePrompt,
                    ["char_captions"] = negCharCaptions,
                },
                ["use_coords"] = useCoords,
                ["use_order"] = false,
                ["legacy_uc"] = naiParams.LegacyUc && ModelCapabilities.SupportsLegacyUc(naiParams.Model),
            };
        }
        else
        {
            // SMEA is unsupported for image-based requests.
            parameters["sm"] = false;
            parameters["sm_dyn"] = false;
        }

        if (preciseReferences is { Count: > 0 } && SupportsPreciseReferenceModel(naiParams.Model))
            ApplyPreciseReferenceParameters(parameters, preciseReferences);
        else if (vibeTransfers is { Count: > 0 } && SupportsVibeTransferModel(naiParams.Model))
        {
            if (IsV4PlusModel(naiParams.Model))
                await EncodeUnencodedVibesAsync(vibeTransfers, naiParams.Model, ct);
            ApplyVibeTransferParameters(parameters, vibeTransfers, naiParams.Model);
        }

        var payload = new Dictionary<string, object>
        {
            ["input"] = effectivePrompt,
            ["model"] = naiParams.Model,
            ["action"] = "img2img",
            ["parameters"] = parameters,
        };
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var content = diagnostic.Content(payload);
            var endpoints = GetApiEndpoints();
            var url = streamGeneration ? endpoints.GenerateStreamUrl : endpoints.GenerateUrl;
            using var requestTimeoutCts = CreateRequestTimeoutTokenSource(ImageGenerationRequestTimeout, ct);
            var requestCt = requestTimeoutCts.Token;
            var account = await SelectAccountAsync(ApiRequestCost.Image(naiParams.Model, pricedSize.Width, pricedSize.Height, naiParams.Steps,
                    strength: naiParams.DenoiseStrength,
                    referenceAnlas: GenerationReferenceAnlas(naiParams.Model, vibeTransfers, preciseReferences),
                    characterReference: SupportsPreciseReferenceModel(naiParams.Model) && preciseReferences is { Count: > 0 }, smDyn: false), requestCt);
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            if (streamGeneration)
                request.Headers.Accept.ParseAdd("application/octet-stream");
            using var response = await SendAccountRequestAsync(request,
                account,
                HttpCompletionOption.ResponseHeadersRead,
                requestCt);

            if (!response.IsSuccessStatusCode)
            {
                var errorText = SafeStatus((int)response.StatusCode);
                stopwatch.Stop();
                WriteRequestLog("Image-to-image generation", payload, stopwatch.ElapsedMilliseconds, response, errorText);
                return (null, Lf("api.error.status", (int)response.StatusCode, errorText));
            }

            byte[]? imageBytes = streamGeneration
                ? await ReadGeneratedImageStreamAsync(response.Content, progress, requestCt)
                : await ReadGeneratedImageBytesAsync(response.Content, requestCt);
            if (imageBytes == null)
            {
                stopwatch.Stop();
                WriteRequestLog("Image-to-image generation", payload, stopwatch.ElapsedMilliseconds, response);
                return (null, L("api.error.empty_zip"));
            }

            progress?.Report(imageBytes);
            stopwatch.Stop();
            WriteRequestLog("Image-to-image generation", payload, stopwatch.ElapsedMilliseconds, response, imageBytes: imageBytes);
            return (imageBytes, null);
        }
        catch (TaskCanceledException ex)
        {
            stopwatch.Stop();
            WriteRequestLog("Image-to-image generation", payload, stopwatch.ElapsedMilliseconds, exception: ex);
            return (null, SafeException(ex));
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            WriteRequestLog("Image-to-image generation", payload, stopwatch.ElapsedMilliseconds, exception: ex);
            var msg = Lf("api.error.network_failed", SafeException(ex));
            if (ex.InnerException != null) msg += "\n" + Lf("api.error.inner", SafeException(ex.InnerException));
            return (null, msg);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            WriteRequestLog("Image-to-image generation", payload, stopwatch.ElapsedMilliseconds, exception: ex);
            return (null, Lf("api.error.request_failed", SafeException(ex)));
        }
    }

    public async Task<(byte[]? ImageBytes, string? Error)> GenerateAsync(
        int width, int height,
        string prompt, string negativePrompt,
        List<CharacterPromptInfo>? characters = null,
        List<VibeTransferInfo>? vibeTransfers = null,
        List<PreciseReferenceInfo>? preciseReferences = null,
        IProgress<byte[]>? progress = null,
        CancellationToken ct = default)
    {
        LastGenerationErrorStatusCode = null;

        using var diagnostic = BeginBusinessLog(new { operation = "generate", width, height, prompt, negativePrompt, characters, vibeTransfers, preciseReferences, parameters = _settings.Settings.GenParameters });
        if (!_settings.HasApiTokens)
        {
            diagnostic.Stage("request_rejected", new { reason = "missing_api_token" });
            return (null, L("api.error.token_missing_network_api"));
        }
        var naiParams = ModelCapabilities.CreateRequestSnapshot(_settings.Settings.GenParameters);
        bool streamGeneration = _settings.Settings.StreamGeneration && IsV4PlusModel(naiParams.Model);
        string model = naiParams.Model;
        if (!_settings.Settings.UsesCustomApiBaseUrl &&
            NovelAiAnlasCalculator.PaidBaseCost(naiParams.Model, width, height, naiParams.Steps,
                sm: naiParams.Sm, strength: 1, smDyn: naiParams.Sm && naiParams.SmDyn) > NovelAiAnlasCalculator.MaxBaseCost)
        {
            diagnostic.Stage("request_rejected", new { reason = "generation_cost_limit", maximum_base_cost = NovelAiAnlasCalculator.MaxBaseCost });
            return (null, Lf("api.error.generation_cost_limit", NovelAiAnlasCalculator.MaxBaseCost));
        }

        object seed = SeedValue.ToRequestValue(SeedValue.Resolve(naiParams.Seed));
        bool isV4Plus = IsV4PlusModel(model);
        bool isV45 = IsV45Model(model);
        string effectivePrompt = ApplyQualityTags(prompt, naiParams);
        string effectiveNegativePrompt = NovelAiPromptPresets.ResolveUc(model, NovelAiPromptPresets.ResolveUcId(naiParams), effectivePrompt, ModelCapabilities.IsMedium(naiParams) ? "" : negativePrompt);

        if (IsV5Model(naiParams.Model)) effectivePrompt = NovelAiPromptPresets.AddAutoText(effectivePrompt, characters);

        var parameters = new Dictionary<string, object?>
        {
            ["params_version"] = 4,
            ["width"] = width,
            ["height"] = height,
            ["scale"] = naiParams.Scale,
            ["sampler"] = naiParams.Sampler,
            ["steps"] = naiParams.Steps,
            ["n_samples"] = 1,
            ["seed"] = seed,
            ["noise_schedule"] = naiParams.Schedule,
            ["uc"] = effectiveNegativePrompt,
            ["negative_prompt"] = effectiveNegativePrompt,
            ["ucPreset"] = naiParams.UcPreset,
            ["uc_preset"] = naiParams.UcPreset,
            ["cfg_rescale"] = naiParams.CfgRescale,
            ["legacy"] = false,
            ["legacy_v3_extend"] = false,
            ["dynamic_thresholding"] = naiParams.DynamicThresholding,
            ["skip_cfg_above_sigma"] = null,
            ["qualityToggle"] = naiParams.QualityToggle,
            ["quality_toggle"] = naiParams.QualityToggle,
        };
        if (IsV5Model(model))
        {
            parameters["tag_hint_transparent_background"] = naiParams.TagHintTransparentBackground;
            parameters["straight_alpha"] = naiParams.StraightAlpha;
        }

        ApplyVerifiedModelParameters(parameters, naiParams, width, height);
        if (streamGeneration) parameters["stream"] = "msgpack";

        if (naiParams.Sampler == "k_euler_ancestral" && naiParams.Schedule != "native")
        {
            parameters["deliberate_euler_ancestral_bug"] = false;
            parameters["prefer_brownian"] = true;
        }

        if (isV4Plus)
        {
            var posCharCaptions = new List<object>();
            var negCharCaptions = new List<object>();
            bool useCoords = false;

            if (characters is { Count: > 0 })
            {
                foreach (var ch in characters.Where(ch => ch.PositivePrompt.Length > 0))
                {
                    var center = new Dictionary<string, double>
                    {
                        ["x"] = IsV5Model(naiParams.Model) ? ch.CenterX : 0.1 + 0.2 * Math.Clamp((int)Math.Floor(5 * ch.CenterX), 0, 4),
                        ["y"] = IsV5Model(naiParams.Model) ? ch.CenterY : 0.1 + 0.2 * Math.Clamp((int)Math.Floor(5 * ch.CenterY), 0, 4),
                    };
                    posCharCaptions.Add(new Dictionary<string, object>
                    {
                        ["char_caption"] = ch.PositivePrompt,
                        ["centers"] = new[] { center },
                    });
                    negCharCaptions.Add(new Dictionary<string, object>
                    {
                        ["char_caption"] = ModelCapabilities.IsMedium(naiParams) ? "" : ch.NegativePrompt,
                        ["centers"] = new[] { center },
                    });
                    if (ch.UseCustomPosition)
                        useCoords = true;
                }
            }

            parameters["use_coords"] = useCoords;
            parameters["v4_prompt"] = new Dictionary<string, object>
            {
                ["caption"] = new Dictionary<string, object>
                {
                    ["base_caption"] = effectivePrompt,
                    ["char_captions"] = posCharCaptions,
                },
                ["use_coords"] = useCoords,
                ["use_order"] = true,
            };
            parameters["v4_negative_prompt"] = new Dictionary<string, object>
            {
                ["caption"] = new Dictionary<string, object>
                {
                    ["base_caption"] = effectiveNegativePrompt,
                    ["char_captions"] = negCharCaptions,
                },
                ["use_coords"] = useCoords,
                ["use_order"] = false,
                ["legacy_uc"] = naiParams.LegacyUc && ModelCapabilities.SupportsLegacyUc(naiParams.Model),
            };
        }
        else
        {
            parameters["sm"] = naiParams.Sm;
            parameters["sm_dyn"] = naiParams.Sm && naiParams.SmDyn;
        }

        if (preciseReferences is { Count: > 0 } && SupportsPreciseReferenceModel(model))
            ApplyPreciseReferenceParameters(parameters, preciseReferences);
        else if (vibeTransfers is { Count: > 0 } && SupportsVibeTransferModel(model))
        {
            if (IsV4PlusModel(model))
                await EncodeUnencodedVibesAsync(vibeTransfers, model, ct);
            ApplyVibeTransferParameters(parameters, vibeTransfers, naiParams.Model);
        }

        var payload = new Dictionary<string, object>
        {
            ["input"] = effectivePrompt,
            ["model"] = model,
            ["action"] = "generate",
            ["parameters"] = parameters,
        };
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var content = diagnostic.Content(payload);
            var endpoints = GetApiEndpoints();
            var url = streamGeneration ? endpoints.GenerateStreamUrl : endpoints.GenerateUrl;
            using var requestTimeoutCts = CreateRequestTimeoutTokenSource(ImageGenerationRequestTimeout, ct);
            var requestCt = requestTimeoutCts.Token;
            var account = await SelectAccountAsync(ApiRequestCost.Image(model, width, height, naiParams.Steps, sm: naiParams.Sm,
                    referenceAnlas: GenerationReferenceAnlas(model, vibeTransfers, preciseReferences),
                    characterReference: SupportsPreciseReferenceModel(model) && preciseReferences is { Count: > 0 }, smDyn: naiParams.Sm && naiParams.SmDyn), requestCt);
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            if (streamGeneration)
                request.Headers.Accept.ParseAdd("application/octet-stream");
            using var response = await SendAccountRequestAsync(request,
                account,
                HttpCompletionOption.ResponseHeadersRead,
                requestCt);

            if (!response.IsSuccessStatusCode)
            {
                var errorText = SafeStatus((int)response.StatusCode);
                stopwatch.Stop();
                LastGenerationErrorStatusCode = (int)response.StatusCode;
                WriteRequestLog("Image generation", payload, stopwatch.ElapsedMilliseconds, response, errorText);
                return (null, Lf("api.error.status", (int)response.StatusCode, errorText));
            }

            byte[]? imageBytes = streamGeneration
                ? await ReadGeneratedImageStreamAsync(response.Content, progress, requestCt)
                : await ReadGeneratedImageBytesAsync(response.Content, requestCt);

            if (imageBytes == null)
            {
                stopwatch.Stop();
                WriteRequestLog("Image generation", payload, stopwatch.ElapsedMilliseconds, response);
                return (null, L("api.error.empty_zip"));
            }

            progress?.Report(imageBytes);
            stopwatch.Stop();
            WriteRequestLog("Image generation", payload, stopwatch.ElapsedMilliseconds, response, imageBytes: imageBytes);
            return (imageBytes, null);
        }
        catch (TaskCanceledException ex)
        {
            stopwatch.Stop();
            WriteRequestLog("Image generation", payload, stopwatch.ElapsedMilliseconds, exception: ex);
            return (null, SafeException(ex));
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            WriteRequestLog("Image generation", payload, stopwatch.ElapsedMilliseconds, exception: ex);
            var msg = Lf("api.error.network_failed", SafeException(ex));
            if (SafeException(ex).Contains("proxy", StringComparison.OrdinalIgnoreCase))
                msg += L("api.error.proxy_hint");
            return (null, msg);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            WriteRequestLog("Image generation", payload, stopwatch.ElapsedMilliseconds, exception: ex);
            return (null, Lf("api.error.request_failed", SafeException(ex)));
        }
    }

    /// <summary>
    /// Encode source pixels losslessly; masks use an eighth-resolution intermediate PNG, expanded before sending.
    /// </summary>
    public static Task<string> EncodeRenderTargetAsync(CanvasRenderTarget target, bool isMask = false)
    {
        int w = (int)target.SizeInPixels.Width;
        int h = (int)target.SizeInPixels.Height;
        var pixels = target.GetPixelBytes();

        using var skBitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        Marshal.Copy(pixels, 0, skBitmap.GetPixels(), pixels.Length);

        byte[] encoded;
        if (isMask)
        {
            encoded = InpaintImageProcessing.EncodeMask(skBitmap);
        }
        else
        {
            using var pixmap = skBitmap.PeekPixels();
            using var wstream = new SKDynamicMemoryWStream();
            bool ok = pixmap.Encode(wstream, new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 75));
            if (ok)
            {
                using var data = wstream.DetachAsData();
                encoded = data.ToArray();
            }
            else
            {
                using var image = SKImage.FromBitmap(skBitmap);
                using var data = image.Encode(SKEncodedImageFormat.Webp, 90);
                encoded = data.ToArray();
            }
        }

        return Task.FromResult(Convert.ToBase64String(encoded));
    }

    /// <summary>
    /// 调用 NovelAI encode-vibe 端点，将图片编码为 vibe 数据。
    /// 每次调用消耗 2 Anlas，需要关闭账号资产保护模式。
    /// </summary>
    public async Task<(byte[]? VibeData, string? Error)> EncodeVibeAsync(
        string imageBase64, string model, double informationExtracted,
        CancellationToken ct = default)
    {
        using var diagnostic = BeginBusinessLog(new { operation = "encode_vibe", imageBase64, model, informationExtracted });
        if (!_settings.HasApiTokens)
        {
            diagnostic.Stage("request_rejected", new { reason = "missing_api_token" });
            return (null, L("api.error.token_missing_network_api"));
        }
        if (!SupportsVibeTransferModel(model))
        {
            diagnostic.Stage("request_rejected", new { reason = "vibe_model_unsupported" });
            return (null, "This model does not support Vibe encoding.");
        }
        var payload = new Dictionary<string, object>
        {
            ["image"] = imageBase64,
            ["model"] = model,
            ["information_extracted"] = informationExtracted,
        };
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var content = diagnostic.Content(payload);
            var endpoints = GetApiEndpoints();
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoints.EncodeVibeUrl) { Content = content };
            using var timeout = CreateRequestTimeoutTokenSource(ImageGenerationRequestTimeout, ct);
            var account = await SelectAccountAsync(new ApiRequestCost(false, 2), timeout.Token);
            using var response = await SendAccountRequestAsync(request, account,
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                var errorText = SafeStatus((int)response.StatusCode);
                stopwatch.Stop();
                WriteRequestLog("Vibe encoding", payload, stopwatch.ElapsedMilliseconds, response, errorText);
                return (null, Lf("api.error.status", (int)response.StatusCode, errorText));
            }

            using var vibeStream = await response.Content.ReadAsStreamAsync(timeout.Token);
            byte[] vibeData = await NovelAIImageResponse.ReadBoundedAsync(vibeStream, timeout.Token);
            stopwatch.Stop();
            WriteRequestLog("Vibe encoding", payload, stopwatch.ElapsedMilliseconds, response);
            return (vibeData, null);
        }
        catch (TaskCanceledException ex)
        {
            stopwatch.Stop();
            WriteRequestLog("Vibe encoding", payload, stopwatch.ElapsedMilliseconds, exception: ex);
            return (null, SafeException(ex));
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            WriteRequestLog("Vibe encoding", payload, stopwatch.ElapsedMilliseconds, exception: ex);
            var msg = Lf("api.error.network_failed", SafeException(ex));
            if (SafeException(ex).Contains("proxy", StringComparison.OrdinalIgnoreCase))
                msg += L("api.error.proxy_hint");
            return (null, msg);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            WriteRequestLog("Vibe encoding", payload, stopwatch.ElapsedMilliseconds, exception: ex);
            return (null, Lf("api.error.request_failed", SafeException(ex)));
        }
    }

    private static string? ExtractOpenAiTextResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (TryGetPropertyIgnoreCase(root, "choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0)
        {
            var first = choices[0];

            // standard chat completions: choices[0].message.content
            if (TryGetNestedElement(first, out var messageContent, "message", "content") &&
                messageContent.ValueKind == JsonValueKind.String)
                return messageContent.GetString();

            // streaming chat completions: choices[0].delta.content
            if (TryGetNestedElement(first, out var deltaContent, "delta", "content") &&
                deltaContent.ValueKind == JsonValueKind.String)
                return deltaContent.GetString();

            // standard completions: choices[0].text
            if (TryGetPropertyIgnoreCase(first, "text", out var text) &&
                text.ValueKind == JsonValueKind.String)
                return text.GetString();
        }

        if (TryGetPropertyIgnoreCase(root, "generated_text", out var generatedText) &&
            generatedText.ValueKind == JsonValueKind.String)
            return generatedText.GetString();

        if (TryGetPropertyIgnoreCase(root, "output", out var output) &&
            output.ValueKind == JsonValueKind.String)
            return output.GetString();

        return null;
    }

    public async Task<(string? Text, string? Error)> GeneratePromptTextAsync(
        string instruction, string model, CancellationToken ct = default)
    {
        using var diagnostic = BeginBusinessLog(new { operation = "prompt_text", instruction, model });
        if (!_settings.HasApiTokens)
        {
            diagnostic.Stage("request_rejected", new { reason = "missing_api_token" });
            return (null, L("api.error.token_missing_network_api"));
        }
        var messages = new[]
        {
            new Dictionary<string, string> { ["role"] = "user", ["content"] = instruction },
        };
        var payload = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["temperature"] = 0.7,
            ["top_p"] = 0.95,
            ["max_tokens"] = 320,
            ["stream"] = true,
        };
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var content = diagnostic.Content(payload);

            var endpoints = GetApiEndpoints();
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoints.TextChatCompletionUrl) { Content = content };
            request.Headers.Accept.ParseAdd("text/event-stream");
            using var timeout = CreateRequestTimeoutTokenSource(TextRequestTimeout, ct);
            var account = await SelectAccountAsync(
                new ApiRequestCost(false, 0, RequiresSubscription: true, RequiresOpus: model == "xialong-v1"), timeout.Token);
            using var response = await SendAccountRequestAsync(request, account,
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                var errorText = SafeStatus((int)response.StatusCode);
                stopwatch.Stop();
                WriteRequestLog("Prompt text generation", payload, stopwatch.ElapsedMilliseconds, response, errorText);
                return (null, Lf("api.error.status", (int)response.StatusCode, errorText));
            }

            var textBuilder = new StringBuilder();
            bool textFinished = false;
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            await foreach (var streamEvent in NovelAIStreamFrames.ReadSseAsync(stream, timeout.Token))
            {
                if (streamEvent.Event == "error") throw new InvalidDataException("Text stream returned an error.");
                var data = streamEvent.Data;
                if (data == "[DONE]") { textFinished = true; continue; }
                if (data.Length == 0) continue;

                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var root = doc.RootElement;
                    if (TryGetPropertyIgnoreCase(root, "error", out _))
                        throw new InvalidDataException("Text stream returned an error.");

                    if (!TryGetPropertyIgnoreCase(root, "choices", out var choices) ||
                        choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                        continue;

                    var first = choices[0];
                    if (TryGetPropertyIgnoreCase(first, "finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String)
                    {
                        if (reason.GetString() == "error") throw new InvalidDataException("Text stream returned an error.");
                        textFinished = true;
                    }

                    if (TryGetPropertyIgnoreCase(first, "text", out var textEl) &&
                        textEl.ValueKind == JsonValueKind.String)
                    {
                        var chunk = textEl.GetString();
                        if (!string.IsNullOrEmpty(chunk))
                            textBuilder.Append(chunk);
                    }

                    if (TryGetNestedElement(first, out var deltaContent, "delta", "content") &&
                        deltaContent.ValueKind == JsonValueKind.String)
                    {
                        var chunk = deltaContent.GetString();
                        if (!string.IsNullOrEmpty(chunk))
                            textBuilder.Append(chunk);
                    }
                }
                catch (JsonException) { throw new InvalidDataException("Malformed text stream event."); }
                if (textBuilder.Length > 1024 * 1024) throw new InvalidDataException("Text response exceeds the size limit.");
            }

            stopwatch.Stop();
            var result = textBuilder.ToString();

            WriteRequestLog("Prompt text generation (streaming)", payload, stopwatch.ElapsedMilliseconds, response, result);

            if (!textFinished) throw new InvalidDataException("Text stream ended without completion.");
            if (!string.IsNullOrWhiteSpace(result))
                return (result, null);

            Debug.WriteLine($"[NAI] 提示词生成：流式响应未包含可解析的文本内容");
            return (null, L("api.error.empty_text_response"));
        }
        catch (TaskCanceledException ex)
        {
            stopwatch.Stop();
            WriteRequestLog("Prompt text generation", payload, stopwatch.ElapsedMilliseconds, exception: ex);
            return (null, SafeException(ex));
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            WriteRequestLog("Prompt text generation", payload, stopwatch.ElapsedMilliseconds, exception: ex);
            var msg = Lf("api.error.network_failed", SafeException(ex));
            if (SafeException(ex).Contains("proxy", StringComparison.OrdinalIgnoreCase))
                msg += L("api.error.proxy_hint");
            return (null, msg);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            WriteRequestLog("Prompt text generation", payload, stopwatch.ElapsedMilliseconds, exception: ex);
            return (null, Lf("api.error.request_failed", SafeException(ex)));
        }
    }

    public void Dispose()
    {
        lock (_httpClientLock)
        {
            _httpClient?.Dispose();
            foreach (var client in _retiredHttpClients)
                client.Dispose();
            _retiredHttpClients.Clear();
        }
    }
}
