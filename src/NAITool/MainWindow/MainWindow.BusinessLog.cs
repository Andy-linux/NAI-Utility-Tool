using NAITool.Services;

namespace NAITool;

public sealed partial class MainWindow
{
    // Import provenance is scoped to the edited target, never treated as returned server Info.
    private readonly Dictionary<string, System.Text.Json.JsonElement> _businessImports = new();
    private void RememberBusinessImport(string target, BusinessLog log, ImageMetadata meta, string fileName, object selection)
    {
        _businessImports[target] = System.Text.Json.JsonSerializer.SerializeToElement(new
        { import_id = log.GenerationId, meta.RawJson, meta.Source, fileName, selected_options = selection });
    }
    private static object[] CaptureBusinessCharacters(IEnumerable<CharacterEntry> entries, bool visible) => entries.Select(x => (object)new
    {
        positive = visible && x.IsPositiveTab ? x.PromptBox?.Text ?? x.PositivePrompt : x.PositivePrompt,
        negative = visible && !x.IsPositiveTab ? x.PromptBox?.Text ?? x.NegativePrompt : x.NegativePrompt,
        x.CenterX, x.CenterY, x.IsDisabled, enabled = !x.IsDisabled, x.UseCustomPosition
    }).ToArray();
    private string BusinessPrompt(AppMode mode, bool positive, string buffered) =>
        _currentMode == mode && _isPositiveTab == positive ? TxtPrompt.Text : buffered;
    private string BusinessStyle(AppMode mode, string buffered) =>
        _currentMode == mode && _isPositiveTab && _isSplitPrompt ? TxtStylePrompt.Text : buffered;
    private string BusinessTarget => _currentMode == AppMode.ImageGeneration ? "generation" : "i2i";
    private object CaptureBusinessInputs() => new
    {
        mode = _currentMode.ToString(), image_mode = _i2iEditMode.ToString(),
        last_applied_import = _businessImports.TryGetValue(BusinessTarget, out var imported) ? (object)imported : null,
        generation = new { positive = BusinessPrompt(AppMode.ImageGeneration, true, _genPositivePrompt),
            style = BusinessStyle(AppMode.ImageGeneration, _genStylePrompt), negative = BusinessPrompt(AppMode.ImageGeneration, false, _genNegativePrompt),
            characters = CaptureBusinessCharacters(_genCharacters, _currentMode == AppMode.ImageGeneration), parameters = _settings.Settings.GenParameters, width = _customWidth, height = _customHeight },
        i2i = new { positive = BusinessPrompt(AppMode.I2I, true, _i2iPositivePrompt),
            style = BusinessStyle(AppMode.I2I, _i2iStylePrompt), negative = BusinessPrompt(AppMode.I2I, false, _i2iNegativePrompt),
            characters = CaptureBusinessCharacters(_i2iCharacters, _currentMode == AppMode.I2I), parameters = ImageRequestParameters },
        edited_vibes = _genVibeTransfers.Select(x => new { x.FileName, x.ImageBase64, x.Strength,
            x.InformationExtracted, x.IsDisabled, x.IsEncodedFile, x.OriginalImageBase64, x.OriginalImagePath, x.IsCachedEncoding }).ToArray(),
        edited_references = _genPreciseReferences.Select(x => new { x.FileName, x.ImageBase64,
            x.ReferenceType, x.Strength, x.Fidelity, x.IsDisabled }).ToArray(),
        stream = _settings.Settings.StreamGeneration, privacy_image_saving = _settings.Settings.PrivacyMode,
        strip_saved_metadata = _settings.Settings.StripSavedImageMetadata
    };
    private void LogBusinessMetadata(string stage, byte[] image)
    {
        if (BusinessLog.Current?.Debug != true) return;
        try { BusinessLog.Current.Stage(stage, ImageMetadataService.ReadFromBytes(image)); }
        catch (Exception ex) { BusinessLog.Current.Stage(stage + "_failed", ex.ToString()); }
    }
    private BusinessLog BeginUiBusinessLog(string operation, object? extra = null)
    {
        return _naiService.BeginBusinessLog(new { operation, inputs = CaptureBusinessInputs(), extra });
    }
}
