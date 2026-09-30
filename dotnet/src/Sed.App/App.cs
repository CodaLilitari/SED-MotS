using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace Sed.App;

public class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            string? ready = null;
            if (desktop.Args is ["--recover-update", var path, "--recovery-sha256", var recoveryHash, "--update-ready", var recoveryReady]
                && Path.IsPathFullyQualified(path) && Path.GetExtension(path).Equals(".jkl", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            { window.UpdateRecoveryPath = path; window.UpdateRecoverySha256 = recoveryHash; ready = recoveryReady; }
            else if (desktop.Args is ["--update-ready", var emptyReady]) ready = emptyReady;
            if (ready is not null && Path.IsPathFullyQualified(ready)
                && Path.GetDirectoryName(Path.GetFullPath(ready)) == EditorUpdater.UpdateRoot
                && Path.GetFileName(ready).StartsWith("handoff-", StringComparison.Ordinal) && Path.GetExtension(ready) == ".ready")
                window.UpdateReadyPath = ready;
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
