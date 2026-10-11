using System;
using Microsoft.UI.Xaml.Controls;
using NAITool.Mobile.Core;

namespace NAITool.Mobile;

public sealed partial class MainPage
{
    private void OnSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) UpdatePaidAnlasHint();
    }

    private void UpdatePaidAnlasHint()
    {
        if (!_ready) return;
        try
        {
            var request = BuildRequest();
            int cost = MobileGenerationClient.EstimatePaidAnlas(
                request.Parameters, request.Width, request.Height);
            PaidAnlasHint.Text = $"付费基价参考：{cost} Anlas · Opus/V5 免费额度另计";
        }
        catch
        {
            PaidAnlasHint.Text = "请检查生成尺寸和参数";
        }
    }
}