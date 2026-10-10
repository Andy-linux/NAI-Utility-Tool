using System.Text.Json;
using NAITool.Services;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    passed++;
    Console.WriteLine($"PASS {name}");
}
Check(new NAIParameters().Steps == 23, "New V5 High defaults to23");
Check(new NAIParameters { Model = NAIParameters.DefaultInpaintModel }.Steps == 28, "Fresh V4.5 infill remains28");
Check(new NAIParameters { Model = NAIParameters.DefaultI2IDenoiseModel }.Steps == 28, "Fresh V4.5 img2img remains28");
Check(JsonSerializer.Deserialize<NAIParameters>("{\"Model\":\"nai-diffusion-3\"}")!.Steps == 28, "Absent old model steps defaults28");
Check(JsonSerializer.Deserialize<NAIParameters>("{\"Model\":\"nai-diffusion-5-full\",\"Steps\":28}")!.Steps == 28, "Saved V5 steps28 preserved");
var saved = JsonSerializer.Deserialize<NAIParameters>("{\"Steps\":37,\"Seed\":123}")!;
Check(saved.Steps == 37, "Saved explicit steps preserved");
Check(saved.Seed == "123", "Legacy numeric seed preserved");
var draft = new NAIParameters { Effort = "medium", Sm = true, SmDyn = false, Variety = true, Steps = 31, CfgRescale = 0.5, UcPreset = 1 };
var restored = JsonSerializer.Deserialize<NAIParameters>(JsonSerializer.Serialize(draft))!;
Check(restored.Effort == "medium" && restored.Steps == 31 && restored.CfgRescale == 0.5 && restored.UcPreset == 1, "Effort and High draft roundtrip");
Check(restored.Sm && !restored.SmDyn && restored.Variety, "SMEA dynamicSMEA Variety independent roundtrip");
Check(NovelAiAnlasCalculator.BaseCost("nai-diffusion-5-full", 832, 1216, 23, true, true) > 0, "V5 exhausted requires paid base");
Check(NovelAiAnlasCalculator.BaseCost("nai-diffusion-5-full", 832, 1216, 23, true, false) == 0, "Known eligible Opus free");
Check(NovelAiAnlasCalculator.ReferenceCost(2, 6, 1) == 13, "References independent fee");
Check(NovelAiAnlasCalculator.BaseCost("nai-diffusion-5-full", 832, 1216, 23, true, null) > 0, "Unknown V5 eligibility never labeled free");
foreach (bool i2i in new[] { false, true })
foreach (bool upscale in new[] { false, true })
foreach (bool effects in new[] { false, true })
{
    var summary = WorkspaceUnsavedSummary.Collect(i2i, upscale, effects);
    Check(summary.Length == (i2i ? 1 : 0) + (upscale ? 1 : 0) + (effects ? 1 : 0), "All dirty workspace combinations summarized independent of current mode");
}
Check(!ModelCapabilities.SupportsVariety("nai-diffusion-5-full"), "V5 Variety disabled");
Check(!ModelCapabilities.SupportsVariety("nai-diffusion-5-full-inpainting"), "V5 infill Variety disabled");
Check(!ModelCapabilities.SupportsSmea("nai-diffusion-4-5-full"), "V4.5 SMEA disabled");
Check(ModelCapabilities.SupportsSmea("nai-diffusion-3"), "V3 SMEA supported");
Check(NovelAiPromptPresets.QualityTags("nai-diffusion-5-full", "light") == "very aesthetic, amazing quality, no text", "Official V5 light quality tags");
Check(NovelAiPromptPresets.QualityTags("nai-diffusion-4-5-full") == "very aesthetic, masterpiece, no text", "Bundle authoritative V4.5 quality tags");
Check(NovelAiPromptPresets.QualityTags("nai-diffusion-5-full-inpainting") == NovelAiPromptPresets.QualityTags("nai-diffusion-5-full"), "Infill quality normalization");
var mediumSnapshot = ModelCapabilities.CreateRequestSnapshot(draft);
Check(mediumSnapshot.Model == "nai-diffusion-5-full-medium" && mediumSnapshot.Steps == 14 && mediumSnapshot.Sampler == "k_euler_ancestral", "Verified Medium effective snapshot");
Check(draft.Steps == 31 && draft.CfgRescale == 0.5 && draft.UcPreset == 1, "Medium snapshot preserves High draft");
Check(ModelCapabilities.GetSkipCfgAboveSigma("nai-diffusion-3", 832, 1216, true) == 19, "V3 Variety base sigma");
Check(ModelCapabilities.GetSkipCfgAboveSigma("nai-diffusion-4-5-full", 832, 1216, true) == 58, "V4.5 Variety base sigma");
Check(ModelCapabilities.GetSkipCfgAboveSigma("nai-diffusion-5-full", 832, 1216, true) == null, "V5 omits Variety wire sigma");
Check(NovelAiPromptPresets.HintId("heavy") == 2 && NovelAiPromptPresets.HintId("none") == 0, "UC wire hint independent of dropdown position");
Check(NovelAiPromptPresets.ResolveUc("nai-diffusion-5-full-medium", "heavy", "portrait", "").StartsWith("nsfw, lowres"), "Medium fixed heavy UC includes verified prefix");
Check(NovelAiAnlasCalculator.PaidBaseCost("nai-diffusion-5-full-medium", 1024, 1024, 14) == 18, "Medium verified pricing factors");
Check(EnhanceRules.GetAvailableAmounts(500, 500).SequenceEqual(new[] { 1d, 1.5, 2d }), "Enhance500 safe scale choices");
Check(EnhanceRules.GetOutputDimensions(500, 500, 1.5) == (750, 750), "Enhance500 dimensions preserve source ratio");
Check(NovelAiPromptPresets.ResolveUcId(new NAIParameters { UcPresetId = "corrupt" }) == "heavy", "Invalid imported UC ID falls back coherently");
Check(NovelAiPromptPresets.QualityTags("nai-diffusion-5-full", "none") == "", "None quality tags empty");
foreach (string model in new[] { "nai-diffusion-5-full", "nai-diffusion-4-5-full", "nai-diffusion-4-5-curated", "nai-diffusion-4-full", "nai-diffusion-4-curated-preview", "nai-diffusion-3", "nai-diffusion-furry-3" })
foreach (string id in NovelAiPromptPresets.UcIds(model))
{
    Check(id == "none" || !string.IsNullOrEmpty(NovelAiPromptPresets.UcTags(model, id)), "All exposed UC options have official tag text");
    var restoredPreset = JsonSerializer.Deserialize<NAIParameters>(JsonSerializer.Serialize(new NAIParameters { Model = model, UcPresetId = id }))!;
    Check(NovelAiPromptPresets.ResolveUcId(restoredPreset) == id, "All model UC IDs roundtrip");
}
var switchedQuality = new NAIParameters { Model = "nai-diffusion-4-5-full", QualityPreset = "light" };
Check(ModelCapabilities.CreateRequestSnapshot(switchedQuality).QualityPresetId == "standard" && switchedQuality.QualityPreset == "light",
    "Unsupported light quality normalizes effective tags/hint while retaining V5 draft");
var invalidQuality = new NAIParameters { QualityPreset = "invalid" };
Check(ModelCapabilities.CreateRequestSnapshot(invalidQuality).QualityPresetId == "standard" && invalidQuality.QualityPreset == "invalid",
    "Invalid quality configuration uses coherent effective standard tags/hint without mutating saved draft");
var referenceCost = ApiRequestCost.Image("nai-diffusion-4-5-full", 832, 1216, 23, referenceAnlas: 5, characterReference: true);
Check(referenceCost.AnlasFor(new NovelAiAccountInfo { IsOpus = true, HasActiveSubscription = true }) == referenceCost.PaidBaseAnlas + 5,
    "Precise reference cancels Opus free base in shared UI/router cost");
var protectedDraft = new NAIParameters { Effort = "medium", Steps = 41 };
Check(HighDraftRules.TransformNegative(protectedDraft, "1girl, custom", text => text.Replace("1girl", "girl")) == "1girl, custom",
    "Medium character prefix stripping never mutates High negative draft");
Check(HighDraftRules.CaptureActiveNegatives(new[] { (true, "disabled"), (false, "active-one"), (true, "disabled-two"), (false, "active-two") }).SequenceEqual(new[] { "active-one", "active-two" }),
    "Metadata High character negatives match enabled-only wire ordering");
HighDraftRules.ApplyProtectedStepLimit(protectedDraft);
Check(protectedDraft.Steps == 41, "Asset protection does not clamp inactive High draft while Medium effective steps14");
Console.WriteLine($"{passed} model assertions passed");
