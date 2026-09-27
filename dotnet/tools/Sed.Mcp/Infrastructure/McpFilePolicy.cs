using System.Text.RegularExpressions;
using Sed.Formats.Automation;

namespace Sed.Mcp.Infrastructure;

/// <summary>
/// Explicit path authorization for this local adapter. Every use rechecks existing
/// ancestors for links/reparse points. This is not an OS sandbox against a hostile
/// process replacing paths between validation and the library's later file open.
/// </summary>
public sealed class McpFilePolicy
{
    public const long MaxTemplateBytes = 16L * 1024 * 1024;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly Regex DeviceName = new(
        @"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[0-9¹²³]|LPT[0-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ShortNameAlias = new(
        @"^[^.]{1,6}~[0-9]+(?:\.[^.]{0,3})?$", RegexOptions.CultureInvariant);
    private readonly string[] _readRoots;
    private readonly string? _builtInDataRoot;
    public string WorkspaceRoot { get; }

    public McpFilePolicy(string workspaceRoot, IEnumerable<string>? resourceRoots = null, string? builtInDataRoot = null)
    {
        WorkspaceRoot = ConfiguredRoot(workspaceRoot);
        _readRoots = new[] { WorkspaceRoot }.Concat((resourceRoots ?? []).Select(ConfiguredRoot)).Distinct(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        _builtInDataRoot = builtInDataRoot is null ? null : ConfiguredRoot(builtInDataRoot);
    }

    public string ReadLevel(string path)
    {
        string full = Resolve(path);
        RequireLevelExtension(full);
        RequireReadScope(full);
        RequireExistingFile(full);
        CheckNativeDependenciesForFullPath(full);
        return full;
    }

    public string ReadResource(string path)
    {
        string full = Resolve(path);
        RequireReadScope(full);
        RequireExistingFile(full);
        return full;
    }

    public string WriteOutput(string path, string extension) => ResolveOutput(path, extension, allowExisting: false);

    /// <summary>Preflight the fixed template directory used by native imports and new levels.</summary>
    public void CheckBundledTemplates()
    {
        string actualDataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "data")));
        if (_builtInDataRoot is null || !SamePath(_builtInDataRoot, actualDataRoot))
            throw Denied("Template loading requires explicit authorization of the loader's AppContext.BaseDirectory/data directory.");
        foreach (string name in new[] { "mots.tpl", "master.tpl" })
            CheckTemplatePath(Path.Combine(_builtInDataRoot, name));
    }

    /// <summary>Call before LoadWithProvenance, which reads native template files during loading.</summary>
    public void CheckNativeDependencies(string levelPath)
    {
        string full = Resolve(levelPath);
        RequireLevelExtension(full);
        RequireReadScope(full);
        RequireExistingFile(full);
        CheckNativeDependenciesForFullPath(full);
    }

    public void CheckDependencies(AgentDocumentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        foreach (var dependency in snapshot.Dependencies)
        {
            string full = Resolve(dependency.Path);
            if (Path.GetFileName(full) is not ("mots.tpl" or "master.tpl"))
                throw Denied("Only explicit native template dependencies are permitted.");
            if (dependency.Role == "bundledTemplates")
            {
                if (_builtInDataRoot is null || !SamePath(Path.GetDirectoryName(full)!, _builtInDataRoot))
                    throw Denied("The bundled template dependency requires the configured read-only data directory.");
            }
            else if (dependency.Role == "companionTemplates") RequireReadScope(full);
            else throw Denied("Unknown native dependency role.");
            CheckTemplatePath(full);
            if (dependency.Exists) RequireExistingFile(full);
        }
    }

    private void CheckNativeDependenciesForFullPath(string full)
    {
        if (Path.GetExtension(full).Equals(".jkl", StringComparison.OrdinalIgnoreCase)) return;
        CheckBundledTemplates();
        // The importer selects one of these after reading the project. Preflight
        // both before allowing that read, including absent/broken-link targets.
        foreach (string name in new[] { "mots.tpl", "master.tpl" })
            CheckTemplatePath(Path.Combine(Path.GetDirectoryName(full)!, name));
    }

    private static void CheckTemplatePath(string path)
    {
        CheckPath(path, allowMissingLeaf: true);
        if (Directory.Exists(path)) throw Denied("Template dependency must be a file, not a directory.");
        if (File.Exists(path) && new FileInfo(path).Length > MaxTemplateBytes)
            throw Denied("Template dependency exceeds the 16 MiB adapter limit.");
    }

    internal string ResolveIntentInput(string path)
    {
        string full = Resolve(path);
        RequireLevelExtension(full);
        RequireReadScope(full);
        CheckPath(full, allowMissingLeaf: true);
        return full;
    }

    internal string ResolveOutput(string path, string extension, bool allowExisting)
    {
        if (!Regex.IsMatch(extension ?? "", @"^\.[a-zA-Z0-9]+$", RegexOptions.CultureInvariant))
            throw Denied("Specify a single output extension such as .jkl or .png.");
        string full = Resolve(path);
        if (!Within(full, WorkspaceRoot) || SamePath(full, WorkspaceRoot))
            throw Denied("Outputs must be files inside the configured workspace.");
        RejectReceiptAccess(full);
        if (!Path.GetExtension(full).Equals(extension, StringComparison.OrdinalIgnoreCase))
            throw Denied($"Output must use the {extension} extension.");
        CheckPath(full, allowMissingLeaf: true);
        if (Directory.Exists(full)) throw Denied("Output path names an existing directory.");
        if (!allowExisting && File.Exists(full)) throw Denied("Output already exists; choose a new path.");
        return full;
    }

    internal string ReceiptDirectory()
    {
        CheckPath(WorkspaceRoot, allowMissingLeaf: false);
        string state = Path.Combine(WorkspaceRoot, ".sed-mcp");
        EnsureDirectory(state);
        string receipts = Path.Combine(state, "receipts");
        EnsureDirectory(receipts);
        return receipts;
    }

    internal void CheckReceiptPath(string full) => CheckPath(full, allowMissingLeaf: true);

    private static void EnsureDirectory(string full)
    {
        CheckPath(full, allowMissingLeaf: true);
        if (File.Exists(full)) throw Denied("MCP state directory is occupied by a file.");
        Directory.CreateDirectory(full);
        CheckPath(full, allowMissingLeaf: false);
    }

    private void RequireReadScope(string full)
    {
        if (!_readRoots.Any(root => Within(full, root))) throw Denied("Read path is outside the explicitly configured workspace/resource roots.");
        RejectReceiptAccess(full);
    }

    private void RejectReceiptAccess(string full)
    {
        if (Within(full, Path.Combine(WorkspaceRoot, ".sed-mcp")))
            throw Denied("The workspace .sed-mcp directory is reserved for adapter state.");
    }

    private static string ConfiguredRoot(string path)
    {
        ValidateSpelling(path);
        if (!Path.IsPathFullyQualified(path)) throw Denied("Configured roots must be explicit absolute directory paths.");
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        CheckPath(full, allowMissingLeaf: false);
        if (!Directory.Exists(full)) throw Denied("Configured root must be an existing directory.");
        return full;
    }

    private string Resolve(string path)
    {
        ValidateSpelling(path);
        if (Path.IsPathRooted(path) && !Path.IsPathFullyQualified(path))
            throw Denied("Drive-relative and root-relative paths are ambiguous; use an absolute or workspace-relative path.");
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, WorkspaceRoot)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { throw Denied("Invalid path: " + error.Message); }
    }

    private static void ValidateSpelling(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path != path.Trim()) throw Denied("Path is empty or has surrounding whitespace.");
        if (path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) || path.StartsWith("\\", StringComparison.Ordinal))
            throw Denied("UNC, device and root-relative Windows paths are not permitted.");
        if (path.Any(c => c < 32 || "<>\"|?*".Contains(c))) throw Denied("Path contains invalid or ambiguous characters.");
        int colon = path.IndexOf(':');
        if (colon >= 0 && (!OperatingSystem.IsWindows() || colon != 1 || !char.IsAsciiLetter(path[0]) ||
            path.Length < 3 || path[2] is not ('\\' or '/') || path.IndexOf(':', colon + 1) >= 0))
            throw Denied("Alternate data streams and drive-relative paths are not permitted.");
        foreach (string part in path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "." || (part.Length == 2 && part[1] == ':' && colon == 1)) continue;
            if (part == "..") throw Denied("Parent traversal is not permitted; supply the resolved path inside an authorized root.");
            // GetFullPath does not expand DOS 8.3 names. In particular SED-MC~1
            // can alias the reserved .sed-mcp directory and bypass a lexical
            // prefix check. Use the long spelling for every path component.
            if (ShortNameAlias.IsMatch(part)) throw Denied("Windows short-name aliases are not permitted; use the long path spelling.");
            if (part.EndsWith(' ') || part.EndsWith('.') || DeviceName.IsMatch(part))
                throw Denied("Windows device names and trailing-dot/space aliases are not permitted.");
        }
    }

    private static void RequireLevelExtension(string full)
    {
        if (Path.GetExtension(full).ToLowerInvariant() is not (".jkl" or ".jed" or ".sed" or ".ned"))
            throw Denied("ReadLevel requires a .jkl, .jed, .sed or .ned file.");
    }

    private static void RequireExistingFile(string full)
    {
        CheckPath(full, allowMissingLeaf: false);
        if (!File.Exists(full)) throw Denied("Read path must name an existing file.");
    }

    private static void CheckPath(string full, bool allowMissingLeaf)
    {
        string root = Path.GetPathRoot(full)!;
        string current = root;
        var parts = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        CheckOne(root, allowMissing: false);
        for (int i = 0; i < parts.Length; i++)
        {
            current = Path.Combine(current, parts[i]);
            bool exists = CheckOne(current, allowMissingLeaf && i == parts.Length - 1);
            if (exists && i < parts.Length - 1 && !Directory.Exists(current)) throw Denied("A path ancestor is not a directory.");
        }
    }

    private static bool CheckOne(string path, bool allowMissing)
    {
        try
        {
            // LinkTarget catches dangling links on platforms where existence
            // checks follow their missing target. Attributes catches junctions.
            if (new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null)
                throw Denied("Symbolic links and reparse points are not permitted in authorized paths.");
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw Denied("Symbolic links and reparse points are not permitted in authorized paths.");
            return true;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            if (allowMissing) return false;
            throw Denied("Path or parent directory does not exist.");
        }
        catch (UnauthorizedAccessException) { throw Denied("Path cannot be inspected with current access."); }
    }

    private static bool SamePath(string left, string right) => string.Equals(left, right, PathComparison);
    private static bool Within(string full, string root) => SamePath(full, root) ||
        full.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, PathComparison);
    private static AgentRequestException Denied(string message) => new("MCP file policy: " + message);
}
