using System.Net;
using System.IO.Compression;
using SkiaSharp;
using System.Reflection;
using System.Text.Json;
using NAITool.Services;

internal static class V5ReferenceTransportChecks
{
    private static int checks;
    private static void Check(bool ok, string message)
    {
        checks++;
        if (!ok) throw new InvalidOperationException(message);
    }

    public static async Task RunAsync()
    {
        var settings = new SettingsService();
        settings.SetApiTokens(["synthetic-v5-parity-token"]);
        settings.Settings.StreamGeneration = false;
        using var handler = new Handler();
        using var client = new HttpClient(handler);
        using var service = new NovelAIService(settings);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(NovelAIService).GetField("_httpClient", flags)!.SetValue(service, client);
        typeof(NovelAIService).GetField("_httpClientProxyKey", flags)!.SetValue(service, "");
        await service.RefreshAccountsAsync();
        using var bitmap = new SKBitmap(832, 1216, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var encodedImage = SKImage.FromBitmap(bitmap);
        using var encoded = encodedImage.Encode(SKEncodedImageFormat.Png, 100);
        byte[] fixtureBytes = encoded.ToArray();
        string image = Convert.ToBase64String(fixtureBytes);
        var p = new NAIParameters { Model="nai-diffusion-5-full", Effort="medium", Steps=23, Seed="123456", QualityToggle=false };
        var a = new PreciseReferenceInfo { ImageBase64=image, ReferenceType=PreciseReferenceType.CharacterMainOutfit, Strength=6.1, Fidelity=-.25 };
        var disabled = new PreciseReferenceInfo { Enabled=false, ImageBase64="invalid" };
        var vibe = new VibeTransferInfo { ImageBase64="must-not-be-encoded" };
        var error = await service.GenerateAsync(832,1216,"landscape","",vibeTransfers:[vibe],preciseReferences:[a,disabled],requestParameters:p);
        var body = handler.LastBody;
        var args = body.GetProperty("parameters");
        Check(body.GetProperty("model").GetString()=="nai-diffusion-5-full", "Medium forced High");
        Check(args.GetProperty("steps").GetInt32()==23, "High steps from draft");
        Check(args.GetProperty("n_samples").GetInt32()==1, "One image per HTTP call");
        Check(args.GetProperty("director_reference_images")[0].GetString()==image, "Exact bucket bytes unchanged");
        Check(args.GetProperty("director_reference_strength_values")[0].GetDouble()==6.1, "Strength above 5 preserved");
        Check(args.GetProperty("director_reference_secondary_strength_values")[0].GetDouble()==0, "V5 secondary=0");
        Check(args.GetProperty("director_reference_descriptions")[0].GetProperty("caption").GetProperty("base_caption").GetString()=="character_mainoutfit", "Outfit preference serialized");
        Check(!args.TryGetProperty("reference_image_multiple",out _), "Vibe suppressed");
        Check(error.Error?.Contains("INVALID_REFERENCE",StringComparison.Ordinal)==true && error.Error.Contains("corr-123") && !error.Error.Contains("synthetic-v5-parity-token"), "Provider error and correlation preserved, token redacted");
        Check(p.Effort=="medium" && p.Steps==23, "Preferred Medium draft preserved");
        await service.GenerateAsync(832,1216,"landscape","",preciseReferences:[disabled],requestParameters:p);
        Check(handler.LastBody.GetProperty("model").GetString()=="nai-diffusion-5-full" &&
            !handler.LastBody.GetProperty("parameters").TryGetProperty("director_reference_images",out _), "Disabled cards keep High without sending images");
        await service.GenerateAsync(832,1216,"landscape","",requestParameters:p);
        Check(handler.LastBody.GetProperty("model").GetString()=="nai-diffusion-5-full-medium" &&
            handler.LastBody.GetProperty("parameters").GetProperty("steps").GetInt32()==14, "Delete cards restores Medium");
        await service.InpaintAsync(image,image,832,1216,"landscape","",preciseReferences:[a],requestParameters:p);
        Check(handler.LastBody.GetProperty("model").GetString()=="nai-diffusion-5-full-medium-inpainting" &&
            !handler.LastBody.GetProperty("parameters").TryGetProperty("director_reference_images",out _), "Masked V5 full stays Medium, skips references");
        p.Model="nai-diffusion-5-curated";
        p.Effort="high";
        a.Strength=-.5; a.Fidelity=1.2;
        var outfit = new PreciseReferenceInfo { ReferenceType=PreciseReferenceType.Outfit, ImageBase64="invalid" };
        await service.InpaintAsync(image,image,832,1216,"landscape","",preciseReferences:[a,outfit],requestParameters:p);
        body=handler.LastBody; args=body.GetProperty("parameters");
        Check(body.GetProperty("model").GetString()=="nai-diffusion-4-5-curated-inpainting", "Curated inpaint fallback");
        Check(args.GetProperty("director_reference_images").GetArrayLength()==1, "Outfit skipped before decode");
        Check(args.GetProperty("director_reference_strength_values")[0].GetDouble()==-.5, "Negative reference preserved");
        Check(Math.Abs(args.GetProperty("director_reference_secondary_strength_values")[0].GetDouble()+.2)<1e-12, "Fidelity above 1 preserved");
        Check(args.GetProperty("director_reference_descriptions")[0].GetProperty("caption").GetProperty("base_caption").GetString()=="character", "V4.5 folds outfit preference");
        using var padded = ReferenceImageProcessor.Decode(Convert.FromBase64String(args.GetProperty("director_reference_images")[0].GetString()!));
        Check(padded.Width==1024 && padded.Height==1536, "V4.5 reference canvas");
        int posts=handler.Posts;
        var excess=await service.GenerateAsync(832,1216,"landscape","",preciseReferences:Enumerable.Repeat(a,17).ToList(),requestParameters:new NAIParameters());
        Check(excess.Error!=null && handler.Posts==posts, "17 effective cards rejected before HTTP");
        using (var archive = WebsiteArchive(fixtureBytes))
        {
            var cards = PreciseReferenceArchive.Read(archive);
            Check(cards.Count == 1 && cards[0].Strength == 6.1 && cards[0].Fidelity == 1 &&
                cards[0].Enabled && cards[0].ReferenceType == PreciseReferenceType.CharacterAndStyle,
                "Read website-format reference ZIP");
            var original = image;
            Check(cards[0].ImageBase64 == original, "ZIP keeps original image bytes");
            cards[0].Strength = -.51;
            cards[0].Fidelity = 1.2;
            cards[0].Enabled = false;
            using var memory = new MemoryStream();
            PreciseReferenceArchive.Write(memory, cards);
            memory.Position = 0;
            var restored = PreciseReferenceArchive.Read(memory).Single();
            Check(restored.ImageBase64 == original && restored.Strength == -.51 && restored.Fidelity == 1.2 && !restored.Enabled,
                "ZIP roundtrip retains finite values and disabled state");
        }
        var remembered = new RememberedPromptState { GenPreciseReferences = [new() { ImageDataKey = "fixture", Strength = -.51, Fidelity = 1.2, IsDisabled = true, ReferenceType = PreciseReferenceType.Outfit }] };
        var persisted = JsonSerializer.Deserialize<RememberedPromptState>(JsonSerializer.Serialize(remembered))!.GenPreciseReferences.Single();
        Check(persisted.Strength == -.51 && persisted.Fidelity == 1.2 && persisted.IsDisabled && persisted.ReferenceType == PreciseReferenceType.Outfit, "Draft settings preserve exact values");
        Console.WriteLine($"Passed {checks} V5 request and archive behavior assertions (synthetic HTTP).");
    }

    private static MemoryStream WebsiteArchive(byte[] image)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var entry = zip.CreateEntry("character.png").Open()) entry.Write(image);
            using var metadata = new StreamWriter(zip.CreateEntry("metadata.json").Open());
            metadata.Write("""[{"filename":"character.png","description":"character&style","strength":6.1,"fidelity":1,"information_extracted":1,"enabled":true}]""");
        }
        stream.Position = 0;
        return stream;
    }

    private sealed class Handler : HttpMessageHandler
    {
        public JsonElement LastBody { get; private set; }
        public int Posts { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method==HttpMethod.Get)
                return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new { subscription = new { tier = 3, active = true, trainingStepsLeft = 1000, expiresAt = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds(), usage = new { percent = 100, isNegative = false, timeUntilNextPercent = 60 } } }))};
            Posts++;
            LastBody=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
            var response=new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content=new StringContent("""{"error":"INVALID_REFERENCE","message":"Reference rejected: synthetic-v5-parity-token"}"""),
                RequestMessage=request
            };
            response.Headers.TryAddWithoutValidation("x-correlation-id","corr-123");
            return response;
        }
    }
}
