using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace NAITool.Services;

public static class ImportedImageModel
{
    // Outside the verified Source aliases below, require the complete NAI name,
    // never a substring of a third-party checkpoint or a generic "Stable Diffusion" source.
    private static readonly Regex DisplayNamePattern = new(
        @"^(?:NovelAI|NAI) Diffusion (?:(Anime|Furry) )?V(3|4\.5|4|5)(?: (Full|Curated)(?: Preview)?)?(?: (Inpainting))?(?: [0-9a-f]{8,64})?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    // The official Source field identifies several editions only by their checkpoint hash.
    private static string? ResolveKnownSource(string source) => source.ToUpperInvariant() switch
    {
        "STABLE DIFFUSION XL B0BDF6C1"
        or "STABLE DIFFUSION XL C1E1DE52"
        or "STABLE DIFFUSION XL 7BCCAA2C"
        or "STABLE DIFFUSION XL 1120E6A9"
        or "STABLE DIFFUSION XL 8BA2AF87" => "nai-diffusion-3",
        "STABLE DIFFUSION XL 4BE8C60C"
        or "STABLE DIFFUSION XL C8704949"
        or "STABLE DIFFUSION XL 37C2B166"
        or "STABLE DIFFUSION XL F306816B"
        or "STABLE DIFFUSION XL 9CC2F394" => "nai-diffusion-furry-3",
        "NOVELAI DIFFUSION V4 37442FCA"
        or "NOVELAI DIFFUSION V4 4F49EC75"
        or "NOVELAI DIFFUSION V4 CA4B7203"
        or "NOVELAI DIFFUSION V4 79F47848"
        or "NOVELAI DIFFUSION V4 F6302A9D" => "nai-diffusion-4-full",
        "NOVELAI DIFFUSION V4 7ABFFA2A"
        or "NOVELAI DIFFUSION V4 C1CCBA86"
        or "NOVELAI DIFFUSION V4 770A9E12" => "nai-diffusion-4-curated-preview",
        "DIFFUSIONMODELMETANAME.NAIV4NEXT 4BDE2A90"
        or "NOVELAI DIFFUSION V4.5 4BDE2A90"
        or "NOVELAI DIFFUSION V4.5 1229B44F"
        or "NOVELAI DIFFUSION V4.5 B9F340FD"
        or "NOVELAI DIFFUSION V4.5 F3D95188" => "nai-diffusion-4-5-full",
        "DIFFUSIONMODELMETANAME.NAIV4NEXT C02D4F98"
        or "NOVELAI DIFFUSION V4.5 C02D4F98"
        or "NOVELAI DIFFUSION V4.5 5AB81C7C"
        or "NOVELAI DIFFUSION V4.5 B5A2A797"
        or "NOVELAI DIFFUSION V4 5AB81C7C"
        or "NOVELAI DIFFUSION V4 B5A2A797" => "nai-diffusion-4-5-curated",
        "NOVELAI DIFFUSION V5 657484A5"
        or "NOVELAI DIFFUSION V5 0ADF9AB7"
        or "NOVELAI DIFFUSION V5 93F4BD30"
        or "NOVELAI DIFFUSION V5 70AB5786" => "nai-diffusion-5-full",
        "NOVELAI DIFFUSION V5 DB276663" => "nai-diffusion-5-curated",
        _ => null,
    };

    public static string? ResolveForTarget(string? name, IReadOnlyList<string> availableModels)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string model = ResolveKnownSource(name.Trim()) ?? name.Trim().ToLowerInvariant();
        if (!model.StartsWith("nai-diffusion-", StringComparison.Ordinal))
        {
            var match = DisplayNamePattern.Match(name.Trim());
            if (!match.Success) return null;
            bool furry = match.Groups[1].Value.Equals("Furry", StringComparison.OrdinalIgnoreCase);
            string version = match.Groups[2].Value.Replace('.', '-');
            bool curated = !match.Groups[3].Success || match.Groups[3].Value.Equals("Curated", StringComparison.OrdinalIgnoreCase);
            if (furry && version != "3") return null;
            if (version == "3" && match.Groups[3].Success) return null;
            model = version == "3"
                ? furry ? "nai-diffusion-furry-3" : "nai-diffusion-3"
                : $"nai-diffusion-{version}-{(curated ? "curated" : "full")}";
        }

        string? exact = availableModels.FirstOrDefault(x => x.Equals(model, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        const string inpainting = "-inpainting";
        if (model.EndsWith(inpainting, StringComparison.Ordinal)) model = model[..^inpainting.Length];
        if (model == "nai-diffusion-4-curated-preview") model = "nai-diffusion-4-curated";
        if (model == "nai-diffusion-5-curated" && availableModels.Contains("nai-diffusion-4-5-curated-inpainting"))
            return "nai-diffusion-4-5-curated-inpainting";
        // Apart from the verified Curated Inpaint mapping above, keep the requested
        // version/edition when resolving generation and inpainting IDs.
        return availableModels.FirstOrDefault(x =>
            x.Equals(model, StringComparison.OrdinalIgnoreCase) ||
            x.Equals(model + inpainting, StringComparison.OrdinalIgnoreCase) ||
            (model == "nai-diffusion-4-curated" && x == "nai-diffusion-4-curated-preview"));
    }
}
