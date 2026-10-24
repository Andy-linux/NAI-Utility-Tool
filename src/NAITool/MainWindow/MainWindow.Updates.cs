using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAITool.Services;

namespace NAITool;

public sealed partial class MainWindow
{
    private bool _updateDialogOpen;
    private string? _pendingUpdateInstaller;

    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        if (_updateDialogOpen) return;
        _updateDialogOpen = true;
        try { await ShowClientUpdatesAsync(); }
        catch (Exception ex) { TxtStatus.Text = Lf("update.failed", ex.Message); }
        finally { _updateDialogOpen = false; }
    }

    private async Task ShowClientUpdatesAsync()
    {
        using var handler = new HttpClientHandler();
        if (_settings.Settings.UseProxy && !string.IsNullOrWhiteSpace(_settings.Settings.ProxyPort))
            handler.Proxy = new WebProxy("http://127.0.0.1:" + _settings.Settings.ProxyPort);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var service = new ReleaseUpdateService(client);
        using var lifetime = new CancellationTokenSource();
        CancellationTokenSource? download = null;
        Task? transfer = null;
        Task? check = null;
        string? installerPath = null;
        bool installRequested = false;
        var current = ReleaseUpdateService.ParseVersion(ReleaseUpdateService.CurrentVersion);
        var status = new TextBlock { Text = L("update.checking"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var versions = new ComboBox { Header = L("update.select_version"), HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = false };
        var details = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
        var notes = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed };
        var downloadButton = new Button { Content = L("update.download"), IsEnabled = false };
        var cancelButton = new Button { Content = L("update.cancel_download"), Visibility = Visibility.Collapsed };
        var refreshButton = new Button { Content = L("update.refresh") };
        var releaseLink = new HyperlinkButton { Content = L("update.release_page"), NavigateUri = new Uri(ReleaseUpdateService.ReleasesPage), Padding = new Thickness(0) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(downloadButton); actions.Children.Add(cancelButton); actions.Children.Add(refreshButton);
        var panel = new StackPanel { Spacing = 12, MinWidth = 380, MaxWidth = 560 };
        panel.Children.Add(new TextBlock { Text = Lf("update.current", ReleaseUpdateService.CurrentVersion), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = L("update.source"), TextWrapping = TextWrapping.Wrap, Opacity = 0.75 });
        panel.Children.Add(versions); panel.Children.Add(details);
        var notesScroll = new ScrollViewer { Content = notes, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(notesScroll);
        panel.Children.Add(status); panel.Children.Add(progress); panel.Children.Add(actions); panel.Children.Add(releaseLink);
        var dialog = new ContentDialog
        {
            Title = L("update.title"), Content = panel, PrimaryButtonText = L("update.install"),
            CloseButtonText = L("common.close"), IsPrimaryButtonEnabled = false,
            DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)Content).RequestedTheme,
        };

        dialog.SizeChanged += (_, _) => notesScroll.MaxHeight = Math.Clamp(Content.XamlRoot.Size.Height - 480, 60, 240);

        void SelectRelease()
        {
            installerPath = null;
            dialog.IsPrimaryButtonEnabled = false;
            if (versions.SelectedItem is not ClientRelease release) return;
            notes.Text = release.Notes;
            details.Text = Lf("update.details", release.PublishedAt.ToLocalTime().ToString("yyyy-MM-dd"), release.Installer.Size / 1048576.0);
            releaseLink.NavigateUri = release.PageUri;
            bool newer = current != null && release.Version > current;
            downloadButton.IsEnabled = newer;
            status.Text = L(newer ? "update.available" : "update.current_or_older");
            progress.Visibility = Visibility.Collapsed;
        }

        async Task RefreshAsync()
        {
            refreshButton.IsEnabled = false; versions.IsEnabled = false; downloadButton.IsEnabled = false;
            dialog.IsPrimaryButtonEnabled = false; installerPath = null; status.Text = L("update.checking");
            try
            {
                var releases = await service.GetReleasesAsync(lifetime.Token);
                if (lifetime.IsCancellationRequested) return;
                versions.ItemsSource = releases;
                versions.IsEnabled = releases.Count > 0;
                if (releases.Count > 0) { versions.SelectedIndex = 0; SelectRelease(); }
                else status.Text = L("update.no_releases");
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception ex) { status.Text = Lf("update.failed", ex.Message); }
            finally { refreshButton.IsEnabled = true; }
        }

        versions.SelectionChanged += (_, _) => SelectRelease();
        refreshButton.Click += (_, _) => check = RefreshAsync();
        cancelButton.Click += (_, _) => download?.Cancel();
        async Task DownloadAsync()
        {
            if (versions.SelectedItem is not ClientRelease release) return;
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            download = operation;
            versions.IsEnabled = false; refreshButton.IsEnabled = false; downloadButton.IsEnabled = false;
            cancelButton.Visibility = Visibility.Visible; progress.Visibility = Visibility.Visible; progress.Value = 0;
            status.Text = L("update.downloading");
            try
            {
                installerPath = await service.DownloadAsync(release, new Progress<double>(value => progress.Value = value), operation.Token);
                if (lifetime.IsCancellationRequested) return;
                status.Text = L("update.ready");
                dialog.IsPrimaryButtonEnabled = true;
            }
            catch (OperationCanceledException) { status.Text = L("update.cancelled"); }
            catch (Exception ex) { status.Text = Lf("update.failed", ex.Message); }
            finally
            {
                download = null; cancelButton.Visibility = Visibility.Collapsed;
                versions.IsEnabled = true; refreshButton.IsEnabled = true;
                downloadButton.IsEnabled = installerPath == null;
            }
        }
        downloadButton.Click += (_, _) => transfer = DownloadAsync();
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (_generateRequestRunning || _autoGenRunning || _continuousGenRunning || _upscaleRunning || _inspectInferenceRunning)
            {
                args.Cancel = true; status.Text = L("update.busy"); return;
            }
            installRequested = installerPath != null;
        };
        dialog.Opened += (_, _) => check = RefreshAsync();
        await dialog.ShowAsync();
        lifetime.Cancel();
        if (check != null) await check;
        if (transfer != null) await transfer;
        if (installRequested && installerPath != null)
        {
            _pendingUpdateInstaller = installerPath;
            Close();
        }
    }

    private void StartPendingUpdateInstaller(AppWindowClosingEventArgs args)
    {
        if (_pendingUpdateInstaller == null) return;
        try
        {
            if (!SaveClosingWorkspaceState())
            {
                args.Cancel = true;
                _pendingUpdateInstaller = null;
                _allowCloseWithUnsavedWorkspace = false;
                TxtStatus.Text = L("update.save_failed");
                return;
            }
            Process.Start(ReleaseUpdateService.CreateInstallerStartInfo(_pendingUpdateInstaller, AppRootDir, Environment.ProcessId));
            _pendingUpdateInstaller = null;
        }
        catch (Exception ex)
        {
            args.Cancel = true;
            _pendingUpdateInstaller = null;
            _allowCloseWithUnsavedWorkspace = false;
            TxtStatus.Text = Lf("update.failed", ex.Message);
        }
    }
}
