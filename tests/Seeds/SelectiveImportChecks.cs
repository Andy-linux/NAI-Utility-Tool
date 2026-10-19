using NAITool.Services;

internal static class SelectiveImportChecks
{
    public static int Run()
    {
        int checks = 0;
        var assembly = typeof(ImageMetadataService).Assembly;
        var strip = assembly.GetType("NAITool.Services.NovelAiPromptPresets", true)!.GetMethods(
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            .Single(m => m.Name == "StripAutoText" && m.GetParameters().Length == 3);
        string StripText(string prompt, ImageMetadata metadata, MetadataImportSelection? selection = null)
        {
            object? options = null;
            if (selection != null)
            {
                options = Activator.CreateInstance(assembly.GetType("NAITool.Services.MetadataImportSelection", true)!, true)!;
                foreach (var property in typeof(MetadataImportSelection).GetProperties().Where(p => p.CanWrite))
                    options.GetType().GetProperty(property.Name)!.SetValue(options, property.GetValue(selection));
            }
            return (string)strip.Invoke(null, [prompt, metadata, options])!;
        }
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
        Check(actual.CharacterPromptsFrom(metadata).SequenceEqual(new[] { "角色一", "", "" }),
            "Actual character entries retain their own slots, including missing or empty captions");
        Check(actual.CharacterPromptsFrom(metadata, negative: true).SequenceEqual(new[] { "negative one", "negative two", "negative three" }),
            "Actual negative character prompts retain ordering");
        Check(metadata.CharacterCenters.SequenceEqual(new[] { (0.1, 0.2), (0.3, 0.4), (0.5, 0.6) }) && metadata.UseCharacterCoordinates == true,
            "Original metadata remains available independently of the selected import");
        Check(original.CharacterPromptsFrom(metadata).SequenceEqual(new[] { "{one|1}", "{two|2}", "{three|3}" }),
            "Selecting actual prompts does not mutate the metadata used by later imports");

        foreach (bool negative in new[] { false, true })
        {
            var captions = negative ? metadata.ActualCharacterNegativePrompts : metadata.ActualCharacterPrompts;
            captions.RemoveAt(0);
            Check(actual.CharacterPromptsFrom(metadata, negative).SequenceEqual(captions.Select(x => x ?? "")),
                "A shorter actual character list replaces the original list");
            captions.Add("extra");
            captions.Add("extra");
            Check(actual.CharacterPromptsFrom(metadata, negative).SequenceEqual(captions.Select(x => x ?? "")),
                "A longer actual character list preserves its own entries");
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
            byte[] png = TestImage.CreatePng();
            string comment = "{\"prompt\":\"original\",\"uc\":\"negative\",\"actual_prompts\":" + actualJson + "}";
            var parsed = ImageMetadataService.ReadFromBytes(ImageMetadataService.ReplacePngTextChunks(png, new Dictionary<string, string>
            { ["Comment"] = comment, ["Description"] = "original" }));
            Check(parsed != null && actual.PositivePromptFrom(parsed) == "resolved" && actual.NegativePromptFrom(parsed) == "",
                "PNG import supports both string and structured actual prompts, including explicitly empty negatives");
            Check(original.PositivePromptFrom(parsed!) == "original" && original.NegativePromptFrom(parsed!) == "negative",
                "PNG import keeps original prompts available independently of actual prompts");
        }

        var empty = ImageMetadataService.TryParseJson("""
            {"v4_prompt":{"caption":{"char_captions":[{"char_caption":"old role"}]}},
             "actual_prompts":{"prompt":{"base_caption":"new prompt","char_captions":[]}}}
            """)!;
        Check(actual.CharacterPromptsFrom(empty).Count == 0, "An explicit empty actual list clears roles");
        var positioned = ImageMetadataService.TryParseJson("""
            {"actual_prompts":{"prompt":{"char_captions":[
                {"char_caption":"adult artist","centers":[{"x":0.8,"y":0.2}]}]}},
             "v4_prompt":{"caption":{"char_captions":[
                {"char_caption":"old role","centers":[{"x":0.1,"y":0.9}]}]}}}
            """)!;
        Check(actual.CharacterCentersFrom(positioned).Single() == (0.8, 0.2), "Actual captions use their own positions");
        Check(original.CharacterCentersFrom(positioned).Single() == (0.1, 0.9), "Original positions remain available");
        var text = ImageMetadataService.TryParseJson("""
            {"prompt":"shop \"OPEN\", teXt: OPEN\n\nHELLO",
             "v4_prompt":{"caption":{"char_captions":[{"char_caption":"adult artist, \"HELLO\""}]}}}
            """)!;
        foreach (var selection in new[]
        {
            new MetadataImportSelection { Characters = false },
            new MetadataImportSelection { AppendCharacters = true },
        })
            Check(StripText(text.PositivePrompt, text, selection) == "shop \"OPEN\"",
                "Automatic text is removed using source roles before partial or appended imports");
        Check(StripText("shop \"OPEN\", text: custom", text) == "shop \"OPEN\", text: custom",
            "Explicit text markers are not removed");
        Console.WriteLine($"Passed {checks} selective import metadata checks.");
        return checks;
    }
}
