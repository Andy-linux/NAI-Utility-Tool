using System;
using System.Collections.Generic;
using NAITool.Services;

namespace NAITool;

public sealed partial class MainWindow
{
    private List<RememberedVibeState> CreateRememberedVibes(List<VibeTransferEntry> entries)
    {
        var result = new List<RememberedVibeState>();
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.ImageBase64)) continue;
            try
            {
                result.Add(new RememberedVibeState
                {
                    FileName = entry.FileName,
                    ImageDataKey = ReferenceDraftStore.Save(AppRootDir, entry.ImageBase64),
                    OriginalDataKey = string.IsNullOrWhiteSpace(entry.OriginalImageBase64)
                        ? null : ReferenceDraftStore.Save(AppRootDir, entry.OriginalImageBase64),
                    OriginalImageHash = entry.OriginalImageHash,
                    OriginalThumbnailHash = entry.OriginalThumbnailHash,
                    OriginalImagePath = entry.OriginalImagePath,
                    EncodingModel = entry.EncodingModel,
                    Strength = entry.Strength,
                    InformationExtracted = entry.InformationExtracted,
                    HasInformationExtracted = entry.HasInformationExtracted,
                    IsEncodedFile = entry.IsEncodedFile,
                    IsCachedEncoding = entry.IsCachedEncoding,
                    IsDisabled = entry.IsDisabled,
                    IsCollapsed = entry.IsCollapsed,
                });
            }
            catch (Exception ex) { DebugLog($"[Vibe] Cannot remember reference {entry.FileName}: {ex.Message}"); }
        }
        return result;
    }

    private void LoadRememberedVibes(List<VibeTransferEntry> target, List<RememberedVibeState>? saved)
    {
        target.Clear();
        if (saved == null) return;
        foreach (var item in saved)
        {
            if (item == null) continue;
            try
            {
                string? image = ReferenceDraftStore.Load(AppRootDir, item.ImageDataKey);
                if (image == null) continue;
                string? original = ReferenceDraftStore.Load(AppRootDir, item.OriginalDataKey);
                target.Add(new VibeTransferEntry
                {
                    FileName = item.FileName,
                    ImageBase64 = image,
                    OriginalImageBase64 = original,
                    OriginalImageHash = original == null ? item.OriginalImageHash
                        : VibeCacheService.ComputeImageHash(Convert.FromBase64String(original)),
                    OriginalThumbnailHash = item.OriginalThumbnailHash,
                    OriginalImagePath = item.OriginalImagePath,
                    EncodingModel = item.EncodingModel,
                    Strength = Math.Clamp(item.Strength, 0, 1),
                    InformationExtracted = Math.Clamp(item.InformationExtracted, 0, 1),
                    HasInformationExtracted = item.HasInformationExtracted,
                    IsEncodedFile = item.IsEncodedFile,
                    IsCachedEncoding = item.IsCachedEncoding,
                    IsDisabled = item.IsDisabled,
                    IsCollapsed = item.IsCollapsed,
                });
            }
            catch (Exception ex) { DebugLog($"[Vibe] Cannot restore reference {item.FileName}: {ex.Message}"); }
        }
    }
}
