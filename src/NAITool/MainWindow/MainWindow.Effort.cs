using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAITool.Services;

namespace NAITool;

public sealed partial class MainWindow
{
    private async void NotifySettingsRecovery()
    {
        try
        {
            await new ContentDialog
        {
            Title = L("settings.recovery.title"),
            Content = L("settings.recovery.notice"),
            CloseButtonText = L("common.ok"),
            XamlRoot = Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)Content).RequestedTheme,
        }.ShowAsync();
        }
        catch (Exception) { TxtStatus.Text = L("settings.recovery.notice"); }
    }

    private void RefreshUcPresetOptions(NAIParameters p)
    {
        if (_advCboUcPreset == null) return;
        string selected = ModelCapabilities.IsMedium(p) ? "heavy" : NovelAiPromptPresets.ResolveUcId(p);
        _advCboUcPreset.Items.Clear();
        foreach (string id in NovelAiPromptPresets.UcIds(p.Model))
        {
            string key = id == "heavy" ? "full" : id;
            var item = CreateTextComboBoxItem(L("dialog.advanced.uc_preset." + key));
            item.Tag = id;
            _advCboUcPreset.Items.Add(item);
            if (id == selected) _advCboUcPreset.SelectedItem = item;
        }
        if (_advCboUcPreset.SelectedIndex < 0) _advCboUcPreset.SelectedIndex = 0;
    }

    private void OnEffortChanged(object sender, RoutedEventArgs e)
    {
        if (IsAdvancedWindowOpen) SaveAdvancedPanelToSettings();
        SaveCurrentPromptToBuffer();
        SyncUIToParams();
        CurrentParams.Effort = ReferenceEquals(sender, EffortMedium) ? "medium" : "high";
        UpdateEffortUI();
        RefreshCharacterPanel();
        UpdateGenerateButtonWarning();
        _settings.Save();
    }

    private void UpdateEffortUI()
    {
        if (EffortPanel == null || TabNegative == null) return;
        var p = CurrentParams;
        bool medium = ModelCapabilities.IsMedium(p);
        string selectedModel = GetCurrentModelKey();
        bool modelSelectionMatches = selectedModel == p.Model;
        medium = medium && modelSelectionMatches;
        EffortPanel.Visibility = ModelCapabilities.SupportsEffort(selectedModel) ? Visibility.Visible : Visibility.Collapsed;
        EffortLabel.Text = L("panel.effort");
        ToolTipService.SetToolTip(EffortLabel, L("panel.effort.hint"));
        EffortHigh.IsChecked = !medium;
        EffortMedium.IsChecked = medium;
        if (medium && !_isPositiveTab)
        {
            // Save the High negative draft before showing the positive editor.
            SaveCurrentPromptToBuffer();
            TabPositive.IsChecked = true;
            OnPromptTabSwitch(TabPositive, new RoutedEventArgs());
        }
        TabNegative.IsEnabled = !medium;
        ToolTipService.SetToolTip(TabNegative, medium ? L("panel.effort.medium_uc_hint") : null);
        if (!IsAdvancedWindowOpen) return;
        bool wasSyncing = _isSyncingSidebarAdv;
        _isSyncingSidebarAdv = true;
        try
        {
            var effective = ModelCapabilities.CreateRequestSnapshot(p);
            _advCboQuality.SelectedIndex = !effective.QualityToggle ? 2 : effective.QualityPreset == "light" ? 1 : 0;
            if (_advCboQuality.Items.Count > 1 && _advCboQuality.Items[1] is ComboBoxItem lightQuality)
                lightQuality.IsEnabled = NovelAiPromptPresets.SupportsLightQuality(p.Model);
            _advNbSteps.Value = effective.Steps;
            _advNbSteps.IsEnabled = !medium;
            _advCboSampler.SelectedIndex = Array.IndexOf(GetAvailableSamplersForModel(p.Model), effective.Sampler);
            _advCboSampler.IsEnabled = !medium;
            RefreshUcPresetOptions(p);
            _advCboUcPreset.IsEnabled = !medium;
            _advSliderCfgRescale.Value = effective.CfgRescale;
            _advSliderCfgRescale.IsEnabled = !medium;
            _advNbScale.IsEnabled = true;
        }
        finally { _isSyncingSidebarAdv = wasSyncing; }
    }
}
