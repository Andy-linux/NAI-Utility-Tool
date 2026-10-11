using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;

namespace NAITool.Mobile;

public sealed partial class MainPage
{
    private static string GuessImageSuffix(byte[] bytes)
    {
        if (bytes.Length >= 12 && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' &&
            bytes[2] == (byte)'F' && bytes[3] == (byte)'F' &&
            bytes[8] == (byte)'W' && bytes[9] == (byte)'E')
            return ".webp";
        if (bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff)
            return ".jpg";
        return ".png";
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_resultBytes == null) return;
        try
        {
#if __IOS__
            // Uno documents iOS FileSavePicker as partial; the system Files export needs
            // its own folder/share adapter, not a fake success state.
            StatusText.Text = "iOS Files 分享正在适配。当前结果可先保存到应用历史。";
#else
            var ext = GuessImageSuffix(_resultBytes);
            var picker = new FileSavePicker
            {
                SuggestedFileName = "NAI_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"),
            };
            picker.FileTypeChoices.Add(ext switch
            {
                ".webp" => "WebP 图片",
                ".jpg" => "JPEG 图片",
                _ => "PNG 图片",
            }, new List<string> { ext });
            var file = await picker.PickSaveFileAsync();
            if (file == null) return;
            using var stream = await file.OpenStreamForWriteAsync();
            stream.SetLength(0);
            await stream.WriteAsync(_resultBytes);
            await stream.FlushAsync();
            StatusText.Text = "已导出到所选位置：" + file.Name;
#endif
        }
        catch (Exception error)
        {
            StatusText.Text = "导出失败：" + error.Message;
        }
    }
}
