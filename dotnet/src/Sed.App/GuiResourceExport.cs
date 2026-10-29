using System.Security.Cryptography;
using System.Text;
using Sed.Core.IO;
using Sed.Formats.Gob;
using Sed.Formats.Jkl;
using Sed.Formats.Resources;

namespace Sed.App;

/// <summary>A file prepared privately before a verified GUI export is promoted.</summary>
public sealed record GuiExportOutput(string Path, Action<string> WriteTemporary);

/// <summary>
/// Captures the active GUI's ordered archives and stages explicit exports.
/// Recovery/autosave do not use this gate: an unresolved project must remain recoverable.
/// </summary>
public static class GuiResourceExport
{
    private sealed record StagedOutput(string Path, string Temporary, string? Backup)
    {
        public string? OriginalHash { get; set; }
        public string? OutputHash { get; set; }
    }

    public static ExplicitArchiveResources? CaptureResources(IEnumerable<GobArchive> archives)
    {
        var requests = new List<ResourceArchiveRequest>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var archive in archives)
        {
            string path = archive.SourcePath
                ?? throw new InvalidOperationException("The current archive has no file identity. Reopen it from its file before verified export.");
            if (!seen.Add(path)) continue;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 536_870_912)
                throw new InvalidOperationException("A resource archive exceeds the 512 MiB verified-export limit.");
            requests.Add(new ResourceArchiveRequest(path, Convert.ToHexStringLower(SHA256.HashData(stream))));
        }
        return requests.Count == 0 ? null : new ExplicitArchiveResources(requests);
    }

    public static Action FreshnessGuard(JklDocument document, string expectedDocumentRevision,
        IArchitectureResourceProvider? resources) => () =>
    {
        if (ResourceValidatedExport.DocumentRevision(document) != expectedDocumentRevision)
            throw new InvalidOperationException("The document changed after export preparation. Prepare the export again; its former validation cannot be reused.");
        resources?.VerifyUnchanged();
    };

    public static GuiExportOutput Text(string path, string content) => Bytes(path, new UTF8Encoding(false).GetBytes(content));

    public static GuiExportOutput Bytes(string path, byte[] content)
    {
        byte[] captured = (byte[])content.Clone();
        return new(path, temporary => File.WriteAllBytes(temporary, captured));
    }

    /// <summary>
    /// Stage every output before replacing anything. Check captured inputs immediately
    /// before each promotion; restore completed replacements if a later promotion fails.
    /// Each replacement is atomic, but the complete batch is not a filesystem transaction.
    /// </summary>
    public static void Write(IReadOnlyList<GuiExportOutput> outputs, Action verifyCurrent)
    {
        ArgumentNullException.ThrowIfNull(verifyCurrent);
        var staged = new List<StagedOutput>();
        var promoted = new List<StagedOutput>();
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var paths = outputs.Select(o => System.IO.Path.GetFullPath(o.Path)).ToArray();
        if (paths.Distinct(comparer).Count() != paths.Length)
            throw new ArgumentException("An export batch contains duplicate output paths.");
        bool retainBackups = false;
        try
        {
            for (int i = 0; i < outputs.Count; i++)
            {
                string path = paths[i];
                string directory = System.IO.Path.GetDirectoryName(path)!;
                Directory.CreateDirectory(directory);
                string temporary = System.IO.Path.Combine(directory, ".sed-export-" + Guid.NewGuid().ToString("N") + ".tmp");
                string? backup = File.Exists(path)
                    ? System.IO.Path.Combine(directory, ".sed-export-" + Guid.NewGuid().ToString("N") + ".bak") : null;
                var output = new StagedOutput(path, temporary, backup);
                staged.Add(output);
                if (backup is not null)
                {
                    File.Copy(path, backup, overwrite: false);
                    output.OriginalHash = HashFile(backup);
                }
                outputs[i].WriteTemporary(temporary);
                using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.Write, FileShare.None))
                    stream.Flush(flushToDisk: true);
                output.OutputHash = HashFile(temporary);
            }
            foreach (var output in staged)
            {
                verifyCurrent();
                if (output.Backup is null)
                {
                    if (File.Exists(output.Path) || Directory.Exists(output.Path))
                        throw new IOException($"Export destination appeared during preparation: '{output.Path}'. It was not overwritten.");
                }
                else if (!File.Exists(output.Path) || HashFile(output.Path) != output.OriginalHash)
                    throw new IOException($"Export destination changed during preparation: '{output.Path}'. It was not overwritten.");
                if (output.Backup is not null) File.Move(output.Temporary, output.Path, overwrite: true);
                else AtomicFilePromotion.MoveNew(output.Temporary, output.Path);
                promoted.Add(output);
            }
        }
        catch (Exception failure)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var output in promoted.AsEnumerable().Reverse())
            {
                try
                {
                    if (!File.Exists(output.Path) || HashFile(output.Path) != output.OutputHash)
                        throw new IOException("The promoted output was changed externally; rollback will preserve that change.");
                    if (output.Backup is not null) File.Move(output.Backup, output.Path, overwrite: true);
                    else File.Delete(output.Path);
                }
                catch (Exception rollback)
                {
                    retainBackups = true;
                    rollbackErrors.Add(new IOException($"Could not restore '{output.Path}'. Retained backup: {output.Backup ?? "none (new output)"}.", rollback));
                }
            }
            if (rollbackErrors.Count > 0)
                throw new AggregateException("Export failed and one or more output replacements could not be restored.", new[] { failure }.Concat(rollbackErrors));
            throw;
        }
        finally
        {
            foreach (var output in staged)
            {
                if (File.Exists(output.Temporary)) File.Delete(output.Temporary);
                if (!retainBackups && output.Backup is not null && File.Exists(output.Backup)) File.Delete(output.Backup);
            }
        }
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
