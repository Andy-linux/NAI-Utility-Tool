using Microsoft.UI.Xaml;
using System;
using System.IO;
using System.Security.Cryptography;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace NAITool;

public sealed partial class MainWindow
{
    private bool _imageFileDragInProgress;

    private static bool IsImageDragModifierDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private async void OnImageFileDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (IsImageDragModifierDown(VirtualKey.Space) || IsImageDragModifierDown(VirtualKey.Control) ||
            ReferenceEquals(sender, GenPreviewImage) && _generateRequestRunning)
        {
            args.Cancel = true;
            return;
        }

        var deferral = args.GetDeferral();
        try
        {
            StopPreviewDrag();
            string? path = null;
            byte[]? bytes = null;
            if (sender is FrameworkElement { Tag: string historyPath })
                path = historyPath;
            else if (ReferenceEquals(sender, GenPreviewImage))
            {
                path = _currentGenImagePath;
                bytes = _currentGenImageBytes;
            }
            else if (ReferenceEquals(sender, InspectPreviewImage))
            {
                path = _inspectRawModified ? null : _inspectImagePath;
                if (path == null || !File.Exists(path))
                    bytes = await GetInspectSaveBytesAsync(stripMetadata: false);
            }

            if (path == null || !File.Exists(path))
            {
                if (bytes == null)
                {
                    args.Cancel = true;
                    return;
                }

                // A byte snapshot also covers unsaved Info edits, without overwriting the source.
                string directory = Path.Combine(AppRootDir, "temp", "drag",
                    Convert.ToHexString(SHA256.HashData(bytes)));
                Directory.CreateDirectory(directory);
                using var codec = SkiaSharp.SKCodec.Create(new MemoryStream(bytes, writable: false));
                string extension = codec?.EncodedFormat switch
                {
                    SkiaSharp.SKEncodedImageFormat.Png => ".png",
                    SkiaSharp.SKEncodedImageFormat.Webp => ".webp",
                    SkiaSharp.SKEncodedImageFormat.Jpeg => ".jpg",
                    SkiaSharp.SKEncodedImageFormat.Bmp => ".bmp",
                    SkiaSharp.SKEncodedImageFormat.Gif => ".gif",
                    _ => throw new InvalidDataException(L("common.unsupported_file_format_inspect")),
                };
                path = Path.Combine(directory, "image" + extension);
                if (!File.Exists(path))
                    await File.WriteAllBytesAsync(path, bytes);
            }

            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
            args.Data.SetStorageItems(new[] { file }, readOnly: true);
            args.Data.RequestedOperation = DataPackageOperation.Copy;
            args.AllowedOperations = DataPackageOperation.Copy;
            _imageFileDragInProgress = true;
        }
        catch (Exception ex)
        {
            args.Cancel = true;
            TxtStatus.Text = Lf("image.drag.failed", ex.Message);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnImageFileDropCompleted(UIElement sender, DropCompletedEventArgs args) =>
        _imageFileDragInProgress = false;
}
