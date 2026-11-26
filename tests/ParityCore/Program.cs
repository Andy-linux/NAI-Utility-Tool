using System.Text.Json;
using NAITool.Services;

if (args.Length != 2) throw new ArgumentException("Usage: ParityCore <suite> <website contract directory>");
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
else throw new ArgumentException($"Unknown suite {suite}");
Console.WriteLine($"{suite}: {checks} behavior assertions passed.");
