namespace NAITool.Services;

/// <summary>Tag text verified against official qualitytags/undesiredcontent documentation.</summary>
internal static class NovelAiPromptPresets
{
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
        return System.Array.IndexOf(UcIds(p.Model), id) >= 0 ? id : "heavy";
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
    internal static string ResolveUc(string model, string id, string positive, string userUc)
    {
        if (System.Array.IndexOf(UcIds(model), id) < 0) id = "heavy";
        string prefix = UcTags(model, id);
        string key = ModelCapabilities.BaseModel(model);
        if (id != "none" && !key.Contains("curated", System.StringComparison.Ordinal) && !positive.Contains("nsfw", System.StringComparison.OrdinalIgnoreCase))
            prefix = "nsfw, " + prefix;
        return string.IsNullOrWhiteSpace(userUc) ? prefix : string.IsNullOrWhiteSpace(prefix) ? userUc : prefix + ", " + userUc;
    }

    internal static string QualityTags(string model, string qualityPreset = "standard") => qualityPreset == "none" ? "" : ModelCapabilities.BaseModel(model) switch
    {
        "nai-diffusion-5-full" or "nai-diffusion-5-curated" or "nai-diffusion-5-full-medium" => qualityPreset == "light"
            ? "very aesthetic, amazing quality, no text" : "very aesthetic, masterpiece, no text",
        "nai-diffusion-4-5-full" => "location, very aesthetic, masterpiece, no text",
        "nai-diffusion-4-5-curated" => "location, masterpiece, no text, -0.8::feet::, rating:general",
        "nai-diffusion-4-full" => "no text, best quality, very aesthetic, absurdres",
        "nai-diffusion-4-curated" or "nai-diffusion-4-curated-preview" => "rating:general, amazing quality, very aesthetic, absurdres",
        "nai-diffusion-3" => "best quality, amazing quality, very aesthetic, absurdres",
        "nai-diffusion-furry-3" => "{best quality}, {amazing quality}",
        _ => "",
    };
}
