using System.Text.Json;
using Sed.Core.Math;
using Sed.Formats.Automation;

namespace Sed.Core.Tests;

/// <summary>The <c>sed-agent overview</c> request contract (Wren's level-preview cameras).</summary>
public sealed class AgentOverviewRequestTests
{
    private static readonly string Hash = new('a', 64);
    private static readonly string ArchivePath = Path.GetFullPath("overview-resources.gob");

    private static Dictionary<string, object> Valid() => new()
    {
        ["schemaVersion"] = 1, ["inputSha256"] = Hash, ["view"] = "overhead", ["width"] = 640, ["height"] = 480,
        ["resourceArchives"] = new[] { new { path = ArchivePath, sha256 = Hash } },
    };

    private static AgentOverviewRequest Parse(Dictionary<string, object> request) => AgentOverviewRequest.Parse(JsonSerializer.Serialize(request));

    [Fact]
    public void DefaultsFollowWrenAndCutAwayBackFaces()
    {
        var request = Parse(Valid());
        Assert.Equal("overhead", request.View);
        Assert.Equal("wren", request.Framing);
        Assert.Equal(0.025, request.Margin);
        Assert.Equal("game", request.LightingMode);
        Assert.Equal("openjk", request.RenderProfile);
        Assert.True(request.StrictAssets);
        Assert.False(request.Fullbright);
        Assert.True(request.CullBackFaces);
        Assert.Null(request.SourceRevision);
        Assert.Equal(new SnapshotArchive(ArchivePath, Hash), Assert.Single(request.ResourceArchives));
    }

    [Fact]
    public void ExplicitOptionsAreKept()
    {
        var fields = Valid();
        fields["view"] = "threequarter"; fields["framing"] = "contain"; fields["margin"] = 0.1; fields["sourceRevision"] = new string('B', 64);
        fields["lightingMode"] = "mono"; fields["renderProfile"] = "palette"; fields["strictAssets"] = false;
        fields["fullbright"] = true; fields["cullBackFaces"] = false;
        var request = Parse(fields);
        Assert.Equal(("threequarter", "contain", 0.1), (request.View, request.Framing, request.Margin));
        Assert.Equal(new string('b', 64), request.SourceRevision);
        Assert.Equal(("mono", "palette"), (request.LightingMode, request.RenderProfile));
        Assert.False(request.StrictAssets); Assert.True(request.Fullbright); Assert.False(request.CullBackFaces);
    }

    [Theory]
    [InlineData("view", "\"sideways\"", "view must be one of: overhead, threequarter.")]
    [InlineData("framing", "\"tight\"", "framing must be one of: wren, contain.")]
    [InlineData("margin", "0.26", "margin must be finite and from 0 through 0.25.")]
    [InlineData("margin", "-0.01", "margin must be finite and from 0 through 0.25.")]
    [InlineData("margin", "\"0.1\"", "margin must be finite and from 0 through 0.25.")]
    [InlineData("width", "63", "width must be an integer from 64 through 2048.")]
    [InlineData("height", "2049", "height must be an integer from 64 through 2048.")]
    [InlineData("schemaVersion", "2", "schemaVersion must be an integer from 1 through 1.")]
    [InlineData("fullbright", "1", "fullbright must be a boolean.")]
    [InlineData("cullBackFaces", "\"true\"", "cullBackFaces must be a boolean.")]
    [InlineData("lightingMode", "\"auto\"", "lightingMode must be one of: game, mono, rgb.")]
    [InlineData("resourceArchives", "[]", "Overview requires resourceArchives with explicit paths and SHA256 hashes.")]
    public void RefusesInvalidFields(string field, string json, string message)
    {
        var fields = Valid();
        fields[field] = JsonDocument.Parse(json).RootElement.Clone();
        var error = Assert.Throws<AgentRequestException>(() => Parse(fields));
        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void RefusesMissingUnknownDuplicateAndOversizedRequests()
    {
        var fields = Valid(); fields.Remove("resourceArchives");
        Assert.Equal("Overview requires resourceArchives with explicit paths and SHA256 hashes.", Assert.Throws<AgentRequestException>(() => Parse(fields)).Message);
        fields = Valid(); fields.Remove("view");
        Assert.Equal("Missing overview property 'view'.", Assert.Throws<AgentRequestException>(() => Parse(fields)).Message);
        fields = Valid(); fields["fieldOfViewDegrees"] = 60;
        Assert.Equal("Duplicate or unsupported overview property 'fieldOfViewDegrees'.", Assert.Throws<AgentRequestException>(() => Parse(fields)).Message);
        fields = Valid(); fields["position"] = new[] { 0.0, 0.0, 0.0 };
        Assert.Throws<AgentRequestException>(() => Parse(fields));
        string json = JsonSerializer.Serialize(Valid());
        Assert.Throws<AgentRequestException>(() => AgentOverviewRequest.Parse(json.Insert(1, "\"width\":128,")));
        Assert.Throws<AgentRequestException>(() => AgentOverviewRequest.Parse(new string(' ', AgentWorkspace.MaxRequestBytes + 1)));
        fields = Valid(); fields["width"] = 2048; fields["height"] = 2048;
        Assert.Equal("Overview exceeds the 2097152-pixel budget.", Assert.Throws<AgentRequestException>(() => Parse(fields)).Message);
        fields = Valid(); fields["resourceArchives"] = new[] { new { path = "relative.gob", sha256 = Hash } };
        Assert.Throws<AgentRequestException>(() => Parse(fields));
    }

    [Fact]
    public void SchemaIsStrictAndListsTheViews()
    {
        var schema = JsonSerializer.SerializeToElement(AgentOverviewRequest.Schema(), AgentWorkspace.JsonOptions);
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema.GetProperty("$schema").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(new[] { "schemaVersion", "inputSha256", "view", "width", "height", "resourceArchives" },
            schema.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
        var properties = schema.GetProperty("properties");
        Assert.Equal(new[] { "overhead", "threequarter" }, properties.GetProperty("view").GetProperty("enum").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(new[] { "wren", "contain" }, properties.GetProperty("framing").GetProperty("enum").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(0.025, properties.GetProperty("margin").GetProperty("default").GetDouble());
        Assert.True(properties.GetProperty("cullBackFaces").GetProperty("default").GetBoolean());
        Assert.Equal(1, properties.GetProperty("schemaVersion").GetProperty("const").GetInt32());
    }

    [Fact]
    public void EquivalentSnapshotRequestIsLitAndCopiesEveryOption()
    {
        var fields = Valid(); fields["sourceRevision"] = new string('c', 64); fields["fullbright"] = true;
        fields["lightingMode"] = "rgb"; fields["strictAssets"] = false;
        var overview = Parse(fields);
        var ortho = overview.ToSnapshotRequest(new Vec3(0, 0, 24), new Vec3(0, 0, 2), "orthographic", 0, 15.75, 10, 34);
        Assert.Equal("lit", ortho.Mode);
        Assert.True(ortho.IsOrthographic);
        Assert.Equal(15.75, ortho.OrthographicHeight);
        Assert.Equal(0, ortho.FieldOfViewDegrees);
        Assert.Equal((10.0, 34.0), (ortho.NearPlane, ortho.FarPlane));
        Assert.Equal((640, 480), (ortho.Width, ortho.Height));
        Assert.Equal((Hash, new string('c', 64)), (ortho.InputSha256, ortho.SourceRevision));
        Assert.Equal(("rgb", "openjk", false, true, true), (ortho.LightingMode, ortho.RenderProfile, ortho.StrictAssets, ortho.Fullbright, ortho.CullBackFaces));
        Assert.Equal(overview.ResourceArchives, ortho.ResourceArchives);
        // Exactly what snapshot would parse from the embedded JSON.
        var reparsed = AgentSnapshotRequest.Parse(ortho.ToJson().ToJsonString());
        Assert.Equal(ortho.ToJson().ToJsonString(), reparsed.ToJson().ToJsonString());

        var perspective = overview.ToSnapshotRequest(new Vec3(11.9, -11.9, 12.2), new Vec3(0, 0, 2), "perspective", 42.18, 0, 4.18, 31.19);
        Assert.False(perspective.IsOrthographic);
        Assert.Equal(42.18, perspective.FieldOfViewDegrees);
        Assert.Equal(0, perspective.OrthographicHeight);
    }

    [Theory]
    [InlineData(2000, 3000, "Computed overview camera is outside the snapshot limits: nearPlane must be finite and from 0.001 through 1000.")]
    [InlineData(10, 200000, "Computed overview camera is outside the snapshot limits: farPlane must be finite and from 0.01 through 100000.")]
    public void CameraOutsideSnapshotLimitsIsRefused(double near, double far, string message)
    {
        var overview = Parse(Valid());
        var error = Assert.Throws<AgentRequestException>(() =>
            overview.ToSnapshotRequest(new Vec3(0, 0, 24), new Vec3(0, 0, 2), "orthographic", 0, 15.75, near, far));
        Assert.Equal(message, error.Message);
        var huge = Assert.Throws<AgentRequestException>(() =>
            overview.ToSnapshotRequest(new Vec3(0, 0, 24), new Vec3(0, 0, 2), "orthographic", 0, 5_000_000, 10, 34));
        Assert.StartsWith("Computed overview camera is outside the snapshot limits: orthographicHeight", huge.Message, StringComparison.Ordinal);
    }
}
