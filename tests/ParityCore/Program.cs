using System.Text.Json;
using NAITool.Services;

if (args.Length is < 2 or > 3) throw new ArgumentException("Usage: ParityCore <suite> <website contract directory> [image fixture directory]");
string suite = args[0], directory = args[1];
int checks = 0;
void Check(bool ok, string description)
{
    checks++;
    if (!ok) throw new InvalidOperationException(description);
}
JsonElement Load(string file) => JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, file))).RootElement;
if (suite == "models")
{
    foreach (var row in Load("model-resolution-oracle.json").GetProperty("vectors").EnumerateArray())
    {
        var result = EffectiveGenerationResolver.Resolve(row.GetProperty("preferred").GetString()!,
            row.GetProperty("cardCount").GetInt32(), row.GetProperty("mode").GetString() == "infill");
        Check(result.EffectiveModel == row.GetProperty("effective").GetString(), $"Effective: {row}");
        Check(result.RequestModel == row.GetProperty("request").GetString(), $"Request: {row}");
    }
}
else if (suite == "pricing")
{
    var oracle = Load("pricing-oracle.json");
    foreach (var row in oracle.GetProperty("vectors").EnumerateArray())
    {
        int I(string key) => row.GetProperty(key).GetInt32();
        bool B(string key) => row.GetProperty(key).GetBoolean();
        var price = NovelAiAnlasCalculator.QuoteBase(row.GetProperty("model").GetString()!,
            I("width"), I("height"), I("steps"), I("nSamples"), I("tier") >= 3 && B("active"),
            B("usageNegative"), B("sm"), row.GetProperty("strength").GetDouble(), B("smDyn"));
        Check(price == I("base"), $"Price {price}: {row}");
    }
    foreach (var row in oracle.GetProperty("vibeVectors").EnumerateArray())
        Check(NovelAiAnlasCalculator.ReferenceCost(0,row.GetProperty("count").GetInt32(),0) ==
            row.GetProperty("extraPerImage").GetInt32(), $"Vibe surcharge: {row}");
    Check(NovelAiAnlasCalculator.ReferenceCost(2,6,0,3)==16, "Encoding once plus per-image surcharge");
    Check(NovelAiAnlasCalculator.ReferenceCost(0,0,3,2)==30, "Precise reference per image");
}
else if (suite == "references")
{
    foreach (var row in Load("reference-rules-oracle.json").GetProperty("vectors").EnumerateArray())
    {
        var cards = row.GetProperty("cards").EnumerateArray().Select(r => new PreciseReferenceInfo
        {
            ReferenceType = PreciseReferenceRules.Parse(r.GetProperty("description").GetString()),
            Enabled = r.GetProperty("enabled").GetBoolean(),
            Strength = r.GetProperty("strength").GetDouble(),
            Fidelity = r.GetProperty("fidelity").GetDouble(),
        }).ToList();
        var filtered = PreciseReferenceRules.Filter(row.GetProperty("model").GetString()!, cards);
        var expected = row.GetProperty("effective").EnumerateArray().ToArray();
        Check(filtered.Count == expected.Length, $"Filter count: {row}");
        for (int i=0; i<filtered.Count; i++)
        {
            Check(PreciseReferenceRules.Description(filtered[i].ReferenceType) == expected[i].GetProperty("description").GetString(), "Reference description");
            Check(filtered[i].Strength == expected[i].GetProperty("strength").GetDouble(), "Reference strength");
            Check(filtered[i].Fidelity == expected[i].GetProperty("fidelity").GetDouble(), "Reference fidelity");
        }
    }
    var outfit = new PreciseReferenceInfo { ReferenceType = PreciseReferenceType.Outfit, Strength = 6.1 };
    Check(PreciseReferenceRules.ForRequest("nai-diffusion-5-full", [outfit]).Count == 1, "V5 outfit");
    Check(PreciseReferenceRules.ForRequest("nai-diffusion-4-5-full", [outfit]).Count == 0, "V4.5 outfit filter");
    Check(PreciseReferenceRules.ForRequest("nai-diffusion-5-full-inpainting", [outfit]).Count == 0, "V5 inpaint ignores references");
    Check(PreciseReferenceRules.ForRequest("nai-diffusion-4-5-full-inpainting", [new()]).Count == 1, "V4.5 inpaint references");
    Check(PreciseReferenceRules.ForRequest("nai-diffusion-5-full", Enumerable.Repeat(new PreciseReferenceInfo{Enabled=false},17)).Count == 0, "Disabled excluded before limit");
    bool rejected = false;
    try { PreciseReferenceRules.ForRequest("nai-diffusion-5-full", Enumerable.Repeat(new PreciseReferenceInfo(),17)); }
    catch (ArgumentException) { rejected = true; }
    Check(rejected, "17 active references rejected");
}
else if (suite == "metadata")
{
    Check(ImportedImageModel.ResolveForTarget("NovelAI Diffusion V5 81E2A1BD", ["nai-diffusion-5-full"]) == "nai-diffusion-5-full", "New full source");
    Check(ImportedImageModel.ResolveForTarget("NovelAI Diffusion V5 AD5A716B", ["nai-diffusion-5-curated"]) == "nai-diffusion-5-curated", "New curated source");
    var json = JsonDocument.Parse("""{"director_reference_images":["a",null,"c"],"director_reference_strengths":[-0.5,0,6.1],"secondary_strengths":[-0.2,0,0.375],"director_reference_descriptions":[{"caption":{"base_caption":"costume"}},null,{"caption":{"base_caption":"character_mainoutfit"}}]}""");
    var cards = PreciseReferenceMetadata.ReadDirector(json.RootElement);
    Check(cards.Count == 2, "Null image skipped");
    Check(cards[0].Strength == -0.5 && cards[0].Fidelity == 1.2 && cards[0].ReferenceType == PreciseReferenceType.Outfit, "Negative and greater than one imported without clamp");
    Check(cards[1].Strength == 6.1 && cards[1].Fidelity == 0.625 && cards[1].ReferenceType == PreciseReferenceType.CharacterMainOutfit, "Preserve array positions and precision");
}
else if (suite == "imaging")
{
    using (var transparent = new SkiaSharp.SKBitmap(2, 1, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul))
    {
        byte[] rgba = [53, 81, 219, 0, 19, 200, 72, 127];
        System.Runtime.InteropServices.Marshal.Copy(rgba, 0, transparent.GetPixels(), rgba.Length);
        using var sameSize = LanczosImageResize.Resize(transparent, 2, 1);
        Check(sameSize.Bytes.SequenceEqual(rgba), "Same-size resize preserves transparent RGB");
        using var roundtrip = ReferenceImageProcessor.Decode(ReferenceImageProcessor.Encode(sameSize));
        Check(roundtrip.Bytes.SequenceEqual(rgba), "Straight PNG preserves transparent RGB");
    }
    foreach (var (width, height) in new[] { (0, 0), (0, 4096), (4096, 0), (1, 16384), (16384, 1), (97, 3001), (3001, 97) })
    {
        var bounds = ReferenceImageProcessor.SelectBucket(width, height);
        Check(bounds.ResizeWidth >= bounds.Width && bounds.ResizeHeight >= bounds.Height &&
              bounds.Left >= 0 && bounds.Top >= 0 &&
              bounds.Left + bounds.Width <= bounds.ResizeWidth && bounds.Top + bounds.Height <= bounds.ResizeHeight,
              $"Bucket crop stays in bounds: {width}x{height}");
    }
    byte[] largeHeader = [137,80,78,71,13,10,26,10, 0,0,0,13, 73,72,68,82, 0,0,16,1, 0,0,16,0];
    Check(ReferenceEquals(ReferenceImageProcessor.Clean(largeHeader), largeHeader), "Oversized PNG header passes without pixel decode");
    byte[] jpegHeader = [0xff,0xd8,0xff];
    Check(ReferenceEquals(ReferenceImageProcessor.Clean(jpegHeader), jpegHeader), "Full JPEG magic passes unchanged");
    bool rejectedShortJpeg = false;
    try { ReferenceImageProcessor.Clean([0xff,0xd8]); }
    catch (InvalidDataException) { rejectedShortJpeg = true; }
    Check(rejectedShortJpeg, "Incomplete JPEG marker is not treated as JPEG");
    foreach (var row in Load("v5-reference-buckets.json").GetProperty("vectors").EnumerateArray())
    {
        int I(string k) => row.GetProperty(k).GetInt32();
        var actual = ReferenceImageProcessor.SelectBucket(I("inputWidth"), I("inputHeight"));
        Check(actual.Width == I("width") && actual.Height == I("height") &&
            actual.ResizeWidth == I("resizeWidth") && actual.ResizeHeight == I("resizeHeight") &&
            actual.Left == I("left") && actual.Top == I("top") &&
            actual.Unchanged == row.GetProperty("unchanged").GetBoolean(), $"Bucket: {row}, actual {actual}");
    }
    string fixtureDir = args.Length == 3 ? args[2] : Path.Combine(directory, "../../../../.runs/restart-20261126/fixtures/reference-oracle");
    foreach (var row in Load("reference-image-oracle.json").GetProperty("vectors").EnumerateArray())
    {
        string id = row.GetProperty("id").GetString()!;
        var input = File.ReadAllBytes(Path.Combine(fixtureDir, row.GetProperty("file").GetString()!));
        var prepared = ReferenceImageProcessor.Clean(input);
        Check(prepared.SequenceEqual(input) == row.GetProperty("preparedPassthrough").GetBoolean(), $"Clean: {id}");
        var wire = ReferenceImageProcessor.Bucket(prepared);
        Check(wire.SequenceEqual(prepared) == row.GetProperty("bucketPassthrough").GetBoolean(), $"Bucket passthrough: {id}");
        using var image = ReferenceImageProcessor.Decode(wire);
        Check(image.Width == row.GetProperty("expectedWidth").GetInt32() && image.Height == row.GetProperty("expectedHeight").GetInt32(), $"Dimensions: {id}");
        byte[] actual = image.Bytes;
        byte[] expected = File.ReadAllBytes(Path.Combine(fixtureDir, row.GetProperty("expectedRaw").GetString()!));
        int differences = actual.Zip(expected).Count(p => p.First != p.Second);
        Console.WriteLine($"{id}: {differences} differing channels / {expected.Length}");
        Check(actual.SequenceEqual(expected), $"RGBA: {id}");
    }
}
else if (suite == "pricing-reference")
{
    var rows = Load("pricing-reference-hook-oracle.json").GetProperty("vectors").EnumerateArray().ToArray();
    (string model, string operation, int n, int count, bool disabled, int vibes) Key(JsonElement row) =>
        (row.GetProperty("model").GetString()!, row.GetProperty("operation").GetString()!,
            row.GetProperty("nSamples").GetInt32(), row.GetProperty("count").GetInt32(),
            row.GetProperty("disabled").GetBoolean(), row.GetProperty("vibeCount").GetInt32());
    var oracle = rows.ToDictionary(Key);
    int normalized = 0;
    foreach (var row in rows)
    {
        var key = Key(row);
        bool inpaint = key.operation == "infill";
        var resolved = EffectiveGenerationResolver.Resolve(key.model, key.count, inpaint);
        string comparison = resolved.EffectiveModel;
        // The website quote hook evaluates capabilities before mapping Curated inpaint.
        // Compare the transmitted model's contract rather than that stale editor quote.
        if (inpaint && key.model == "nai-diffusion-5-curated")
            comparison = ModelCapabilityRules.BaseModel(resolved.RequestModel);
        if (comparison != key.model) normalized++;
        var expected = oracle[(comparison, key.operation, key.n, key.count, key.disabled, key.vibes)].GetProperty("result");
        var cards = Enumerable.Range(0, key.count).Select(i => new PreciseReferenceInfo {
            ReferenceType = i % 3 == 0 ? PreciseReferenceType.Outfit : PreciseReferenceType.CharacterAndStyle,
            Enabled = !key.disabled, Strength = 1, Fidelity = 1
        }).ToList();
        int activeReferences = PreciseReferenceRules.ForRequest(resolved.RequestModel, cards).Count;
        string baseModel = ModelCapabilityRules.BaseModel(resolved.RequestModel);
        bool encodedVibes = !inpaint && key.count == 0 && baseModel.StartsWith("nai-diffusion-4-", StringComparison.Ordinal);
        int additional = NovelAiAnlasCalculator.ReferenceCost(encodedVibes ? row.GetProperty("unencodedVibes").GetInt32() : 0,
            encodedVibes ? key.vibes : 0, activeReferences, key.n);
        int steps = baseModel.Contains("-medium", StringComparison.Ordinal) ? 14 : 23;
        int basic = NovelAiAnlasCalculator.QuoteBase(resolved.RequestModel, 832, 1216, steps, key.n, true, false,
            false, key.operation == "generate" ? 1 : .7, false);
        Check(additional == expected.GetProperty("additionalPrice").GetInt32(), $"Reference fee {additional}: {row}");
        Check(basic + additional == expected.GetProperty("price").GetInt32(), $"Total {basic + additional}: {row}");
    }
    Console.WriteLine($"Compared {rows.Length} actual website quotes; {normalized} editor/request-model cases compared against the equivalent effective-model oracle.");
}

else throw new ArgumentException($"Unknown suite {suite}");
Console.WriteLine($"{suite}: {checks} behavior assertions passed.");
