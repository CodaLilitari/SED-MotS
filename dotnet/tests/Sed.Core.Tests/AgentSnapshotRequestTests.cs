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

    private static Dictionary<string, object> Orthographic(double height = 12.5)
    {
        var request = Valid();
        request.Remove("fieldOfViewDegrees");
        request["projection"] = "orthographic";
        request["orthographicHeight"] = height;
        return request;
    }

    private static Dictionary<string, object> Lit(Dictionary<string, object> request)
    {
        request["mode"] = "lit";
        request["resourceArchives"] = new[] { new { path = Path.GetFullPath("test-resources.goo"), sha256 = Hash } };
        return request;
    }

    [Fact]
    public void OrthographicCamerasTakeAHeightInsteadOfAFieldOfView()
    {
        var ortho = Parse(Orthographic(12.5));
        Assert.True(ortho.IsOrthographic);
        Assert.Equal("orthographic", ortho.Projection);
        Assert.Equal(12.5, ortho.OrthographicHeight);
        Assert.Equal(0, ortho.FieldOfViewDegrees);
        var perspective = Parse(Valid());
        Assert.False(perspective.IsOrthographic);
        Assert.Equal("perspective", perspective.Projection);
        Assert.Equal(0, perspective.OrthographicHeight);
        Assert.Equal(75, perspective.FieldOfViewDegrees);
        var explicitPerspective = Valid(); explicitPerspective["projection"] = "perspective";
        Assert.Equal(75, Parse(explicitPerspective).FieldOfViewDegrees);
    }

    [Theory]
    [InlineData("missing-height", "Missing snapshot property 'orthographicHeight'.", "/orthographicHeight")]
    [InlineData("fov-with-ortho", "fieldOfViewDegrees applies only to perspective cameras.", "/fieldOfViewDegrees")]
    [InlineData("height-with-perspective", "orthographicHeight applies only to orthographic cameras.", "/orthographicHeight")]
    [InlineData("missing-fov", "Missing snapshot property 'fieldOfViewDegrees'.", "/fieldOfViewDegrees")]
    [InlineData("bad-projection", "projection must be one of: perspective, orthographic.", "/projection")]
    [InlineData("height-too-small", "orthographicHeight must be finite and from 0.001 through 1000000.", "/orthographicHeight")]
    [InlineData("height-too-large", "orthographicHeight must be finite and from 0.001 through 1000000.", "/orthographicHeight")]
    public void OrthographicAndPerspectiveFieldsAreExclusive(string problem, string message, string pointer)
    {
        var request = problem switch
        {
            "missing-height" => Orthographic(),
            "fov-with-ortho" => Orthographic(),
            "height-with-perspective" => Valid(),
            "missing-fov" => Valid(),
            "bad-projection" => Valid(),
            "height-too-small" => Orthographic(0.0005),
            _ => Orthographic(2_000_000),
        };
        if (problem == "missing-height") request.Remove("orthographicHeight");
        if (problem == "fov-with-ortho") request["fieldOfViewDegrees"] = 60;
        if (problem == "height-with-perspective") request["orthographicHeight"] = 10;
        if (problem == "missing-fov") request.Remove("fieldOfViewDegrees");
        if (problem == "bad-projection") request["projection"] = "isometric";
        var error = Assert.Throws<AgentRequestException>(() => Parse(request));
        Assert.Equal(message, error.Message);
        Assert.Equal(pointer, error.JsonPointer);
    }

    [Fact]
    public void VerticalViewsAreAllowedOnlyForOrthographicCameras()
    {
        var down = Orthographic(); down["position"] = new[] { 1.0, 2.0, 30.0 }; down["target"] = new[] { 1.0, 2.0, 3.0 };
        Assert.True(Parse(down).IsOrthographic);
        var up = Orthographic(); up["position"] = new[] { 0.0, 0.0, -5.0 }; up["target"] = new[] { 0.0, 0.0, 5.0 };
        Assert.True(Parse(up).IsOrthographic);
        var perspective = Valid(); perspective["position"] = new[] { 1.0, 2.0, 30.0 }; perspective["target"] = new[] { 1.0, 2.0, 3.0 };
        var refused = Assert.Throws<AgentRequestException>(() => Parse(perspective));
        Assert.Equal("Camera position and target must differ and cannot look directly along the Z-up axis.", refused.Message);
        var coincident = Orthographic(); coincident["target"] = coincident["position"];
        Assert.Equal("Camera position and target must differ.", Assert.Throws<AgentRequestException>(() => Parse(coincident)).Message);
    }

    [Fact]
    public void PerspectiveCameraDescriptionIsByteIdenticalToThePreOrthographicReceipt()
    {
        var fields = Valid(); fields["nearPlane"] = 0.02; fields["farPlane"] = 77;
        var request = Parse(fields);
        // The exact anonymous object the runner and MCP evidence built before orthographic support.
        object legacy = new
        {
            position = new[] { request.Position.X, request.Position.Y, request.Position.Z },
            target = new[] { request.Target.X, request.Target.Y, request.Target.Z },
            request.FieldOfViewDegrees, request.NearPlane, request.FarPlane, up = "Z", projection = "perspective",
        };
        byte[] expected = JsonSerializer.SerializeToUtf8Bytes(legacy, AgentWorkspace.JsonOptions);
        Assert.Equal(expected, JsonSerializer.SerializeToUtf8Bytes(request.CameraDescription(), AgentWorkspace.JsonOptions));
        var compact = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        Assert.Equal("""{"position":[0,-0.8,0],"target":[0,1,0],"fieldOfViewDegrees":75,"nearPlane":0.02,"farPlane":77,"up":"Z","projection":"perspective"}""",
            JsonSerializer.Serialize(request.CameraDescription(), compact));
    }

    [Fact]
    public void OrthographicCameraDescriptionNamesItsHeightAndProjection()
    {
        var compact = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        Assert.Equal("""{"position":[0,-0.8,0],"target":[0,1,0],"orthographicHeight":12.5,"nearPlane":0.01,"farPlane":1000,"up":"Z","projection":"orthographic"}""",
            JsonSerializer.Serialize(Parse(Orthographic(12.5)).CameraDescription(), compact));
    }

    [Theory]
    [InlineData("fullbright")]
    [InlineData("cullBackFaces")]
    public void FullbrightAndCutawayAreLitOnlyBooleans(string field)
    {
        var lit = Lit(Valid());
        Assert.False(field == "fullbright" ? Parse(lit).Fullbright : Parse(lit).CullBackFaces);
        lit[field] = true;
        Assert.True(field == "fullbright" ? Parse(lit).Fullbright : Parse(lit).CullBackFaces);
        lit[field] = "yes";
        var typed = Assert.Throws<AgentRequestException>(() => Parse(lit));
        Assert.Equal($"{field} must be a boolean.", typed.Message);
        var diagnostic = Valid(); diagnostic[field] = false;
        Assert.Equal("resourceArchives, lightingMode, renderProfile, strictAssets, fullbright and cullBackFaces apply only to lit snapshots.",
            Assert.Throws<AgentRequestException>(() => Parse(diagnostic)).Message);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CanonicalJsonRoundTripsThroughParse(bool lit, bool orthographic)
    {
        var fields = orthographic ? Orthographic(28.0) : Valid();
        fields["sourceRevision"] = new string('b', 64);
        fields["position"] = new[] { 11.925707526180574, -11.925707526180574, 12.222035022440492 };
        fields["nearPlane"] = 4.181809173731455; fields["farPlane"] = 31.192829897580012;
        if (lit) { Lit(fields); fields["fullbright"] = true; fields["cullBackFaces"] = true; fields["lightingMode"] = "rgb"; fields["strictAssets"] = false; }
        var request = Parse(fields);
        string canonical = request.ToJson().ToJsonString();
        var again = AgentSnapshotRequest.Parse(canonical);
        Assert.Equal(canonical, again.ToJson().ToJsonString());
        Assert.Equal(request with { ResourceArchives = again.ResourceArchives }, again);
        Assert.Equal(request.ResourceArchives, again.ResourceArchives);
        var json = request.ToJson();
        Assert.Equal(orthographic, json.ContainsKey("projection"));
        Assert.Equal(orthographic, json.ContainsKey("orthographicHeight"));
        Assert.Equal(!orthographic, json.ContainsKey("fieldOfViewDegrees"));
        Assert.Equal(lit, json.ContainsKey("fullbright") && json.ContainsKey("cullBackFaces") && json.ContainsKey("resourceArchives"));
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(request.CameraDescription(), AgentWorkspace.JsonOptions),
            JsonSerializer.SerializeToUtf8Bytes(again.CameraDescription(), AgentWorkspace.JsonOptions));
    }

    [Fact]
    public void SchemaMakesTheFieldOfViewConditionalOnProjection()
    {
        var schema = JsonSerializer.SerializeToElement(AgentSnapshotRequest.Schema(), AgentWorkspace.JsonOptions);
        Assert.DoesNotContain("fieldOfViewDegrees", schema.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
        var rule = schema.GetProperty("allOf")[0];
        Assert.Equal("orthographic", rule.GetProperty("if").GetProperty("properties").GetProperty("projection").GetProperty("const").GetString());
        Assert.Equal("orthographicHeight", rule.GetProperty("then").GetProperty("required")[0].GetString());
        Assert.Equal("fieldOfViewDegrees", rule.GetProperty("then").GetProperty("not").GetProperty("required")[0].GetString());
        Assert.Equal("fieldOfViewDegrees", rule.GetProperty("else").GetProperty("required")[0].GetString());
        Assert.Equal("orthographicHeight", rule.GetProperty("else").GetProperty("not").GetProperty("required")[0].GetString());
        foreach (string name in new[] { "projection", "orthographicHeight", "fullbright", "cullBackFaces" })
            Assert.True(schema.GetProperty("properties").TryGetProperty(name, out _), name);
        Assert.False(schema.GetProperty("properties").GetProperty("fullbright").GetProperty("default").GetBoolean());
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
