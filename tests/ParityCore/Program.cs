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
else throw new ArgumentException($"Unknown suite {suite}");
Console.WriteLine($"{suite}: {checks} behavior assertions passed.");
