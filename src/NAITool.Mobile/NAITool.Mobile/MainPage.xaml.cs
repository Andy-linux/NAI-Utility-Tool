using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using NAITool.Mobile.Core;
using NAITool.Services;
using Windows.Storage.Streams;

namespace NAITool.Mobile;

public sealed partial class MainPage : Page
{
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
    private MobileGenerationClient _client = null!;
    private CancellationTokenSource? _generationCancellation;
    private byte[]? _resultBytes;
    private bool _ready;
    private bool _wide;
    private bool _applyingEffort;
    private bool _wasMedium;
    private int _highSteps = 23;
    private int _highSamplerIndex;
    private Tab _tab = Tab.Parameters;

    private enum Tab { Parameters, Preview, Inpaint, History }
    private sealed record HistoryEntry(string FilePath)
    {
        public override string ToString() => Path.GetFileName(FilePath);
    }

    private static string HistoryDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NAITool.Mobile", "history");

    public MainPage()
    {
        InitializeComponent();
        _client = new MobileGenerationClient(_http);
        _ready = true;
        UpdateCharacterLimitHint();
        UpdatePaidAnlasHint();
        UpdateEffortMode();
        UpdateResponsiveLayout();
    }

    private static string TagOf(ComboBox box, string fallback = "") =>
        (box.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_ready) return;
        _wide = e.NewSize.Width >= 840;
        UpdateResponsiveLayout();
    }

    private void UpdateResponsiveLayout()
    {
        if (!_ready) return;
        WorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(_wide ? 11 : 1, GridUnitType.Star);
        WorkspaceGrid.ColumnDefinitions[1].Width = new GridLength(_wide ? 10 : 0, GridUnitType.Star);
        Grid.SetColumn(ParametersPane, 0);
        Grid.SetColumn(PreviewPane, _wide ? 1 : 0);
        ParametersPane.Margin = _wide ? new Thickness(0, 0, 6, 0) : new Thickness(0);
        PreviewPane.Margin = _wide ? new Thickness(6, 0, 0, 0) : new Thickness(0);

        bool history = _tab == Tab.History;
        bool inpaint = _tab == Tab.Inpaint;
        WorkspaceGrid.Visibility = history || inpaint ? Visibility.Collapsed : Visibility.Visible;
        HistoryPane.Visibility = history ? Visibility.Visible : Visibility.Collapsed;
        InpaintPane.Visibility = inpaint ? Visibility.Visible : Visibility.Collapsed;
        ParametersPane.Visibility = !history && !inpaint && (_wide || _tab == Tab.Parameters) ? Visibility.Visible : Visibility.Collapsed;
        PreviewPane.Visibility = !history && !inpaint && (_wide || _tab == Tab.Preview) ? Visibility.Visible : Visibility.Collapsed;
        ParametersTab.IsEnabled = _tab != Tab.Parameters;
        PreviewTab.IsEnabled = _tab != Tab.Preview;
        InpaintTab.IsEnabled = !inpaint;
        HistoryTab.IsEnabled = !history;
        GenerateButton.Content = inpaint ? "开始重绘" : "开始生成";
    }

    private void SwitchTo(Tab tab)
    {
        _tab = tab;
        UpdateResponsiveLayout();
    }

    private void OnParametersTab(object sender, RoutedEventArgs e) => SwitchTo(Tab.Parameters);
    private void OnPreviewTab(object sender, RoutedEventArgs e) => SwitchTo(Tab.Preview);

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        bool expanded = ConnectionPanel.Visibility != Visibility.Visible;
        ConnectionPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        SwitchTo(Tab.Parameters);
        StatusText.Text = expanded ? "设置自定义 API Root 与 API Key；密钥不会写入本地配置。" : "已收起连接设置。";
    }

    private void OnHistoryTab(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Directory.Exists(HistoryDirectory))
            {
                HistoryList.ItemsSource = Array.Empty<HistoryEntry>();
            }
            else
            {
                HistoryList.ItemsSource = Directory.EnumerateFiles(HistoryDirectory)
                    .Where(path => path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                   path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
                                   path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(Path.GetFileName)
                    .Take(100)
                    .Select(path => new HistoryEntry(path))
                    .ToArray();
            }
            SwitchTo(Tab.History);
        }
        catch (Exception error)
        {
            StatusText.Text = "无法读取本地历史：" + error.Message;
        }
    }

    private async void OnHistorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryEntry item) return;
        try
        {
            var bytes = await File.ReadAllBytesAsync(item.FilePath);
            await ShowImageAsync(bytes);
            _resultBytes = bytes;
            SaveButton.IsEnabled = false;
            UseResultAsInpaintButton.IsEnabled = true;
            ExportButton.IsEnabled = true;
            ResultInfo.Text = item.ToString();
            SwitchTo(Tab.Preview);
        }
        catch (Exception error)
        {
            StatusText.Text = "无法打开历史图片：" + error.Message;
        }
        finally { HistoryList.SelectedItem = null; }
    }

    private void OnModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        bool isV5 = TagOf(ModelCombo).StartsWith("nai-diffusion-5", StringComparison.Ordinal);
        if (!isV5 && TagOf(QualityCombo) == "light") QualityCombo.SelectedIndex = 0;
        if (!isV5 && TagOf(EffortCombo) == "medium") EffortCombo.SelectedIndex = 0;
        UpdateEffortMode();
        UpdateCharacterLimitHint();
        UpdatePaidAnlasHint();
    }

    private void OnEffortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) { UpdateEffortMode(); UpdatePaidAnlasHint(); }
    }

    private void UpdateEffortMode()
    {
        if (!_ready || _applyingEffort) return;
        _applyingEffort = true;
        try
        {
            bool supportsEffort = TagOf(ModelCombo) == "nai-diffusion-5-full";
            EffortCombo.IsEnabled = supportsEffort;
            bool medium = supportsEffort && TagOf(EffortCombo) == "medium";
            if (medium && !_wasMedium)
            {
                _highSteps = (int)Math.Round(StepsSlider.Value);
                _highSamplerIndex = SamplerCombo.SelectedIndex;
            }
            if (!medium && _wasMedium)
            {
                StepsSlider.Value = _highSteps;
                SamplerCombo.SelectedIndex = _highSamplerIndex;
            }
            _wasMedium = medium;
            if (medium)
            {
                StepsSlider.Value = 14;
                SamplerCombo.SelectedIndex = 0;
                ScheduleCombo.SelectedIndex = 0;
            }
            StepsSlider.IsEnabled = !medium;
            SamplerCombo.IsEnabled = !medium;
            ScheduleCombo.IsEnabled = !medium;
            NegativePromptBox.IsEnabled = !medium;
            CharacterNegativeBox.IsEnabled = !medium;
            foreach (var character in _additionalCharacters) character.Negative.IsEnabled = !medium;
            UcCombo.IsEnabled = !medium;
            EffortHint.Visibility = medium ? Visibility.Visible : Visibility.Collapsed;
            StepsLabel.Text = $"Steps · {(int)Math.Round(StepsSlider.Value)}";
        }
        finally { _applyingEffort = false; }
    }

    private void OnStepsChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_ready) { StepsLabel.Text = $"Steps · {(int)Math.Round(e.NewValue)}"; UpdatePaidAnlasHint(); }
    }

    private void OnScaleChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_ready) ScaleLabel.Text = $"CFG Scale · {e.NewValue:0.0}";
    }

    private MobileGenerationClient.Request BuildRequest()
    {
        string[] dims = TagOf(SizeCombo, "832x1216").Split('x');
        int width = int.Parse(dims[0]);
        int height = int.Parse(dims[1]);
        string model = TagOf(ModelCombo, NAIParameters.DefaultGenerationModel);
        string quality = TagOf(QualityCombo, "standard");

        var parameters = new NAIParameters
        {
            Model = model,
            Sampler = TagOf(SamplerCombo, "k_euler_ancestral"),
            Schedule = TagOf(ScheduleCombo, "karras"),
            Steps = (int)Math.Round(StepsSlider.Value),
            Scale = ScaleSlider.Value,
            Seed = SeedBox.Text,
            Effort = TagOf(EffortCombo, "high"),
            QualityToggle = quality != "none",
            QualityPreset = quality == "none" ? "standard" : quality,
            UcPresetId = TagOf(UcCombo, "heavy"),
        };

        string prompt = PromptBox.Text.Trim();
        string style = StylePromptBox.Text.Trim();
        if (style.Length > 0)
            prompt = prompt.Length == 0 ? style : prompt + ", " + style;

        var characters = ReadCharacters(model);

        return new MobileGenerationClient.Request(
            ApiRootBox.Text.Trim(), ApiTokenBox.Password, prompt, NegativePromptBox.Text.Trim(),
            parameters, width, height, characters, StreamBox.IsChecked == true);
    }

    private async void OnGenerateClick(object sender, RoutedEventArgs e)
    {
        if (_generationCancellation != null) return;
        MobileGenerationClient.Request request;
        try
        {
            request = BuildRequest();
            if (request.Prompt.Length == 0)
            {
                StatusText.Text = "请先输入正面提示词。";
                SwitchTo(Tab.Parameters);
                return;
            }
            if (string.IsNullOrWhiteSpace(request.ApiToken))
            {
                StatusText.Text = "请先在 API 设置中输入 API Key。";
                ConnectionPanel.Visibility = Visibility.Visible;
                SwitchTo(Tab.Parameters);
                return;
            }
            if (_tab == Tab.Inpaint)
                request = PrepareInpaintRequest(request);
            // Resolve and serialize before any network request.
            _ = MobileGenerationClient.ResolveUrl(request.ApiRoot, request.Stream);
            _ = MobileGenerationClient.BuildRequestJson(request);
        }
        catch (Exception error)
        {
            StatusText.Text = "参数有误：" + error.Message;
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _generationCancellation = cancellation;
        GenerateButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        SaveButton.IsEnabled = false;
        ExportButton.IsEnabled = false;
        UseResultAsInpaintButton.IsEnabled = false;
        _resultBytes = null;
        ResultInfo.Text = request.Stream ? "等待流式预览…" : "等待生成结果…";
        StatusText.Text = "正在生成…";
        try
        {
            var previews = new Progress<byte[]>(bytes =>
            {
                _ = ShowIntermediateAsync(bytes);
            });
            var bytes = await _client.GenerateAsync(request, previews, cancellation.Token);
            // The final result alone is available for saving.
            _resultBytes = bytes;
            await ShowImageAsync(bytes);
            SaveButton.IsEnabled = true;
            ExportButton.IsEnabled = true;
            UseResultAsInpaintButton.IsEnabled = true;
            ResultInfo.Text = $"{request.Width} × {request.Height} · {bytes.Length / 1024.0:0.#} KiB · 未保存";
            StatusText.Text = request.InpaintImageBase64 == null
                ? "生成完成；可以保存、导出或发送到重绘。"
                : "重绘完成；可以保存、导出或继续编辑。";
            SwitchTo(Tab.Preview);
        }
        catch (OperationCanceledException)
        {
            _resultBytes = null;
            SaveButton.IsEnabled = false;
            ExportButton.IsEnabled = false;
            UseResultAsInpaintButton.IsEnabled = false;
            StatusText.Text = "已取消等待；已发送请求的计费状态可能未知。";
            ResultInfo.Text = "没有收到完整的 final 结果。";
        }
        catch (Exception error)
        {
            _resultBytes = null;
            SaveButton.IsEnabled = false;
            ExportButton.IsEnabled = false;
            UseResultAsInpaintButton.IsEnabled = false;
            StatusText.Text = "生成失败：" + error.Message;
            ResultInfo.Text = "未获得完整结果，不能保存流式中间帧。";
        }
        finally
        {
            _generationCancellation = null;
            GenerateButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
        }
    }

    private async Task ShowIntermediateAsync(byte[] bytes)
    {
        try
        {
            if (_generationCancellation == null || _resultBytes != null) return;
            var decoded = await DecodeImageAsync(bytes);
            if (_generationCancellation == null || _resultBytes != null) return;
            ResultImage.Source = decoded;
            ResultImage.Visibility = Visibility.Visible;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            ResultInfo.Text = "流式中间帧（不能保存，等待 final）";
        }
        catch
        {
            // A preview decode failure is not a successful final image.
        }
    }

    private static async Task<BitmapImage> DecodeImageAsync(byte[] bytes)
    {
        using var stream = new InMemoryRandomAccessStream();
        using var writer = new DataWriter(stream);
        writer.WriteBytes(bytes);
        await writer.StoreAsync();
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    private async Task ShowImageAsync(byte[] bytes)
    {
        ResultImage.Source = await DecodeImageAsync(bytes);
        ResultImage.Visibility = Visibility.Visible;
        PreviewPlaceholder.Visibility = Visibility.Collapsed;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => _generationCancellation?.Cancel();

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_resultBytes == null) return;
        try
        {
            Directory.CreateDirectory(HistoryDirectory);
            string suffix = GuessImageSuffix(_resultBytes);
            string fileName = DateTime.Now.ToString("yyyyMMdd_HHmmssfff") + suffix;
            string filePath = Path.Combine(HistoryDirectory, fileName);
            await File.WriteAllBytesAsync(filePath, _resultBytes);
            SaveButton.IsEnabled = false;
            ResultInfo.Text = "已保存至应用私有目录：" + fileName;
            StatusText.Text = "已写入本地历史（尚未导出到系统相册）。";
        }
        catch (Exception error)
        {
            StatusText.Text = "保存失败：" + error.Message;
        }
    }
}
