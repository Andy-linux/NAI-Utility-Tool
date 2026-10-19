using System.Reflection;
using System.Text.Json;
using NAITool.Services;

internal static class PresetImportChecks
{
    public static void Run()
    {
        var type = typeof(ImageMetadataService).Assembly.GetType("NAITool.Services.ImportedPromptPresets", true)!;
        var read = type.GetMethod("Read", BindingFlags.Static | BindingFlags.NonPublic)!;
        using var fixtures = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "WebsitePresetImports.json")));
        int cases = 0;
        foreach (var fixture in fixtures.RootElement.GetProperty("cases").EnumerateArray())
        {
            string id = fixture.GetProperty("id").GetString()!;
            var metadata = ImageMetadataService.TryParseJson(fixture.GetProperty("metadata").GetRawText())!;
            var result = read.Invoke(null, [metadata.PositivePrompt, metadata.NegativePrompt,
                fixture.GetProperty("sourceModel").GetString()!, fixture.GetProperty("targetModel").GetString()!, metadata])!;
            object? Value(string property) => result.GetType().GetProperty(property)!.GetValue(result);
            var actual = new Dictionary<string, object?>
            {
                ["prompt"] = Value("PositivePrompt"),
                ["uc"] = Value("NegativePrompt"),
                ["quality"] = (bool)Value("QualityMatched")! ? Value("QualityPreset") : "none",
                ["transparent"] = Value("TransparentBackground"),
                ["ucPreset"] = Value("UcPresetId"),
            };
            foreach (var expected in fixture.GetProperty("expected").EnumerateObject())
            {
                object expectedValue = expected.Value.ValueKind == JsonValueKind.String
                    ? expected.Value.GetString()! : expected.Value.GetBoolean();
                if (!Equals(actual[expected.Name], expectedValue))
                    throw new Exception($"{id}: {expected.Name} differs from the website import state.");
            }
            cases++;
        }
        Console.WriteLine($"Passed {cases} website preset-import fixtures against the built client assembly.");
    }
}
