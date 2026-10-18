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
    // ═══════════════════════════════════════════════════════════
    //  生成（根据模式分流）
    // ═══════════════════════════════════════════════════════════

    private async void OnGenerate(object sender, RoutedEventArgs e)
    {
        if (_autoGenRunning) { StopAutoGeneration(); return; }
        if (_continuousGenRunning) { StopContinuousGeneration(); return; }
        if (_generateRequestRunning || _i2iResultApplying) return;

        if (!_settings.HasApiTokens)
        {
            OnApiSettings(sender, e);
            return;
        }

        SyncPromptGenerationInputsToState();

        if (GetSizeWarningLevel() == SizeWarningLevel.Red)
        {
            long limit = IsAssetProtectionSizeLimitEnabled() ? 1024L : 2048L;
            TxtStatus.Text = Lf("generate.error.size_limit_exceeded", limit);
            return;
        }

        _settings.Save();

        await ExecuteCurrentGenerationAsync();
    }

    private void SyncPromptGenerationInputsToState()
    {
        SaveCurrentPromptToBuffer();
        SyncUIToParams();
        if (IsAdvancedWindowOpen)
            SaveAdvancedPanelToSettings();
    }

    private Task<bool> ExecuteCurrentGenerationAsync(bool forceRandomSeed = false) =>
        _currentMode == AppMode.ImageGeneration
            ? DoImageGenerationAsync(forceRandomSeed)
            : DoInpaintGenerateAsync(forceRandomSeed);

    // ═══════════════════════════════════════════════════════════
    //  生图模式生成
    // ═══════════════════════════════════════════════════════════

    private async Task<bool> DoImageGenerationAsync(bool forceRandomSeed = false)
    {
        if (_generateRequestRunning || _i2iResultApplying) return false;
        using var diagnostic = BeginUiBusinessLog("generate", new { forceRandomSeed, size = GetSelectedSize(), automation = _automationRunContext });
        _lastGenerationFailureStatusCode = null;
        var autoContext = _autoGenRunning ? _automationRunContext : null;
        var (w, h) = autoContext?.CurrentSizeOverride ?? GetSelectedSize();
        bool keepGenerateButtonInteractive = _autoGenRunning || _continuousGenRunning;
        if (!keepGenerateButtonInteractive) BtnGenerate.IsEnabled = false;
        SetGenerationRequestRunning(true);
        UpdateBtnGenerateForApiKey();
        TxtStatus.Text = L("generate.status.generating");
        var p = ModelCapabilities.CreateRequestSnapshot(_settings.Settings.GenParameters);
        string restoreSeed = p.Seed;
        string? pendingHistoryId = null;
        bool previewsOpen = true;
        bool finalAccepted = false;
        bool responseReceived = false;
        string localProcessingStage = "response";
        string? savedOutputPath = null;
        var previousPreviewSource = GenPreviewImage.Source;
        var previousImageBytes = _currentGenImageBytes;
        var previousImagePath = _currentGenImagePath;
        double previousPreviewWidth = GenPreviewImage.Width;
        double previousPreviewHeight = GenPreviewImage.Height;

        try
        {
            _generateCts?.Cancel();
            _generateCts = new CancellationTokenSource();
            var ct = _generateCts.Token;
            SaveCurrentPromptToBuffer();
            if (!TryValidateReferenceRequest(out string referenceError))
            {
                TxtStatus.Text = referenceError;
                return false;
            }

            string actualSeed;
            string prompt;
            string negPrompt;
            List<CharacterPromptInfo>? chars;
            List<VibeTransferInfo>? vibes;
            List<PreciseReferenceInfo>? preciseReferences;
            ApplyCharCountPrefixStrip();
            while (true)
            {
                actualSeed = SeedValue.Resolve(p.Seed, forceRandomSeed);
                p.Seed = actualSeed;

                var wildcardContext = CreateWildcardContext(actualSeed, p.Model);
                string automationPrompt = autoContext?.CurrentPromptOverride ?? _genPositivePrompt;
                string positiveRaw = MergeStyleAndMain(_genStylePrompt, automationPrompt);
                string negativeRaw = _genNegativePrompt;
                if (_autoGenRunning && _activeAutomationSettings?.Randomization.RandomizeStyleTags == true)
                {
                    var styleOptions = new RandomStyleOptions(
                        _activeAutomationSettings.Randomization.StyleTagCount,
                        _activeAutomationSettings.Randomization.StyleMinCount,
                        _activeAutomationSettings.Randomization.StyleUseWeight);
                    string? stylePrefix = BuildRandomStylePrefixForRequest(styleOptions);
                    if (string.IsNullOrWhiteSpace(stylePrefix))
                        return false;
                    positiveRaw = MergeStyleAndMain(_genStylePrompt, MergeStyleAndMain(stylePrefix, automationPrompt));
                }

                prompt = ExpandPromptFeatures(positiveRaw, wildcardContext);
                negPrompt = ExpandPromptFeatures(negativeRaw, wildcardContext, isNegativeText: true);
                if (string.IsNullOrWhiteSpace(prompt))
                {
                    TxtStatus.Text = L("generate.error.prompt_required");
                    return false;
                }

                chars = (CurrentCharacterEntries.Count > 0 && !IsCurrentModelV3()) ? GetCharacterData(wildcardContext) : null;
                preciseReferences = GetPreciseReferenceData();
                if (autoContext?.CurrentVibeOverride == null &&
                    ActiveVibeTransferCount() > 0 &&
                    ActivePreciseReferenceCount() == 0)
                {
                    string? encodeError = await EnsureVibesEncodedAsync(p.Model, ct);
                    if (encodeError != null) { TxtStatus.Text = encodeError; return false; }
                }

                vibes = autoContext?.CurrentVibeOverride ?? GetVibeTransferData();
                ct.ThrowIfCancellationRequested();
                var signature = BuildImageGenerationRequestSignature(
                    p, w, h, actualSeed, prompt, negPrompt, chars, vibes, preciseReferences);
                var duplicateDecision = await CheckDuplicateGenerationRequestAsync(signature, restoreSeed);
                if (duplicateDecision == DuplicateGenerationDecision.Cancel)
                    return false;
                if (duplicateDecision == DuplicateGenerationDecision.ProceedWithRandomSeed)
                {
                    restoreSeed = "0";
                    forceRandomSeed = true;
                    continue;
                }

                RememberLastGenerationRequest(signature);
                break;
            }

            if (!_settings.Settings.PrivacyMode)
                pendingHistoryId = AddPendingHistoryItem();
            DebugLog($"[Generate] Start | {w}x{h} | Model={p.Model} | Seed={actualSeed}");
            IProgress<byte[]>? progress = _settings.Settings.StreamGeneration
                ? new Progress<byte[]>(bytes =>
                {
                    if (!previewsOpen || ct.IsCancellationRequested) return;
                    _ = ShowGenPreviewAsync(bytes, w, h, () => previewsOpen && !ct.IsCancellationRequested);
                })
                : null;
            diagnostic.Stage("preprocessing_complete", new { w, h, prompt, negPrompt, chars, vibes, preciseReferences, actualSeed });
            var (imageBytes, error) = await _naiService.GenerateAsync(
                w, h, prompt, negPrompt,
                chars, vibes, preciseReferences, progress, ct, p);
            _ = RefreshAnlasInfoAsync(forceRefresh: true);
            previewsOpen = false;
            ct.ThrowIfCancellationRequested();
            _lastUsedSeed = actualSeed;

            if (error != null)
            {
                if (pendingHistoryId != null)
                    RemovePendingHistoryItem(pendingHistoryId);
                _lastGenerationFailureStatusCode = _naiService.LastGenerationErrorStatusCode;
                DebugLog($"[Generate] API error: {error}");
                TxtStatus.Text = error;
                return false;
            }
            if (imageBytes == null)
            {
                if (pendingHistoryId != null)
                    RemovePendingHistoryItem(pendingHistoryId);
                DebugLog("[Generate] API returned no image");
                TxtStatus.Text = L("generate.error.empty_result");
                return false;
            }

            responseReceived = true;
            LogBusinessMetadata("returned_original_info", imageBytes);
            byte[] finalBytes = imageBytes;
            localProcessingStage = "save_original";
            string? originalSavedPath = await SaveToOutputAsync(imageBytes, resolvedPrompt: prompt, sampleSeed: actualSeed);
            savedOutputPath = originalSavedPath;
            string? finalSavedPath = originalSavedPath;
            string postSummary = "";

            if (_autoGenRunning && _activeAutomationSettings?.Effects.Enabled == true)
            {
                localProcessingStage = "automation_effects";
                var postResult = await RunAutomationEffectsProcessAsync(imageBytes, _activeAutomationSettings.Effects, ct);
                finalBytes = postResult.Bytes;
                if (!ReferenceEquals(finalBytes, imageBytes))
                    finalBytes = ImageMetadataService.ReapplyNovelAiMetadata(finalBytes, ImageMetadataService.ReadGenerationTextChunks(imageBytes));
                localProcessingStage = "save_effect_result";
                finalSavedPath = await SaveToOutputAsync(finalBytes, "auto", prompt, actualSeed);
                savedOutputPath = finalSavedPath ?? originalSavedPath;
                postSummary = postResult.Summary;
            }

            _currentGenImageBytes = finalBytes;
            _currentGenImagePath = finalSavedPath;

            // The saved result belongs in history even if the WinUI preview decoder fails.
            localProcessingStage = "history_update";
            if (finalSavedPath != null)
            {
                if (pendingHistoryId != null)
                {
                    ResolvePendingHistoryItem(pendingHistoryId, finalSavedPath);
                    pendingHistoryId = null;
                }
                else
                    AddHistoryItem(finalSavedPath);
            }
            else if (pendingHistoryId != null)
            {
                RemovePendingHistoryItem(pendingHistoryId);
                pendingHistoryId = null;
            }

            localProcessingStage = "ui_preview";
            diagnostic.Stage("ui_preview_start", new { format = ImageOutputNaming.Extension(finalBytes) });
            await ShowGenPreviewAsync(finalBytes, w, h);
            diagnostic.Stage("ui_preview_complete", new { format = ImageOutputNaming.Extension(finalBytes) });
            finalAccepted = true;
            ArmNewImageDeleteProtection(finalSavedPath);

            if (!_autoGenRunning)
                SetGenResultBarRequested(true, resetPosition: true);
            UpdateDynamicMenuStates();
            DebugLog($"[Generate] Completed | Seed={actualSeed} | Saved={finalSavedPath}");
            TxtStatus.Text = _settings.Settings.PrivacyMode
                ? L("generate.status.completed_unsaved_privacy")
                : string.IsNullOrWhiteSpace(postSummary)
                ? Lf("generate.status.completed_saved", finalSavedPath)
                : Lf("generate.status.completed_post_saved", postSummary, finalSavedPath);
            return true;
        }
        catch (OperationCanceledException)
        {
            if (pendingHistoryId != null)
                RemovePendingHistoryItem(pendingHistoryId);
            DebugLog("[Generate] Cancelled");
            TxtStatus.Text = L("generate.status.cancelled");
            return false;
        }
        catch (Exception ex)
        {
            if (pendingHistoryId != null)
                RemovePendingHistoryItem(pendingHistoryId);
            if (responseReceived)
            {
                diagnostic.Stage("local_processing_failed", new { stage = localProcessingStage, exception = ex.GetType().Name, hresult = ex.HResult, stack = ex.StackTrace, saved = savedOutputPath != null });
                DebugLog($"[Generate] Post-response local failure at {localProcessingStage}: {ex.GetType().Name} (0x{ex.HResult:X8})");
                TxtStatus.Text = savedOutputPath != null
                    ? Lf("generate.warning.saved_ui_failed", savedOutputPath)
                    : L("generate.warning.generated_local_failed");
                return true;
            }
            DebugLog($"[Generate] Failed before response: {ex.GetType().Name}");
            TxtStatus.Text = L(NovelAIRequestPolicy.ExceptionKey(ex));
            return false;
        }
        finally
        {
            previewsOpen = false;
            if (!finalAccepted)
            {
                _currentGenImageBytes = previousImageBytes;
                _currentGenImagePath = previousImagePath;
                GenPreviewImage.Source = previousPreviewSource;
                GenPreviewImage.Width = previousPreviewWidth;
                GenPreviewImage.Height = previousPreviewHeight;
                GenPlaceholder.Visibility = previousPreviewSource == null ? Visibility.Visible : Visibility.Collapsed;
            }
            SetGenerationRequestRunning(false);
            UpdateBtnGenerateForApiKey();
        }
    }

    private static byte[] GetWinUiDisplayBytes(byte[] imageBytes) =>
        InpaintImageProcessing.PrepareDisplayBytes(imageBytes);

    private async Task ShowGenPreviewAsync(byte[] imageBytes, int targetW = 0, int targetH = 0, Func<bool>? acceptPreview = null)
    {
        var bitmapImage = new BitmapImage();
        using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        using var writer = new Windows.Storage.Streams.DataWriter(ms);
        writer.WriteBytes(GetWinUiDisplayBytes(imageBytes));
        await writer.StoreAsync();
        writer.DetachStream();
        ms.Seek(0);
        await bitmapImage.SetSourceAsync(ms);
        if (acceptPreview != null && !acceptPreview()) return;
        GenPreviewImage.Source = bitmapImage;
        GenPreviewImage.Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform;
        if (targetW > 0 && targetH > 0)
        {
            GenPreviewImage.Width = targetW;
            GenPreviewImage.Height = targetH;
        }
        else if (bitmapImage.PixelWidth > 0 && bitmapImage.PixelHeight > 0)
        {
            GenPreviewImage.Width = bitmapImage.PixelWidth;
            GenPreviewImage.Height = bitmapImage.PixelHeight;
        }
        GenPlaceholder.Visibility = Visibility.Collapsed;

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => FitGenPreviewToScreen());
    }

    private void FitGenPreviewToScreen()
    {
        if (GenPreviewImage.Source is not BitmapImage bmp) return;
        double imgW = GenPreviewImage.Width > 0 && !double.IsNaN(GenPreviewImage.Width) ? GenPreviewImage.Width : bmp.PixelWidth;
        double imgH = GenPreviewImage.Height > 0 && !double.IsNaN(GenPreviewImage.Height) ? GenPreviewImage.Height : bmp.PixelHeight;
        if (imgW <= 0 || imgH <= 0) return;

        double viewW = GenImageScroller.ViewportWidth;
        double viewH = GenImageScroller.ViewportHeight;
        if (viewW <= 0 || viewH <= 0) return;

        float zoom = (float)Math.Min(viewW / imgW, viewH / imgH);
        zoom = Math.Min(zoom, 1.0f);
        GenImageScroller.ChangeView(0, 0, zoom);
    }

    private async Task<string?> SaveToOutputAsync(byte[] imageBytes, string prefix = "gen", string? resolvedPrompt = null, string? sampleSeed = null)
    {
        BusinessLog.Current?.Stage("save_start", new { prefix });
        if (_settings.Settings.PrivacyMode)
        {
            BusinessLog.Current?.Stage("save_result", new { save_outcome = "skipped_privacy" });
            return null;
        }
        try
        {
            var dateDir = Path.Combine(OutputBaseDir, DateTime.Now.ToString("yyyy-MM"));
            var meta = ImageMetadataService.ReadFromBytes(imageBytes);
            var stem = ImageOutputNaming.Stem(meta?.PositivePrompt ?? resolvedPrompt ?? "", meta?.Seed ?? sampleSeed ?? "unknown");
            var bytesToSave = await PrepareImageBytesForSaveAsync(imageBytes, stripMetadata: false,
                forcePng: _settings.Settings.SaveGeneratedImagesAsPng);
            var filePath = await ImageOutputNaming.SaveAsync(dateDir, stem, bytesToSave);
            LogBusinessMetadata("post_save_metadata", bytesToSave);
            BusinessLog.Current?.Stage("save_result", new { save_outcome = "success", result_path = filePath });
            return filePath;
        }
        catch (Exception ex)
        {
            BusinessLog.Current?.Stage("save_result", new { save_outcome = "failed", exception = ex.ToString() });
            throw;
        }
    }

    // ═══ 生图模式浮动操作窗 ═══

    private void OnSendToI2I(object sender, RoutedEventArgs e)
    {
        if (_currentGenImageBytes == null)
        { TxtStatus.Text = L("generate.error.no_result_to_send"); return; }

        SetGenResultBarRequested(false);
        SendImageToI2I(_currentGenImageBytes, _currentGenImagePath);
    }

    private async void OnSendToEffectsFromGen(object sender, RoutedEventArgs e)
    {
        if (_currentGenImageBytes == null)
        {
            TxtStatus.Text = L("generate.error.no_result_to_send");
            return;
        }

        SetGenResultBarRequested(false);
        await SendBytesToEffectsAsync(_currentGenImageBytes, _currentGenImagePath);
    }

    private async void OnSendToEffectsFromI2I(object sender, RoutedEventArgs e)
    {
        try
        {
            byte[]? bytesToSend;
            if (MaskCanvas.IsInPreviewMode && _pendingResultBitmap != null)
            {
                await ApplyInpaintResultAsync();
                bytesToSend = _lastGeneratedImageBytes ?? await CreateCurrentFullImageBytes();
            }
            else
            {
                bytesToSend = await CreateCurrentFullImageBytes();
            }

            if (bytesToSend == null || bytesToSend.Length == 0)
            {
                TxtStatus.Text = L("post.error.no_image_to_send");
                return;
            }

            await SendBytesToEffectsAsync(bytesToSend, MaskCanvas.LoadedFilePath);
        }
        catch (Exception ex)
        {
            TxtStatus.Text = Lf("post.error.send_failed", ex.Message);
        }
    }

    private async void OnGenSendToInspect(object sender, RoutedEventArgs e)
    {
        if (_currentGenImageBytes == null)
        { TxtStatus.Text = L("generate.error.no_result_to_send"); return; }
        SetGenResultBarRequested(false);
        SwitchMode(AppMode.Inspect);
        await LoadInspectImageFromBytesAsync(_currentGenImageBytes, _currentGenImagePath != null ? Path.GetFileName(_currentGenImagePath) : null);
    }

    private async void OnDeleteGenResult(object sender, RoutedEventArgs e)
    {
        if (TryBlockNewImageDeletion(_currentGenImagePath, isCurrentPreviewAction: true))
            return;

        if (!_genResultBarResident)
            SetGenResultBarRequested(false);

        string? deletedPath = _currentGenImagePath;
        if (!string.IsNullOrEmpty(deletedPath) && File.Exists(deletedPath))
        {
            try
            {
                int idx = _historyFiles.IndexOf(deletedPath);
                if (!TryDeleteImageFileWithConfiguredBehavior(deletedPath))
                    return;

                RemoveHistoryFile(deletedPath);
                RefreshHistoryPanel();

                string? nextPath = null;
                if (idx >= 0 && _historyFiles.Count > 0)
                    nextPath = _historyFiles[Math.Min(idx, _historyFiles.Count - 1)];

                if (nextPath != null)
                {
                    await ShowHistoryImageAsync(nextPath);
                    TxtStatus.Text = L("common.deleted");
                    return;
                }
                TxtStatus.Text = L("history.generated_result_deleted");
            }
            catch (Exception ex) { TxtStatus.Text = Lf("common.delete_failed", ex.Message); }
        }

        ClearCurrentGenPreview();
    }

    private void CopyImageToClipboard(byte[] imageBytes)
    {
        try
        {
            var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var writer = new Windows.Storage.Streams.DataWriter(stream);
            writer.WriteBytes(imageBytes);
            _ = writer.StoreAsync().AsTask().ContinueWith(_ =>
            {
                stream.Seek(0);
                DispatcherQueue.TryEnqueue(() =>
                {
                    var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    dp.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(stream));
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
                    TxtStatus.Text = L("image.copied_to_clipboard");
                });
            });
        }
        catch (Exception ex) { TxtStatus.Text = Lf("common.copy_failed", ex.Message); }
    }

    private async void OnHistoryCopyImage(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string filePath)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(filePath);
                CopyImageToClipboard(bytes);
            }
            catch (Exception ex) { TxtStatus.Text = Lf("common.copy_failed", ex.Message); }
        }
    }

    private void OnCloseGenResultBar(object sender, RoutedEventArgs e)
    {
        _genResultBarAutoOpenEnabled = false;
        _genResultBarResident = false;
        SetGenResultBarRequested(false);
    }

    private void OnGenResultBarDrag(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        GenResultBarTranslate.X += e.Delta.Translation.X;
        GenResultBarTranslate.Y += e.Delta.Translation.Y;
    }

    // ═══════════════════════════════════════════════════════════
    //  生图预览区右键菜单 & 拖放
    // ═══════════════════════════════════════════════════════════

    private void SetupGenPreviewContextMenu()
    {
        var flyout = new MenuFlyout();
        flyout.Opening += (_, _) =>
        {
            flyout.Items.Clear();
            bool hasImage = _currentGenImageBytes != null;
            flyout.Items.Add(BuildHistoryFavoriteMenuItem(hasImage ? _currentGenImagePath : null));
            flyout.Items.Add(new MenuFlyoutSeparator());

            var copyItem = new MenuFlyoutItem
            {
                Text = L("common.copy"),
                Icon = new SymbolIcon(Symbol.Copy),
                IsEnabled = hasImage,
            };
            copyItem.Click += (_, _) =>
            {
                if (_currentGenImageBytes != null)
                    CopyImageToClipboard(_currentGenImageBytes);
            };
            flyout.Items.Add(copyItem);

            var enhanceItem = new MenuFlyoutItem
            {
                Text = L("button.enhance"),
                Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE771" },
                IsEnabled = hasImage && !_generateRequestRunning,
            };
            enhanceItem.Click += async (_, _) =>
            {
                if (_currentGenImageBytes != null)
                    await BeginGenEnhanceAsync(_currentGenImageBytes, _currentGenImagePath);
            };
            flyout.Items.Add(enhanceItem);

            var saveAsItem = new MenuFlyoutItem
            {
                Text = L("menu.file.save_as"),
                Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE792" },
                IsEnabled = hasImage,
            };
            saveAsItem.Click += async (_, _) =>
            {
                if (_currentGenImageBytes != null)
                    await SaveImageBytesAsAsync(_currentGenImageBytes, stripMetadata: false, _currentGenImagePath);
            };
            flyout.Items.Add(saveAsItem);

            var saveAsStrippedItem = new MenuFlyoutItem
            {
                Text = L("menu.file.save_as_stripped"),
                Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE792" },
                IsEnabled = hasImage,
            };
            saveAsStrippedItem.Click += async (_, _) =>
            {
                if (_currentGenImageBytes != null)
                    await SaveImageBytesAsAsync(_currentGenImageBytes, stripMetadata: true, _currentGenImagePath);
            };
            flyout.Items.Add(saveAsStrippedItem);

            flyout.Items.Add(new MenuFlyoutSeparator());

            var readerItem = new MenuFlyoutItem
            {
                Text = L("action.send_to_inspect"),
                Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uEE6F" },
                IsEnabled = hasImage,
            };
            readerItem.Click += async (_, _) =>
            {
                if (_currentGenImageBytes == null) return;
                SwitchMode(AppMode.Inspect);
                await LoadInspectImageFromBytesAsync(_currentGenImageBytes,
                    _currentGenImagePath != null ? Path.GetFileName(_currentGenImagePath) : null);
            };
            flyout.Items.Add(readerItem);

            var postItem = new MenuFlyoutItem
            {
                Text = L("action.send_to_post"),
                Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uEB3C" },
                IsEnabled = hasImage,
            };
            postItem.Click += async (_, _) =>
            {
                if (_currentGenImageBytes == null) return;
                await SendBytesToEffectsAsync(_currentGenImageBytes, _currentGenImagePath);
            };
            flyout.Items.Add(postItem);

            var i2iItem = new MenuFlyoutItem
            {
                Text = L("action.send_to_i2i"),
                Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uEDFB" },
                IsEnabled = hasImage,
            };
            i2iItem.Click += (_, _) =>
            {
                if (_currentGenImageBytes != null) SendImageToI2I(_currentGenImageBytes, _currentGenImagePath);
            };
            flyout.Items.Add(i2iItem);

            var upscaleItem = new MenuFlyoutItem
            {
                Text = L("action.send_to_upscale"),
                Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uECE9" },
                IsEnabled = hasImage,
            };
            upscaleItem.Click += async (_, _) =>
            {
                if (_currentGenImageBytes == null) return;
                await SendBytesToUpscaleAsync(_currentGenImageBytes, _currentGenImagePath);
            };
            flyout.Items.Add(upscaleItem);

            if (!string.IsNullOrEmpty(_currentGenImagePath))
            {
                var folderItem = new MenuFlyoutItem
                {
                    Text = L("action.open_containing_folder"),
                    Icon = new SymbolIcon(Symbol.OpenLocal),
                };
                folderItem.Click += (_, _) =>
                {
                    try
                    {
                        var dir = Path.GetDirectoryName(_currentGenImagePath);
                        if (dir != null) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_currentGenImagePath}\"");
                    }
                    catch { }
                };
                flyout.Items.Add(folderItem);
            }

            flyout.Items.Add(new MenuFlyoutSeparator());

            var useParamsItem = new MenuFlyoutItem
            {
                Text = L("action.use_parameters"),
                Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE8B6" },
                IsEnabled = hasImage,
            };
            useParamsItem.Click += async (_, _) =>
            {
                if (_currentGenImageBytes != null)
                    await ApplyDroppedImageMetadata(_currentGenImageBytes, L("image.preview_label"));
            };
            flyout.Items.Add(useParamsItem);

            var useParamsNoSeedItem = new MenuFlyoutItem
            {
                Text = L("action.use_parameters_no_seed"),
                Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE8B5" },
                IsEnabled = hasImage,
            };
            useParamsNoSeedItem.Click += async (_, _) =>
            {
                if (_currentGenImageBytes != null)
                    await ApplyDroppedImageMetadata(_currentGenImageBytes, L("image.preview_label"), skipSeed: true);
            };
            flyout.Items.Add(useParamsNoSeedItem);

            var useSeedItem = new MenuFlyoutItem
            {
                Text = L("action.use_seed"),
                Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uF0B9" },
                IsEnabled = hasImage,
            };
            useSeedItem.Click += async (_, _) =>
            {
                if (_currentGenImageBytes != null)
                    await ApplyImageSeedToGenerationAsync(_currentGenImageBytes, L("image.preview_label"));
            };
            flyout.Items.Add(useSeedItem);

            flyout.Items.Add(new MenuFlyoutSeparator());

            var deleteItem = new MenuFlyoutItem
            {
                Text = L("common.delete"),
                Icon = new SymbolIcon(Symbol.Delete),
                IsEnabled = hasImage,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 196, 43, 28)),
            };
            deleteItem.Click += OnDeleteGenResult;
            flyout.Items.Add(deleteItem);

            foreach (var item in flyout.Items)
                ApplyMenuTypography(item);
        };
        GenPreviewArea.ContextFlyout = flyout;
    }

    private void OnGenPreviewDragOver(object sender, DragEventArgs e)
    {
        TryAcceptImageFileDrag(e);
    }

    private async void OnGenPreviewDrop(object sender, DragEventArgs e)
    {
        var file = await GetFirstDroppedImageFileAsync(e, includeBmp: false);
        if (file == null)
            return;

        var bytes = await File.ReadAllBytesAsync(file.Path);
        await ApplyDroppedImageMetadata(bytes, file.Name, promptForOptions: true);
    }

    private async Task ApplyDroppedImageMetadata(byte[] bytes, string fileName, bool skipSeed = false, bool promptForOptions = false)
    {
        using var diagnostic = BeginUiBusinessLog("metadata_read", new { fileName });
        var meta = await Task.Run(() => ImageMetadataService.ReadFromBytes(bytes));
        diagnostic.Stage("import_raw_info", new { meta?.RawJson, meta });
        if (meta == null || (!meta.IsNaiParsed && !(promptForOptions && meta.IsSdFormat)))
        {
            diagnostic.Stage("import_parse_failed", new { fileName });
            TxtStatus.Text = Lf("metadata.drop_no_nai_data", fileName);
            return;
        }

        if (promptForOptions)
        {
            await ImportMetadataAsync(meta, fileName: fileName);
            return;
        }

        var selection = new MetadataImportSelection
        {
            Seed = !skipSeed,
            References = meta.VibeTransfers.Count > 0 || meta.PreciseReferences.Count > 0,
        };
        ApplyMetadataToGeneration(meta, selection);
        if (skipSeed) TxtStatus.Text += "; " + L("metadata.note.seed_skipped");
        RememberBusinessImport("generation", diagnostic, meta, fileName, selection);
        diagnostic.Stage("import_applied", CaptureBusinessInputs());
    }

    private async Task ApplyImageSeedToGenerationAsync(byte[] bytes, string fileName)
    {
        var meta = await Task.Run(() => ImageMetadataService.ReadFromBytes(bytes));
        if (meta == null || string.IsNullOrEmpty(meta.Seed))
        {
            TxtStatus.Text = Lf("metadata.no_usable_seed", fileName);
            return;
        }

        string seed = meta.Seed;
        _settings.Settings.GenParameters.Seed = seed;
        NbSeed.Value = seed;
        if (IsAdvancedWindowOpen) SyncSidebarToAdvanced();
        UpdateSeedRandomizeButtonStyle();

        TxtStatus.Text = Lf("metadata.seed_applied", fileName, seed);
    }

    // ═══════════════════════════════════════════════════════════
    //  发送到重绘
    // ═══════════════════════════════════════════════════════════
}
