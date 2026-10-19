using System.Collections.Generic;
using NAITool.Services;

namespace NAITool;

public sealed partial class MainWindow
{
    private void ApplyMetadataToGeneration(ImageMetadata meta, MetadataImportSelection options)
    {
        if (!options.HasSelection) return;
        SwitchMode(AppMode.ImageGeneration);
        SaveCurrentPromptToBuffer();
        SaveAllCharacterPrompts();

        bool blockOversizedSteps = IsAssetProtectionStepLimitEnabled();
        bool blockOversizedDimensions = IsAssetProtectionSizeLimitEnabled();
        var skipped = new List<string>();
        var notes = new List<string>();

        string positivePrompt = options.PositivePromptFrom(meta);
        if (meta.IsNaiParsed && !options.ActualPrompt)
            positivePrompt = NovelAiPromptPresets.StripAutoText(positivePrompt, meta);
        string negativePrompt = options.NegativePromptFrom(meta);
        if (meta.IsNaiParsed && negativePrompt == "lowres") negativePrompt = "";

        if (meta.IsSdFormat)
        {
            positivePrompt = ImageMetadataService.ConvertSdPromptToNai(positivePrompt);
            negativePrompt = ImageMetadataService.ConvertSdPromptToNai(negativePrompt);
            if (options.Prompt || options.NegativePrompt) notes.Add(L("metadata.note.sd_converted"));
        }

        var p = _settings.Settings.GenParameters;
        bool legacyUc = ModelCapabilities.SupportsLegacyUc(p.Model) && p.LegacyUc;
        if (options.Model) ApplyImportedImageModel(meta, p, GenerationModels);
        if (options.Settings && ModelCapabilities.SupportsEffort(p.Model))
            p.Effort = meta.Effort ?? (meta.ModelDisplayName.Contains("-medium", System.StringComparison.Ordinal) ? "medium" : "high");
        var presetMatch = ExtractImportedPromptPresetMatch(positivePrompt, negativePrompt, p.Model, meta);
        if (options.Settings || meta.IsNaiParsed)
        {
            positivePrompt = presetMatch.PositivePrompt;
            negativePrompt = presetMatch.NegativePrompt;
        }

        if (options.Prompt && (meta.IsNaiParsed || meta.HasPositivePrompt || options.ActualPrompt && meta.ActualPositivePrompt != null))
        {
            _genPositivePrompt = positivePrompt;
            _genStylePrompt = "";
        }
        if (options.NegativePrompt && (meta.IsNaiParsed || meta.HasNegativePrompt || options.ActualPrompt && meta.ActualNegativePrompt != null))
            _genNegativePrompt = negativePrompt;

        if (options.Settings)
        {
            var defaults = new NAIParameters { Model = p.Model };
            if (meta.IsNaiParsed)
            {
                p.DenoiseStrength = meta.Strength ?? defaults.DenoiseStrength;
                p.DenoiseNoise = meta.Noise ?? defaults.DenoiseNoise;
                p.InpaintStrength = defaults.InpaintStrength;
            }
            p.QualityToggle = meta.QualityToggle ?? presetMatch.QualityMatched;
            p.QualityPreset = meta.QualityPreset ?? presetMatch.QualityPreset;
            int steps = meta.HasSteps ? meta.Steps : defaults.Steps;
            if ((meta.HasSteps || meta.IsNaiParsed) && !ModelCapabilities.IsMedium(p))
            {
                if (blockOversizedSteps && steps > 28)
                    skipped.Add(Lf("metadata.skipped.steps", steps));
                else
                    p.Steps = steps;
            }
            if (meta.HasScale) p.Scale = meta.Scale;
            else if (meta.IsNaiParsed) p.Scale = defaults.Scale;
            if (!meta.IsSdFormat)
            {
                if (!ModelCapabilities.IsMedium(p)) p.CfgRescale = meta.CfgRescale;
            }
            if (!ModelCapabilities.IsMedium(p) && (meta.IsNaiParsed || !string.IsNullOrEmpty(meta.Sampler)))
                p.Sampler = NormalizeSamplerForModel(string.IsNullOrEmpty(meta.Sampler) ? defaults.Sampler : meta.Sampler, p.Model);
            if (meta.IsNaiParsed || !string.IsNullOrEmpty(meta.NoiseSchedule))
                p.Schedule = NormalizeScheduleForModel(string.IsNullOrEmpty(meta.NoiseSchedule) ? defaults.Schedule : meta.NoiseSchedule, p.Model, sampler: p.Sampler);
            if (!meta.IsSdFormat)
            {
                p.TagHintTransparentBackground = meta.TagHintTransparentBackground ?? false;
                p.DynamicThresholding = meta.DynamicThresholding;
                p.LegacyUc = legacyUc;
                p.Sm = meta.Sm || meta.SmDyn;
                p.SmDyn = meta.SmDyn;
                p.Variety = meta.Variety;
            }

            p.Sampler = NormalizeSamplerForModel(p.Sampler, p.Model);
            p.Schedule = NormalizeScheduleForModel(p.Schedule, p.Model, sampler: p.Sampler);
        }
        if (options.Settings || options.NegativePrompt && meta.IsNaiParsed)
        {
            p.UcPreset = meta.UcPresetId == "none" ? 2 : presetMatch.UcPresetMatched ?? 2;
            p.UcPresetId = meta.UcPresetId == "none" ? "none" : presetMatch.UcPresetId ?? "none";
        }
        if (options.Seed && !string.IsNullOrEmpty(meta.Seed)) p.Seed = meta.Seed;

        if (options.Size && meta.Width > 0 && meta.Height > 0)
        {
            if (blockOversizedDimensions && (long)meta.Width * meta.Height > 1024L * 1024)
                skipped.Add(Lf("metadata.skipped.size", meta.Width, meta.Height));
            else
            {
                _customWidth = meta.Width;
                _customHeight = meta.Height;
            }
        }

        ImportSelectedCharacters(meta, options, toI2I: false, notes);
        if (options.References) ApplyReferenceDataFromMetadata(meta);

        RefreshCharacterPanel();

        SetSizeInputsSilently(_customWidth, _customHeight);
        NbSeed.Value = p.Seed;
        ChkVariety.IsChecked = p.Variety;
        if (IsAdvancedWindowOpen) SyncSidebarToAdvanced();

        LoadPromptFromBuffer();
        UpdateEffortUI();
        UpdateSplitVisibility();
        UpdateSizeWarningVisuals();

        if (options.Settings && presetMatch.QualityMatched) notes.Add(L("metadata.note.quality_extracted"));
        if (options.Settings && presetMatch.UcPresetMatched.HasValue)
            notes.Add(Lf("metadata.note.negative_quality_extracted", GetUcPresetDisplayName(presetMatch.UcPresetMatched.Value)));
        if (skipped.Count > 0) notes.Add(Lf("metadata.note.incompatible_skipped", string.Join(", ", skipped)));
        if (options.References) AppendReferenceImportNotes(meta, notes);

        TxtStatus.Text = notes.Count > 0
            ? Lf("inspect.sent_parameters_with_notes", string.Join("; ", notes))
            : L("inspect.sent_parameters_to_generate");
    }

    private void ApplyMetadataToI2I(ImageMetadata meta, string fileName, MetadataImportSelection options)
    {
        if (!options.HasSelection) return;
        SaveCurrentPromptToBuffer();
        SyncUIToParams();
        var notes = new List<string>();
        var skipped = new List<string>();
        bool blockOversizedSteps = IsAssetProtectionStepLimitEnabled();

        string positivePrompt = options.PositivePromptFrom(meta);
        if (meta.IsNaiParsed && !options.ActualPrompt)
            positivePrompt = NovelAiPromptPresets.StripAutoText(positivePrompt, meta);
        string negativePrompt = options.NegativePromptFrom(meta);
        if (meta.IsNaiParsed && negativePrompt == "lowres") negativePrompt = "";

        if (meta.IsSdFormat)
        {
            positivePrompt = ImageMetadataService.ConvertSdPromptToNai(positivePrompt);
            negativePrompt = ImageMetadataService.ConvertSdPromptToNai(negativePrompt);
            if (options.Prompt || options.NegativePrompt) notes.Add(L("metadata.note.sd_converted"));
        }

        var p = ImageRequestParameters;
        bool legacyUc = ModelCapabilities.SupportsLegacyUc(p.Model) && p.LegacyUc;
        if (options.Model) ApplyImportedImageModel(meta, p, _i2iEditMode == I2IEditMode.Denoise ? GenerationModels : I2IModels);
        if (options.Settings && ModelCapabilities.SupportsEffort(p.Model))
            p.Effort = meta.Effort ?? (meta.ModelDisplayName.Contains("-medium", System.StringComparison.Ordinal) ? "medium" : "high");
        var presetMatch = ExtractImportedPromptPresetMatch(positivePrompt, negativePrompt, p.Model, meta);
        if (options.Settings || meta.IsNaiParsed)
        {
            positivePrompt = presetMatch.PositivePrompt;
            negativePrompt = presetMatch.NegativePrompt;
        }

        if (options.Prompt && (meta.IsNaiParsed || meta.HasPositivePrompt || options.ActualPrompt && meta.ActualPositivePrompt != null))
        {
            _i2iPositivePrompt = positivePrompt;
            _i2iStylePrompt = "";
        }
        if (options.NegativePrompt && (meta.IsNaiParsed || meta.HasNegativePrompt || options.ActualPrompt && meta.ActualNegativePrompt != null))
            _i2iNegativePrompt = negativePrompt;

        if (options.Settings)
        {
            var defaults = new NAIParameters { Model = p.Model };
            if (meta.IsNaiParsed)
            {
                p.DenoiseStrength = meta.Strength ?? defaults.DenoiseStrength;
                p.DenoiseNoise = meta.Noise ?? defaults.DenoiseNoise;
                p.InpaintStrength = defaults.InpaintStrength;
            }
            p.QualityToggle = meta.QualityToggle ?? presetMatch.QualityMatched;
            p.QualityPreset = meta.QualityPreset ?? presetMatch.QualityPreset;
            int steps = meta.HasSteps ? meta.Steps : defaults.Steps;
            if ((meta.HasSteps || meta.IsNaiParsed) && !ModelCapabilities.IsMedium(p))
            {
                if (blockOversizedSteps && steps > 28)
                    skipped.Add(Lf("metadata.skipped.steps", steps));
                else
                    p.Steps = steps;
            }
            if (meta.HasScale) p.Scale = meta.Scale;
            else if (meta.IsNaiParsed) p.Scale = defaults.Scale;
            if (!meta.IsSdFormat && !ModelCapabilities.IsMedium(p)) p.CfgRescale = meta.CfgRescale;
            if (!ModelCapabilities.IsMedium(p) && (meta.IsNaiParsed || !string.IsNullOrEmpty(meta.Sampler)))
                p.Sampler = NormalizeSamplerForModel(string.IsNullOrEmpty(meta.Sampler) ? defaults.Sampler : meta.Sampler, p.Model);
            if (meta.IsNaiParsed || !string.IsNullOrEmpty(meta.NoiseSchedule))
                p.Schedule = NormalizeScheduleForModel(string.IsNullOrEmpty(meta.NoiseSchedule) ? defaults.Schedule : meta.NoiseSchedule, p.Model, sampler: p.Sampler);
            if (!meta.IsSdFormat)
            {
                p.TagHintTransparentBackground = meta.TagHintTransparentBackground ?? false;
                p.DynamicThresholding = meta.DynamicThresholding;
                p.LegacyUc = legacyUc;
                p.Sm = meta.Sm || meta.SmDyn;
                p.SmDyn = meta.SmDyn;
                p.Variety = meta.Variety;
            }

            p.Sampler = NormalizeSamplerForModel(p.Sampler, p.Model);
            p.Schedule = NormalizeScheduleForModel(p.Schedule, p.Model, sampler: p.Sampler);
        }
        if (options.Settings || options.NegativePrompt && meta.IsNaiParsed)
        {
            p.UcPreset = meta.UcPresetId == "none" ? 2 : presetMatch.UcPresetMatched ?? 2;
            p.UcPresetId = meta.UcPresetId == "none" ? "none" : presetMatch.UcPresetId ?? "none";
        }
        if (options.Seed && !string.IsNullOrEmpty(meta.Seed)) p.Seed = meta.Seed;

        NbSeed.Value = p.Seed;
        ChkVariety.IsChecked = p.Variety;

        if (meta.IsNaiParsed)
        {
            ImportSelectedCharacters(meta, options, toI2I: true, notes);
            if (options.References) ApplyReferenceDataFromMetadata(meta, AppMode.I2I);
        }

        RefreshCharacterPanel();
        LoadPromptFromBuffer();
        UpdateEffortUI();
        UpdateSplitVisibility();
        if (IsAdvancedWindowOpen) SyncSidebarToAdvanced();

        if (options.Settings && presetMatch.QualityMatched) notes.Add(L("metadata.note.quality_extracted"));
        if (options.Settings && presetMatch.UcPresetMatched.HasValue)
            notes.Add(Lf("metadata.note.negative_quality_extracted", GetUcPresetDisplayName(presetMatch.UcPresetMatched.Value)));
        if (skipped.Count > 0) notes.Add(Lf("metadata.note.skipped", string.Join(", ", skipped)));
        if (options.References) AppendReferenceImportNotes(meta, notes);

        TxtStatus.Text = notes.Count > 0
            ? Lf("metadata.applied_with_notes", fileName, string.Join("; ", notes))
            : Lf("metadata.applied", fileName);
    }
}
