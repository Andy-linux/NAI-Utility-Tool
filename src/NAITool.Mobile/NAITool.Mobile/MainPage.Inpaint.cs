using System;
using System.IO;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using NAITool.Mobile.Core;
using NAITool.Services;
using Windows.Storage.Pickers;

namespace NAITool.Mobile;

public sealed partial class MainPage
{
    private void OnInpaintTab(object sender, RoutedEventArgs e) => SwitchTo(Tab.Inpaint);

    private void OnUseResultAsInpaint(object sender, RoutedEventArgs e)
    {
        if (_resultBytes == null)
        {
            StatusText.Text = "请先生成或选择一张历史图片，或点击“导入图片”。";
            SwitchTo(Tab.Inpaint);
            return;
        }
        OpenInpaintEditor(_resultBytes);
    }

    private void OpenInpaintEditor(byte[] imageBytes)
    {
        try
        {
            // The existing selected NAI size is the fixed authority for composite and mask bytes.
            var dims = TagOf(SizeCombo, "832x1216").Split('x');
            MaskEditor.LoadImage(imageBytes, int.Parse(dims[0]), int.Parse(dims[1]));
            EraserBox.IsChecked = false;
            MaskEditor.Eraser = false;
            MaskEditor.Radius = (float)BrushSlider.Value;
            MaskEditor.Visibility = Visibility.Visible;
            MaskPlaceholder.Visibility = Visibility.Collapsed;
            MaskStatus.Text = $"底图已加载 · {MaskEditor.CanvasWidth} × {MaskEditor.CanvasHeight} · 在要修改的区域涂抹";
            SwitchTo(Tab.Inpaint);
        }
        catch (Exception ex)
        {
            StatusText.Text = "导入重绘图片失败：" + ex.Message;
        }
    }

    private async void OnLoadInpaintImage(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".webp");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;
            using var stream = await file.OpenStreamForReadAsync();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            if (buffer.Length > 64L * 1024 * 1024)
                throw new InvalidDataException("图片超过 64MB，请先压缩后导入。");
            OpenInpaintEditor(buffer.ToArray());
        }
        catch (Exception ex)
        {
            StatusText.Text = "选择图片失败：" + ex.Message;
        }
    }

    private void OnEraserChanged(object sender, RoutedEventArgs e)
    {
        MaskEditor.Eraser = EraserBox.IsChecked == true;
    }

    private void OnBrushChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (MaskEditor != null) MaskEditor.Radius = (float)e.NewValue;
    }

    private void OnMaskUndo(object sender, RoutedEventArgs e)
    {
        MaskEditor.Undo();
        MaskStatus.Text = "已撤销一笔";
    }

    private void OnMaskRedo(object sender, RoutedEventArgs e)
    {
        MaskEditor.Redo();
        MaskStatus.Text = "已重做一笔";
    }

    private void OnMaskClear(object sender, RoutedEventArgs e)
    {
        MaskEditor.Clear();
        MaskStatus.Text = "遮罩已清空";
    }

    private MobileGenerationClient.Request PrepareInpaintRequest(MobileGenerationClient.Request request)
    {
        if (!MaskEditor.HasImage || !MaskEditor.HasMask)
            throw new InvalidOperationException("请先导入图片并绘制有效遮罩。");
        var (imageWebp, maskWebp) = MaskEditor.ExportWebp();
        var settings = JsonSerializer.Deserialize<NAIParameters>(JsonSerializer.Serialize(request.Parameters))
            ?? throw new InvalidOperationException("无法复制生成参数。");
        if (!settings.Model.EndsWith("-inpainting", StringComparison.Ordinal))
            settings.Model += "-inpainting";
        settings.InpaintStrength = InpaintStrengthSlider.Value;
        settings.InpaintNoise = InpaintNoiseSlider.Value;
        return request with
        {
            Parameters = settings,
            Width = MaskEditor.CanvasWidth,
            Height = MaskEditor.CanvasHeight,
            InpaintImageBase64 = imageWebp,
            InpaintMaskBase64 = maskWebp,
        };
    }
}
