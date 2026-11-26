using System.Text.Json;

namespace NAITool.Services;

public static class PreciseReferenceMetadata
{
    public static JsonElement Array(JsonElement root, params string[] names)
    {
        foreach (var name in names)
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array)
                return value;
        return default;
    }

    public static double NumberAt(JsonElement array, int index, double fallback)
    {
        if (array.ValueKind != JsonValueKind.Array || index >= array.GetArrayLength() || index < 0) return fallback;
        var value = array[index];
        if (value.ValueKind != JsonValueKind.Number) return fallback;
        var number = value.GetDouble();
        if (!double.IsFinite(number)) throw new ArgumentException("Non-finite reference metadata.");
        return number;
    }

    public static List<PreciseReferenceInfo> ReadDirector(JsonElement root)
    {
        var images = Array(root, "director_reference_images");
        var result = new List<PreciseReferenceInfo>();
        if (images.ValueKind != JsonValueKind.Array) return result;
        var strengths = Array(root, "director_reference_strength_values", "director_reference_strengths");
        var secondary = Array(root, "director_reference_secondary_strength_values", "director_reference_secondary_strengths", "secondary_strengths");
        var descriptions = Array(root, "director_reference_descriptions");
        for (int i = 0; i < images.GetArrayLength(); i++)
        {
            if (images[i].ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(images[i].GetString())) continue;
            string? type = null;
            if (descriptions.ValueKind == JsonValueKind.Array && i < descriptions.GetArrayLength())
            {
                var entry = descriptions[i];
                if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("caption", out var caption) &&
                    caption.ValueKind == JsonValueKind.Object && caption.TryGetProperty("base_caption", out var text) &&
                    text.ValueKind == JsonValueKind.String) type = text.GetString();
                else if (entry.ValueKind == JsonValueKind.String) type = entry.GetString();
            }
            result.Add(new PreciseReferenceInfo
            {
                ImageBase64 = images[i].GetString()!,
                FileName = $"reference_{i + 1}.png",
                ReferenceType = PreciseReferenceRules.Parse(type),
                Strength = NumberAt(strengths, i, 1.0),
                Fidelity = 1.0 - NumberAt(secondary, i, 0.0),
            });
        }
        return result;
    }
}
