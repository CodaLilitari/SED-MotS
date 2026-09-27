using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Sed.Core.Model;
using Sed.Formats.Automation;
using Sed.Formats.Jkl;
using Sed.Mcp.Infrastructure;

namespace Sed.Mcp.Tests;

public sealed class McpFilePolicyTests : IDisposable
{
    private readonly McpPolicyTestDirectory _files = new();
    private McpFilePolicy Policy(params string[] resources) => new(_files.Workspace, resources);

    [Fact]
    public void WorkspaceRelativeLevelsAndExplicitResourceRootsAreAccepted()
    {
        string level = _files.Write("workspace/room.JKL", "original");
        string resource = _files.Write("resources/model.3do", "asset");
        var policy = Policy(_files.Resources);
        Assert.Equal(level, policy.ReadLevel("./room.JKL"));
        Assert.Equal(resource, policy.ReadResource(resource));
        Assert.Equal(_files.PathIn("workspace/new.jkl"), policy.WriteOutput("new.jkl", ".jkl"));
    }

    [Fact]
    public void ScopeBoundariesDoNotAcceptSiblingPrefixesOrWriteAccessToResources()
    {
        string sibling = _files.Write("workspace-other/room.jkl", "outside");
        var policy = Policy(_files.Resources);
        Assert.Throws<AgentRequestException>(() => policy.ReadLevel(sibling));
        Assert.Throws<AgentRequestException>(() => policy.WriteOutput(_files.PathIn("resources/new.jkl"), ".jkl"));
        Assert.Throws<AgentRequestException>(() => Policy().ReadResource(_files.Write("resources/asset.mat", "asset")));
    }

    [Theory]
    [InlineData("../room.jkl")]
    [InlineData("sub/../room.jkl")]
    [InlineData(" room.jkl")]
    [InlineData("room.jkl ")]
    [InlineData("sub./room.jkl")]
    [InlineData("sub /room.jkl")]
    [InlineData("room.jkl.")]
    [InlineData("room?.jkl")]
    [InlineData("room*.jkl")]
    [InlineData("room\u0001.jkl")]
    [InlineData("room.jkl:stream")]
    [InlineData("C:room.jkl")]
    [InlineData("\\room.jkl")]
    [InlineData("\\\\host\\share\\room.jkl")]
    [InlineData("\\\\?\\C:\\room.jkl")]
    [InlineData("//host/share/room.jkl")]
    [InlineData("CON.jkl")]
    [InlineData("aux.jkl")]
    [InlineData("NUL.jkl")]
    [InlineData("COM1.jkl")]
    [InlineData("lpt9.jkl")]
    [InlineData("COM¹.jkl")]
    [InlineData("CONOUT$.jkl")]
    [InlineData("SED-MC~1/new.jkl")]
    [InlineData("LONGFI~1.jkl")]
    public void AmbiguousTraversalDeviceAndStreamSpellingsAreRefusedBeforeFileAccess(string path)
    {
        var error = Assert.Throws<AgentRequestException>(() => Policy().WriteOutput(path, ".jkl"));
        Assert.StartsWith("MCP file policy:", error.Message);
        Assert.Empty(Directory.GetFiles(_files.Workspace));
    }

    [Theory]
    [InlineData("../.jkl")]
    [InlineData("jkl")]
    [InlineData(".jkl:stream")]
    [InlineData(".jkl.png")]
    public void OutputExtensionIsOneExplicitSuffix(string extension) =>
        Assert.Throws<AgentRequestException>(() => Policy().WriteOutput("new.jkl", extension));

    [Fact]
    public void OutputsRequireAnExistingParentCorrectExtensionAndUnoccupiedLeaf()
    {
        var policy = Policy();
        string existing = _files.Write("workspace/kept.jkl", "user work");
        Directory.CreateDirectory(_files.PathIn("workspace/directory.jkl"));
        Assert.Throws<AgentRequestException>(() => policy.WriteOutput(existing, ".jkl"));
        Assert.Throws<AgentRequestException>(() => policy.WriteOutput("directory.jkl", ".jkl"));
        Assert.Throws<AgentRequestException>(() => policy.WriteOutput("missing/new.jkl", ".jkl"));
        Assert.Throws<AgentRequestException>(() => policy.WriteOutput("new.png", ".jkl"));
        Assert.Throws<AgentRequestException>(() => policy.ReadLevel(existing + ".missing"));
        Assert.Equal("user work", File.ReadAllText(existing));
    }

    [Fact]
    public void ConfiguredRootsMustBeExplicitExistingDirectories()
    {
        Assert.Throws<AgentRequestException>(() => new McpFilePolicy("."));
        Assert.Throws<AgentRequestException>(() => new McpFilePolicy(_files.PathIn("absent")));
        Assert.Throws<AgentRequestException>(() => new McpFilePolicy(_files.Write("file", "data")));
    }

    [Fact]
    public void AdapterStateIsNotReadableOrWritableThroughOrdinaryTools()
    {
        string state = _files.Write("workspace/.sed-mcp/receipts/private.jkl", "receipt");
        var policy = Policy();
        Assert.Throws<AgentRequestException>(() => policy.ReadLevel(state));
        Assert.Throws<AgentRequestException>(() => policy.ReadResource(state));
        Assert.Throws<AgentRequestException>(() => policy.WriteOutput(".sed-mcp/new.jkl", ".jkl"));
        Assert.Equal("receipt", File.ReadAllText(state));
    }

    [Fact]
    public void ActualWindowsShortNameCannotBypassReservedAdapterState()
    {
        if (!OperatingSystem.IsWindows()) return;
        string state = _files.Write("workspace/.sed-mcp/receipts/private.jkl", "private receipt");
        string longDirectory = _files.PathIn("workspace/.sed-mcp");
        string shortName = Path.GetFileName(McpPolicyTestDirectory.ShortPath(longDirectory));
        // Some volumes disable 8.3 creation; the spelling-level cases still run
        // everywhere. On an enabled volume this exercises the actual alias.
        if (string.Equals(shortName, ".sed-mcp", StringComparison.OrdinalIgnoreCase)) return;
        string alias = Path.Combine(_files.Workspace, shortName, "receipts", "private.jkl");
        Assert.True(File.Exists(alias));
        var policy = Policy();
        Assert.Throws<AgentRequestException>(() => policy.ReadResource(alias));
        Assert.Throws<AgentRequestException>(() => policy.ReadLevel(alias));
        Assert.Throws<AgentRequestException>(() => policy.WriteOutput(Path.Combine(_files.Workspace, shortName, "new.jkl"), ".jkl"));
        Assert.Equal("private receipt", File.ReadAllText(state));
    }

    [Fact]
    public void ReparseAncestorsAreRejectedForReadsWritesAndConfiguredRoots()
    {
        string target = _files.PathIn("resources");
        string linked = _files.CreateLinkedDirectory("workspace/link", target);
        _files.Write("resources/room.jkl", "outside");
        var policy = Policy();
        Assert.Throws<AgentRequestException>(() => policy.ReadLevel(Path.Combine(linked, "room.jkl")));
        Assert.Throws<AgentRequestException>(() => policy.WriteOutput(Path.Combine(linked, "new.jkl"), ".jkl"));
        Assert.Throws<AgentRequestException>(() => new McpFilePolicy(linked));
        Assert.False(File.Exists(_files.PathIn("resources/new.jkl")));
    }

    [Fact]
    public void DanglingReparseLeavesAreNotTreatedAsAvailableOutputPaths()
    {
        _files.CreateLinkedDirectory("workspace/new.jkl", _files.PathIn("does-not-exist"));
        Assert.Throws<AgentRequestException>(() => Policy().WriteOutput("new.jkl", ".jkl"));
    }

    [Fact]
    public void ReparseReplacementIsRecheckedOnEveryUse()
    {
        string directory = _files.PathIn("workspace/sub");
        Directory.CreateDirectory(directory);
        var policy = Policy();
        Assert.Equal(Path.Combine(directory, "new.jkl"), policy.WriteOutput("sub/new.jkl", ".jkl"));
        Directory.Delete(directory);
        _files.CreateLinkedDirectory("workspace/sub", _files.Resources);
        Assert.Throws<AgentRequestException>(() => policy.WriteOutput("sub/new.jkl", ".jkl"));
    }

    [Fact]
    public void ReparseStateDirectoryCannotRedirectJournalCreation()
    {
        _files.CreateLinkedDirectory("workspace/.sed-mcp", _files.Resources);
        Assert.Throws<AgentRequestException>(() => new OperationStore(Policy()));
        Assert.Empty(Directory.GetFileSystemEntries(_files.Resources));
    }

    [Fact]
    public void NativeTemplateReadsRequireExactBundledDirectoryAuthorization()
    {
        string native = _files.Write("workspace/native.sed", "SED 0.3.0");
        Assert.Throws<AgentRequestException>(() => Policy().ReadLevel(native));
        Assert.Throws<AgentRequestException>(() => Policy().CheckBundledTemplates());
        var wrong = new McpFilePolicy(_files.Workspace, builtInDataRoot: _files.Resources);
        Assert.Throws<AgentRequestException>(() => wrong.CheckBundledTemplates());
    }

    [Fact]
    public void ExplicitNativeDependenciesHaveBoundedRecognizedRolesAndPaths()
    {
        string template = _files.Write("workspace/mots.tpl", "template none");
        var policy = Policy();
        policy.CheckDependencies(Snapshot(new AgentInputDependency("companionTemplates", template, true, 13, null)));
        Assert.Throws<AgentRequestException>(() => policy.CheckDependencies(Snapshot(new AgentInputDependency("other", template, true, 13, null))));
        Assert.Throws<AgentRequestException>(() => policy.CheckDependencies(Snapshot(new AgentInputDependency("bundledTemplates", template, true, 13, null))));
        Assert.Throws<AgentRequestException>(() => policy.CheckDependencies(Snapshot(new AgentInputDependency("companionTemplates",
            _files.Write("resources/mots.tpl", "outside"), true, 7, null))));
        Assert.Throws<AgentRequestException>(() => policy.CheckDependencies(Snapshot(new AgentInputDependency("companionTemplates",
            _files.Write("workspace/other.tpl", "unknown"), true, 7, null))));
    }

    [Fact]
    public void MissingTemplateIsAllowedButOversizeAndDirectoryTemplatesAreRefused()
    {
        string template = _files.PathIn("workspace/mots.tpl");
        var policy = Policy();
        policy.CheckDependencies(Snapshot(new AgentInputDependency("companionTemplates", template, false, null, null)));
        using (var stream = File.Create(template)) stream.SetLength(McpFilePolicy.MaxTemplateBytes + 1);
        Assert.Throws<AgentRequestException>(() => policy.CheckDependencies(Snapshot(new AgentInputDependency("companionTemplates", template, true, null, null))));
        File.Delete(template);
        Directory.CreateDirectory(template);
        Assert.Throws<AgentRequestException>(() => policy.CheckDependencies(Snapshot(new AgentInputDependency("companionTemplates", template, false, null, null))));
    }

    [Fact]
    public void DanglingTemplateDependencyCannotMasqueradeAsMissing()
    {
        string template = _files.CreateLinkedDirectory("workspace/mots.tpl", _files.PathIn("absent"));
        Assert.Throws<AgentRequestException>(() => Policy().CheckDependencies(Snapshot(new AgentInputDependency("companionTemplates", template, false, null, null))));
    }

    private static AgentDocumentSnapshot Snapshot(params AgentInputDependency[] dependencies) =>
        new(new JklDocument(LevelFactory.CreateUntexturedRoom(ProjectType.MysteriesOfTheSith)), new string('a', 64), new string('b', 64), dependencies);

    public void Dispose() => _files.Dispose();
}

// Every fixture path is confined to its own generated temporary directory.
// Windows directory junctions exercise reparse checks without requiring the
// developer-mode/admin privilege that CreateSymbolicLink requires on Windows.
internal sealed class McpPolicyTestDirectory : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sed-mcp-policy-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _links = [];
    public string Workspace => PathIn("workspace");
    public string Resources => PathIn("resources");

    public McpPolicyTestDirectory()
    {
        Directory.CreateDirectory(Workspace);
        Directory.CreateDirectory(Resources);
    }

    public string PathIn(string relative)
    {
        string path = Path.GetFullPath(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test path escaped its fixture.");
        return path;
    }

    public string Write(string relative, string content)
    {
        string path = PathIn(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    public string CreateLinkedDirectory(string relative, string target)
    {
        string path = PathIn(relative);
        if (!Path.GetFullPath(target).StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test link target escaped its fixture.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _links.Add(path);
        if (!OperatingSystem.IsWindows()) Directory.CreateSymbolicLink(path, target);
        else
        {
            Directory.CreateDirectory(path);
            byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
            byte[] print = Encoding.Unicode.GetBytes(target);
            byte[] data = new byte[16 + substitute.Length + 2 + print.Length + 2];
            BitConverter.GetBytes(0xA0000003u).CopyTo(data, 0); // IO_REPARSE_TAG_MOUNT_POINT
            BitConverter.GetBytes(checked((ushort)(data.Length - 8))).CopyTo(data, 4);
            BitConverter.GetBytes(checked((ushort)substitute.Length)).CopyTo(data, 10);
            BitConverter.GetBytes(checked((ushort)(substitute.Length + 2))).CopyTo(data, 12);
            BitConverter.GetBytes(checked((ushort)print.Length)).CopyTo(data, 14);
            substitute.CopyTo(data, 16);
            print.CopyTo(data, 16 + substitute.Length + 2);
            using var handle = CreateFileW(path, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (handle.IsInvalid || !DeviceIoControl(handle, 0x000900A4, data, data.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return path;
    }

    public void Dispose()
    {
        foreach (string link in _links.AsEnumerable().Reverse())
        {
            try { Directory.Delete(link); }
            catch (DirectoryNotFoundException) { }
        }
        // _root is the exact generated fixture, never a caller-supplied path.
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    internal static string ShortPath(string path)
    {
        var buffer = new StringBuilder(32768);
        uint length = GetShortPathNameW(path, buffer, (uint)buffer.Capacity);
        if (length == 0 || length >= buffer.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
        return buffer.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string path, StringBuilder output, uint capacity);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputLength,
        IntPtr output, int outputLength, out int returned, IntPtr overlapped);
}
