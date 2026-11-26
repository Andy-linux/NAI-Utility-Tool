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
else throw new ArgumentException($"Unknown suite {suite}");
Console.WriteLine($"{suite}: {checks} behavior assertions passed.");
