using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NAITool.Services;
using SkiaSharp;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace NAITool;

public sealed partial class MainWindow
{
    private async void OnAddVibeTransfer(object sender, RoutedEventArgs e)
    {
        int maxVibeTransfers = GetMaxAllowedVibeTransfers();
        if (TryReportVibeTransferUnavailable(maxVibeTransfers))
            return;

        var newEntry = await CreateVibeTransferEntryAsync();
        if (newEntry == null)
            return;

        if (ActiveVibeTransferCount() >= maxVibeTransfers)
        {
            TxtStatus.Text = IsAssetProtectionPaidFeatureLimitEnabled()
                ? Lf("references.error.asset_protection_vibe_count_limit", AssetProtectionFreeVibeLimit)
                : Lf("references.error.vibe_count_limit", maxVibeTransfers);
            return;
        }

        _genVibeTransfers.Add(newEntry);
        RecheckVibeTransferCacheState(refreshPanel: false);
        RefreshReferencePanels();
        UpdateGenerateButtonWarning();
        TxtStatus.Text = Lf("references.status.added_vibe", newEntry.FileName);
    }

    private async void OnAddPreciseReference(object sender, RoutedEventArgs e)
    {
        if (TryReportPreciseReferenceUnavailable())
            return;

        var newEntry = await CreatePreciseReferenceEntryAsync();
        if (newEntry == null)
            return;

        _genPreciseReferences.Add(newEntry);
        RefreshReferencePanels();
        UpdateGenerateButtonWarning();
        TxtStatus.Text = Lf("references.status.added_precise", newEntry.FileName);
    }

    private async Task<VibeTransferEntry?> CreateVibeTransferEntryAsync()
    {
        var picked = await PickVibeTransferSourceAsync();
        return CreateVibeTransferEntry(picked);
    }

    private async Task<PreciseReferenceEntry?> CreatePreciseReferenceEntryAsync()
    {
        var picked = await PickReferenceImageAsync();
        return CreatePreciseReferenceEntry(picked);
    }

    private VibeTransferEntry? CreateVibeTransferEntry(VibePickResult? picked)
    {
        if (picked == null)
            return null;

        return new VibeTransferEntry
        {
            FileName = picked.FileName,
            ImageBase64 = picked.ImageBase64,
            IsEncodedFile = picked.IsEncodedFile,
            OriginalImageHash = picked.ImageHash,
            OriginalThumbnailHash = picked.ThumbnailHash,
            OriginalImageBase64 = picked.OriginalBase64,
            OriginalImagePath = picked.OriginalImagePath,
            IsCachedEncoding = picked.IsCachedHit,
            EncodingModel = picked.EncodingModel ?? (picked.IsCachedHit ? GetCurrentModelKey() : null),
            HasInformationExtracted = picked.HasInformationExtracted,
            InformationExtracted = picked.InformationExtracted,
            Strength = picked.Strength,
        };
    }

    private PreciseReferenceEntry? CreatePreciseReferenceEntry((string FileName, string ImageBase64)? picked)
    {
        if (picked == null)
            return null;

        return new PreciseReferenceEntry
        {
            FileName = picked.Value.FileName,
            ImageBase64 = picked.Value.ImageBase64,
        };
    }

    private async Task<(string FileName, string ImageBase64)?> PickReferenceImageAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".webp");
        picker.FileTypeFilter.Add(".bmp");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var file = await picker.PickSingleFileAsync();
        if (file == null)
            return null;

        return await CreateReferenceImagePickResultAsync(file);
    }

    private async Task<(string FileName, string ImageBase64)?> CreateReferenceImagePickResultAsync(StorageFile file)
    {
        byte[]? pngBytes = await ReadImageFileAsPngAsync(file);
        if (pngBytes == null || pngBytes.Length == 0)
        {
            TxtStatus.Text = Lf("references.error.cannot_read_reference_image", file.Name);
            return null;
        }

        return (file.Name, Convert.ToBase64String(pngBytes));
    }

    private sealed record VibePickResult(
        string FileName,
        string ImageBase64,
        bool IsEncodedFile,
        string? ImageHash = null,
        string? ThumbnailHash = null,
        string? OriginalBase64 = null,
        bool IsCachedHit = false,
        string? OriginalImagePath = null,
        string? EncodingModel = null,
        bool HasInformationExtracted = true,
        double InformationExtracted = 1.0,
        double Strength = 0.6);

    private async Task<VibePickResult?> PickVibeTransferSourceAsync()
    {
        var picker = new FileOpenPicker();

        if (RequiresEncodedVibeFileOnly())
        {
            picker.FileTypeFilter.Add(".naiv4vibe");
        }
        else
        {
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".webp");
            picker.FileTypeFilter.Add(".bmp");
            picker.FileTypeFilter.Add(".naiv4vibe");
        }

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var file = await picker.PickSingleFileAsync();
        if (file == null)
            return null;

        return await CreateVibeTransferPickResultAsync(file);
    }

    private async Task<VibePickResult?> CreateVibeTransferPickResultAsync(StorageFile file)
    {
        if (file.FileType.Equals(".naiv4vibe", StringComparison.OrdinalIgnoreCase))
        {
            byte[] bytes = await File.ReadAllBytesAsync(file.Path);
            if (bytes.Length == 0)
            {
                TxtStatus.Text = Lf("references.error.cannot_read_encoded_vibe", file.Name);
                return null;
            }

            string json = System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
            if (json.StartsWith("{"))
            {
                string? encoding = VibeCacheService.ExtractEncodingFromVibeJson(json, model: VibeCacheService.ExtractModelFromVibeJson(json) ?? GetCurrentModelKey());
                using var document = System.Text.Json.JsonDocument.Parse(json);
                var root = document.RootElement;
                string? model = VibeCacheService.ExtractModelFromVibeJson(json) ?? GetCurrentModelKey();
                string? source = root.TryGetProperty("type", out var type) && type.GetString() == "image" &&
                    root.TryGetProperty("image", out var image) && image.ValueKind == System.Text.Json.JsonValueKind.String
                    ? image.GetString() : null;
                bool knownExtraction = root.TryGetProperty("importInfo", out var importInfo) &&
                    importInfo.TryGetProperty("information_extracted", out var ie) && ie.TryGetDouble(out _);
                double extraction = knownExtraction
                    ? importInfo.GetProperty("information_extracted").GetDouble()
                    : VibeTransferInfo.DefaultInformationExtracted(model);
                double strength = importInfo.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    importInfo.TryGetProperty("strength", out var strengthValue) && strengthValue.TryGetDouble(out var storedStrength)
                    ? Math.Clamp(storedStrength, 0, 1) : 0.6;
                if (!string.IsNullOrWhiteSpace(source))
                    encoding = knownExtraction ? VibeCacheService.ExtractEncodingFromVibeJson(json, extraction, model) : null;
                if (!string.IsNullOrWhiteSpace(source))
                {
                    byte[] sourceBytes = Convert.FromBase64String(source);
                    string hash = VibeCacheService.ComputeImageHash(sourceBytes);
                    if (encoding != null && knownExtraction && model != null)
                        VibeCacheService.SaveVibe(VibeCacheService.GetCacheDir(AppRootDir), sourceBytes, sourceBytes,
                            Convert.FromBase64String(encoding), extraction, model);
                    return new VibePickResult(file.Name, encoding ?? source, IsEncodedFile: encoding != null, ImageHash: hash,
                        OriginalBase64: source, IsCachedHit: encoding != null, EncodingModel: model,
                        HasInformationExtracted: knownExtraction, InformationExtracted: extraction, Strength: strength);
                }
                if (encoding == null)
                {
                    TxtStatus.Text = Lf("references.error.cannot_read_encoded_vibe", file.Name);
                    return null;
                }
                return new VibePickResult(file.Name, encoding, IsEncodedFile: true,
                    EncodingModel: model, HasInformationExtracted: knownExtraction, InformationExtracted: extraction, Strength: strength);
            }
            return new VibePickResult(file.Name, Convert.ToBase64String(bytes), IsEncodedFile: true,
                EncodingModel: GetCurrentModelKey(), HasInformationExtracted: false);
        }

        if (RequiresEncodedVibeFileOnly())
        {
            TxtStatus.Text = L("superdrop.error.asset_protection_vibe_image");
            return null;
        }

        byte[]? pngBytes = await ReadImageFileAsPngAsync(file);
        if (pngBytes == null || pngBytes.Length == 0)
        {
            TxtStatus.Text = Lf("references.error.cannot_read_reference_image", file.Name);
            return null;
        }

        string imageHash = VibeCacheService.ComputeImageHash(pngBytes);
        string thumbnailHash = VibeCacheService.ComputeThumbnailHash(
            VibeCacheService.CreateCanonicalThumbnail(pngBytes));
        string originalBase64 = Convert.ToBase64String(pngBytes);
        string currentModel = GetCurrentModelKey();
        double defaultExtraction = VibeTransferInfo.DefaultInformationExtracted(currentModel);
        string cacheDir = VibeCacheService.GetCacheDir(AppRootDir);
        string? originalPath = string.IsNullOrWhiteSpace(file.Path) ? null : file.Path;

        string? cachedEncoding = VibeCacheService.TryGetCachedVibeByLookup(
            cacheDir, imageHash, defaultExtraction, currentModel);
        if (cachedEncoding != null)
        {
            TxtStatus.Text = Lf("references.status.cached_vibe_loaded", file.Name, currentModel);
            return new VibePickResult(file.Name, cachedEncoding, IsEncodedFile: true,
                ImageHash: imageHash, ThumbnailHash: thumbnailHash, OriginalBase64: originalBase64, IsCachedHit: true,
                OriginalImagePath: originalPath, InformationExtracted: defaultExtraction);
        }

        var (thumbnailMatchedEncoding, matchedImageHash) = VibeCacheService.TryGetCachedVibeByThumbnailHash(
            cacheDir, thumbnailHash, defaultExtraction, currentModel);
        if (thumbnailMatchedEncoding != null && matchedImageHash != null)
        {
            TxtStatus.Text = Lf("references.status.cached_vibe_loaded", file.Name, currentModel);
            // A thumbnail match identifies an encoding, not the source pixels used to create it.
            foreach (var cached in VibeCacheService.ListAllEntries(cacheDir).Where(x => x.ImageHash == matchedImageHash))
            {
                string? sourcePath = VibeCacheService.ResolveOriginalPath(cacheDir, cached.OriginalImagePath);
                if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) continue;
                try
                {
                    var sourceFile = await StorageFile.GetFileFromPathAsync(sourcePath);
                    var sourceBytes = await ReadImageFileAsPngAsync(sourceFile);
                    if (sourceBytes == null || VibeCacheService.ComputeImageHash(sourceBytes) != matchedImageHash) continue;
                    return new VibePickResult(file.Name, thumbnailMatchedEncoding, IsEncodedFile: true,
                        ImageHash: matchedImageHash, ThumbnailHash: thumbnailHash,
                        OriginalBase64: Convert.ToBase64String(sourceBytes), IsCachedHit: true,
                        OriginalImagePath: sourcePath, EncodingModel: currentModel, InformationExtracted: defaultExtraction);
                }
                catch (Exception ex) { DebugLog($"[Vibe] Original unavailable: {ex.Message}"); }
            }
            // Without the verified original, extraction cannot be changed on this encoding.
            return new VibePickResult(file.Name, thumbnailMatchedEncoding, IsEncodedFile: true,
                ImageHash: matchedImageHash, ThumbnailHash: thumbnailHash, IsCachedHit: true,
                EncodingModel: currentModel, InformationExtracted: defaultExtraction);
        }

        return new VibePickResult(file.Name, originalBase64, IsEncodedFile: false,
            ImageHash: imageHash, ThumbnailHash: thumbnailHash, OriginalBase64: originalBase64,
            OriginalImagePath: originalPath, InformationExtracted: defaultExtraction);
    }

    private bool TryReportVibeTransferUnavailable(int maxVibeTransfers)
    {
        if (!SupportsVibeTransferFeature())
        {
            TxtStatus.Text = L("references.error.vibe_unsupported_model");
            return true;
        }

        if (ActivePreciseReferenceCount() > 0)
        {
            TxtStatus.Text = L("references.validation.mixed_reference_types");
            return true;
        }

        if (ActiveVibeTransferCount() >= maxVibeTransfers)
        {
            TxtStatus.Text = IsAssetProtectionPaidFeatureLimitEnabled()
                ? Lf("references.error.asset_protection_vibe_count_limit", AssetProtectionFreeVibeLimit)
                : Lf("references.error.vibe_count_limit", maxVibeTransfers);
            return true;
        }

        return false;
    }

    private bool TryReportPreciseReferenceUnavailable()
    {
        if (IsPreciseReferenceBlockedByAssetProtection())
        {
            TxtStatus.Text = L("superdrop.error.asset_protection_precise_reference");
            return true;
        }

        if (!SupportsPreciseReferenceFeature())
        {
            TxtStatus.Text = L("references.error.precise_requires_v45_model");
            return true;
        }

        if (SupportsVibeTransferFeature() && ActiveVibeTransferCount() > 0)
        {
            TxtStatus.Text = L("references.validation.mixed_reference_types");
            return true;
        }

        if (ActivePreciseReferenceCount() >= MaxPreciseReferences)
        {
            TxtStatus.Text = Lf("references.error.precise_count_limit", MaxPreciseReferences);
            return true;
        }

        return false;
    }

    private async Task AddDroppedVibeTransferAsync(StorageFile file)
    {
        int maxVibeTransfers = GetMaxAllowedVibeTransfers();
        if (TryReportVibeTransferUnavailable(maxVibeTransfers))
            return;

        var newEntry = CreateVibeTransferEntry(await CreateVibeTransferPickResultAsync(file));
        if (newEntry == null)
            return;

        if (ActiveVibeTransferCount() >= maxVibeTransfers)
        {
            if (IsAssetProtectionPaidFeatureLimitEnabled())
                TxtStatus.Text = Lf("references.error.asset_protection_vibe_count_limit", AssetProtectionFreeVibeLimit);
            return;
        }

        _genVibeTransfers.Add(newEntry);
        RecheckVibeTransferCacheState(refreshPanel: false);
        RefreshReferencePanels();
        UpdateGenerateButtonWarning();
        TxtStatus.Text = Lf("references.status.added_vibe", newEntry.FileName);
    }

    private async Task AddDroppedPreciseReferenceAsync(StorageFile file)
    {
        if (TryReportPreciseReferenceUnavailable())
            return;

        var newEntry = CreatePreciseReferenceEntry(await CreateReferenceImagePickResultAsync(file));
        if (newEntry == null)
            return;

        _genPreciseReferences.Add(newEntry);
        RefreshReferencePanels();
        UpdateGenerateButtonWarning();
        TxtStatus.Text = Lf("references.status.added_precise", newEntry.FileName);
    }

    private static async Task<byte[]?> ReadImageFileAsPngAsync(StorageFile file)
    {
        using var stream = await file.OpenReadAsync();
        using var source = stream.AsStreamForRead();
        using var memory = new MemoryStream();
        await source.CopyToAsync(memory);
        byte[] bytes = memory.ToArray();

        using var bitmap = SKBitmap.Decode(bytes);
        if (bitmap == null)
            return null;

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray();
    }

    /// <summary>
    /// 重新检测所有原始图片条目的缓存命中状态（基于当前模型 + 各条目 IE）。
}
