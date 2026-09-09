using System.Text.Json;
using Sed.Core.Editing;
using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Formats.Automation;
using Sed.Formats.Jkl;

namespace Sed.Core.Tests;

public sealed class AgentSnapshotRequestTests
{
    private static readonly string Hash = new('a', 64);
    private static Dictionary<string, object> Valid() => new()
    {
        ["schemaVersion"] = 1, ["inputSha256"] = Hash,
        ["position"] = new[] { 0.0, -0.8, 0.0 }, ["target"] = new[] { 0.0, 1.0, 0.0 },
        ["fieldOfViewDegrees"] = 75, ["width"] = 640, ["height"] = 480,
    };
    private static AgentSnapshotRequest Parse(Dictionary<string, object> request) => AgentSnapshotRequest.Parse(JsonSerializer.Serialize(request));

    [Fact]
    public void LitSnapshotsBindExplicitArchiveHashesAndSelectLighting()
    {
        var request = Valid(); request["mode"] = "lit"; request["lightingMode"] = "mono";
        string path = Path.GetFullPath("test-resources.goo");
        request["resourceArchives"] = new[] { new { path, sha256 = Hash.ToUpperInvariant() } };
        var parsed = Parse(request);
        Assert.Equal("lit", parsed.Mode); Assert.Equal("mono", parsed.LightingMode); Assert.True(parsed.StrictAssets);
        Assert.Equal(new SnapshotArchive(path, Hash), Assert.Single(parsed.ResourceArchives));
        request["strictAssets"] = false; Assert.False(Parse(request).StrictAssets);
    }

    [Fact]
    public void LitSnapshotRejectsUnboundRelativeDuplicateAndUnknownArchives()
    {
        var request = Valid(); request["mode"] = "lit";
        Assert.Throws<AgentRequestException>(() => Parse(request));
        request["resourceArchives"] = new[] { new { path = "relative.goo", sha256 = Hash } };
        Assert.Throws<AgentRequestException>(() => Parse(request));
        var entry = new { path = Path.GetFullPath("a.goo"), sha256 = Hash };
        request["resourceArchives"] = new[] { entry, entry }; Assert.Throws<AgentRequestException>(() => Parse(request));
        request["resourceArchives"] = new[] { new { entry.path, entry.sha256, discover = true } }; Assert.Throws<AgentRequestException>(() => Parse(request));
        request["resourceArchives"] = new[] { new { entry.path, sha256 = "bad" } }; Assert.Throws<AgentRequestException>(() => Parse(request));
        request["resourceArchives"] = new[] { entry }; request["mode"] = "diagnostic"; Assert.Throws<AgentRequestException>(() => Parse(request));
    }

    [Theory]
    [InlineData("mode", "auto")][InlineData("lightingMode", "auto")][InlineData("strictAssets", "true")]
    public void RejectsMistypedSnapshotModes(string name, string value)
    {
        var request = Valid(); request[name] = value;
        Assert.Throws<AgentRequestException>(() => Parse(request));
    }

    [Fact]
    public void ValidCameraKeepsExplicitPoseAndDefaults()
    {
        var request = Parse(Valid());
        Assert.Equal(new Vec3(0, -0.8, 0), request.Position);
        Assert.Equal(new Vec3(0, 1, 0), request.Target);
        Assert.Equal(0.01, request.NearPlane); Assert.Equal(1000, request.FarPlane);
        Assert.Equal(640, request.Width); Assert.Equal(Hash, request.InputSha256);
        var schema = JsonSerializer.SerializeToElement(AgentSnapshotRequest.Schema());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains("inputSha256", schema.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
    }

    [Theory]
    [InlineData("width", 0)] [InlineData("height", 2049)] [InlineData("fieldOfViewDegrees", 180)]
    [InlineData("nearPlane", 0)] [InlineData("farPlane", -1)] [InlineData("schemaVersion", 2)]
    public void RefusesInvalidNumericalBounds(string field, int value)
    {
        var fields = Valid(); fields[field] = value;
        Assert.Throws<AgentRequestException>(() => Parse(fields));
    }

    [Fact]
    public void RefusesTotalPixelBudgetAndReversedClipping()
    {
        var fields = Valid(); fields["width"] = 2048; fields["height"] = 2048;
        Assert.Throws<AgentRequestException>(() => Parse(fields));
        fields = Valid(); fields["nearPlane"] = 100; fields["farPlane"] = 10;
        Assert.Throws<AgentRequestException>(() => Parse(fields));
    }

    [Fact]
    public void RefusesUnknownDuplicateMissingAndNonFiniteFields()
    {
        var fields = Valid(); fields["autoCamera"] = true;
        Assert.Throws<AgentRequestException>(() => Parse(fields));
        fields = Valid(); fields.Remove("target");
        Assert.Throws<AgentRequestException>(() => Parse(fields));
        string json = JsonSerializer.Serialize(Valid());
        Assert.Throws<AgentRequestException>(() => AgentSnapshotRequest.Parse(json.Insert(1, "\"width\":128,")));
        Assert.Throws<AgentRequestException>(() => AgentSnapshotRequest.Parse(json.Replace("\"fieldOfViewDegrees\":75", "\"fieldOfViewDegrees\":1e999")));
        Assert.Throws<AgentRequestException>(() => AgentSnapshotRequest.Parse(new string(' ', AgentWorkspace.MaxRequestBytes + 1)));
    }

    [Fact]
    public void RefusesCoincidentOrVerticalCamera()
    {
        var fields = Valid(); fields["target"] = fields["position"];
        Assert.Throws<AgentRequestException>(() => Parse(fields));
        fields["target"] = new[] { 0.0, -0.8, 2.0 };
        Assert.Throws<AgentRequestException>(() => Parse(fields));
    }

    [Fact]
    public void InputAndNativeDependencyRevisionAreGuarded()
    {
        var document = new JklDocument(LevelFactory.CreateUntexturedRoom(ProjectType.MysteriesOfTheSith));
        var snapshot = new AgentDocumentSnapshot(document, Hash, Hash, Array.Empty<AgentInputDependency>());
        Parse(Valid()).VerifySource(snapshot);
        Assert.Throws<AgentConflictException>(() => Parse(Valid()).VerifySource(snapshot with { InputSha256 = new string('b', 64) }));
        var native = snapshot with { SourceRevision = new string('c', 64), Dependencies = new[] { new AgentInputDependency("companionTemplates", "mots.tpl", false, null, null) } };
        Assert.Throws<AgentRequestException>(() => Parse(Valid()).VerifySource(native));
        var fields = Valid(); fields["sourceRevision"] = native.SourceRevision;
        Parse(fields).VerifySource(native);
        Assert.Throws<AgentConflictException>(() => Parse(fields).VerifySource(native with { SourceRevision = new string('d', 64) }));
    }

    [Fact]
    public void PortalPolicyKeepsUntexturedRoomWallsAndOpensOnlyReciprocalVisibleAdjoins()
    {
        var level = LevelFactory.CreateUntexturedRoom(ProjectType.MysteriesOfTheSith);
        var a = level.Sectors[0].Surfaces[0]; var b = level.Sectors[0].Surfaces[1];
        Assert.False(AgentSnapshotRequest.IsOpenPortal(a)); // an untextured solid wall still renders
        new MakeAdjoinCommand(a, b).Apply();
        Assert.True(AgentSnapshotRequest.IsOpenPortal(a)); Assert.True(AgentSnapshotRequest.IsOpenPortal(b));
        a.Material = "window.mat"; a.Geo = 4;
        Assert.False(AgentSnapshotRequest.IsOpenPortal(a));
        a.Geo = 0; Assert.True(AgentSnapshotRequest.IsOpenPortal(a));
        a.AdjoinFlags = 0; Assert.False(AgentSnapshotRequest.IsOpenPortal(a));
        a.AdjoinFlags = AdjoinFlags.Visible; b.Adjoin = null;
        Assert.False(AgentSnapshotRequest.IsOpenPortal(a));
    }

    [Fact]
    public void OutputPreflightNeverReusesFilesOrCreatesParents()
    {
        string root = Path.Combine(Path.GetTempPath(), "sed-snapshot-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string output = Path.Combine(root, "view.png");
            Assert.Equal(output, AgentSnapshotRequest.PrepareOutputPath(output)); Assert.False(File.Exists(output));
            File.WriteAllText(output, "retained");
            Assert.Throws<AgentConflictException>(() => AgentSnapshotRequest.PrepareOutputPath(output));
            Assert.Equal("retained", File.ReadAllText(output));
            Assert.Throws<AgentRequestException>(() => AgentSnapshotRequest.PrepareOutputPath(Path.Combine(root, "view.jkl")));
            Assert.Throws<AgentRequestException>(() => AgentSnapshotRequest.PrepareOutputPath(Path.Combine(root, "absent", "view.png")));
            Assert.False(Directory.Exists(Path.Combine(root, "absent")));
        }
        finally { File.Delete(Path.Combine(root, "view.png")); Directory.Delete(root); }
    }
}
