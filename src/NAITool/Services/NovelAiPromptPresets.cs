using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace NAITool.Services;

/// <summary>Tag text verified against official qualitytags/undesiredcontent documentation.</summary>
internal static class NovelAiPromptPresets
{
    internal static readonly Regex TextMarker = new(@"(?:^|\s|[,.:[\]{}、。])text:(?!:)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AutoTextMarker = new(@"(?:^|\s|[,.:[\]{}、。])teXt:(?!:)", RegexOptions.CultureInvariant);

    internal static string[] SplitPromptMix(string prompt)
    {
        var parts = new List<string>();
        int start = 0;
        bool random = false;
        for (int i = 0; i < prompt.Length; i++)
        {
            if (prompt[i] != '|') continue;
            if (i + 1 < prompt.Length && prompt[i + 1] == '|') { random = !random; i++; }
            else if (!random && parts.Count < 5) { parts.Add(prompt[start..i]); start = i + 1; }
        }
        parts.Add(prompt[start..]);
        return parts.ToArray();
    }

    private static List<string> ExtractDialogue(string prompt)
    {
        var lines = new List<string>();
        for (int i = 0; i < prompt.Length; i++)
        {
            char end = prompt[i] switch { '"' => '"', '“' => '”', '「' => '」', '\'' => '\'', '‘' => '’', _ => '\0' };
            if (end == '\0' || (prompt[i] == '\'' && i > 0 && !Regex.IsMatch(prompt[(i - 1)..i], @"[\s,.]"))) continue;
            int j = i + 1;
            while (j < prompt.Length && (prompt[j] != end ||
                ((end == '\'' || end == '’') && j + 1 < prompt.Length && Regex.IsMatch(prompt[(j + 1)..], @"^[\p{L}\p{N}]")))) j++;
            if (j == prompt.Length) continue;
            string line = prompt[(i + 1)..j].Trim();
            if (line.Length > 0) lines.Add(line);
            i = j;
        }
        return lines;
    }

    private static IEnumerable<CharacterPromptInfo> OrderDialogueRoles(List<CharacterPromptInfo> roles)
    {
        if (roles.Count <= 1) return roles;
        int split = 1;
        double gap = -1;
        for (int i = 1; i < roles.Count; i++)
        {
            double delta = roles[i].CenterY - roles[i - 1].CenterY;
            if (delta > gap) { gap = delta; split = i; }
        }
        if (roles[^1].CenterY - roles[0].CenterY <= 0.15 && gap <= 0.1)
            return roles.OrderBy(ch => ch.CenterX);
        return OrderDialogueRoles(roles.GetRange(0, split)).Concat(OrderDialogueRoles(roles.GetRange(split, roles.Count - split)));
    }

    private static string DialogueText(string prompt, List<CharacterPromptInfo> roles, bool useCoords)
    {
        var ordered = useCoords ? OrderDialogueRoles(roles.OrderBy(ch => ch.CenterY).ToList()) : roles;
        var groups = new List<List<string>> { ExtractDialogue(prompt) };
        groups.AddRange(ordered.Select(ch => ExtractDialogue(ch.PositivePrompt)));
        string all = string.Concat(groups.SelectMany(g => g));
        if (all.Length > 0 && (double)Regex.Matches(all, @"[\u3000-\u303F\u3040-\u309F\u30A0-\u30FF\uFF00-\uFF9F\u4E00-\u9FAF\u3400-\u4DBF]").Count / all.Length > 0.3)
            foreach (var group in groups) group.Reverse();
        return string.Join("\n\n", groups.SelectMany(g => g));
    }

    internal static string AddAutoText(string prompt, List<CharacterPromptInfo>? characters)
    {
        var roles = characters?.Where(ch => ch.PositivePrompt.Length > 0).ToList() ?? new();
        if (TextMarker.IsMatch(prompt) || roles.Any(ch => TextMarker.IsMatch(ch.PositivePrompt))) return prompt;
        var mix = SplitPromptMix(prompt);
        string text = DialogueText(mix[0], roles, roles.Any(ch => ch.UseCustomPosition));
        if (text.Length == 0) return prompt;
        string body = Regex.Replace(mix[0], @"[\s,]+$", "");
        mix[0] = (body.Length > 0 ? body + ", " : "") + "teXt: " + text;
        return string.Join("|", mix);
    }

    internal static string StripAutoText(string prompt, ImageMetadata metadata, MetadataImportSelection? selection = null)
    {
        var captions = selection?.CharacterPromptsFrom(metadata) ?? metadata.CharacterPrompts;
        var centers = selection?.CharacterCentersFrom(metadata) ?? metadata.CharacterCenters;
        var roles = captions.Select((text, i) => new CharacterPromptInfo
        {
            PositivePrompt = text,
            CenterX = i < centers.Count ? centers[i].X : 0.5,
            CenterY = i < centers.Count ? centers[i].Y : 0.5,
            UseCustomPosition = metadata.UseCharacterCoordinates == true,
        }).ToList();
        return StripAutoText(prompt, roles);
    }

    private static string StripAutoText(string prompt, List<CharacterPromptInfo>? characters)
    {
        var roles = characters?.Where(ch => ch.PositivePrompt.Length > 0).ToList() ?? new();
        var mix = SplitPromptMix(prompt);
        for (int i = 0; i < mix.Length; i++)
        {
            var marker = AutoTextMarker.Match(mix[i]);
            if (!marker.Success) continue;
            string body = mix[i][..marker.Index];
            if (mix[i][(marker.Index + marker.Length)..].Trim() == DialogueText(body, roles, roles.Any(ch => ch.UseCustomPosition)))
                mix[i] = Regex.Replace(body, @"[\s,]+$", "");
        }
        return string.Join("|", mix);
    }

    internal static bool SupportsLightQuality(string model) => ModelCapabilities.BaseModel(model) is
        "nai-diffusion-5-full" or "nai-diffusion-5-curated" or "nai-diffusion-5-full-medium";

    internal static int HintId(string id) => id switch { "standard" => 1, "heavy" => 2, "light" => 3, "humanFocus" => 4, "furryFocus" => 5, "lowQualityPlusBadAnatomy" => 6, "lowQuality" => 7, "badAnatomy" => 8, _ => 0 };
    internal static string[] UcIds(string model) => ModelCapabilities.BaseModel(model) switch
    {
        "nai-diffusion-5-full" or "nai-diffusion-5-full-medium" or "nai-diffusion-5-curated" or "nai-diffusion-4-5-full" => new[] { "heavy", "light", "furryFocus", "humanFocus", "none" },
        "nai-diffusion-4-5-curated" or "nai-diffusion-3" => new[] { "heavy", "light", "humanFocus", "none" },
        _ => new[] { "heavy", "light", "none" },
    };
    internal static string ResolveUcId(NAIParameters p)
    {
        string id = p.UcPresetId ?? (p.UcPreset switch { 0 => "heavy", 1 => "light", _ => "none" });
        return System.Array.IndexOf(UcIds(p.Model), id) >= 0 ? id : "none";
    }
    private const string Heavy = "lowres, artistic error, film grain, scan artifacts, worst quality, bad quality, jpeg artifacts, very displeasing, chromatic aberration, dithering, halftone, screentone, multiple views, logo, too many watermarks, negative space, blank page";
    private const string FurryFocus = "{worst quality}, distracting watermark, unfinished, bad quality, {widescreen}, upscale, {sequence}, {{grandfathered content}}, blurred foreground, chromatic aberration, sketch, everyone, [sketch background], simple, [flat colors], ych (character), outline, multiple scenes, [[horror (theme)]], comic";
    internal static string UcTags(string model, string id)
    {
        string key = ModelCapabilities.BaseModel(model);
        if (id == "none") return key is "nai-diffusion-3" or "nai-diffusion-furry-3" ? "lowres" : "";
        if (key is "nai-diffusion-5-full" or "nai-diffusion-5-curated" or "nai-diffusion-5-full-medium" or "nai-diffusion-4-5-full")
            return id switch
            {
                "heavy" => Heavy,
                "humanFocus" => Heavy + ", @_@, mismatched pupils, glowing eyes, bad anatomy",
                "furryFocus" => FurryFocus,
                "light" => key == "nai-diffusion-4-5-full" ? "lowres, artistic error, scan artifacts, worst quality, bad quality, jpeg artifacts, multiple views, very displeasing, too many watermarks, negative space, blank page" : "lowres, bad hands, bad anatomy, artistic error, sepia, white haze, worst quality, very displeasing, jpeg artifacts, 0::ai-generated::",
                _ => "",
            };
        return (key, id) switch
        {
            ("nai-diffusion-4-5-curated", "heavy") => "blurry, lowres, upscaled, artistic error, film grain, scan artifacts, worst quality, bad quality, jpeg artifacts, very displeasing, chromatic aberration, halftone, multiple views, logo, too many watermarks, negative space, blank page",
            ("nai-diffusion-4-5-curated", "light") => "blurry, lowres, upscaled, artistic error, scan artifacts, jpeg artifacts, logo, too many watermarks, negative space, blank page",
            ("nai-diffusion-4-5-curated", "humanFocus") => "blurry, lowres, upscaled, artistic error, film grain, scan artifacts, bad anatomy, bad hands, worst quality, bad quality, jpeg artifacts, very displeasing, chromatic aberration, halftone, multiple views, logo, too many watermarks, @_@, mismatched pupils, glowing eyes, negative space, blank page",
            ("nai-diffusion-4-full", "heavy") => "blurry, lowres, error, film grain, scan artifacts, worst quality, bad quality, jpeg artifacts, very displeasing, chromatic aberration, multiple views, logo, too many watermarks, white blank page, blank page",
            ("nai-diffusion-4-full", "light") => "blurry, lowres, error, worst quality, bad quality, jpeg artifacts, very displeasing, white blank page, blank page",
            ("nai-diffusion-4-curated" or "nai-diffusion-4-curated-preview", "heavy") => "blurry, lowres, error, film grain, scan artifacts, worst quality, bad quality, jpeg artifacts, very displeasing, chromatic aberration, logo, dated, signature, multiple views, gigantic breasts, white blank page, blank page",
            ("nai-diffusion-4-curated" or "nai-diffusion-4-curated-preview", "light") => "blurry, lowres, error, worst quality, bad quality, jpeg artifacts, very displeasing, logo, dated, signature, white blank page, blank page",
            ("nai-diffusion-3", "heavy") => "lowres, {bad}, error, fewer, extra, missing, worst quality, jpeg artifacts, bad quality, watermark, unfinished, displeasing, chromatic aberration, signature, extra digits, artistic error, username, scan, [abstract]",
            ("nai-diffusion-3", "humanFocus") => UcTags(model, "heavy") + ", bad anatomy, bad hands, @_@, mismatched pupils, heart-shaped pupils, glowing eyes",
            ("nai-diffusion-3", "light") => "lowres, jpeg artifacts, worst quality, watermark, blurry, very displeasing",
            ("nai-diffusion-furry-3", "heavy") => "{{worst quality}}, [displeasing], {unusual pupils}, guide lines, {{unfinished}}, {bad}, url, artist name, {{tall image}}, mosaic, {sketch page}, comic panel, impact (font), [dated], {logo}, ych, {what}, {where is your god now}, {distorted text}, repeated text, {floating head}, {1994}, {widescreen}, absolutely everyone, sequence, {compression artifacts}, hard translated, {cropped}, {commissioner name}, unknown text, high contrast",
            ("nai-diffusion-furry-3", "light") => "{worst quality}, guide lines, unfinished, bad, url, tall image, widescreen, compression artifacts, unknown text",
            _ => "",
        };
    }
    internal static bool AddsAutomaticNsfw(string model, string id, string positive) =>
        id != "none" && UcTags(model, id).Length > 0 &&
        ModelCapabilities.BaseModel(model) is not ("safe-diffusion" or "test" or "custom" or "nai-diffusion-4-curated" or "nai-diffusion-4-curated-preview" or "nai-diffusion-4-5-curated" or "nai-diffusion-5-curated") &&
        !positive.Contains("nsfw", StringComparison.OrdinalIgnoreCase);

    internal static string ResolveUc(string model, string id, string positive, string userUc)
    {
        if (System.Array.IndexOf(UcIds(model), id) < 0) id = "none";
        string prefix = UcTags(model, id);
        string key = ModelCapabilities.BaseModel(model);
        bool addNsfw = AddsAutomaticNsfw(model, id, positive);

        // The website prefixes only the first UC segment for V4/V5 prompt mixes.
        bool characterPrompts = key.StartsWith("nai-diffusion-4", System.StringComparison.Ordinal) ||
            key.StartsWith("nai-diffusion-5", System.StringComparison.Ordinal);
        int split = characterPrompts ? userUc.IndexOf('|') : -1;
        if (!characterPrompts && id == "none" && userUc.Length > 0) prefix = "";
        string first = split < 0 ? userUc : userUc[..split];
        string remainder = split < 0 ? "" : userUc[split..];
        string withPreset = prefix.Length == 0 ? first : first.Length == 0 ? prefix : prefix + ", " + first;
        return (addNsfw ? (withPreset.Length == 0 ? "nsfw" : "nsfw, " + withPreset) : withPreset) + remainder;
    }

    internal static string AddQuality(string prompt, NAIParameters p)
    {
        string preset = QualityTags(p.Model, p.QualityToggle ? p.QualityPresetId ?? "standard" : "none");
        if (NovelAIService.IsV5Model(p.Model) && p.TagHintTransparentBackground)
            preset = "transparent background" + (preset.Length > 0 ? ", " + preset : "");
        if (preset.Length == 0) return prompt;
        string Apply(string body) => ModelCapabilities.BaseModel(p.Model) == "nai-diffusion-2"
            ? preset + (body.Length > 0 ? ", " + body : "")
            : (body.Length > 0 ? body + ", " : "") + preset;
        if (NovelAIService.IsV4PlusModel(p.Model))
        {
            var mix = SplitPromptMix(prompt);
            var marker = TextMarker.Match(mix[0]);
            mix[0] = marker.Success ? Apply(mix[0][..marker.Index]) + mix[0][marker.Index..] : Apply(mix[0]);
            return string.Join("|", mix);
        }
        return string.Join("|", prompt.Split('|').Select(part =>
        {
            var weight = Regex.Match(part, @":[\d.]+$");
            return weight.Success ? Apply(part[..weight.Index]) + weight.Value : Apply(part);
        }));
    }

    internal static string QualityTags(string model, string qualityPreset = "standard") => qualityPreset == "none" ? "" : ModelCapabilities.BaseModel(model) switch
    {
        "nai-diffusion-5-full" or "nai-diffusion-5-curated" or "nai-diffusion-5-full-medium" => qualityPreset == "light"
            ? "very aesthetic, amazing quality, no text" : "very aesthetic, masterpiece, no text",
        "nai-diffusion-4-5-full" => "very aesthetic, masterpiece, no text",
        "nai-diffusion-4-5-curated" => "very aesthetic, masterpiece, no text, -0.8::feet::, rating:general",
        "nai-diffusion-4-full" => "no text, best quality, very aesthetic, absurdres",
        "nai-diffusion-4-curated" or "nai-diffusion-4-curated-preview" => "rating:general, best quality, very aesthetic, absurdres",
        "nai-diffusion-2" => "very aesthetic, best quality, absurdres",
        "nai-diffusion-3" => "best quality, amazing quality, very aesthetic, absurdres",
        "nai-diffusion-furry-3" => "{best quality}, {amazing quality}",
        _ => "",
    };
}
