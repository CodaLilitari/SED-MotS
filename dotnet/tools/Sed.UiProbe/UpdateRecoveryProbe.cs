using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sed.App;
using Sed.Core.Model;
using Sed.Formats.Game;
using Sed.Formats.Jkl;

/// <summary>Original synthetic startup recovery only: no updater download, process start, configured games or persisted settings.</summary>
internal static class UpdateRecoveryProbe
{
    public static void Run(Action<string, bool, string> report)
    {
        string directory = Path.Combine(Path.GetTempPath(), "sed-update-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Case("valid", false, false);
            Case("invalid", true, false);
            Case("hash-mismatch", false, true);
        }
        finally { Directory.Delete(directory, recursive: true); }

        void Case(string label, bool invalid, bool wrongHash)
        {
            void Check(string name, bool value) => report("update recovery: " + label + " " + name, value, "");
            string recovery = Path.Combine(directory, label + ".jkl");
            string ready = Path.Combine(directory, "handoff-" + Guid.NewGuid().ToString("N") + ".ready");
            var recovered = LevelFactory.CreateUntexturedRoom(ProjectType.MysteriesOfTheSith);
            recovered.Header.Gravity = 9;
            recovered.Sectors[0].Surfaces[0].Corners[0].MonoIntensity = .25f;
            string text = invalid ? "This is deliberately not an editable level." : JklWriter.Build(new JklDocument(recovered));
            File.WriteAllText(recovery, text, new UTF8Encoding(false));
            byte[] original = File.ReadAllBytes(recovery);
            string hash = Convert.ToHexString(SHA256.HashData(original));
            var settings = new AppSettings { ProjectDir = directory, AutoSave = false, CheckForUpdates = false,
                JediKnightDir = null, MysteriesDir = null, InfernalMachineDir = null };
            var window = new MainWindow(settings);
            try
            {
                var prior = new JklDocument(LevelFactory.CreateUntexturedRoom(ProjectType.MysteriesOfTheSith));
                string priorText = JklWriter.Build(prior);
                typeof(MainWindow).GetMethod("CommitPreparedOpen", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [PreparedLevelOpen.FromDocument(prior, "original-prior.jkl"), "original prior"]);
                Set("UpdateRecoveryPath", recovery);
                Set("UpdateReadyPath", ready);
                Set("UpdateRecoverySha256", wrongHash ? new string('0', 64) : hash);
                Check("does not acknowledge before startup", !File.Exists(ready) && ReferenceEquals(prior, window.CurrentDocument));
                window.Show(); Dispatcher.UIThread.RunJobs();
                if (invalid || wrongHash)
                {
                    Check("refusal emits no acknowledgement", !File.Exists(ready));
                    Check("refusal preserves prior document", ReferenceEquals(prior, window.CurrentDocument) && JklWriter.Build(prior) == priorText);
                    Check("refusal is visible", window.GetVisualDescendants().OfType<TextBlock>()
                        .Any(t => t.Text?.Contains("Recovery remains at", StringComparison.Ordinal) == true));
                }
                else
                {
                    Check("parsed recovery becomes current document", window.CurrentDocument is { } active && !ReferenceEquals(prior, active)
                        && active.Level.Header.Gravity == 9 && active.Level.Sectors[0].Surfaces[0].Corners[0].MonoIntensity == .25f);
                    Check("successful load emits acknowledgement", File.Exists(ready));
                    if (File.Exists(ready))
                    {
                        using var ack = JsonDocument.Parse(File.ReadAllBytes(ready));
                        Check("acknowledgement binds version nonce and recovery bytes",
                            ack.RootElement.GetProperty("Version").GetString() == EditorUpdater.CurrentVersion
                            && ack.RootElement.GetProperty("Nonce").GetString() == Path.GetFileNameWithoutExtension(ready)
                            && ack.RootElement.GetProperty("RecoverySha256").GetString() == hash);
                    }
                    Check("replaced prior document remains unchanged", JklWriter.Build(prior) == priorText);
                }
                Check("startup leaves recovery bytes intact", original.SequenceEqual(File.ReadAllBytes(recovery)));
                void Set(string property, string value) => typeof(MainWindow).GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(window, value);
            }
            catch (Exception error) { report("update recovery: " + label + " startup flow", false, error.ToString()); }
            finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
        }
    }
}
