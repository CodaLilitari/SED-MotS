using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Sed.Core.Updating;
using Sed.Formats.Game;
using Sed.Formats.Jkl;
using Sed.Formats.Launching;
using System.Security.Cryptography;

namespace Sed.App;

public partial class MainWindow
{
    private readonly PortableUpdateService _updater = EditorUpdater.Create();
    private readonly CancellationTokenSource _updateLifetime = new();
    private MenuItem? _updatesMenu;
    private UpdateWindow? _updateWindow;
    internal string? UpdateRecoveryPath { get; set; }
    internal string? UpdateRecoverySha256 { get; set; }
    internal string? UpdateReadyPath { get; set; }

    private MenuItem BuildUpdatesMenuItem()
    {
        _updatesMenu = new MenuItem { Header = "Check for _updates…" };
        _updatesMenu.Click += (_, _) =>
        {
            if (_updateWindow is { IsVisible: true }) { _updateWindow.Activate(); return; }
            _updateWindow = new UpdateWindow(_updater, _settings, RestartIntoUpdateAsync);
            _updateWindow.Show(this);
        };
        return _updatesMenu;
    }

    private void InitializeUpdates()
    {
        Closed += (_, _) => _updateLifetime.Cancel();
        Opened += async (_, _) =>
        {
            UpdateHandoff? recovered = null;
            if (UpdateRecoveryPath is { } recovery)
            {
                try
                {
                    // Retain a read-only lease through parsing/commit so these are the exact hashed bytes on Windows.
                    using var input = new FileStream(recovery, FileMode.Open, FileAccess.Read, FileShare.Read);
                    string hash = Convert.ToHexString(SHA256.HashData(input));
                    if (hash != UpdateRecoverySha256) throw new InvalidDataException("Update recovery SHA256 does not match the saved document.");
                    var prepared = PreparedLevelOpen.FromPath(recovery, _settings.DirFor);
                    CommitPreparedOpen(prepared, Path.GetFileName(recovery) + " (update recovery)");
                    if (UpdateReadyPath is { } recoveryReady)
                        recovered = new(EditorUpdater.CurrentVersion, Path.GetFileNameWithoutExtension(recoveryReady), hash);
                }
                catch (Exception ex) { _status.Text = $"Recovery remains at {recovery}: {ex.Message}"; return; }
            }
            if (UpdateReadyPath is { } ready)
            {
                try { (recovered ?? UpdateHandoff.Expected(ready, EditorUpdater.CurrentVersion, null)).Write(ready); }
                catch (Exception ex) { _status.Text = $"Update handoff could not be acknowledged: {ex.Message}"; }
            }
            if (!_settings.CheckForUpdates || !EditorUpdater.Supported) return;
            try
            {
                // Network waits and hash work never run synchronously in the window constructor.
                var check = await _updater.CheckAsync(EditorUpdater.CurrentVersion, _settings.UpdateChannel, false, _updateLifetime.Token);
                if (check.Available is { } release && _updatesMenu is not null)
                    _updatesMenu.Header = $"Update available: {release.Version}…";
            }
            catch (OperationCanceledException) { }
            catch { /* Check failures stay in the update window; editing remains usable. */ }
        };
    }

    private async Task<string?> RestartIntoUpdateAsync(StagedUpdate staged, CancellationToken updateWindowCancellation)
    {
        if (!EditorUpdater.Supported) return "Automatic installation is available on Windows x64 only.";
        if (OwnedWindows.Any(w => w != _updateWindow && w.IsVisible))
            return "Close the other editor windows first so their pending edits can be committed or cancelled.";
        string? acknowledgement = null;
        using var restart = CancellationTokenSource.CreateLinkedTokenSource(_updateLifetime.Token, updateWindowCancellation);
        try
        {
            await PortableUpdateService.VerifyStagedAsync(staged, restart.Token);
            if (OwnedWindows.Any(w => w != _updateWindow && w.IsVisible))
                return "Close the other editor windows before restarting.";
            IsEnabled = false;
            // Serialize again after asynchronous verification so a concurrent edit cannot escape recovery.
            // Fail closed even for import-only documents: restart must not silently abandon current work.
            string? recovery = _currentDoc is null ? null : DocumentRecovery.Save(
                Path.Combine(_settings.ResolvedProjectDir(), "recovery"), _levelName, JklWriter.Build(_currentDoc));
            _settings.Save();
            Directory.CreateDirectory(EditorUpdater.UpdateRoot);
            acknowledgement = Path.Combine(EditorUpdater.UpdateRoot, $"handoff-{Guid.NewGuid():N}.ready");
            var expected = UpdateHandoff.Expected(acknowledgement, staged.Release.Version, recovery);
            using var process = await PortableUpdateService.StartVerifiedAsync(staged,
                recovery is null ? ["--update-ready", acknowledgement] : ["--recover-update", recovery, "--recovery-sha256", expected.RecoverySha256!, "--update-ready", acknowledgement], restart.Token);
            // Do not close this session until the new editor confirms that its recovery document was parsed and committed.
            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (!File.Exists(acknowledgement))
            {
                if (process.HasExited) return $"The updated editor exited during startup (code {process.ExitCode}). This editor and your recovery copy are retained.";
                if (DateTimeOffset.UtcNow >= deadline) return "The updated editor has not confirmed recovery. This editor remains open; check the new window before closing either copy.";
                await Task.Delay(100, restart.Token);
            }
            restart.Token.ThrowIfCancellationRequested();
            if (!expected.Matches(acknowledgement) || process.HasExited) return "The new editor did not confirm the expected version and recovery; this editor remains open.";
            (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
            return null;
        }
        catch (Exception ex) { return $"Restart stopped; this editor remains open. {ex.Message}"; }
        finally
        {
            IsEnabled = true;
            if (acknowledgement is not null) { try { File.Delete(acknowledgement); } catch { } }
        }
    }
}
