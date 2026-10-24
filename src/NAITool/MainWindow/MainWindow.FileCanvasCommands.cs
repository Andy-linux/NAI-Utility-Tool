using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using NAITool.Controls;
using NAITool.Models;
using NAITool.Services;
using SkiaSharp;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;
using System.Runtime.InteropServices.WindowsRuntime;

namespace NAITool;

public sealed partial class MainWindow
{
    private static bool IsPngFilePath(string filePath) =>
        string.Equals(Path.GetExtension(filePath), ".png", StringComparison.OrdinalIgnoreCase);

    private static string ResolveOverwriteSavePath(string filePath, out bool redirectedToPng)
    {
        redirectedToPng = !IsPngFilePath(filePath);
        return redirectedToPng ? Path.ChangeExtension(filePath, ".png") : filePath;
    }

    private static bool HasPngSignature(byte[] data) =>
        data.Length >= 8 &&
        data[0] == 137 &&
        data[1] == 80 &&
        data[2] == 78 &&
        data[3] == 71 &&
        data[4] == 13 &&
        data[5] == 10 &&
        data[6] == 26 &&
        data[7] == 10;

    private static byte[] EnsurePngEncoded(byte[] imageBytes)
    {
        if (HasPngSignature(imageBytes))
            return imageBytes;

        using var stream = new SKMemoryStream(imageBytes);
        using var codec = SKCodec.Create(stream);
        if (codec == null) return imageBytes;
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        if (codec.GetPixels(info, bitmap.GetPixels()) != SKCodecResult.Success) return imageBytes;
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        if (data == null) return imageBytes;
        // The website's WebP-to-PNG download copies only its six text fields,
        // keeping the signed Comment and alpha bits without normalizing metadata.
        var source = ImageMetadataService.ReadConversionTextChunks(imageBytes);
        var saved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in new[] { "Title", "Description", "Software", "Source", "Generation time", "Comment" })
            if (source.TryGetValue(key, out string? value)) saved[key] = value;
        return ImageMetadataService.ReplacePngTextChunks(data.ToArray(), saved);
    }

    private bool ShouldStripSavedImageMetadata(bool stripMetadata) =>
        stripMetadata || _settings.Settings.StripSavedImageMetadata;

    private Task<byte[]> PrepareImageBytesForSaveAsync(byte[] imageBytes, bool stripMetadata, bool forcePng = false) =>
        EncodeImageForSaveAsync(imageBytes, ShouldStripSavedImageMetadata(stripMetadata), forcePng);

    private async Task<byte[]> EncodeImageForSaveAsync(byte[] imageBytes, bool strip, bool forcePng)
    {
        if (!forcePng && !strip) return imageBytes;
        var pngBytes = await Task.Run(() => EnsurePngEncoded(imageBytes));
        if (!HasPngSignature(pngBytes))
            throw new InvalidDataException(L("file.error.png_conversion_failed"));
        return strip ? await Task.Run(() => ImageMetadataService.StripPngMetadata(pngBytes)) : pngBytes;
    }

    // ═══════════════════════════════════════════════════════════
    //  菜单事件
    // ═══════════════════════════════════════════════════════════

    private async void OnOpenImage(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".bmp");
        picker.FileTypeFilter.Add(".webp");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            if (_currentMode == AppMode.Inspect)
            {
                await LoadInspectImageAsync(file);
            }
            else if (_currentMode == AppMode.Effects)
            {
                await LoadEffectsImageAsync(file);
            }
            else if (_currentMode == AppMode.Upscale)
            {
                await LoadUpscaleImageAsync(file);
            }
            else if (_currentMode == AppMode.I2I)
            {
                await MaskCanvas.LoadImageAsync(file);
            }
            else if (_currentMode == AppMode.ImageGeneration)
            {
                try
                {
                    var bytes = await StorageFileReader.ReadAllBytesAsync(file);
                    var meta = await Task.Run(() => ImageMetadataService.ReadFromBytes(bytes));
                    if (meta != null && (meta.IsNaiParsed || meta.IsSdFormat || meta.IsModelInference))
                        await ImportMetadataAsync(meta);
                    else
                        TxtStatus.Text = Lf("metadata.no_usable_generation_metadata", file.Name);
                }
                catch (Exception ex) { TxtStatus.Text = Lf("common.read_failed", ex.Message); }
            }
        }
    }

    private async void OnSaveOverwrite(object sender, RoutedEventArgs e)
    {
        if (_currentMode == AppMode.Inspect)
        {
            await SaveInspectOverwriteAsync();
        }
        else if (_currentMode == AppMode.Effects)
        {
            await SaveEffectsOverwriteAsync();
        }
        else if (_currentMode == AppMode.I2I)
        {
            await SaveInpaintOverwriteAsync();
        }
        else
        {
            if (_currentGenImageBytes == null)
            { TxtStatus.Text = L("file.error.no_generated_result_to_save"); return; }
            if (!string.IsNullOrEmpty(_currentGenImagePath) && File.Exists(_currentGenImagePath))
            {
                TxtStatus.Text = Lf("file.saved_path", _currentGenImagePath);
            }
            else
            {
                TxtStatus.Text = _settings.Settings.PrivacyMode
                    ? L("generate.status.privacy_manual_save_required")
                    : L("generate.status.auto_saved_output");
            }
        }
    }

    private async Task SaveInpaintOverwriteAsync()
    {
        var filePath = MaskCanvas.LoadedFilePath;
        if (string.IsNullOrEmpty(filePath))
        { TxtStatus.Text = L("inpaint.error.no_image_to_save"); return; }

        WorkspaceSaveSnapshot? snapshot;
        try { snapshot = await CaptureWorkspaceSaveAsync(stripMetadata: false); }
        catch (Exception ex) { TxtStatus.Text = Lf("inpaint.error.compose_preview_failed", ex.Message); return; }

        if (snapshot == null)
        { TxtStatus.Text = L("file.error.no_image_content_to_save"); return; }

        var savePath = ResolveOverwriteSavePath(filePath, out bool redirectedToPng);

        if (!redirectedToPng)
        {
            string sizeWarning = "";
            try
            {
                var fileInfo = new FileInfo(filePath);
                if (fileInfo.Exists)
                {
                    using var origStream = File.OpenRead(filePath);
                    using var skBmp = SkiaSharp.SKBitmap.Decode(origStream);
                    if (skBmp != null)
                    {
                        using var newStream = new MemoryStream(snapshot.Bytes);
                        using var newBmp = SkiaSharp.SKBitmap.Decode(newStream);
                        if (newBmp != null && (skBmp.Width != newBmp.Width || skBmp.Height != newBmp.Height))
                            sizeWarning = Lf("file.confirm_save.size_warning", skBmp.Width, skBmp.Height, newBmp.Width, newBmp.Height);
                    }
                }
            }
            catch { }

            var dialog = new ContentDialog
            {
                Title = L("file.confirm_save.title"),
                Content = Lf("file.confirm_save.overwrite", filePath, sizeWarning),
                PrimaryButtonText = L("common.save"),
                CloseButtonText = L("common.cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content.XamlRoot,
                RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }

        try
        {
            await File.WriteAllBytesAsync(savePath, snapshot.Bytes);
            CompleteWorkspaceSave(snapshot, savePath, updateI2IPath: redirectedToPng);
            TxtStatus.Text = snapshot.StripMetadata
                ? Lf("file.saved_path_stripped", savePath)
                : Lf("file.saved_path", savePath);
        }
        catch (Exception ex) { TxtStatus.Text = Lf("common.save_failed", ex.Message); }
    }

    private async Task<byte[]?> CreateCurrentFullImageBytes()
    {
        var device = MaskCanvas.GetDevice();
        if (device == null) return null;
        var doc = MaskCanvas.Document;
        if (doc.OriginalImage == null) return null;

        var offset = doc.PixelAlignedImageOffset;
        int canvasW = MaskCanvas.CanvasW, canvasH = MaskCanvas.CanvasH;
        float origW = doc.OriginalImage.SizeInPixels.Width;
        float origH = doc.OriginalImage.SizeInPixels.Height;

        float canvasInOrigX = -offset.X, canvasInOrigY = -offset.Y;
        float minX = Math.Min(0, canvasInOrigX);
        float minY = Math.Min(0, canvasInOrigY);
        float maxX = Math.Max(origW, canvasInOrigX + canvasW);
        float maxY = Math.Max(origH, canvasInOrigY + canvasH);
        int compositeW = (int)Math.Ceiling(maxX - minX);
        int compositeH = (int)Math.Ceiling(maxY - minY);

        using var composite = new CanvasRenderTarget(device, compositeW, compositeH, 96f);
        using (var ds = composite.CreateDrawingSession())
        {
            ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            ds.DrawImage(doc.OriginalImage, -minX, -minY);
        }

        using var saveStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        await composite.SaveAsync(saveStream, CanvasBitmapFileFormat.Png);
        saveStream.Seek(0);
        var bytes = new byte[saveStream.Size];
        using var reader = new Windows.Storage.Streams.DataReader(saveStream);
        await reader.LoadAsync((uint)saveStream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    private async void OnSaveAs(object sender, RoutedEventArgs e)
    {
        await SaveAsInternal(stripMetadata: false);
    }

    private async void OnSaveAsStripped(object sender, RoutedEventArgs e)
    {
        await SaveAsInternal(stripMetadata: true);
    }

    private static string GetSuggestedSaveFileName(string? sourcePath)
    {
        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            var fileName = Path.GetFileNameWithoutExtension(sourcePath);
            if (!string.IsNullOrWhiteSpace(fileName))
                return fileName;
        }

        return $"nai_{DateTime.Now:yyyyMMdd_HHmmss}";
    }

    private async Task SaveImageBytesAsAsync(byte[]? imageBytes, bool stripMetadata, string? sourcePath = null)
    {
        if (imageBytes == null || imageBytes.Length == 0)
        {
            TxtStatus.Text = L("file.error.no_image_to_save");
            return;
        }

        bool strip = ShouldStripSavedImageMetadata(stripMetadata);
        var picker = new FileSavePicker();
        bool canKeepWebp = !strip && ImageOutputNaming.Extension(imageBytes) == ".webp";
        if (canKeepWebp && !_settings.Settings.SaveGeneratedImagesAsPng)
            picker.FileTypeChoices.Add("WebP", new List<string> { ".webp" });
        picker.FileTypeChoices.Add(L("file.png_image"), new List<string> { ".png" });
        if (canKeepWebp && _settings.Settings.SaveGeneratedImagesAsPng)
            picker.FileTypeChoices.Add("WebP", new List<string> { ".webp" });
        picker.SuggestedFileName = GetSuggestedSaveFileName(sourcePath);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var file = await picker.PickSaveFileAsync();
        if (file == null)
            return;

        try
        {
            var bytesToSave = await EncodeImageForSaveAsync(
                imageBytes, strip, forcePng: IsPngFilePath(file.Path));
            await Windows.Storage.FileIO.WriteBytesAsync(file, bytesToSave);
            TxtStatus.Text = strip
                ? Lf("file.saved_path_stripped", file.Path)
                : Lf("file.saved_path", file.Path);
        }
        catch (Exception ex)
        {
            TxtStatus.Text = Lf("common.save_failed", ex.Message);
        }
    }

    private sealed record WorkspaceSaveSnapshot(
        AppMode Mode, byte[] Bytes, bool StripMetadata, Func<bool> IsCurrent);

    private Func<bool> CaptureWorkspaceSaveOwnership(AppMode mode)
    {
        Func<bool> ownsImage;
        if (mode == AppMode.I2I)
        {
            int version = MaskCanvas.WorkspaceVersion;
            var original = MaskCanvas.Document.OriginalImage;
            var pending = _pendingResultBytes;
            bool preview = MaskCanvas.IsInPreviewMode;
            var offset = MaskCanvas.Document.ImageOffset;
            int width = MaskCanvas.CanvasW, height = MaskCanvas.CanvasH;
            var region = MaskCanvas.Document.InpaintRegion;
            double context = MaskCanvas.MinimumContextArea;
            var mask = MaskCanvas.Document.GetMaskSnapshot();
            ownsImage = () => version == MaskCanvas.WorkspaceVersion &&
                ReferenceEquals(original, MaskCanvas.Document.OriginalImage) &&
                ReferenceEquals(pending, _pendingResultBytes) && preview == MaskCanvas.IsInPreviewMode &&
                offset == MaskCanvas.Document.ImageOffset &&
                width == MaskCanvas.CanvasW && height == MaskCanvas.CanvasH &&
                region == MaskCanvas.Document.InpaintRegion && context == MaskCanvas.MinimumContextArea &&
                (mask ?? Array.Empty<byte>()).AsSpan().SequenceEqual(MaskCanvas.Document.GetMaskSnapshot());
        }
        else if (mode == AppMode.Effects)
        {
            var source = _effectsImageBytes;
            var effects = JsonSerializer.Serialize(_effects);
            ownsImage = () => ReferenceEquals(source, _effectsImageBytes) &&
                effects == JsonSerializer.Serialize(_effects);
        }
        else if (mode == AppMode.Upscale)
        {
            var source = _upscaleInputImageBytes;
            ownsImage = () => ReferenceEquals(source, _upscaleInputImageBytes);
        }
        else if (mode == AppMode.Inspect)
        {
            var source = _inspectImageBytes;
            var comment = _inspectMetadata?.RawJson;
            bool modified = _inspectRawModified;
            ownsImage = () => ReferenceEquals(source, _inspectImageBytes) &&
                comment == _inspectMetadata?.RawJson && modified == _inspectRawModified;
        }
        else
        {
            var source = _currentGenImageBytes;
            ownsImage = () => ReferenceEquals(source, _currentGenImageBytes);
        }
        return () => _currentMode == mode && ownsImage();
    }

    private async Task<WorkspaceSaveSnapshot?> CaptureWorkspaceSaveAsync(bool stripMetadata)
    {
        var mode = _currentMode;
        var isCurrent = CaptureWorkspaceSaveOwnership(mode);
        bool strip = ShouldStripSavedImageMetadata(stripMetadata);
        byte[]? bytes = mode switch
        {
            AppMode.Inspect => await GetInspectSaveBytesAsync(strip),
            AppMode.Effects => await GetEffectsSaveBytesAsync(),
            AppMode.I2I => await BuildI2ISaveBytesAsync(strip),
            AppMode.Upscale => _upscaleInputImageBytes,
            _ => _currentGenImageBytes,
        };
        if (bytes == null || bytes.Length == 0) return null;
        bytes = await EncodeImageForSaveAsync(bytes, strip, forcePng: true);
        return new WorkspaceSaveSnapshot(mode, bytes, strip, isCurrent);
    }

    private void CompleteWorkspaceSave(WorkspaceSaveSnapshot snapshot, string path, bool updateI2IPath = false)
    {
        if (!snapshot.IsCurrent()) return;
        if (snapshot.Mode == AppMode.I2I)
        {
            if (updateI2IPath) MaskCanvas.SetLoadedFilePath(path);
            MarkI2IWorkspaceClean();
        }
        else if (snapshot.Mode == AppMode.Upscale)
        {
            _upscaleImagePath = path;
            MarkUpscaleWorkspaceClean();
        }
        else if (snapshot.Mode == AppMode.Effects)
        {
            _effectsImagePath = path;
            MarkEffectsWorkspaceClean();
            RefreshEffectsPanel();
        }
    }

    private async Task SaveAsInternal(bool stripMetadata)
    {
        if (_currentMode == AppMode.ImageGeneration)
        {
            await SaveImageBytesAsAsync(_currentGenImageBytes, stripMetadata, _currentGenImagePath);
            return;
        }

        try
        {
            var snapshot = await CaptureWorkspaceSaveAsync(stripMetadata);
            if (snapshot == null)
            { TxtStatus.Text = L("file.error.no_image_to_save"); return; }

            var picker = new FileSavePicker();
            picker.FileTypeChoices.Add(L("file.png_image"), new List<string> { ".png" });
            picker.SuggestedFileName = $"nai_{DateTime.Now:yyyyMMdd_HHmmss}";
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file == null) return;

            await Windows.Storage.FileIO.WriteBytesAsync(file, snapshot.Bytes);
            CompleteWorkspaceSave(snapshot, file.Path);
            TxtStatus.Text = snapshot.StripMetadata
                ? Lf("file.saved_path_stripped", file.Path)
                : Lf("file.saved_path", file.Path);
        }
        catch (Exception ex) { TxtStatus.Text = Lf("common.save_failed", ex.Message); }
    }

    private async void OnExportCanvasMask(object sender, RoutedEventArgs e)
    {
        if (_currentMode != AppMode.I2I || MaskCanvas.Document?.OriginalImage == null)
        {
            TxtStatus.Text = L("i2i.export.error.no_image");
            return;
        }

        await ShowExportCanvasMaskDialogAsync();
    }

    private async Task<byte[]?> CreateI2IResultCanvasBytesAsync()
    {
        var bytes = _pendingResultBytes;
        var region = _pendingResultRegion;
        bool pngMetadata = _pendingResultPngMetadata;
        if (bytes == null || region == null) return bytes;
        var device = MaskCanvas.GetDevice();
        if (device == null) return null;
        using var source = MaskCanvas.Document.CreateCompositeForExport(device);
        if (source == null) return null;
        var sourceBase64 = await NovelAIService.EncodeRenderTargetAsync(source, isMask: false);
        return await Task.Run(() => InpaintImageProcessing.PlaceRawResult(
            pngMetadata ? EnsurePngEncoded(bytes) : bytes, sourceBase64, region.Value));
    }

    private async Task<byte[]?> CreatePreviewCompositeBytes()
    {
        var device = MaskCanvas.GetDevice();
        if (device == null || _pendingResultBytes == null) return null;
        var doc = MaskCanvas.Document;
        if (doc.OriginalImage == null) return await CreateI2IResultCanvasBytesAsync();

        var offset = doc.PixelAlignedImageOffset;
        int canvasW = MaskCanvas.CanvasW, canvasH = MaskCanvas.CanvasH;
        float origW = doc.OriginalImage.SizeInPixels.Width;
        float origH = doc.OriginalImage.SizeInPixels.Height;
        float canvasInOrigX = -offset.X, canvasInOrigY = -offset.Y;
        float minX = Math.Min(0, canvasInOrigX), minY = Math.Min(0, canvasInOrigY);
        float maxX = Math.Max(origW, canvasInOrigX + canvasW);
        float maxY = Math.Max(origH, canvasInOrigY + canvasH);
        int compositeW = (int)Math.Ceiling(maxX - minX);
        int compositeH = (int)Math.Ceiling(maxY - minY);
        if (compositeW == canvasW && compositeH == canvasH)
            return await CreateI2IResultCanvasBytesAsync();

        using var composite = new CanvasRenderTarget(device, compositeW, compositeH, 96f);
        using (var ds = composite.CreateDrawingSession())
        {
            ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            ds.DrawImage(doc.OriginalImage, -minX, -minY);
        }
        var canvasBytes = await CreateI2IResultCanvasBytesAsync();
        if (canvasBytes == null) return null;
        using var preview = await LoadI2IDisplayBitmapAsync(device, canvasBytes);
        using (var ds = composite.CreateDrawingSession())
            ds.DrawImage(preview, canvasInOrigX - minX, canvasInOrigY - minY);

        using var saveStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        await composite.SaveAsync(saveStream, CanvasBitmapFileFormat.Png);
        saveStream.Seek(0);
        var bytes = new byte[saveStream.Size];
        using var reader = new Windows.Storage.Streams.DataReader(saveStream);
        await reader.LoadAsync((uint)saveStream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    private async Task<byte[]?> BuildI2ISaveBytesAsync(bool stripMetadata)
    {
        byte[]? bytes;
        byte[]? metadataSource;
        IReadOnlyDictionary<string, string>? textChunks;
        int canvasW = MaskCanvas.CanvasW, canvasH = MaskCanvas.CanvasH;
        if (MaskCanvas.IsInPreviewMode && _pendingResultBitmap != null)
        {
            metadataSource = _pendingResultBytes;
            textChunks = _pendingResultTextChunks;
            bytes = await CreatePreviewCompositeBytes();
            if (!stripMetadata && bytes != null && ReferenceEquals(bytes, metadataSource))
                return bytes;
        }
        else
        {
            if (!stripMetadata && CanSaveCurrentI2IImageFromRawBytes())
                return _lastGeneratedImageBytes;
            metadataSource = _lastGeneratedImageBytes;
            textChunks = _i2iImageTextChunks;
            bytes = await CreateCurrentFullImageBytes();
        }
        if (bytes == null || bytes.Length == 0) return bytes;
        if (stripMetadata)
            return await Task.Run(() => ImageMetadataService.StripPngMetadata(bytes));
        return await Task.Run(() => metadataSource != null
            ? ImageMetadataService.ReapplyNovelAiMetadata(bytes, EnsurePngEncoded(metadataSource), canvasW, canvasH)
            : ImageMetadataService.ReapplyNovelAiMetadata(bytes, textChunks));
    }

    private bool CanSaveCurrentI2IImageFromRawBytes()
    {
        if (_lastGeneratedImageBytes == null || _lastGeneratedImageBytes.Length == 0)
            return false;

        var original = MaskCanvas.Document.OriginalImage;
        if (original == null)
            return false;

        var offset = MaskCanvas.Document.PixelAlignedImageOffset;
        return offset.X == 0 &&
               offset.Y == 0 &&
               (int)original.SizeInPixels.Width == MaskCanvas.CanvasW &&
               (int)original.SizeInPixels.Height == MaskCanvas.CanvasH;
    }

    private void OnOpenImageFolder(object sender, RoutedEventArgs e)
    {
        if (_currentMode == AppMode.ImageGeneration)
        {
            if (!string.IsNullOrEmpty(_currentGenImagePath) && File.Exists(_currentGenImagePath))
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_currentGenImagePath}\"");
                return;
            }
            var outputDir = OutputBaseDir;
            if (Directory.Exists(outputDir))
            {
                System.Diagnostics.Process.Start("explorer.exe", outputDir);
                return;
            }
            TxtStatus.Text = L("file.error.no_output_folder");
            return;
        }

        if (_currentMode == AppMode.Effects)
        {
            if (string.IsNullOrEmpty(_effectsImagePath) || !File.Exists(_effectsImagePath))
            { TxtStatus.Text = L("file.error.no_image_for_folder"); return; }
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_effectsImagePath}\"");
            return;
        }

        if (_currentMode == AppMode.Upscale)
        {
            if (string.IsNullOrEmpty(_upscaleImagePath) || !File.Exists(_upscaleImagePath))
            { TxtStatus.Text = L("file.error.no_image_for_folder"); return; }
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_upscaleImagePath}\"");
            return;
        }

        var path = MaskCanvas.LoadedFilePath;
        if (string.IsNullOrEmpty(path))
        { TxtStatus.Text = L("file.error.no_image_for_folder"); return; }
        var dir = Path.GetDirectoryName(path);
        if (dir != null && Directory.Exists(dir))
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        else
            TxtStatus.Text = L("file.error.folder_not_found");
    }

    private void OnExit(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void OnReloadImage(object sender, RoutedEventArgs e)
    {
        await ReloadCurrentWorkspaceImageAsync();
    }

    private async Task ReloadCurrentWorkspaceImageAsync()
    {
        if (_currentMode == AppMode.I2I)
        {
            var path = MaskCanvas.LoadedFilePath;
            if (MaskCanvas.IsInPreviewMode)
            {
                TxtStatus.Text = L("image.reload.blocked_preview");
                return;
            }
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                TxtStatus.Text = L("image.reload.no_source");
                return;
            }

            bool wasDirty = MaskCanvas.HasWorkspaceChangesSinceClean();
            bool previewDirty = _i2iPreviewDirty;
            var reloadedBytes = await MaskCanvas.ReloadImagePreservingWorkspaceAsync(path);
            if (reloadedBytes == null) return;
            _lastGeneratedImageBytes = reloadedBytes;
            _i2iImageTextChunks = ImageMetadataService.ReadRoundTripTextChunks(reloadedBytes);
            if (!wasDirty && !previewDirty)
                MarkI2IWorkspaceClean();
            _i2iPreviewDirty = previewDirty;
            UpdateDynamicMenuStates();
            return;
        }

        if (_currentMode == AppMode.Upscale)
        {
            if (_upscaleRunning)
                return;
            if (string.IsNullOrWhiteSpace(_upscaleImagePath) || !File.Exists(_upscaleImagePath))
            {
                TxtStatus.Text = L("image.reload.no_source");
                return;
            }

            await LoadUpscaleImageAsync(_upscaleImagePath, preserveDirtyState: true);
            UpdateDynamicMenuStates();
            return;
        }

        if (_currentMode == AppMode.Effects)
        {
            if (string.IsNullOrWhiteSpace(_effectsImagePath) || !File.Exists(_effectsImagePath))
            {
                TxtStatus.Text = L("image.reload.no_source");
                return;
            }

            await ReloadEffectsImageAsync(_effectsImagePath);
            UpdateDynamicMenuStates();
        }
    }

    private async void OnUndo(object sender, RoutedEventArgs e)
    {
        if (_currentMode == AppMode.I2I && !MaskCanvas.IsInPreviewMode)
        {
            if (_generateRequestRunning || _i2iResultApplying) return;
            if (_i2iEditMode == I2IEditMode.Inpaint && MaskCanvas.UndoMgr.CanUndo)
            {
                MaskCanvas.PerformUndo();
                return;
            }

            if (_i2iApplyUndoStack.Count > 0)
            {
                await RestoreI2IHistoryAsync(redo: false);
                return;
            }
        }

        if (_currentMode == AppMode.Effects && _effectsUndoStack.Count > 0)
        {
            _effectsRedoStack.Push(CaptureEffectsWorkspaceState());
            var state = _effectsUndoStack.Pop();
            await RestoreEffectsWorkspaceStateAsync(state);
            TxtStatus.Text = L("post.status.undo");
        }
    }

    private async void OnRedo(object sender, RoutedEventArgs e)
    {
        if (_currentMode == AppMode.I2I && !MaskCanvas.IsInPreviewMode)
        {
            if (_generateRequestRunning || _i2iResultApplying) return;
            if (_i2iEditMode == I2IEditMode.Inpaint && MaskCanvas.UndoMgr.CanRedo)
            {
                MaskCanvas.PerformRedo();
                return;
            }

            if (_i2iApplyRedoStack.Count > 0)
            {
                await RestoreI2IHistoryAsync(redo: true);
                return;
            }
        }

        if (_currentMode == AppMode.Effects && _effectsRedoStack.Count > 0)
        {
            _effectsUndoStack.Push(CaptureEffectsWorkspaceState());
            var state = _effectsRedoStack.Pop();
            await RestoreEffectsWorkspaceStateAsync(state);
            TxtStatus.Text = L("post.status.redo");
        }
    }

    private async Task RestoreI2IHistoryAsync(bool redo)
    {
        var source = redo ? _i2iApplyRedoStack : _i2iApplyUndoStack;
        var target = redo ? _i2iApplyUndoStack : _i2iApplyRedoStack;
        var state = source.Peek();
        int workspaceVersion = MaskCanvas.WorkspaceVersion;
        _i2iResultApplying = true;
        UpdateI2IResultNavigator();
        try
        {
            var current = await CaptureI2IApplyWorkspaceStateAsync();
            if (workspaceVersion != MaskCanvas.WorkspaceVersion ||
                source.Count == 0 || !ReferenceEquals(source.Peek(), state)) return;
            if (!await RestoreI2IApplyWorkspaceStateAsync(state)) return;
            source.Pop();
            target.Push(current);
            TxtStatus.Text = L(redo ? "post.status.redo" : "post.status.undo");
        }
        finally
        {
            _i2iResultApplying = false;
            UpdateI2IResultNavigator();
            UpdateDynamicMenuStates();
        }
    }

    private void OnClearMask(object sender, RoutedEventArgs e)
    { if (_currentMode == AppMode.I2I && _i2iEditMode == I2IEditMode.Inpaint && !MaskCanvas.IsInPreviewMode) MaskCanvas.ClearMask(); }

    private void OnFillEmpty(object sender, RoutedEventArgs e)
    {
        if (_currentMode != AppMode.I2I || _i2iEditMode != I2IEditMode.Inpaint || MaskCanvas.IsInPreviewMode) return;
        MaskCanvas.FillEmptyAreas();
        TxtStatus.Text = L("inpaint.mask.fill_empty_done");
    }

    private void OnInvertMask(object sender, RoutedEventArgs e)
    {
        if (_currentMode != AppMode.I2I || _i2iEditMode != I2IEditMode.Inpaint || MaskCanvas.IsInPreviewMode) return;
        MaskCanvas.InvertMask();
        TxtStatus.Text = L("inpaint.mask.inverted");
    }

    private void OnExpandMask(object sender, RoutedEventArgs e)
    {
        if (_currentMode != AppMode.I2I || _i2iEditMode != I2IEditMode.Inpaint || MaskCanvas.IsInPreviewMode) return;
        MaskCanvas.ExpandMask();
        TxtStatus.Text = L("inpaint.mask.expanded");
    }

    private void OnShrinkMask(object sender, RoutedEventArgs e)
    {
        if (_currentMode != AppMode.I2I || _i2iEditMode != I2IEditMode.Inpaint || MaskCanvas.IsInPreviewMode) return;
        MaskCanvas.ShrinkMask();
        TxtStatus.Text = L("inpaint.mask.shrunk");
    }

    private void OnTrimCanvas(object sender, RoutedEventArgs e)
    {
        if (_currentMode != AppMode.I2I) return;
        if (MaskCanvas.IsInPreviewMode) { TxtStatus.Text = L("inpaint.canvas.trim_blocked_preview"); return; }
        if (MaskCanvas.TrimCanvas())
            TxtStatus.Text = Lf("inpaint.canvas.trimmed", MaskCanvas.CanvasW, MaskCanvas.CanvasH);
        else
            TxtStatus.Text = L("inpaint.canvas.trim_empty");
    }

    private void OnFitToScreen(object sender, RoutedEventArgs e)
    {
        if (_currentMode == AppMode.I2I) MaskCanvas.FitToScreen();
        else if (_currentMode == AppMode.Inspect) FitInspectPreviewToScreen();
        else if (_currentMode == AppMode.Upscale) FitUpscalePreviewToScreen();
        else if (_currentMode == AppMode.Effects) FitEffectsPreviewToScreen();
        else FitGenPreviewToScreen();
    }
    private void OnActualSize(object sender, RoutedEventArgs e)
    {
        float dpiScale = (float)(this.Content.XamlRoot?.RasterizationScale ?? 1.0);
        float trueZoom = 1.0f / dpiScale;
        if (_currentMode == AppMode.I2I) MaskCanvas.ActualSize();
        else if (_currentMode == AppMode.Inspect) InspectImageScroller.ChangeView(null, null, trueZoom);
        else if (_currentMode == AppMode.Upscale) UpscaleImageScroller.ChangeView(null, null, trueZoom);
        else if (_currentMode == AppMode.Effects) EffectsImageScroller.ChangeView(null, null, trueZoom);
        else GenImageScroller.ChangeView(null, null, trueZoom);
    }
    private void OnCenterView(object sender, RoutedEventArgs e)
    {
        if (_currentMode == AppMode.I2I) MaskCanvas.CenterView();
        else if (_currentMode == AppMode.Inspect) FitInspectPreviewToScreen();
        else if (_currentMode == AppMode.Upscale) FitUpscalePreviewToScreen();
        else if (_currentMode == AppMode.Effects) CenterEffectsPreview();
        else FitGenPreviewToScreen();
    }

    private Microsoft.UI.Windowing.OverlappedPresenterState _lastPresenterState;
    private void OnMaskCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_currentMode != AppMode.I2I) return;
        if (AppWindow?.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p)
        {
            var state = p.State;
            if (state != _lastPresenterState)
            {
                _lastPresenterState = state;
                MaskCanvas.CenterView();
            }
        }
        QueueThumbnailRender();
    }

    private void OnZoomIn(object sender, RoutedEventArgs e)
    {
        if (_currentMode == AppMode.I2I) MaskCanvas.ZoomIn();
        else if (_currentMode == AppMode.Inspect)
            InspectImageScroller.ChangeView(null, null, InspectImageScroller.ZoomFactor * 1.25f);
        else if (_currentMode == AppMode.Upscale)
            UpscaleImageScroller.ChangeView(null, null, UpscaleImageScroller.ZoomFactor * 1.25f);
        else if (_currentMode == AppMode.Effects)
            EffectsImageScroller.ChangeView(null, null, EffectsImageScroller.ZoomFactor * 1.25f);
        else GenImageScroller.ChangeView(null, null, GenImageScroller.ZoomFactor * 1.25f);
    }
    private void OnZoomOut(object sender, RoutedEventArgs e)
    {
        if (_currentMode == AppMode.I2I) MaskCanvas.ZoomOut();
        else if (_currentMode == AppMode.Inspect)
            InspectImageScroller.ChangeView(null, null, InspectImageScroller.ZoomFactor / 1.25f);
        else if (_currentMode == AppMode.Upscale)
            UpscaleImageScroller.ChangeView(null, null, UpscaleImageScroller.ZoomFactor / 1.25f);
        else if (_currentMode == AppMode.Effects)
            EffectsImageScroller.ChangeView(null, null, EffectsImageScroller.ZoomFactor / 1.25f);
        else GenImageScroller.ChangeView(null, null, GenImageScroller.ZoomFactor / 1.25f);
    }

    private void SetupPreviewScrollZoomAndDrag()
    {
        ToolTipService.SetToolTip(GenPreviewImage, L("image.drag.hint"));
        ToolTipService.SetToolTip(InspectPreviewImage, L("image.drag.hint"));
        GenImageScroller.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnPreviewWheelZoom), true);
        GenPreviewImage.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnPreviewWheelZoom), true);
        InspectImageScroller.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnPreviewWheelZoom), true);
        InspectPreviewImage.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnPreviewWheelZoom), true);
        EffectsImageScroller.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnPreviewWheelZoom), true);
        EffectsPreviewContent.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnPreviewWheelZoom), true);
        EffectsOverlayCanvas.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnPreviewWheelZoom), true);
        UpscaleImageScroller.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnPreviewWheelZoom), true);
        UpscalePreviewImage.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnPreviewWheelZoom), true);

        foreach (var image in new[] { GenPreviewImage, InspectPreviewImage, EffectsPreviewImage, UpscalePreviewImage })
        {
            image.PointerPressed += OnPreviewDragStart;
            image.PointerMoved += OnPreviewDragMove;
            image.PointerReleased += OnPreviewDragEnd;
            image.PointerCanceled += OnPreviewDragCanceled;
            image.PointerCaptureLost += OnPreviewDragCanceled;
        }
        EffectsOverlayCanvas.PointerCaptureLost += OnPreviewDragCanceled;
        GenPreviewImage.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(OnGenPreviewPointerPressed), true);
    }

    private void OnPreviewWheelZoom(object sender, PointerRoutedEventArgs e)
    {
        if (e.Handled) return;
        var sv = GetPreviewScroller(sender);
        if (sv == null) return;
        var point = e.GetCurrentPoint(sv);
        int delta = point.Properties.MouseWheelDelta;
        if (delta == 0) return;
        StopPreviewDrag();

        float factor = delta > 0 ? 1.15f : (1f / 1.15f);
        float newZoom = Math.Clamp(sv.ZoomFactor * factor, sv.MinZoomFactor, sv.MaxZoomFactor);

        double mouseX = point.Position.X;
        double mouseY = point.Position.Y;
        double contentX = (sv.HorizontalOffset + mouseX) / sv.ZoomFactor;
        double contentY = (sv.VerticalOffset + mouseY) / sv.ZoomFactor;
        double newOffsetX = contentX * newZoom - mouseX;
        double newOffsetY = contentY * newZoom - mouseY;

        sv.ChangeView(Math.Max(0, newOffsetX), Math.Max(0, newOffsetY), newZoom, false);
        e.Handled = true;
    }

    private ScrollViewer? GetPreviewScroller(object? sender)
    {
        if (sender is ScrollViewer sv) return sv;
        if (ReferenceEquals(sender, GenPreviewImage)) return GenImageScroller;
        if (ReferenceEquals(sender, InspectPreviewImage)) return InspectImageScroller;
        if (ReferenceEquals(sender, EffectsPreviewImage) || ReferenceEquals(sender, EffectsPreviewContent) || ReferenceEquals(sender, EffectsOverlayCanvas))
            return EffectsImageScroller;
        if (ReferenceEquals(sender, UpscalePreviewImage)) return UpscaleImageScroller;
        return null;
    }

    private void OnPreviewDragStart(object sender, PointerRoutedEventArgs e)
    {
        if (_previewPan.IsActive || sender is not UIElement el) return;
        var props = e.GetCurrentPoint(el).Properties;
        bool useMiddle = props.IsMiddleButtonPressed &&
            (ReferenceEquals(el, GenPreviewImage) || ReferenceEquals(el, InspectPreviewImage));
        if (!useMiddle && !props.IsLeftButtonPressed) return;
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Control);
        if (!useMiddle && ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        // File previews use left-drag to share; middle or Space+left still pans.
        if (!useMiddle && el.CanDrag && !IsImageDragModifierDown(Windows.System.VirtualKey.Space)) return;
        var sv = GetPreviewScroller(el);
        if (sv == null || !el.CapturePointer(e.Pointer)) return;
        var start = e.GetCurrentPoint(sv).Position;
        _imgDragScroller = sv;
        _imgDragElement = el;
        _imgDragPointer = e.Pointer;
        _imgDragUsesMiddleButton = useMiddle;
        _previewPan.Begin(start.X, start.Y, sv.HorizontalOffset, sv.VerticalOffset);
        e.Handled = true;
    }

    private void OnPreviewDragMove(object sender, PointerRoutedEventArgs e)
    {
        if (!_previewPan.IsActive || _imgDragScroller == null || _imgDragPointer?.PointerId != e.Pointer.PointerId) return;
        var point = e.GetCurrentPoint(_imgDragScroller);
        _previewPan.Move(point.Position.X, point.Position.Y);
        if (!(_imgDragUsesMiddleButton ? point.Properties.IsMiddleButtonPressed : point.Properties.IsLeftButtonPressed))
        {
            StopPreviewDrag();
            e.Handled = true;
            return;
        }
        if (!_previewPanFrameQueued)
        {
            _previewPanFrameQueued = true;
            CompositionTarget.Rendering += OnPreviewPanRendering;
        }
        e.Handled = true;
    }

    private void OnPreviewDragEnd(object sender, PointerRoutedEventArgs e)
    {
        if (!_previewPan.IsActive || _imgDragScroller == null || _imgDragPointer?.PointerId != e.Pointer.PointerId) return;
        var point = e.GetCurrentPoint(_imgDragScroller);
        // Releasing another button must not interrupt the button that started the drag.
        if (_imgDragUsesMiddleButton ? point.Properties.IsMiddleButtonPressed : point.Properties.IsLeftButtonPressed) return;
        _previewPan.Move(point.Position.X, point.Position.Y);
        StopPreviewDrag();
        e.Handled = true;
    }

    private void OnPreviewDragCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_imgDragPointer?.PointerId == e.Pointer.PointerId)
            StopPreviewDrag();
    }

    private void OnPreviewPanRendering(object? sender, object e)
    {
        RemovePreviewPanRendering();
        ApplyPendingPreviewPan();
    }

    private void ApplyPendingPreviewPan()
    {
        if (_imgDragScroller is { } scroller &&
            _previewPan.TryTakeOffsets(scroller.ScrollableWidth, scroller.ScrollableHeight, out double horizontal, out double vertical))
            scroller.ChangeView(horizontal, vertical, null, disableAnimation: true);
    }

    private void RemovePreviewPanRendering()
    {
        if (!_previewPanFrameQueued) return;
        CompositionTarget.Rendering -= OnPreviewPanRendering;
        _previewPanFrameQueued = false;
    }

    private void StopPreviewDrag()
    {
        RemovePreviewPanRendering();
        ApplyPendingPreviewPan();
        _previewPan.End();
        var element = _imgDragElement;
        var pointer = _imgDragPointer;
        _imgDragScroller = null;
        _imgDragElement = null;
        _imgDragPointer = null;
        if (pointer != null) element?.ReleasePointerCapture(pointer);
    }

    private async void OnGenPreviewPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(sender as UIElement);
        if (_previewPan.IsActive) return;
        if (!pt.Properties.IsLeftButtonPressed) return;
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Control);
        if (!ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        if (_currentGenImageBytes == null) return;

        await ApplyDroppedImageMetadata(_currentGenImageBytes, L("image.preview_label"), skipSeed: true);
        e.Handled = true;
    }

    private void OnAlign(object sender, RoutedEventArgs e)
    {
        if (_currentMode != AppMode.I2I || MaskCanvas.IsInPreviewMode) return;
        if (sender is MenuFlyoutItem item && item.Tag is string tag)
        {
            MaskCanvas.AlignImage(tag);
            TxtStatus.Text = L("image.aligned");
        }
    }

    private async Task ShowExportCanvasMaskDialogAsync()
    {
        var device = MaskCanvas.GetDevice();
        if (device == null || MaskCanvas.Document?.OriginalImage == null) return;

        var cbArea = new ComboBox
        {
            ItemsSource = new[] { L("i2i.export.area.canvas"), L("i2i.export.area.whole") },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 16)
        };

        var cbType = new ComboBox
        {
            ItemsSource = new[]
            {
                L("i2i.export.type.image"),
                L("i2i.export.type.mask"),
                L("i2i.export.type.merged"),
                L("i2i.export.type.separated")
            },
            SelectedIndex = 2, // Default to merged
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 16)
        };

        var cbColor = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 16)
        };

        void UpdateColorOptions()
        {
            if (cbType.SelectedIndex == 0) // Image Only
            {
                cbColor.ItemsSource = new[] { L("i2i.export.color.white_on_black") };
                cbColor.SelectedIndex = 0;
                cbColor.IsEnabled = false;
            }
            else if (cbType.SelectedIndex == 2) // Merged
            {
                cbColor.ItemsSource = new[]
                {
                    L("i2i.export.color.red"),
                    L("i2i.export.color.green"),
                    L("i2i.export.color.blue"),
                    L("i2i.export.color.yellow"),
                    L("i2i.export.color.cyan"),
                    L("i2i.export.color.magenta"),
                    L("i2i.export.color.black"),
                    L("i2i.export.color.white")
                };
                cbColor.SelectedIndex = 1; // Default to Green
                cbColor.IsEnabled = true;
            }
            else // Mask Only or Separated
            {
                cbColor.ItemsSource = new[]
                {
                    L("i2i.export.color.white_on_black"),
                    L("i2i.export.color.black_on_white")
                };
                cbColor.SelectedIndex = 0;
                cbColor.IsEnabled = true;
            }
        }

        cbType.SelectionChanged += (_, _) => UpdateColorOptions();
        UpdateColorOptions();

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = L("i2i.export.area") });
        panel.Children.Add(cbArea);
        panel.Children.Add(new TextBlock { Text = L("i2i.export.type") });
        panel.Children.Add(cbType);
        panel.Children.Add(new TextBlock { Text = L("i2i.export.color") });
        panel.Children.Add(cbColor);

        var dialog = new ContentDialog
        {
            Title = L("i2i.export.title"),
            Content = panel,
            PrimaryButtonText = L("i2i.export.btn_export"),
            CloseButtonText = L("i2i.export.btn_cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot
        };
        dialog.Resources["ContentDialogMaxWidth"] = 400.0;

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        // Export Logic
        bool isCanvasArea = cbArea.SelectedIndex == 0;
        int exportType = cbType.SelectedIndex; // 0: Image, 1: Mask, 2: Merged, 3: Separated
        int colorMode = cbColor.SelectedIndex;

        var picker = new FileSavePicker();
        picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
        picker.FileTypeChoices.Add("PNG Image", new[] { ".png" });
        picker.SuggestedFileName = "export";
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        try
        {
            await ExportCanvasMaskInternalAsync(file, isCanvasArea, exportType, colorMode);
            TxtStatus.Text = Lf("file.saved_path", file.Path);
        }
        catch (Exception ex)
        {
            TxtStatus.Text = Lf("common.save_failed", ex.Message);
        }
    }

    private async Task ExportCanvasMaskInternalAsync(StorageFile file, bool isCanvasArea, int exportType, int colorMode)
    {
        var device = MaskCanvas.GetDevice();
        var doc = MaskCanvas.Document;
        if (device == null || doc == null || doc.OriginalImage == null) return;

        int canvasW = MaskCanvas.CanvasW;
        int canvasH = MaskCanvas.CanvasH;
        int origW = (int)doc.OriginalImage.SizeInPixels.Width;
        int origH = (int)doc.OriginalImage.SizeInPixels.Height;

        int outW = isCanvasArea ? canvasW : origW;
        int outH = isCanvasArea ? canvasH : origH;

        Vector2 imageOffset = doc.PixelAlignedImageOffset;

        void DrawImage(CanvasDrawingSession ds)
        {
            if (isCanvasArea)
            {
                ds.DrawImage(doc.OriginalImage, imageOffset);
            }
            else
            {
                ds.DrawImage(doc.OriginalImage, Vector2.Zero);
            }
        }

        void DrawMask(CanvasDrawingSession ds, Windows.UI.Color color)
        {
            if (doc.MaskTarget == null) return;

            using var effect = new Microsoft.Graphics.Canvas.Effects.ColorMatrixEffect
            {
                Source = doc.MaskTarget,
                ColorMatrix = new Microsoft.Graphics.Canvas.Effects.Matrix5x4
                {
                    M11 = 0, M12 = 0, M13 = 0, M14 = 0,
                    M21 = 0, M22 = 0, M23 = 0, M24 = 0,
                    M31 = 0, M32 = 0, M33 = 0, M34 = 0,
                    M41 = color.R / 255f, M42 = color.G / 255f, M43 = color.B / 255f, M44 = color.A / 255f,
                    M51 = 0, M52 = 0, M53 = 0, M54 = 0
                }
            };

            if (isCanvasArea)
            {
                ds.DrawImage(effect, Vector2.Zero);
            }
            else
            {
                ds.DrawImage(effect, -imageOffset);
            }
        }

        async Task<byte[]> GetTargetBytesAsync(Action<CanvasDrawingSession> draw)
        {
            using var target = new CanvasRenderTarget(device, outW, outH, 96f);
            using (var ds = target.CreateDrawingSession())
            {
                draw(ds);
            }
            using var saveStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await target.SaveAsync(saveStream, CanvasBitmapFileFormat.Png);
            saveStream.Seek(0);
            var bytes = new byte[saveStream.Size];
            using var reader = new Windows.Storage.Streams.DataReader(saveStream);
            await reader.LoadAsync((uint)saveStream.Size);
            reader.ReadBytes(bytes);
            return bytes;
        }

        if (exportType == 0) // Image Only
        {
            var bytes = await GetTargetBytesAsync(ds =>
            {
                ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                DrawImage(ds);
            });
            bytes = await PrepareImageBytesForSaveAsync(bytes, stripMetadata: false);
            await Windows.Storage.FileIO.WriteBytesAsync(file, bytes);
        }
        else if (exportType == 1) // Mask Only
        {
            var fg = colorMode == 0 ? Windows.UI.Color.FromArgb(255, 255, 255, 255) : Windows.UI.Color.FromArgb(255, 0, 0, 0);
            var bg = colorMode == 0 ? Windows.UI.Color.FromArgb(255, 0, 0, 0) : Windows.UI.Color.FromArgb(255, 255, 255, 255);
            var bytes = await GetTargetBytesAsync(ds =>
            {
                ds.Clear(bg);
                DrawMask(ds, fg);
            });
            bytes = await PrepareImageBytesForSaveAsync(bytes, stripMetadata: false);
            await Windows.Storage.FileIO.WriteBytesAsync(file, bytes);
        }
        else if (exportType == 2) // Merged
        {
            var fg = colorMode switch
            {
                0 => Windows.UI.Color.FromArgb(255, 255, 0, 0),   // Red
                1 => Windows.UI.Color.FromArgb(255, 0, 255, 0),   // Green
                2 => Windows.UI.Color.FromArgb(255, 0, 0, 255),   // Blue
                3 => Windows.UI.Color.FromArgb(255, 255, 255, 0), // Yellow
                4 => Windows.UI.Color.FromArgb(255, 0, 255, 255), // Cyan
                5 => Windows.UI.Color.FromArgb(255, 255, 0, 255), // Magenta
                6 => Windows.UI.Color.FromArgb(255, 0, 0, 0),     // Black
                _ => Windows.UI.Color.FromArgb(255, 255, 255, 255)// White
            };

            var bytes = await GetTargetBytesAsync(ds =>
            {
                ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                DrawImage(ds);
                DrawMask(ds, fg);
            });
            bytes = await PrepareImageBytesForSaveAsync(bytes, stripMetadata: false);
            await Windows.Storage.FileIO.WriteBytesAsync(file, bytes);
        }
        else if (exportType == 3) // Separated
        {
            // Save image
            var imgBytes = await GetTargetBytesAsync(ds =>
            {
                ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                DrawImage(ds);
            });
            imgBytes = await PrepareImageBytesForSaveAsync(imgBytes, stripMetadata: false);
            await Windows.Storage.FileIO.WriteBytesAsync(file, imgBytes);

            // Save mask
            var fg = colorMode == 0 ? Windows.UI.Color.FromArgb(255, 255, 255, 255) : Windows.UI.Color.FromArgb(255, 0, 0, 0);
            var bg = colorMode == 0 ? Windows.UI.Color.FromArgb(255, 0, 0, 0) : Windows.UI.Color.FromArgb(255, 255, 255, 255);
            var maskBytes = await GetTargetBytesAsync(ds =>
            {
                ds.Clear(bg);
                DrawMask(ds, fg);
            });
            maskBytes = await PrepareImageBytesForSaveAsync(maskBytes, stripMetadata: false);
            
            string dir = Path.GetDirectoryName(file.Path) ?? "";
            string name = Path.GetFileNameWithoutExtension(file.Path);
            string ext = Path.GetExtension(file.Path);
            string maskPath = Path.Combine(dir, $"{name}_Mask{ext}");
            
            await File.WriteAllBytesAsync(maskPath, maskBytes);
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  外观主题
    // ═══════════════════════════════════════════════════════════
}
