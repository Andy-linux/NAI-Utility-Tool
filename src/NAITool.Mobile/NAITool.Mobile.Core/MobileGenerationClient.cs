using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NAITool.Services;

namespace NAITool.Mobile.Core;

/// <summary>Mobile-only transport adapter. The parameter/model/stream rules come from the desktop dev sources.</summary>
public sealed class MobileGenerationClient(HttpClient httpClient)
{
    public sealed record Character(string Positive, string Negative = "", double X = 0.5, double Y = 0.5, bool UseCoordinates = false);

    public sealed record Request(
        string ApiRoot, string ApiToken, string Prompt, string NegativePrompt,
        NAIParameters Parameters, int Width, int Height,
        IReadOnlyList<Character>? Characters = null,
        bool Stream = false,
        string? InpaintImageBase64 = null, string? InpaintMaskBase64 = null);

    public static int MaxCharactersForModel(string model) =>
        model.Contains("-4", StringComparison.Ordinal) || model.Contains("-5", StringComparison.Ordinal)
            ? CharacterPromptRules.MaxForModel(model) : 0;

    public static int EstimatePaidAnlas(NAIParameters parameters, int width, int height, bool inpaint = false)
    {
        var p = ModelCapabilities.CreateRequestSnapshot(parameters);
        return NovelAiAnlasCalculator.PaidBaseCost(p.Model, width, height, p.Steps,
            p.Sm, inpaint ? p.InpaintStrength : 1, p.Sm && p.SmDyn);
    }

    public static string ResolveUrl(string apiRoot, bool stream = false) =>
        stream ? new ApiEndpointResolver(apiRoot).GenerateStreamUrl : new ApiEndpointResolver(apiRoot).GenerateUrl;

    public static string BuildRequestJson(Request request)
    {
        bool inpaint = request.InpaintImageBase64 != null || request.InpaintMaskBase64 != null;
        if (inpaint && (string.IsNullOrEmpty(request.InpaintImageBase64) || string.IsNullOrEmpty(request.InpaintMaskBase64)))
            throw new ArgumentException("Inpaint requires both image and mask.");
        if (request.Width <= 0 || request.Height <= 0 || request.Width % 64 != 0 || request.Height % 64 != 0)
            throw new ArgumentOutOfRangeException(nameof(request.Width), "Canvas dimensions must be positive multiples of 64.");

        NAIParameters p = ModelCapabilities.CreateRequestSnapshot(request.Parameters);
        if (string.IsNullOrWhiteSpace(request.ApiRoot) &&
            EstimatePaidAnlas(p, request.Width, request.Height, inpaint) > NovelAiAnlasCalculator.MaxBaseCost)
            throw new ArgumentException("The request exceeds the NovelAI paid base-cost limit.");
        var isV4Plus = p.Model.Contains("-4", StringComparison.Ordinal) || p.Model.Contains("-5", StringComparison.Ordinal);
        var isV45 = p.Model.Contains("4-5", StringComparison.Ordinal);
        bool isMedium = ModelCapabilities.IsMedium(p);
        int characterCount = request.Characters?.Count ?? 0;
        if (characterCount > MaxCharactersForModel(p.Model))
            throw new ArgumentException("Character prompt count exceeds the model capability.");

        string effectivePrompt = p.QualityToggle
            ? MergeTags(request.Prompt, NovelAiPromptPresets.QualityTags(p.Model, p.QualityPresetId))
            : request.Prompt;
        string uc = NovelAiPromptPresets.ResolveUc(p.Model, NovelAiPromptPresets.ResolveUcId(p),
            request.Prompt, isMedium ? "" : request.NegativePrompt);
        var parameters = new Dictionary<string, object?>
        {
            ["params_version"] = 3,
            ["width"] = request.Width,
            ["height"] = request.Height,
            ["scale"] = p.Scale,
            ["sampler"] = p.Sampler,
            ["steps"] = p.Steps,
            ["n_samples"] = 1,
            ["seed"] = SeedValue.ToRequestValue(SeedValue.Resolve(p.Seed)),
            ["noise_schedule"] = p.Schedule,
            ["negative_prompt"] = uc,
            ["cfg_rescale"] = p.CfgRescale,
            ["legacy"] = false,
            ["legacy_v3_extend"] = false,
            ["dynamic_thresholding"] = p.CfgRescale > 0,
            ["image_format"] = "webp",
            ["tag_hint_qt"] = NovelAiPromptPresets.HintId(p.QualityToggle ? p.QualityPresetId : "none"),
            ["tag_hint_uc_preset"] = NovelAiPromptPresets.HintId(NovelAiPromptPresets.ResolveUcId(p)),
        };

        var sigma = ModelCapabilities.GetSkipCfgAboveSigma(p.Model, request.Width, request.Height, p.Variety);
        if (sigma.HasValue) parameters["skip_cfg_above_sigma"] = sigma.Value;
        if (isMedium) parameters.Remove("cfg_rescale");
        if (NovelAiAnlasCalculator.IsV5Model(p.Model))
        {
            parameters["tag_hint_transparent_background"] = p.TagHintTransparentBackground;
            parameters["straight_alpha"] = p.StraightAlpha;
            parameters["noise_schedule"] = "karras";
        }
        if (p.Sampler == "k_euler_ancestral" && p.Schedule != "native")
        {
            parameters["deliberate_euler_ancestral_bug"] = false;
            parameters["prefer_brownian"] = true;
        }

        if (isV4Plus)
        {
            var positive = new List<object>();
            var negative = new List<object>();
            bool coords = false;
            foreach (var ch in request.Characters ?? [])
            {
                var center = new Dictionary<string, double>
                {
                    ["x"] = CharacterPromptRules.NormalizeCoordinate(ch.X),
                    ["y"] = CharacterPromptRules.NormalizeCoordinate(ch.Y),
                };
                positive.Add(new Dictionary<string, object>
                {
                    ["char_caption"] = ch.Positive,
                    ["centers"] = new[] { center },
                });
                negative.Add(new Dictionary<string, object>
                {
                    ["char_caption"] = isMedium ? "" : ch.Negative,
                    ["centers"] = new[] { center },
                });
                coords |= ch.UseCoordinates;
            }
            parameters["use_coords"] = coords;
            parameters["v4_prompt"] = new Dictionary<string, object>
            {
                ["caption"] = new Dictionary<string, object> { ["base_caption"] = effectivePrompt, ["char_captions"] = positive },
                ["use_coords"] = coords,
                ["use_order"] = true,
            };
            parameters["v4_negative_prompt"] = new Dictionary<string, object>
            {
                ["caption"] = new Dictionary<string, object> { ["base_caption"] = uc, ["char_captions"] = negative },
                ["use_coords"] = coords,
                ["use_order"] = false,
                ["legacy_uc"] = !isV45,
            };
        }
        else
        {
            parameters["sm"] = p.Sm;
            parameters["sm_dyn"] = p.Sm && p.SmDyn;
        }
        if (inpaint)
        {
            // Match desktop NovelAIService.InpaintAsync (WEBP input+mask, infill action).
            parameters["image"] = request.InpaintImageBase64;
            parameters["mask"] = request.InpaintMaskBase64;
            parameters["add_original_image"] = false;
            parameters["strength"] = isV4Plus ? Math.Clamp(p.InpaintStrength, 0, 1) : 1.0;
            parameters["noise"] = isV4Plus ? Math.Clamp(p.InpaintNoise, 0, 1) : 0.0;
            if (!isV4Plus) { parameters["sm"] = false; parameters["sm_dyn"] = false; }
        }
        if (request.Stream) parameters["stream"] = "msgpack";
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["input"] = effectivePrompt,
            ["model"] = p.Model,
            ["action"] = inpaint ? "infill" : "generate",
            ["parameters"] = parameters,
        });
    }

    public async Task<byte[]> GenerateAsync(Request request, IProgress<byte[]>? previews = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.ApiToken))
            throw new ArgumentException("API token is required for generation.");
        var uri = ResolveUrl(request.ApiRoot, request.Stream);
        using var message = new HttpRequestMessage(HttpMethod.Post, uri);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiToken.Trim());
        if (request.Stream) message.Headers.Accept.ParseAdd("application/octet-stream");
        message.Content = new StringContent(BuildRequestJson(request), Encoding.UTF8, "application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(6));
        using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Image request returned HTTP {(int)response.StatusCode} ({response.StatusCode}). Billing may be unknown.", null, response.StatusCode);
        using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        if (request.Stream)
            return await NovelAIImageStream.ReadAsync(stream, response.Content.Headers.ContentType?.MediaType, LooksLikeImage, previews, timeout.Token).ConfigureAwait(false);
        return await NovelAIImageResponse.ReadSingleAsync(await NovelAIImageResponse.ReadBoundedAsync(stream, timeout.Token),
            LooksLikeImage, timeout.Token).ConfigureAwait(false);
    }

    // Header-level validation keeps this lightweight; platform image decoder validates before presentation.
    private static bool LooksLikeImage(byte[] bytes) =>
        bytes.Length > 24 &&
        (bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
         (bytes.Length > 16 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8)) ||
         (bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[^2] == 0xff && bytes[^1] == 0xd9));

    private static string MergeTags(params string[] segments)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<string>();
        foreach (string segment in segments)
            foreach (string part in segment.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                if (seen.Add(part)) merged.Add(part);
        return string.Join(", ", merged);
    }
}
