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
    private void SetI2IRequestImageMoveLocked(bool locked)
    {
        if (MaskCanvas != null)
            MaskCanvas.IsImageMoveLocked = locked;
    }

    private static Dictionary<string, string>? CloneTextChunks(Dictionary<string, string>? textChunks) =>
        textChunks == null ? null : new Dictionary<string, string>(textChunks, StringComparer.Ordinal);

    private void ResetI2IResultSession()
    {
        ClearI2IResultCandidates();
        _i2iResultSessionId++;
        _i2iResultSequence = 0;
    }

    private void ClearI2IResultCandidates()
    {
        _i2iResultSelectionVersion++;
        foreach (var candidate in _i2iResultCandidates)
        {
            try
            {
                if (File.Exists(candidate.FilePath))
                    File.Delete(candidate.FilePath);
            }
            catch
            {
            }
        }

        _i2iResultCandidates.Clear();
        _i2iResultIndex = -1;
        UpdateI2IResultNavigator();
    }

    private void SetTransientI2IPreview(CanvasBitmap bitmap, InpaintRegion? region = null)
    {
        var oldPending = _pendingResultBitmap;
        var oldPreview = MaskCanvas.SetPreview(bitmap, region);
        if (oldPreview != null && !ReferenceEquals(oldPreview, bitmap))
        {
            oldPreview.Dispose();
            if (ReferenceEquals(oldPreview, oldPending))
                _pendingResultBitmap = null;
        }
    }

    private void SetPendingI2IResult(CanvasBitmap bitmap, byte[] imageBytes, Dictionary<string, string>? textChunks, InpaintRegion? region = null, bool pngMetadata = false)
    {
        var oldPending = _pendingResultBitmap;
        _pendingResultBitmap = bitmap;
        _pendingResultBytes = imageBytes;
        _pendingResultRegion = region;
        _pendingResultPngMetadata = pngMetadata;
        _pendingResultTextChunks = CloneTextChunks(textChunks);

        var oldPreview = MaskCanvas.SetPreview(bitmap, region);
        bool disposedOldPending = false;
        if (oldPreview != null && !ReferenceEquals(oldPreview, bitmap))
        {
            oldPreview.Dispose();
            disposedOldPending = ReferenceEquals(oldPreview, oldPending);
        }

        if (oldPending != null && !ReferenceEquals(oldPending, bitmap) && !disposedOldPending)
            oldPending.Dispose();

        _i2iPreviewDirty = true;
        UpdateI2IResultNavigator();
    }

    private static async Task<CanvasBitmap> LoadI2IDisplayBitmapAsync(CanvasDevice device, byte[] imageBytes)
    {
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        using var writer = new Windows.Storage.Streams.DataWriter(stream);
        writer.WriteBytes(GetWinUiDisplayBytes(imageBytes));
        await writer.StoreAsync();
        writer.DetachStream();
        stream.Seek(0);
        return await CanvasBitmap.LoadAsync(device, stream, 96f);
    }

    private void ValidateI2IRequest(int workspaceVersion, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (workspaceVersion != MaskCanvas.WorkspaceVersion)
            throw new OperationCanceledException(ct);
    }

    private async Task RestoreI2IPreviewAfterRequestAsync(int workspaceVersion)
    {
        if (workspaceVersion != MaskCanvas.WorkspaceVersion) return;
        if (_i2iResultCandidates.Count > 0)
        {
            await RestoreSelectedI2IResultCandidateAsync();
            return;
        }
        var preview = MaskCanvas.ClearPreview();
        if (_pendingResultBitmap != null && !ReferenceEquals(preview, _pendingResultBitmap))
            _pendingResultBitmap.Dispose();
        preview?.Dispose();
        _pendingResultBitmap = null;
        _pendingResultBytes = null;
        _pendingResultRegion = null;
        _pendingResultTextChunks = null;
        _i2iPreviewDirty = false;
        UpdateFloatingResultBarsVisibility();
    }

    private async Task RegisterI2IResultCandidateAsync(
        byte[] imageBytes, Dictionary<string, string>? textChunks, CanvasDevice device,
        int workspaceVersion, CancellationToken ct, bool resetCandidates,
        InpaintRegion? region = null, bool pngMetadata = false)
    {
        ValidateI2IRequest(workspaceVersion, ct);
        int session = resetCandidates ? _i2iResultSessionId + 1 : _i2iResultSessionId;
        int sequence = resetCandidates ? 1 : _i2iResultSequence + 1;
        Directory.CreateDirectory(I2ITempDir);
        string cachePath = Path.Combine(I2ITempDir, $"i2i_{session}_{sequence}{ImageOutputNaming.Extension(imageBytes)}");
        CanvasBitmap? bitmap = null;
        try
        {
            BusinessLog.Current?.Stage("save_start", new { destination = "i2i_candidate_cache", result_path = cachePath });
            await File.WriteAllBytesAsync(cachePath, imageBytes, ct);
            bitmap = await LoadI2IDisplayBitmapAsync(device, imageBytes);
            ValidateI2IRequest(workspaceVersion, ct);

            if (resetCandidates) ResetI2IResultSession();
            _i2iResultSequence = sequence;
            _i2iResultCandidates.Add(new I2IResultCandidate
            {
                FilePath = cachePath,
                Region = region,
                PngMetadata = pngMetadata,
                TextChunks = CloneTextChunks(textChunks),
            });
            _i2iResultIndex = _i2iResultCandidates.Count - 1;
            SetPendingI2IResult(bitmap, imageBytes, textChunks, region, pngMetadata);
            bitmap = null;
            LogBusinessMetadata("post_save_metadata", imageBytes);
            BusinessLog.Current?.Stage("save_result", new { save_outcome = "success", destination = "i2i_candidate_cache", result_path = cachePath });
        }
        catch
        {
            bitmap?.Dispose();
            if (File.Exists(cachePath)) File.Delete(cachePath);
            throw;
        }
    }

    private async Task<bool> SelectI2IResultCandidateAsync(int index, bool allowDuringApply = false)
    {
        if (_i2iResultApplying && !allowDuringApply)
            return false;
        if (index < 0 || index >= _i2iResultCandidates.Count)
            return false;

        int selectionVersion = ++_i2iResultSelectionVersion;
        var candidate = _i2iResultCandidates[index];
        if (!File.Exists(candidate.FilePath))
            return false;

        var device = MaskCanvas.GetDevice();
        if (device == null)
            return false;

        byte[] imageBytes = await File.ReadAllBytesAsync(candidate.FilePath);
        var bitmap = await LoadI2IDisplayBitmapAsync(device, imageBytes);

        if ((_i2iResultApplying && !allowDuringApply) ||
            selectionVersion != _i2iResultSelectionVersion ||
            !_i2iResultCandidates.Contains(candidate))
        {
            bitmap.Dispose();
            return false;
        }

        _i2iResultIndex = index;
        SetPendingI2IResult(bitmap, imageBytes, candidate.TextChunks, candidate.Region, candidate.PngMetadata);
        return true;
    }

    private async Task RestoreSelectedI2IResultCandidateAsync(bool allowDuringApply = false)
    {
        if (_i2iResultIndex >= 0 && _i2iResultIndex < _i2iResultCandidates.Count)
            await SelectI2IResultCandidateAsync(_i2iResultIndex, allowDuringApply);
    }

    private void UpdateI2IResultNavigator()
    {
        if (TxtI2IResultIndex == null || BtnPreviousI2IResult == null || BtnNextI2IResult == null)
            return;

        int count = _i2iResultCandidates.Count;
        int current = _i2iResultIndex >= 0 ? _i2iResultIndex + 1 : 1;
        TxtI2IResultIndex.Text = current.ToString(CultureInfo.InvariantCulture);
        BtnPreviousI2IResult.IsEnabled = count > 1 && _i2iResultIndex > 0 && !_generateRequestRunning && !_i2iResultApplying;
        BtnNextI2IResult.IsEnabled = count > 1 && _i2iResultIndex < count - 1 && !_generateRequestRunning && !_i2iResultApplying;
    }

    private async Task<I2IApplyWorkspaceState> CaptureI2IApplyWorkspaceStateAsync() => new()
    {
        Canvas = await MaskCanvas.CaptureWorkspaceSnapshotAsync(),
        LastGeneratedImageBytes = _lastGeneratedImageBytes,
        TextChunks = CloneTextChunks(_i2iImageTextChunks),
    };

    private async Task<bool> RestoreI2IApplyWorkspaceStateAsync(I2IApplyWorkspaceState state)
    {
        var oldPreview = MaskCanvas.ClearPreview();
        if (oldPreview != null && !ReferenceEquals(oldPreview, _pendingResultBitmap))
            oldPreview.Dispose();
        _pendingResultBitmap = null;
        _pendingResultBytes = null;
        _pendingResultRegion = null;
        _pendingResultTextChunks = null;
        _i2iPreviewDirty = false;
        ClearI2IResultCandidates();

        if (!await MaskCanvas.RestoreWorkspaceSnapshotAsync(state.Canvas)) return false;
        _lastGeneratedImageBytes = state.LastGeneratedImageBytes;
        _i2iImageTextChunks = CloneTextChunks(state.TextChunks);
        BtnGenerate.IsEnabled = true;
        UpdateFloatingResultBarsVisibility();
        UpdateDynamicMenuStates();
        return true;
    }

    private async void SendImageToI2I(byte[] imageBytes, string? sourcePath = null)
    {
        await SendImageToI2IAsync(imageBytes, sourcePath);
    }

    private async Task<bool> SendImageToI2IAsync(byte[] imageBytes, string? sourcePath = null)
    {
        int workspaceVersion = MaskCanvas.BeginWorkspaceReplacement();
        int importVersion = _i2iImageImportVersion;
        CanvasBitmap? bitmap = null;
        bool applying = false;
        bool OwnsImport() => workspaceVersion == MaskCanvas.WorkspaceVersion && importVersion == _i2iImageImportVersion;
        try
        {
            var device = MaskCanvas.GetDevice() ?? CanvasDevice.GetSharedDevice();
            bitmap = await LoadI2IDisplayBitmapAsync(device, imageBytes);
            var textChunks = await Task.Run(() => ImageMetadataService.ReadRoundTripTextChunks(imageBytes));
            if (!OwnsImport()) return false;

            applying = true;
            SaveCurrentPromptToBuffer();
            var oldPreview = MaskCanvas.ClearPreview();
            if (oldPreview != null && !ReferenceEquals(oldPreview, _pendingResultBitmap))
                oldPreview.Dispose();
            _pendingResultBitmap?.Dispose();
            _pendingResultBitmap = null;
            _pendingResultBytes = null;
            _pendingResultRegion = null;
            _pendingResultTextChunks = null;
            _pendingResultPngMetadata = false;
            ClearI2IResultCandidates();
            _i2iApplyUndoStack.Clear();
            _i2iApplyRedoStack.Clear();

            int imgW = (int)bitmap.SizeInPixels.Width;
            int imgH = (int)bitmap.SizeInPixels.Height;
            SwitchMode(AppMode.I2I);
            var importSize = MaskCanvasControl.ResolveImportCanvasSize(
                imgW, imgH, IsAssetProtectionSizeLimitEnabled());
            int canvasW = importSize.W;
            int canvasH = importSize.H;
            bool sizeApplied = canvasW == imgW && canvasH == imgH;
            _customWidth = canvasW;
            _customHeight = canvasH;
            SetSizeInputsSilently(_customWidth, _customHeight);
            int idx = Array.FindIndex(MaskCanvasControl.CanvasPresets,
                p => p.W == canvasW && p.H == canvasH);
            if (idx >= 0) CboSize.SelectedIndex = idx;

            LoadPromptFromBuffer();
            RefreshCharacterPanel();
            MaskCanvas.InitializeCanvas(canvasW, canvasH);
            string? reloadPath = !string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath)
                ? sourcePath
                : null;
            MaskCanvas.LoadImageFromBitmap(bitmap, reloadPath);
            bitmap = null;
            _lastGeneratedImageBytes = imageBytes;
            _i2iImageTextChunks = textChunks;
            _i2iPreviewDirty = false;
            MaskCanvas.FitToScreen();
            UpdatePromptHighlights();
            TxtStatus.Text = sizeApplied
                ? Lf("i2i.sent_with_synced_size", imgW, imgH)
                : Lf("i2i.sent_with_canvas_size", imgW, imgH, canvasW, canvasH);
            return true;
        }
        catch (Exception ex)
        {
            if (applying || OwnsImport()) TxtStatus.Text = Lf("i2i.send_failed", ex.Message);
            return false;
        }
        finally
        {
            bitmap?.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  重绘模式生成
    // ═══════════════════════════════════════════════════════════

    private async Task<bool> DoInpaintGenerateAsync(bool forceRandomSeed = false)
    {
        if (_generateRequestRunning || _i2iResultApplying) return false;
        using var diagnostic = BeginUiBusinessLog("inpaint", new { forceRandomSeed });
        if (_i2iEditMode == I2IEditMode.Denoise)
            return await DoDenoiseGenerateAsync(forceRandomSeed);

        if (MaskCanvas.IsInPreviewMode)
        {
            return await RedoInpaintGenerateAsync(forceRandomSeed);
        }
        if (MaskCanvas.Document.MaskTarget == null || MaskCanvas.GetDevice() == null)
        { TxtStatus.Text = L("inpaint.error.canvas_not_initialized"); return false; }
        if (!MaskCanvas.HasMaskContent() && MaskCanvas.SelectedRegion == null)
        { TxtStatus.Text = L("inpaint.error.mask_required"); return false; }
        ApplyCharCountPrefixStrip();

        bool keepGenerateButtonInteractive = _continuousGenRunning;
        if (!keepGenerateButtonInteractive)
            BtnGenerate.IsEnabled = false;
        SetGenerationRequestRunning(true);
        SetI2IRequestImageMoveLocked(true);
        UpdateBtnGenerateForApiKey();
        TxtStatus.Text = L("generate.status.generating");
        var ip = ModelCapabilities.CreateRequestSnapshot(_settings.Settings.InpaintParameters);
        string restoreSeed = ip.Seed;
        int workspaceVersion = MaskCanvas.WorkspaceVersion;
        int canvasW = MaskCanvas.CanvasW, canvasH = MaskCanvas.CanvasH;

        try
        {
            _generateCts?.Cancel();
            _generateCts = new CancellationTokenSource();
            var ct = _generateCts.Token;
            var device = MaskCanvas.GetDevice()!;

            var region = MaskCanvas.SelectedRegion;
            double minimumContextArea = MaskCanvas.MinimumContextArea;
            var imageOffset = MaskCanvas.Document.PixelAlignedImageOffset;
            using var exportImage = MaskCanvas.Document.CreateCompositeForExport(device);
            if (exportImage == null) { TxtStatus.Text = L("inpaint.error.export_image_failed"); BtnGenerate.IsEnabled = true; return false; }

            InpaintRequestImage? inpaintImage = null;
            if (!TryValidateReferenceRequest(out string referenceError))
            {
                TxtStatus.Text = referenceError;
                return false;
            }

            string actualSeed;
            while (true)
            {
                actualSeed = SeedValue.Resolve(ip.Seed, forceRandomSeed);
                ip.Seed = actualSeed;
                var wildcardContext = CreateWildcardContext(actualSeed, ip.Model);
                var (prompt, negPrompt) = GetPrompts(wildcardContext);
                var chars = (CurrentCharacterEntries.Count > 0 && !IsCurrentModelV3()) ? GetCharacterData(wildcardContext) : null;
                var preciseReferences = GetPreciseReferenceData();
                if (ActiveVibeTransferCount() > 0 && ActivePreciseReferenceCount() == 0)
                {
                    string? encodeError = await EnsureVibesEncodedAsync(ip.Model, ct);
                    if (encodeError != null) { TxtStatus.Text = encodeError; return false; }
                }
                var vibes = GetVibeTransferData();
                ValidateI2IRequest(workspaceVersion, ct);
                if (inpaintImage == null)
                {
                    var imageBase64 = await NovelAIService.EncodeRenderTargetAsync(exportImage, isMask: false);
                    ValidateI2IRequest(workspaceVersion, ct);
                    var maskBase64 = await NovelAIService.EncodeRenderTargetAsync(MaskCanvas.Document.MaskTarget!, isMask: true);
                    inpaintImage = await Task.Run(() => InpaintImageProcessing.PrepareFocusedRequest(imageBase64, maskBase64, region, minimumContextArea), ct);
                    ValidateI2IRequest(workspaceVersion, ct);
                }
                chars = InpaintImageProcessing.RemapCharacters(chars, inpaintImage);
                var signature = BuildI2IGenerationRequestSignature(
                    "i2i-inpaint",
                    ip,
                    inpaintImage.Width,
                    inpaintImage.Height,
                    actualSeed,
                    prompt,
                    negPrompt,
                    chars,
                    vibes,
                    preciseReferences,
                    inpaintImage.ImageBase64,
                    inpaintImage.MaskBase64,
                    imageOffset, inpaintImage.Region);
                var duplicateDecision = await CheckDuplicateGenerationRequestAsync(signature, restoreSeed);
                ValidateI2IRequest(workspaceVersion, ct);
                if (duplicateDecision == DuplicateGenerationDecision.Cancel)
                    return false;
                if (duplicateDecision == DuplicateGenerationDecision.ProceedWithRandomSeed)
                {
                    restoreSeed = "0";
                    forceRandomSeed = true;
                    continue;
                }

                RememberLastGenerationRequest(signature);
                DebugLog($"[Inpaint] Start | Model={ip.Model} | Seed={actualSeed}");

                var resultBitmap = await SendInpaintRequestAsync(
                    inpaintImage, prompt, negPrompt, ip, chars, vibes, preciseReferences, ct, workspaceVersion, resetResultCandidates: true);
                _lastUsedSeed = actualSeed;

                if (resultBitmap == null) return false;

                _i2iPreviewDirty = true;
                ShowI2IResultBar(resetPosition: true);
                if (!keepGenerateButtonInteractive)
                    BtnGenerate.IsEnabled = true;
                DebugLog($"[Inpaint] Completed | Seed={actualSeed}");
                TxtStatus.Text = L("generate.status.completed");
                return true;
            }
        }
        catch (OperationCanceledException)
        {
            DebugLog("[Inpaint] Cancelled");
            TxtStatus.Text = L("generate.status.cancelled");
            if (!keepGenerateButtonInteractive)
                BtnGenerate.IsEnabled = true;
            return false;
        }
        catch (Exception ex)
        {
            DebugLog($"[Inpaint] Failed: {ex.GetType().Name}");
            TxtStatus.Text = L(NovelAIRequestPolicy.ExceptionKey(ex));
            if (!keepGenerateButtonInteractive)
                BtnGenerate.IsEnabled = true;
            return false;
        }
        finally
        {
            SetGenerationRequestRunning(false);
            SetI2IRequestImageMoveLocked(false);
            UpdateBtnGenerateForApiKey();
            UpdateI2IResultNavigator();
        }
    }

    private async Task<CanvasBitmap?> SendInpaintRequestAsync(
        InpaintRequestImage request, string prompt, string negPrompt, NAIParameters parameters,
        List<CharacterPromptInfo>? chars, List<VibeTransferInfo>? vibes,
        List<PreciseReferenceInfo>? preciseReferences, CancellationToken ct, int workspaceVersion,
        bool resetResultCandidates = false)
    {
        ValidateI2IRequest(workspaceVersion, ct);
        var device = MaskCanvas.GetDevice()!;
        BusinessLog.Current?.Stage("inpaint_region", new
        {
            request.Region, request.CanvasWidth, request.CanvasHeight, request.Width, request.Height,
        });
        int streamPreviewVer = 0;
        bool acceptStreamPreview = true;
        bool accepted = false;
        IProgress<byte[]>? progress = _settings.Settings.StreamGeneration
            ? new Progress<byte[]>(async bytes =>
            {
                if (!acceptStreamPreview || ct.IsCancellationRequested ||
                    workspaceVersion != MaskCanvas.WorkspaceVersion) return;
                int myVer = ++streamPreviewVer;
                try
                {
                    var bmp = await LoadI2IDisplayBitmapAsync(device, bytes);
                    if (!acceptStreamPreview || ct.IsCancellationRequested ||
                        myVer != streamPreviewVer || workspaceVersion != MaskCanvas.WorkspaceVersion)
                    { bmp.Dispose(); return; }
                    SetTransientI2IPreview(bmp, request.Region);
                }
                catch { }
            })
            : null;

        try
        {
            byte[]? imageBytes;
            string? error;
            try
            {
                (imageBytes, error) = await _naiService.InpaintAsync(
                    request.ImageBase64, request.MaskBase64, request.Width, request.Height,
                    prompt, negPrompt, chars, vibes, preciseReferences, progress, ct, parameters);
            }
            finally
            {
                acceptStreamPreview = false;
                ++streamPreviewVer;
            }
            _ = RefreshAnlasInfoAsync(forceRefresh: true);
            ValidateI2IRequest(workspaceVersion, ct);
            if (error != null || imageBytes == null)
            {
                TxtStatus.Text = error ?? L("generate.error.empty_result");
                return null;
            }

            LogBusinessMetadata("returned_original_info", imageBytes);
            var originalChunks = await Task.Run(() => ImageMetadataService.ReadRoundTripTextChunks(imageBytes), ct);
            await RegisterI2IResultCandidateAsync(imageBytes, originalChunks, device,
                workspaceVersion, ct, resetResultCandidates, request.Region, pngMetadata: true);
            var arrivedBytes = await Task.Run(() => EnsurePngEncoded(imageBytes), ct);
            var compositedBytes = await Task.Run(() => InpaintImageProcessing.Composite(arrivedBytes, request), ct);
            var textChunks = await Task.Run(() => ImageMetadataService.ReadRoundTripTextChunks(compositedBytes), ct);
            await RegisterI2IResultCandidateAsync(compositedBytes, textChunks, device,
                workspaceVersion, ct, resetCandidates: false);
            accepted = true;
            return _pendingResultBitmap;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            BusinessLog.Current?.Stage("local_processing_failed", new { stage = "i2i_result", exception = ex.GetType().Name, hresult = ex.HResult, stack = ex.StackTrace });
            DebugLog($"[I2I] Post-response local failure: {ex.GetType().Name} (0x{ex.HResult:X8})");
            TxtStatus.Text = L("generate.warning.generated_local_failed");
            return null;
        }
        finally
        {
            acceptStreamPreview = false;
            ++streamPreviewVer;
            if (!accepted) await RestoreI2IPreviewAfterRequestAsync(workspaceVersion);
        }
    }

    private async Task<bool> DoDenoiseGenerateAsync(bool forceRandomSeed = false)
    {
        if (_generateRequestRunning || _i2iResultApplying) return false;
        if (!MaskCanvas.IsInPreviewMode) ApplyCharCountPrefixStrip();
        using var diagnostic = BeginUiBusinessLog("denoise", new { forceRandomSeed });
        if (MaskCanvas.IsInPreviewMode)
        {
            return await RedoInpaintGenerateAsync(forceRandomSeed);
        }
        if (MaskCanvas.Document.MaskTarget == null || MaskCanvas.GetDevice() == null)
        { TxtStatus.Text = L("inpaint.error.canvas_not_initialized"); return false; }
        if (MaskCanvas.Document.OriginalImage == null)
        { TxtStatus.Text = L("i2i.error.no_image_to_send"); return false; }

        bool keepGenerateButtonInteractive = _continuousGenRunning;
        if (!keepGenerateButtonInteractive)
            BtnGenerate.IsEnabled = false;
        SetGenerationRequestRunning(true);
        SetI2IRequestImageMoveLocked(true);
        UpdateBtnGenerateForApiKey();
        TxtStatus.Text = L("generate.status.generating");
        var dp = ModelCapabilities.CreateRequestSnapshot(_settings.Settings.I2IDenoiseParameters);
        string restoreSeed = dp.Seed;
        int workspaceVersion = MaskCanvas.WorkspaceVersion;
        int canvasW = MaskCanvas.CanvasW, canvasH = MaskCanvas.CanvasH;
        var imageOffset = MaskCanvas.Document.PixelAlignedImageOffset;

        try
        {
            _generateCts?.Cancel();
            _generateCts = new CancellationTokenSource();
            var ct = _generateCts.Token;
            var device = MaskCanvas.GetDevice()!;

            using var exportImage = MaskCanvas.Document.CreateCompositeForExport(device);
            if (exportImage == null) { TxtStatus.Text = L("inpaint.error.export_image_failed"); BtnGenerate.IsEnabled = true; return false; }

            string? imageBase64 = null;
            if (!TryValidateReferenceRequest(out string referenceError))
            {
                TxtStatus.Text = referenceError;
                return false;
            }

            string actualSeed;
            while (true)
            {
                actualSeed = SeedValue.Resolve(dp.Seed, forceRandomSeed);
                dp.Seed = actualSeed;
                var wildcardContext = CreateWildcardContext(actualSeed, dp.Model);
                var (prompt, negPrompt) = GetPrompts(wildcardContext);
                var chars = (CurrentCharacterEntries.Count > 0 && !IsCurrentModelV3()) ? GetCharacterData(wildcardContext) : null;
                var preciseReferences = GetPreciseReferenceData();
                if (ActiveVibeTransferCount() > 0 && ActivePreciseReferenceCount() == 0)
                {
                    string? encodeError = await EnsureVibesEncodedAsync(dp.Model, ct);
                    if (encodeError != null) { TxtStatus.Text = encodeError; return false; }
                }
                var vibes = GetVibeTransferData();
                ValidateI2IRequest(workspaceVersion, ct);
                imageBase64 ??= await NovelAIService.EncodeRenderTargetAsync(exportImage, isMask: false);
                ValidateI2IRequest(workspaceVersion, ct);
                var signature = BuildI2IGenerationRequestSignature(
                    "i2i-denoise",
                    dp,
                    canvasW,
                    canvasH,
                    actualSeed,
                    prompt,
                    negPrompt,
                    chars,
                    vibes,
                    preciseReferences,
                    imageBase64,
                    null,
                    imageOffset);
                var duplicateDecision = await CheckDuplicateGenerationRequestAsync(signature, restoreSeed);
                ValidateI2IRequest(workspaceVersion, ct);
                if (duplicateDecision == DuplicateGenerationDecision.Cancel)
                    return false;
                if (duplicateDecision == DuplicateGenerationDecision.ProceedWithRandomSeed)
                {
                    restoreSeed = "0";
                    forceRandomSeed = true;
                    continue;
                }

                RememberLastGenerationRequest(signature);
                DebugLog($"[Denoise] Start | Model={dp.Model} | Seed={actualSeed} | Strength={dp.DenoiseStrength:0.##} | Noise={dp.DenoiseNoise:0.##}");

                var resultBitmap = await SendDenoiseRequestAsync(
                    imageBase64, canvasW, canvasH, prompt, negPrompt, dp, chars, vibes, preciseReferences, ct, workspaceVersion, resetResultCandidates: true);
                _lastUsedSeed = actualSeed;

                if (resultBitmap == null) return false;

                _i2iPreviewDirty = true;
                ShowI2IResultBar(resetPosition: true);
                if (!keepGenerateButtonInteractive)
                    BtnGenerate.IsEnabled = true;
                DebugLog($"[Denoise] Completed | Seed={actualSeed}");
                TxtStatus.Text = L("generate.status.completed");
                return true;
            }
        }
        catch (OperationCanceledException)
        {
            DebugLog("[Denoise] Cancelled");
            TxtStatus.Text = L("generate.status.cancelled");
            if (!keepGenerateButtonInteractive)
                BtnGenerate.IsEnabled = true;
            return false;
        }
        catch (Exception ex)
        {
            DebugLog($"[Denoise] Failed: {ex.GetType().Name}");
            TxtStatus.Text = L(NovelAIRequestPolicy.ExceptionKey(ex));
            if (!keepGenerateButtonInteractive)
                BtnGenerate.IsEnabled = true;
            return false;
        }
        finally
        {
            SetGenerationRequestRunning(false);
            SetI2IRequestImageMoveLocked(false);
            UpdateBtnGenerateForApiKey();
            UpdateI2IResultNavigator();
        }
    }

    private async Task<CanvasBitmap?> SendDenoiseRequestAsync(
        string imageBase64, int width, int height, string prompt, string negPrompt,
        NAIParameters parameters, List<CharacterPromptInfo>? chars,
        List<VibeTransferInfo>? vibes, List<PreciseReferenceInfo>? preciseReferences,
        CancellationToken ct, int workspaceVersion, bool resetResultCandidates = false)
    {
        ValidateI2IRequest(workspaceVersion, ct);
        var device = MaskCanvas.GetDevice()!;
        int streamPreviewVer = 0;
        bool acceptStreamPreview = true;
        bool accepted = false;
        IProgress<byte[]>? progress = _settings.Settings.StreamGeneration
            ? new Progress<byte[]>(async bytes =>
            {
                if (!acceptStreamPreview || ct.IsCancellationRequested ||
                    workspaceVersion != MaskCanvas.WorkspaceVersion) return;
                int myVer = ++streamPreviewVer;
                try
                {
                    var bmp = await LoadI2IDisplayBitmapAsync(device, bytes);
                    if (!acceptStreamPreview || ct.IsCancellationRequested ||
                        myVer != streamPreviewVer || workspaceVersion != MaskCanvas.WorkspaceVersion)
                    { bmp.Dispose(); return; }
                    SetTransientI2IPreview(bmp);
                }
                catch { }
            })
            : null;

        try
        {
            byte[]? imageBytes;
            string? error;
            try
            {
                (imageBytes, error) = await _naiService.ImageToImageAsync(
                    imageBase64, width, height, prompt, negPrompt,
                    chars, vibes, preciseReferences, progress, ct, parameters);
            }
            finally
            {
                acceptStreamPreview = false;
                ++streamPreviewVer;
            }
            _ = RefreshAnlasInfoAsync(forceRefresh: true);
            ValidateI2IRequest(workspaceVersion, ct);
            if (error != null || imageBytes == null)
            {
                TxtStatus.Text = error ?? L("generate.error.empty_result");
                return null;
            }

            LogBusinessMetadata("returned_original_info", imageBytes);
            var textChunks = await Task.Run(() => ImageMetadataService.ReadRoundTripTextChunks(imageBytes), ct);
            await RegisterI2IResultCandidateAsync(imageBytes, textChunks, device,
                workspaceVersion, ct, resetResultCandidates);
            accepted = true;
            return _pendingResultBitmap;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            BusinessLog.Current?.Stage("local_processing_failed", new { stage = "i2i_result", exception = ex.GetType().Name, hresult = ex.HResult, stack = ex.StackTrace });
            DebugLog($"[I2I] Post-response local failure: {ex.GetType().Name} (0x{ex.HResult:X8})");
            TxtStatus.Text = L("generate.warning.generated_local_failed");
            return null;
        }
        finally
        {
            acceptStreamPreview = false;
            ++streamPreviewVer;
            if (!accepted) await RestoreI2IPreviewAfterRequestAsync(workspaceVersion);
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  重绘预览操作：应用 / 重做 / 舍弃
    // ═══════════════════════════════════════════════════════════

    private async void OnApplyResult(object sender, RoutedEventArgs e)
    {
        if (_i2iResultApplying || _generateRequestRunning) return;
        _i2iResultApplying = true;
        _i2iResultSelectionVersion++;
        SetResultBarEnabled(false);
        UpdateI2IResultNavigator();
        try
        {
            if (_pendingResultBitmap == null && _i2iResultIndex >= 0)
                await RestoreSelectedI2IResultCandidateAsync(allowDuringApply: true);
            if (_pendingResultBitmap != null && await ApplyInpaintResultAsync())
                TxtStatus.Text = L("inpaint.result_applied");
        }
        catch (Exception ex) { TxtStatus.Text = Lf("inpaint.apply_failed", ex.Message); }
        finally
        {
            _i2iResultApplying = false;
            SetResultBarEnabled(true);
            UpdateI2IResultNavigator();
        }
    }

    private async Task<bool> RedoInpaintGenerateAsync(bool forceRandomSeed = false)
    {
        if (_generateRequestRunning || _i2iResultApplying) return false;
        ApplyCharCountPrefixStrip();
        using var diagnostic = BeginUiBusinessLog("i2i_regenerate", new { forceRandomSeed });
        var editMode = _i2iEditMode;
        if (MaskCanvas.Document.MaskTarget == null || MaskCanvas.GetDevice() == null)
        { TxtStatus.Text = L("inpaint.error.canvas_not_initialized"); return false; }
        if (editMode == I2IEditMode.Inpaint && !MaskCanvas.HasMaskContent() && MaskCanvas.SelectedRegion == null)
        { TxtStatus.Text = L("inpaint.error.mask_required"); return false; }
        if (editMode == I2IEditMode.Denoise && MaskCanvas.Document.OriginalImage == null)
        { TxtStatus.Text = L("i2i.error.no_image_to_send"); return false; }

        BtnGenerate.IsEnabled = false;
        SetGenerationRequestRunning(true);
        SetI2IRequestImageMoveLocked(true);
        UpdateBtnGenerateForApiKey();
        SetResultBarEnabled(false);
        TxtStatus.Text = L("generate.status.regenerating");
        var ip = ModelCapabilities.CreateRequestSnapshot(editMode == I2IEditMode.Denoise
            ? _settings.Settings.I2IDenoiseParameters
            : _settings.Settings.InpaintParameters);
        string restoreSeed = ip.Seed;
        int workspaceVersion = MaskCanvas.WorkspaceVersion;
        int canvasW = MaskCanvas.CanvasW, canvasH = MaskCanvas.CanvasH;

        try
        {
            _generateCts?.Cancel();
            _generateCts = new CancellationTokenSource();
            var ct = _generateCts.Token;
            var device = MaskCanvas.GetDevice()!;

            var region = MaskCanvas.SelectedRegion;
            double minimumContextArea = MaskCanvas.MinimumContextArea;
            var imageOffset = MaskCanvas.Document.PixelAlignedImageOffset;
            using var exportImage = MaskCanvas.Document.CreateCompositeForExport(device);
            if (exportImage == null) { TxtStatus.Text = L("inpaint.error.export_image_failed"); BtnGenerate.IsEnabled = true; return false; }

            string? imageBase64 = null, maskBase64 = null;
            InpaintRequestImage? inpaintImage = null;
            if (!TryValidateReferenceRequest(out string referenceError))
            {
                TxtStatus.Text = referenceError;
                return false;
            }

            string actualSeed;
            while (true)
            {
                actualSeed = SeedValue.Resolve(ip.Seed, forceRandomSeed);
                ip.Seed = actualSeed;
                var wildcardContext = CreateWildcardContext(actualSeed, ip.Model);
                var (prompt, negPrompt) = GetPrompts(wildcardContext);
                var chars = (CurrentCharacterEntries.Count > 0 && !IsCurrentModelV3()) ? GetCharacterData(wildcardContext) : null;
                var preciseReferences = GetPreciseReferenceData();
                if (ActiveVibeTransferCount() > 0 && ActivePreciseReferenceCount() == 0)
                {
                    string? encodeError = await EnsureVibesEncodedAsync(ip.Model, ct);
                    if (encodeError != null) { TxtStatus.Text = encodeError; return false; }
                }
                var vibes = GetVibeTransferData();
                ValidateI2IRequest(workspaceVersion, ct);
                if (imageBase64 == null)
                {
                    imageBase64 = await NovelAIService.EncodeRenderTargetAsync(exportImage, isMask: false);
                    ValidateI2IRequest(workspaceVersion, ct);
                    if (editMode == I2IEditMode.Inpaint)
                    {
                        maskBase64 = await NovelAIService.EncodeRenderTargetAsync(MaskCanvas.Document.MaskTarget!, isMask: true);
                        inpaintImage = await Task.Run(() => InpaintImageProcessing.PrepareFocusedRequest(imageBase64, maskBase64, region, minimumContextArea), ct);
                        ValidateI2IRequest(workspaceVersion, ct);
                    }
                }
                if (inpaintImage != null) chars = InpaintImageProcessing.RemapCharacters(chars, inpaintImage);
                var signature = BuildI2IGenerationRequestSignature(
                    editMode == I2IEditMode.Denoise ? "i2i-denoise" : "i2i-inpaint",
                    ip,
                    inpaintImage?.Width ?? canvasW,
                    inpaintImage?.Height ?? canvasH,
                    actualSeed,
                    prompt,
                    negPrompt,
                    chars,
                    vibes,
                    preciseReferences,
                    inpaintImage?.ImageBase64 ?? imageBase64,
                    inpaintImage?.MaskBase64 ?? maskBase64,
                    imageOffset, inpaintImage?.Region);
                var duplicateDecision = await CheckDuplicateGenerationRequestAsync(signature, restoreSeed);
                ValidateI2IRequest(workspaceVersion, ct);
                if (duplicateDecision == DuplicateGenerationDecision.Cancel)
                    return false;
                if (duplicateDecision == DuplicateGenerationDecision.ProceedWithRandomSeed)
                {
                    restoreSeed = "0";
                    forceRandomSeed = true;
                    continue;
                }

                RememberLastGenerationRequest(signature);
                var resultBitmap = editMode == I2IEditMode.Denoise
                    ? await SendDenoiseRequestAsync(imageBase64, canvasW, canvasH, prompt, negPrompt, ip, chars, vibes, preciseReferences, ct, workspaceVersion)
                    : await SendInpaintRequestAsync(
                        inpaintImage!, prompt, negPrompt, ip, chars, vibes, preciseReferences, ct, workspaceVersion);
                _lastUsedSeed = actualSeed;

                if (resultBitmap != null)
                {
                    _i2iPreviewDirty = true;
                    TxtStatus.Text = L("generate.status.regenerated");
                    return true;
                }

                break;
            }
        }
        catch (OperationCanceledException) { TxtStatus.Text = L("generate.status.cancelled"); }
        catch (Exception ex) { TxtStatus.Text = Lf("generate.status.regenerate_failed", ex.Message); }
        finally
        {
            SetGenerationRequestRunning(false);
            SetI2IRequestImageMoveLocked(false);
            UpdateBtnGenerateForApiKey();
            SetResultBarEnabled(true);
            UpdateI2IResultNavigator();
        }
        return false;
    }

    private async void OnRedoGenerate(object sender, RoutedEventArgs e)
    {
        if (!_settings.HasApiTokens)
        {
            OnApiSettings(sender, e);
            return;
        }

        SyncPromptGenerationInputsToState();
        _settings.Save();
        await RedoInpaintGenerateAsync();
    }

    private async void OnPreviousI2IResult(object sender, RoutedEventArgs e)
    {
        if (_generateRequestRunning || _i2iResultApplying || _i2iResultIndex <= 0)
            return;

        await SelectI2IResultCandidateAsync(_i2iResultIndex - 1);
    }

    private async void OnNextI2IResult(object sender, RoutedEventArgs e)
    {
        if (_generateRequestRunning || _i2iResultApplying || _i2iResultIndex >= _i2iResultCandidates.Count - 1)
            return;

        await SelectI2IResultCandidateAsync(_i2iResultIndex + 1);
    }

    private void OnDiscardResult(object sender, RoutedEventArgs e)
    {
        ExitPreviewMode();
        UpdateI2IRedoButtonWarning();
        TxtStatus.Text = L("generate.status.discarded");
    }

    private void ExitPreviewMode()
    {
        var oldPreview = MaskCanvas.ClearPreview();
        bool disposedPending = false;
        if (oldPreview != null)
        {
            oldPreview.Dispose();
            disposedPending = ReferenceEquals(oldPreview, _pendingResultBitmap);
        }
        _i2iPreviewDirty = false;
        if (_pendingResultBitmap != null && !disposedPending)
            _pendingResultBitmap.Dispose();
        _pendingResultBitmap = null;
        _pendingResultBytes = null;
        _pendingResultRegion = null;
        _pendingResultTextChunks = null;
        ClearI2IResultCandidates();
        BtnGenerate.IsEnabled = true;
        UpdateFloatingResultBarsVisibility();
    }

    private void SetResultBarEnabled(bool enabled)
    {
        foreach (var child in ((StackPanel)((ScrollViewer)((Border)ResultActionBar.Children[0]).Child).Content).Children)
        {
            if (child is Button btn) btn.IsEnabled = enabled;
        }
    }

    private void OnResultBarDrag(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        ResultBarTranslate.X += e.Delta.Translation.X;
        ResultBarTranslate.Y += e.Delta.Translation.Y;
    }

    private void OnComparePressed(object sender, PointerRoutedEventArgs e)
    {
        MaskCanvas.IsComparing = true;
        (sender as UIElement)?.CapturePointer(e.Pointer);
    }

    private void OnCompareReleased(object sender, PointerRoutedEventArgs e)
    {
        MaskCanvas.IsComparing = false;
    }

    private async Task<bool> ApplyInpaintResultAsync()
    {
        var device = MaskCanvas.GetDevice();
        var pendingBytes = _pendingResultBytes;
        bool pngMetadata = _pendingResultPngMetadata;
        int workspaceVersion = MaskCanvas.WorkspaceVersion;
        if (device == null || pendingBytes == null) return false;

        var doc = MaskCanvas.Document;
        var offset = doc.PixelAlignedImageOffset;
        var newOffset = new Vector2(offset.X + Math.Min(0, -offset.X),
            offset.Y + Math.Min(0, -offset.Y));
        int canvasW = MaskCanvas.CanvasW, canvasH = MaskCanvas.CanvasH;
        var undoState = await CaptureI2IApplyWorkspaceStateAsync();
        if (workspaceVersion != MaskCanvas.WorkspaceVersion || !ReferenceEquals(pendingBytes, _pendingResultBytes))
            return false;

        byte[]? appliedBytes = await CreatePreviewCompositeBytes();
        if (appliedBytes == null) return false;
        if (!ReferenceEquals(appliedBytes, pendingBytes))
            appliedBytes = await Task.Run(() => ImageMetadataService.ReapplyNovelAiMetadata(
                appliedBytes, pngMetadata ? EnsurePngEncoded(pendingBytes) : pendingBytes, canvasW, canvasH));
        var textChunks = await Task.Run(() => ImageMetadataService.ReadRoundTripTextChunks(appliedBytes));
        var bitmap = await LoadI2IDisplayBitmapAsync(device, appliedBytes);
        if (workspaceVersion != MaskCanvas.WorkspaceVersion || !ReferenceEquals(pendingBytes, _pendingResultBytes))
        {
            bitmap.Dispose();
            return false;
        }

        _i2iApplyUndoStack.Push(undoState);
        _i2iApplyRedoStack.Clear();
        doc.SetOriginalImage(bitmap);
        doc.ImageOffset = newOffset;
        _lastGeneratedImageBytes = appliedBytes;
        _i2iImageTextChunks = textChunks;
        var oldPreview = MaskCanvas.ClearPreview();
        if (_pendingResultBitmap != null && !ReferenceEquals(oldPreview, _pendingResultBitmap))
            _pendingResultBitmap.Dispose();
        oldPreview?.Dispose();
        _pendingResultBitmap = null;
        _pendingResultBytes = null;
        _pendingResultRegion = null;
        _pendingResultTextChunks = null;
        ClearI2IResultCandidates();
        doc.ClearMask();
        MaskCanvas.UndoMgr.Clear();
        BtnGenerate.IsEnabled = true;
        MaskCanvas.RefreshCanvas();
        UpdateFloatingResultBarsVisibility();
        return true;
    }
}
