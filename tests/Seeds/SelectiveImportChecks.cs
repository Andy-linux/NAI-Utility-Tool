using NAITool.Services;

internal static class SelectiveImportChecks
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            checks++;
            if (!condition) throw new Exception(message);
        }

        var original = new MetadataImportSelection();
        var actual = new MetadataImportSelection { ActualPrompt = true };
        var metadata = ImageMetadataService.TryParseJson("""
            {"prompt":"{red|blue} sky","uc":"{rain|snow}","seed":42,
             "actual_prompts":{"prompt":{"base_caption":"blue sky","char_captions":[
                {"char_caption":"角色一"},null,{"char_caption":""}]},
                "negative_prompt":{"base_caption":"snow","char_captions":[
                {"char_caption":"negative one"},{"char_caption":"negative two"},{"char_caption":"negative three"}]}},
             "v4_prompt":{"use_coords":true,"caption":{"char_captions":[
                {"char_caption":"{one|1}","centers":[{"x":0.1,"y":0.2}]},
                {"char_caption":"{two|2}","centers":[{"x":0.3,"y":0.4}]},
                {"char_caption":"{three|3}","centers":[{"x":0.5,"y":0.6}]}]}},
             "v4_negative_prompt":{"caption":{"char_captions":[
                {"char_caption":"neg1"},{"char_caption":"neg2"},{"char_caption":"neg3"}]}}}
            """) ?? throw new Exception("Actual prompts must not prevent ordinary metadata parsing");
        Check(original.PositivePromptFrom(metadata) == "{red|blue} sky" && original.NegativePromptFrom(metadata) == "{rain|snow}",
            "Actual prompts are opt-in; the original randomization syntax is preserved by default");
        Check(actual.PositivePromptFrom(metadata) == "blue sky" && actual.NegativePromptFrom(metadata) == "snow",
            "Actual base captions are imported when explicitly selected");
        Check(actual.CharacterPromptsFrom(metadata).SequenceEqual(new[] { "角色一", "{two|2}", "" }),
            "Malformed actual character entries fall back in place, while explicitly empty captions remain empty");
        Check(actual.CharacterPromptsFrom(metadata, negative: true).SequenceEqual(new[] { "negative one", "negative two", "negative three" }),
            "Actual negative character prompts retain ordering");
        Check(metadata.CharacterCenters.SequenceEqual(new[] { (0.1, 0.2), (0.3, 0.4), (0.5, 0.6) }) && metadata.UseCharacterCoordinates == true,
            "Choosing actual text does not replace original character coordinates or their usage flag");
        Check(original.CharacterPromptsFrom(metadata).SequenceEqual(new[] { "{one|1}", "{two|2}", "{three|3}" }),
            "Selecting actual prompts does not mutate the metadata used by later imports");

        foreach (bool negative in new[] { false, true })
        {
            var captions = negative ? metadata.ActualCharacterNegativePrompts : metadata.ActualCharacterPrompts;
            captions.RemoveAt(0);
            Check(actual.CharacterPromptsFrom(metadata, negative).SequenceEqual(original.CharacterPromptsFrom(metadata, negative)),
                "A shorter actual character list falls back to the full original list rather than shifting coordinates");
            captions.Add("extra");
            captions.Add("extra");
            Check(actual.CharacterPromptsFrom(metadata, negative).SequenceEqual(original.CharacterPromptsFrom(metadata, negative)),
                "A longer actual character list cannot overflow or change the original slot layout");
        }

        foreach (string actualJson in new[] { "null", "[]", "1", "{}", "{\"prompt\":null,\"negative_prompt\":false}" })
        {
            var parsed = ImageMetadataService.TryParseJson("{\"prompt\":\"original\",\"uc\":\"negative\",\"actual_prompts\":" + actualJson + "}");
            Check(parsed != null && actual.PositivePromptFrom(parsed) == "original" && actual.NegativePromptFrom(parsed) == "negative",
                "Missing or malformed actual prompts preserve the original text");
        }

        foreach (string actualJson in new[]
        {
            "{\"prompt\":\"resolved\",\"negative_prompt\":\"\"}",
            "{\"prompt\":{\"base_caption\":\"resolved\"},\"negative_prompt\":{\"base_caption\":\"\"}}",
        })
        {
            byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a9l8AAAAASUVORK5CYII=");
            string comment = "{\"prompt\":\"original\",\"uc\":\"negative\",\"actual_prompts\":" + actualJson + "}";
            var parsed = ImageMetadataService.ReadFromBytes(ImageMetadataService.ReplacePngComment(png, comment));
            Check(parsed != null && actual.PositivePromptFrom(parsed) == "resolved" && actual.NegativePromptFrom(parsed) == "",
                "PNG import supports both string and structured actual prompts, including explicitly empty negatives");
            Check(original.PositivePromptFrom(parsed!) == "original" && original.NegativePromptFrom(parsed!) == "negative",
                "PNG import keeps original prompts available independently of actual prompts");
        }

        foreach (bool sm in new[] { false, true })
        foreach (bool dynamicSm in new[] { false, true })
        foreach (bool variety in new[] { false, true })
        {
            var parsed = ImageMetadataService.TryParseJson($"{{\"prompt\":\"test\",\"sm\":{sm.ToString().ToLowerInvariant()},\"sm_dyn\":{dynamicSm.ToString().ToLowerInvariant()},\"skip_cfg_above_sigma\":{(variety ? "19" : "null")},\"quality_toggle\":false,\"ucPreset\":1}}");
            Check(parsed != null && parsed.Sm == sm && parsed.SmDyn == dynamicSm && parsed.Variety == variety,
                "SMEA, dynamic SMEA and Variety metadata parse independently");
            Check(parsed!.QualityToggle == false && parsed.UcPreset == 1,
                "Explicit quality and UC metadata flags remain available for import");
        }
        var draftMetadata = ImageMetadataService.TryParseJson("""
            {"prompt":"portrait","model":"nai-diffusion-5-full-medium","steps":14,
             "nai_tool_effort":"medium","nai_tool_high_negative":"High negative draft",
             "nai_tool_high_parameters":{"Model":"nai-diffusion-5-full","Effort":"medium","Steps":31,"Sampler":"k_dpmpp_2m","CfgRescale":0.35,"UcPresetId":"humanFocus"}}
            """);
        Check(draftMetadata?.HighDraft?.Steps == 31 && draftMetadata.HighDraft.Sampler == "k_dpmpp_2m" && draftMetadata.HighDraft.CfgRescale == 0.35,
            "Local Medium metadata retains High parameter draft separately from wire steps14");
        Check(draftMetadata?.HighNegativePrompt == "High negative draft" && draftMetadata.Effort == "medium",
            "Local Medium metadata retains High negative and effort");
        bool rejectedNonPng = false;
        try { ImageMetadataService.ReplacePngComment(new byte[] { 82, 73, 70, 70, 0, 0, 0, 0, 87, 69, 66, 80 }, "{}"); }
        catch (ArgumentException) { rejectedNonPng = true; }
        Check(rejectedNonPng, "PNG-only metadata writer rejects non-PNG bytes rather than corrupting format");
        Console.WriteLine($"Passed {checks} selective import metadata checks.");
        return checks;
    }
}
