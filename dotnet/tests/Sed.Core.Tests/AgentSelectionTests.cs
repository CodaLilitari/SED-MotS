using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sed.Core.Editing;
using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Formats.Automation;
using Sed.Formats.Jkl;

namespace Sed.Core.Tests;

public sealed class AgentSelectionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sed-selection-" + Guid.NewGuid().ToString("N"));
    private readonly string _input;
    public AgentSelectionTests()
    {
        Directory.CreateDirectory(_directory);
        _input = Path.Combine(_directory, "original-synthetic-room.jkl");
        Save(Scene());
    }
    private static JklDocument Scene() => new(LevelFactory.CreateUntexturedRoom(ProjectType.MysteriesOfTheSith));
    private void Save(JklDocument doc) => File.WriteAllText(_input, JklWriter.Build(doc));
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, AgentWorkspace.JsonOptions);
    private JsonElement Select(string request) => Json(AgentWorkspace.Select(_input, request));
    private JsonElement SectorToken() => Select("""{"schemaVersion":1,"kind":"sector","sectorPoint":[0,0,0]}""").GetProperty("selection");
    private JsonElement SurfaceToken() => Select("""{"schemaVersion":1,"kind":"surface","sectorPoint":[0,0,0],"outward":[1,0,0]}""").GetProperty("selection");
    private PreparedAgentEdit Plan(params object[] operations) => PlanText(JsonSerializer.Serialize(operations));
    private PreparedAgentEdit PlanText(string operations)
    {
        var snapshot = AgentWorkspace.LoadWithProvenance(_input);
        return AgentWorkspace.Plan(_input, $$"""{"schemaVersion":1,"inputSha256":"{{snapshot.InputSha256}}","sourceRevision":"{{snapshot.SourceRevision}}","operations":{{operations}}}""");
    }

    [Fact]
    public void UniqueSectorSelectionIsReadOnlyAndBoundToExactSource()
    {
        byte[] original = File.ReadAllBytes(_input);
        var result = Select("""{"schemaVersion":1,"kind":"sector","sectorPoint":[0,0,0]}""");
        Assert.Equal("unique", result.GetProperty("status").GetString());
        Assert.Equal("sector", result.GetProperty("stage").GetString());
        Assert.Equal(1, result.GetProperty("total").GetInt32());
        var token = result.GetProperty("selection");
        Assert.Equal(0, token.GetProperty("sector").GetInt32());
        Assert.False(token.TryGetProperty("surface", out _));
        Assert.Equal(AgentWorkspace.Hash(original), token.GetProperty("sourceRevision").GetString());
        Assert.Equal(64, token.GetProperty("geometrySha256").GetString()!.Length);
        Assert.Equal(original, File.ReadAllBytes(_input));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void SurfaceSelectionNormalizesOutwardDirectionAndRequiresPolygonPoint()
    {
        var result = Select("""{"schemaVersion":1,"kind":"surface","sectorPoint":[0,0,0],"outward":[12,0,0],"surfacePoint":[1,0.2,0.3]}""");
        Assert.Equal("unique", result.GetProperty("status").GetString());
        Assert.Equal("surface", result.GetProperty("stage").GetString());
        Assert.Equal(5, result.GetProperty("selection").GetProperty("surface").GetInt32());
        Assert.Equal(1, result.GetProperty("candidates")[0].GetProperty("outwardNormal")[0].GetDouble());
        var outside = Select("""{"schemaVersion":1,"kind":"surface","sectorPoint":[0,0,0],"outward":[1,0,0],"surfacePoint":[1,2,0]}""");
        Assert.Equal("none", outside.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, outside.GetProperty("selection").ValueKind);
        var tiny = Select("""{"schemaVersion":1,"kind":"surface","sectorPoint":[0,0,0],"outward":[1e-200,0,0]}""");
        Assert.Equal(result.GetProperty("selection").GetRawText(), tiny.GetProperty("selection").GetRawText());
    }

    [Fact]
    public void NoContainingSectorReturnsNoToken()
    {
        var result = Select("""{"schemaVersion":1,"kind":"sector","sectorPoint":[5,0,0]}""");
        Assert.Equal("none", result.GetProperty("status").GetString());
        Assert.Equal(0, result.GetProperty("total").GetInt32());
        Assert.Empty(result.GetProperty("candidates").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("selection").ValueKind);
    }

    [Fact]
    public void SharedBoundaryRefusesSurfaceSelectionBeforeDirectionAndLimitCanGuess()
    {
        var doc = Scene();
        new CreateSectorCommand(doc.Level, SectorFactory.CreateBox(doc.Level, new(2, 0, 0), 1, "", -1)).Apply();
        Save(doc);
        var result = Select("""{"schemaVersion":1,"kind":"surface","sectorPoint":[1,0,0],"outward":[1,0,0],"limit":1}""");
        Assert.Equal("ambiguous", result.GetProperty("status").GetString());
        Assert.Equal("sector", result.GetProperty("stage").GetString());
        Assert.Equal(2, result.GetProperty("total").GetInt32());
        Assert.Single(result.GetProperty("candidates").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("selection").ValueKind);
    }

    [Fact]
    public void CoplanarFacesRequireUniquePolygonMatchEvenWhenLimitIsOne()
    {
        var doc = Scene(); var sector = doc.Level.Sectors[0]; var face = sector.Surfaces[5];
        var corners = face.Corners.ToArray();
        face.Corners.Clear(); face.Corners.AddRange([corners[0], corners[1], corners[2]]);
        var triangle = new Surface(sector);
        triangle.Corners.AddRange([new() { Vertex = corners[0].Vertex }, new() { Vertex = corners[2].Vertex }, new() { Vertex = corners[3].Vertex }]);
        sector.Surfaces.Add(triangle); Save(doc);
        var result = Select("""{"schemaVersion":1,"kind":"surface","sectorPoint":[0,0,0],"outward":[1,0,0],"limit":1}""");
        Assert.Equal("ambiguous", result.GetProperty("status").GetString());
        Assert.Equal("surface", result.GetProperty("stage").GetString());
        Assert.Equal(2, result.GetProperty("total").GetInt32());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("selection").ValueKind);
        Vec3 inside = (corners[0].Vertex.Position + corners[1].Vertex.Position + corners[2].Vertex.Position) / 3;
        var unique = Select(JsonSerializer.Serialize(new { schemaVersion = 1, kind = "surface", sectorPoint = new[] { 0, 0, 0 },
            outward = new[] { 1, 0, 0 }, surfacePoint = new[] { inside.X, inside.Y, inside.Z } }));
        Assert.Equal("unique", unique.GetProperty("status").GetString());
        Assert.Equal(5, unique.GetProperty("selection").GetProperty("surface").GetInt32());
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"kind\":\"sector\",\"sectorPoint\":[0,0,0],\"outward\":[1,0,0]}")]
    [InlineData("{\"schemaVersion\":1,\"kind\":\"surface\",\"sectorPoint\":[0,0,0]}")]
    [InlineData("{\"schemaVersion\":1,\"kind\":\"surface\",\"sectorPoint\":[0,0,0],\"outward\":[0,0,0]}")]
    [InlineData("{\"schemaVersion\":1,\"kind\":\"sector\",\"sectorPoint\":[0,0,0],\"limit\":0}")]
    [InlineData("{\"schemaVersion\":1,\"kind\":\"sector\",\"sectorPoint\":[0,0,0],\"limit\":201}")]
    [InlineData("{\"schemaVersion\":1,\"kind\":\"sector\",\"sectorPoint\":[0,0,0],\"tolerance\":0}")]
    [InlineData("{\"schemaVersion\":1,\"kind\":\"sector\",\"sectorPoint\":[0,0,0],\"tolerance\":0.01}")]
    [InlineData("{\"schemaVersion\":1,\"kind\":\"sector\",\"kind\":\"sector\",\"sectorPoint\":[0,0,0]}")]
    [InlineData("{\"schemaVersion\":1,\"kind\":\"sector\",\"sectorPoint\":[1e400,0,0]}")]
    public void SelectionRequestsRejectMalformedOrInapplicableFields(string request)
        => Assert.Throws<AgentRequestException>(() => Select(request));

    [Fact]
    public void MetadataEditsReuseTokensAndFinalReadbackUsesTheSameBoundObjects()
    {
        var sector = SectorToken(); var surface = SurfaceToken();
        var plan = Plan(new { op = "setSectorAmbient", target = sector, value = .25 },
            new { op = "setSurfaceFlags", target = surface, flags = 4 },
            new { op = "setSectorAmbient", target = sector, value = .5 },
            new { op = "setSurfaceFlags", target = surface, flags = 8 });
        var reopened = JklParser.ParseDocument(plan.OutputText);
        Assert.Equal(.5f, reopened.Level.Sectors[0].Ambient.R);
        Assert.Equal(8, reopened.Level.Sectors[0].Surfaces[5].SurfFlags);
        var changes = Json(plan.Report).GetProperty("changes");
        Assert.Equal(JsonSerializer.Serialize(sector), JsonSerializer.Serialize(changes[0].GetProperty("selectionBindings").GetProperty("target")));
        Assert.Equal(changes[0].GetProperty("serializedFinal").GetRawText(), changes[2].GetProperty("serializedFinal").GetRawText());
    }

    [Fact]
    public void TokenExtrusionUsesNormalizedTargetForReadbackAfterGeometryChanges()
    {
        var token = SurfaceToken();
        var plan = Plan(new { op = "extrudeSurface", target = token, distance = 1 });
        var reopened = JklParser.ParseDocument(plan.OutputText);
        Assert.Equal(2, reopened.Level.Sectors.Count);
        Assert.Same(reopened.Level.Sectors[1].Surfaces[0], reopened.Level.Sectors[0].Surfaces[5].Adjoin);
        Assert.Equal(1, Json(plan.Report).GetProperty("changes")[0].GetProperty("serializedFinal").GetProperty("target").GetProperty("createdSector").GetInt32());
    }

    [Fact]
    public void LateStaleTokenFailureLeavesSourceAndRetryEquivalentToCleanPlan()
    {
        var token = SurfaceToken(); byte[] original = File.ReadAllBytes(_input);
        var error = Assert.Throws<AgentConflictException>(() => Plan(new { op = "extrudeSurface", target = token, distance = 1 },
            new { op = "setSurfaceFlags", target = token, flags = 4 }));
        Assert.Equal(1, error.OperationIndex); Assert.Equal("setSurfaceFlags", error.Operation);
        Assert.Equal("/operations/1/target/geometrySha256", error.JsonPointer);
        Assert.Equal(original, File.ReadAllBytes(_input)); Assert.Single(Directory.GetFiles(_directory));
        var retry = Plan(new { op = "setSurfaceFlags", target = token, flags = 4 });
        var clean = Plan(new { op = "setSurfaceFlags", sector = 0, surface = 5, flags = 4 });
        Assert.Equal(clean.OutputText, retry.OutputText);
        Assert.Equal(Json(clean.Report).GetProperty("outputSha256").GetString(), Json(retry.Report).GetProperty("outputSha256").GetString());
    }

    [Fact]
    public void MovingAnotherSectorBoundaryInvalidatesSurfaceToken()
    {
        var token = SurfaceToken(); var doc = AgentWorkspace.Load(_input).Document;
        int index = doc.Level.Sectors[0].Vertices.FindIndex(v => v.Position.X == -1);
        var old = doc.Level.Sectors[0].Vertices[index].Position;
        var error = Assert.Throws<AgentConflictException>(() => Plan(
            new { op = "setVertexPosition", sector = 0, vertex = index, position = new[] { old.X - .1, old.Y, old.Z } },
            new { op = "setSurfaceFlags", target = token, flags = 4 }));
        Assert.Equal("/operations/1/target/geometrySha256", error.JsonPointer);
    }

    [Fact]
    public void AppendingUnrelatedGeometryDoesNotInvalidateUnchangedSectorToken()
    {
        var token = SectorToken();
        var plan = Plan(new { op = "addBox", center = new[] { 10, 0, 0 }, halfSize = 1 },
            new { op = "setSectorAmbient", target = token, value = .25 });
        var level = JklParser.ParseDocument(plan.OutputText).Level;
        Assert.Equal(2, level.Sectors.Count); Assert.Equal(.25f, level.Sectors[0].Ambient.R);
    }

    [Fact]
    public void FreshInputHashCannotMakeOldRevisionTokenCurrent()
    {
        var token = SectorToken(); var doc = Scene(); doc.Level.Header.Gravity = 6; Save(doc);
        var error = Assert.Throws<AgentConflictException>(() => Plan(new { op = "setSectorAmbient", target = token, value = .25 }));
        Assert.Equal(0, error.OperationIndex); Assert.Equal("/operations/0/target/sourceRevision", error.JsonPointer);
    }

    [Fact]
    public void NativeCompanionRevisionInvalidatesTokenEvenWithUnchangedSourceBytes()
    {
        string native = Path.Combine(_directory, "original-synthetic-room.sed");
        var sector = Scene().Level.Sectors[0];
        var text = new StringBuilder("SED 0.3.0\nSEC: MOTS\nEND\nSEC: HEADER\nHDR 1 4 15 2 3 - 320\n"
            + "HOR 100 768 0 0\nCEIL 0 0\nMIP 1 2 3 4\nLOD 0.1 0.2 0.3 0.4\nEND\n"
            + "SEC: GEO\nSECS 1\n- - 0 0 0 0 0 0 0 0\nVXS 8\n");
        foreach (var vertex in sector.Vertices)
            text.AppendLine(FormattableString.Invariant($"{vertex.Position.X} {vertex.Position.Y} {vertex.Position.Z}"));
        text.AppendLine("SURFS 6");
        foreach (var face in sector.Surfaces)
        {
            text.AppendLine("- 0 0 4 3 3 0 4 -1 -1 0 1 1");
            text.AppendLine(string.Join(" ", face.Corners.Select(c => $"{sector.Vertices.IndexOf(c.Vertex)} 0 0 1")));
        }
        text.AppendLine("END"); File.WriteAllText(native, text.ToString());
        var selected = Json(AgentWorkspace.Select(native, """{"schemaVersion":1,"kind":"sector","sectorPoint":[0,0,0]}"""));
        var token = selected.GetProperty("selection");
        Assert.Equal("unique", selected.GetProperty("status").GetString());
        Assert.Contains(selected.GetProperty("dependencies").EnumerateArray(), item => item.GetProperty("role").GetString() == "companionTemplates"
            && !item.GetProperty("exists").GetBoolean());
        File.WriteAllText(Path.Combine(_directory, "mots.tpl"), "syntheticmarker none type=ghost\n");
        var current = AgentWorkspace.LoadWithProvenance(native);
        Assert.Equal(selected.GetProperty("inputSha256").GetString(), current.InputSha256);
        Assert.NotEqual(selected.GetProperty("sourceRevision").GetString(), current.SourceRevision);
        string patch = JsonSerializer.Serialize(new { schemaVersion = 1, inputSha256 = current.InputSha256, sourceRevision = current.SourceRevision,
            operations = new[] { new { op = "setSectorAmbient", target = token, value = .25 } } });
        var error = Assert.Throws<AgentConflictException>(() => AgentWorkspace.Plan(native, patch));
        Assert.Equal("/operations/0/target/sourceRevision", error.JsonPointer);
    }

    [Fact]
    public void TokenAddressTamperingCannotSelectAnotherIdenticalSectorOrFace()
    {
        var doc = Scene(); new CreateSectorCommand(doc.Level, SectorFactory.CreateBox(doc.Level, new(3, 0, 0), 1, "", -1)).Apply(); Save(doc);
        var sector = JsonNode.Parse(SectorToken().GetRawText())!; sector["sector"] = 1;
        var sectorError = Assert.Throws<AgentConflictException>(() => Plan(new { op = "setSectorAmbient", target = sector, value = .25 }));
        Assert.Equal("/operations/0/target/geometrySha256", sectorError.JsonPointer);
        var surface = JsonNode.Parse(SurfaceToken().GetRawText())!; surface["surface"] = 4;
        var surfaceError = Assert.Throws<AgentConflictException>(() => Plan(new { op = "setSurfaceFlags", target = surface, flags = 4 }));
        Assert.Equal("/operations/0/target/geometrySha256", surfaceError.JsonPointer);
    }

    [Fact]
    public void TokensAdjoinTwoExplicitlySelectedFaces()
    {
        var doc = Scene(); new CreateSectorCommand(doc.Level, SectorFactory.CreateBox(doc.Level, new(2, 0, 0), 1, "", -1)).Apply(); Save(doc);
        var first = SurfaceToken();
        var second = Select("""{"schemaVersion":1,"kind":"surface","sectorPoint":[2,0,0],"outward":[-1,0,0]}""").GetProperty("selection");
        var plan = Plan(new { op = "adjoinSurfaces", target = first, otherTarget = second });
        var level = JklParser.ParseDocument(plan.OutputText).Level;
        Assert.Same(level.Sectors[1].Surfaces[4], level.Sectors[0].Surfaces[5].Adjoin);
        Assert.Same(level.Sectors[0].Surfaces[5], level.Sectors[1].Surfaces[4].Adjoin);
    }

    [Fact]
    public void MixedAddressingWrongKindAndUnsupportedTokenScopeAreRefused()
    {
        var sector = SectorToken(); var surface = SurfaceToken();
        Assert.Throws<AgentRequestException>(() => Plan(new { op = "setSectorAmbient", target = sector, sector = 0, value = .25 }));
        Assert.Throws<AgentRequestException>(() => Plan(new { op = "setSectorAmbient", target = surface, value = .25 }));
        Assert.Throws<AgentRequestException>(() => Plan(new { op = "setVertexPosition", target = sector, vertex = 0, position = new[] { 0, 0, 0 } }));
        Assert.Throws<AgentRequestException>(() => Plan(new { op = "adjoinSurfaces", target = surface, otherSector = 1, otherSurface = 4 }));
    }

    [Fact]
    public void DuplicateOperationAndNestedTokenPropertiesAreRejectedBeforeNormalization()
    {
        string token = SectorToken().GetRawText();
        var duplicateOperation = Assert.Throws<AgentRequestException>(() => PlanText($$"""[{"op":"setSectorAmbient","target":{{token}},"target":{{token}},"value":0.25}]"""));
        Assert.Equal("/operations/0/target", duplicateOperation.JsonPointer);
        string duplicateToken = token.Insert(token.LastIndexOf('}'), ",\"sector\":0");
        var duplicateNested = Assert.Throws<AgentRequestException>(() => PlanText($$"""[{"op":"setSectorAmbient","target":{{duplicateToken}},"value":0.25}]"""));
        Assert.Equal("/operations/0/target/sector", duplicateNested.JsonPointer);
    }

    [Fact]
    public void TokenSchemaAlternativesAreStrictAndKeepOriginalIntegerVariants()
    {
        var patch = Json(AgentSchema.Patch("adjoinSurfaces"));
        var variants = patch.GetProperty("properties").GetProperty("operations").GetProperty("items").GetProperty("oneOf");
        Assert.Equal(2, variants.GetArrayLength());
        Assert.True(variants[0].GetProperty("properties").TryGetProperty("sector", out _));
        var selected = variants[1].GetProperty("properties");
        Assert.False(selected.TryGetProperty("sector", out _)); Assert.True(selected.TryGetProperty("otherTarget", out _));
        Assert.False(selected.GetProperty("target").GetProperty("additionalProperties").GetBoolean());
        var vertex = Json(AgentSchema.Patch("setVertexPosition")).GetProperty("properties").GetProperty("operations").GetProperty("items").GetProperty("oneOf");
        Assert.Single(vertex.EnumerateArray());
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
