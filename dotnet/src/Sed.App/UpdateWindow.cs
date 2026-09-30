using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Sed.Core.Updating;

namespace Sed.App;

/// <summary>User initiated download/restart; opening this window does not install anything.</summary>
public sealed class UpdateWindow : Window
{
    private readonly PortableUpdateService _service;
    private readonly AppSettings _settings;
    private readonly Func<StagedUpdate, CancellationToken, Task<string?>> _restart;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _location = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button _check = new() { Content = "Check now" };
    private readonly Button _download = new() { Content = "Download and prepare", IsEnabled = false };
    private readonly Button _open = new() { Content = "Save recovery and restart", IsEnabled = false };
    private readonly ComboBox _channel = new() { ItemsSource = new[] { "preview", "stable" }, Width = 150 };
    private UpdateRelease? _release;
    private StagedUpdate? _staged;
    private bool _busy;

    public UpdateWindow(PortableUpdateService service, AppSettings settings, Func<StagedUpdate, CancellationToken, Task<string?>> restart)
    {
        _service = service; _settings = settings; _restart = restart;
        Title = "SED updates"; Width = 640; Height = 410; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _channel.SelectedItem = settings.UpdateChannel is "preview" ? "preview" : "stable";
        var automatic = new CheckBox { Content = "Check for updates once a day in the background", IsChecked = settings.CheckForUpdates };
        automatic.IsCheckedChanged += (_, _) => { settings.CheckForUpdates = automatic.IsChecked == true; settings.Save(); };
        _channel.SelectionChanged += (_, _) => { settings.UpdateChannel = (string?)_channel.SelectedItem ?? "stable"; settings.Save(); _release = null; _staged = null; _download.IsEnabled = false; _open.IsEnabled = false; _location.IsVisible = false; };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(_check); buttons.Children.Add(_download); buttons.Children.Add(_open);
        Content = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 14,
            Children =
            {
                new TextBlock { Text = $"Installed version: {EditorUpdater.CurrentVersion}", FontSize = 18 },
                automatic,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { new TextBlock { Text = "Release channel", VerticalAlignment = VerticalAlignment.Center }, _channel } },
                _status, _location, buttons,
                new TextBlock { Text = "Updates create a separate portable installation. Your current copy and files stay in place for rollback. Settings and external projects are shared; local plug-ins are not copied. Existing shortcuts still open the old copy.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray },
            },
        };
        _check.Click += async (_, _) => await CheckAsync(true);
        _download.Click += async (_, _) => await DownloadAsync();
        _open.Click += async (_, _) =>
        {
            if (_staged is null || _busy) return;
            Busy(true); _status.Text = "Verifying the prepared installation and saving your current level for recovery…";
            string? error = await _restart(_staged, _lifetime.Token);
            if (error is not null) { _status.Text = error; Busy(false); }
        };
        Opened += async (_, _) => await CheckAsync(false);
        Closed += (_, _) => _lifetime.Cancel();
    }

    private void Busy(bool value)
    {
        _busy = value; _check.IsEnabled = !value; _channel.IsEnabled = !value;
        _download.IsEnabled = !value && _release is not null && _staged is null;
        _open.IsEnabled = !value && _staged is not null;
    }
    private async Task CheckAsync(bool force)
    {
        if (_busy) return;
        if (!EditorUpdater.Supported) { _status.Text = "Automatic installation is available on Windows x64. Download the appropriate portable release manually on this platform."; _download.IsEnabled = false; return; }
        Busy(true); _status.Text = "Checking release metadata…";
        try
        {
            var result = await _service.CheckAsync(EditorUpdater.CurrentVersion, _settings.UpdateChannel, force, _lifetime.Token);
            _release = result.Available;
            _status.Text = result.Message + (result.Cached ? " (Cached; Check now refreshes.)" : "");
        }
        catch (OperationCanceledException) { _status.Text = "Update check cancelled."; }
        catch (Exception ex) { _status.Text = $"Update check failed: {ex.Message}"; }
        finally { Busy(false); }
    }
    private async Task DownloadAsync()
    {
        if (_release is null || _busy) return;
        Busy(true); _status.Text = "Downloading, verifying SHA256 and preparing a separate installation. You can continue editing; closing this window cancels the download.";
        try
        {
            _staged = await _service.StageAsync(_release, AppContext.BaseDirectory, Path.Combine(EditorUpdater.UpdateRoot, "versions"), _lifetime.Token);
            _location.Text = $"Prepared installation: {_staged.Directory}"; _location.IsVisible = true;
            _status.Text = "Ready. Save recovery and restart writes the current level to the recovery folder and opens that copy in the updated editor. Close other editor windows first. Cancelling leaves this installation available and your current editor open.";
        }
        catch (OperationCanceledException) { _status.Text = "Download cancelled; the current installation was not changed."; }
        catch (Exception ex) { _status.Text = $"Update preparation failed: {ex.Message}"; }
        finally { Busy(false); }
    }
}
