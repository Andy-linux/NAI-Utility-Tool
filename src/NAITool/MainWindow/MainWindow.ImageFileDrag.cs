using Microsoft.UI.Xaml;
using NAITool.Services;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;
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
    private SoftwareBitmap? _imageFileDragPreview;

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
            string fileName = "image";
            byte[]? bytes = null;
            if (sender is FrameworkElement { Tag: string historyPath })
                path = historyPath;
            else if (ReferenceEquals(sender, GenPreviewImage))
            {
                fileName = Path.GetFileNameWithoutExtension(_currentGenImagePath) ?? "image";
                bytes = _currentGenImageBytes;
                if (bytes != null && IsPngFilePath(_currentGenImagePath ?? ""))
                    bytes = await Task.Run(() => EnsurePngEncoded(bytes));
                if (bytes == null) path = _currentGenImagePath;
            }
            else if (ReferenceEquals(sender, InspectPreviewImage))
            {
                fileName = Path.GetFileNameWithoutExtension(_inspectImagePath) ?? "image";
                bytes = await GetInspectSaveBytesAsync(stripMetadata: false);
            }

            if (path == null || !File.Exists(path))
            {
                if (bytes == null)
                {
                    args.Cancel = true;
                    return;
                }

                // Export the captured image bytes, including unsaved Info edits, as an OLE file.
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
                path = Path.Combine(directory, fileName + extension);
                if (!File.Exists(path))
                    await File.WriteAllBytesAsync(path, bytes);
            }

            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
            args.Data.SetStorageItems(new[] { file }, readOnly: true);
            // Only the drag cursor uses this 160 px thumbnail; the transferred file stays full size.
            var pixels = await HistoryThumbnailService.DecodeAsync(path, 160, 160, CancellationToken.None);
            _imageFileDragPreview = SoftwareBitmap.CreateCopyFromBuffer(pixels.Pixels.AsBuffer(),
                BitmapPixelFormat.Bgra8, pixels.Width, pixels.Height, BitmapAlphaMode.Premultiplied);
            args.DragUI.SetContentFromSoftwareBitmap(_imageFileDragPreview, new Point(12, 12));
            args.Data.RequestedOperation = DataPackageOperation.Copy;
            args.AllowedOperations = DataPackageOperation.Copy;
            _imageFileDragInProgress = true;
        }
        catch (Exception ex)
        {
            args.Cancel = true;
            _imageFileDragPreview?.Dispose();
            _imageFileDragPreview = null;
            TxtStatus.Text = Lf("image.drag.failed", ex.Message);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnImageFileDropCompleted(UIElement sender, DropCompletedEventArgs args)
    {
        _imageFileDragInProgress = false;
        _imageFileDragPreview?.Dispose();
        _imageFileDragPreview = null;
    }
}
