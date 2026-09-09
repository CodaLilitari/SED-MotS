using System.Text.Json;
using Sed.Core.Editing;
using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Core.Validation;
using Sed.Formats.Automation;
using Sed.Formats.Jkl;

namespace Sed.Core.Tests;

public sealed class AgentGeometryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sed-agent-geometry-" + Guid.NewGuid().ToString("N"));
    private readonly string _input;

    public AgentGeometryTests()
    {
        Directory.CreateDirectory(_directory);
        _input = Path.Combine(_directory, "rooms.jkl");
        Save(Scene());
    }

    private static JklDocument Scene(ProjectType game = ProjectType.MysteriesOfTheSith)
    {
        var level = LevelFactory.CreateUntexturedRoom(game);
        var doc = new JklDocument(level);
        doc.Materials.Add("synthetic-wall.mat");
        foreach (var surface in level.Sectors[0].Surfaces)
            new SetMaterialCommand(surface, doc.Materials[0], 0).Apply();
        return doc;
    }
    private void Save(JklDocument doc) => File.WriteAllText(_input, JklWriter.Build(doc));
    private PreparedAgentEdit Plan(string operations) => AgentWorkspace.Plan(_input,
        "{\"schemaVersion\":1,\"inputSha256\":\"" + AgentWorkspace.Load(_input).Hash + "\",\"operations\":" + operations + "}");
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, AgentWorkspace.JsonOptions);

    private static void AssertInwardClosed(Sector sector)
    {
        var center = TransformVerticesCommand.Centroid(sector.Vertices);
        var edges = new List<(Vertex a, Vertex b)>();
        foreach (var surface in sector.Surfaces)
        {
            surface.RecalcNormal();
            Assert.True(surface.Normal.Dot(center - surface.Corners[0].Vertex.Position) > 1e-6,
                $"Surface {sector.Surfaces.IndexOf(surface)} does not face inward.");
            for (int i = 0; i < surface.Corners.Count; i++)
                edges.Add((surface.Corners[i].Vertex, surface.Corners[(i + 1) % surface.Corners.Count].Vertex));
        }
        foreach (var (a, b) in edges)
            Assert.Single(edges, edge => edge.a == b && edge.b == a);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void ExtrusionFromEveryBoxFaceHasInwardCapsAndSurvivesSerialization(int face)
    {
        var doc = Scene(); var source = doc.Level.Sectors[0].Surfaces[face];
        source.RecalcNormal(); var originalNormal = source.Normal;
        var sourcePositions = source.Corners.Select(c => c.Vertex.Position).ToArray();
        new ExtrudeSurfaceCommand(source, 2).Apply();
        var appended = doc.Level.Sectors[1];
        AssertInwardClosed(appended);
        Assert.Equal(-1, source.Normal.Dot(source.Adjoin!.Normal), 10);
        Assert.All(sourcePositions, p => Assert.Contains(appended.Vertices, v => v.Position == p - originalNormal * 2));
        Assert.Equal(0, source.Geo); Assert.Equal(0, source.Adjoin.Geo);
        Assert.Equal(3, source.AdjoinFlags); Assert.Equal(3, source.Adjoin.AdjoinFlags);
        Assert.Equal(4, appended.Surfaces[1].Geo);
        var reopened = JklParser.ParseDocument(JklWriter.Build(doc)).Level;
        Assert.Equal(2, reopened.Sectors.Count);
        Assert.All(reopened.Sectors, AssertInwardClosed);
        Assert.DoesNotContain(ConsistencyChecker.Check(reopened), i => i.Severity == IssueSeverity.Error);
        Assert.Same(reopened.Sectors[1].Surfaces[0], reopened.Sectors[0].Surfaces[face].Adjoin);
        Assert.Equal(0, reopened.Sectors[1].Surfaces[0].Geo);
    }

    [Fact]
    public void ExtrusionPreservesPortalCornerChannelsAndUndoRedoRestoresOpaqueWall()
    {
        var doc = Scene(); var source = doc.Level.Sectors[0].Surfaces[5];
        source.AdjoinFlags = 0x80;
        for (int i = 0; i < source.Corners.Count; i++)
        {
            source.Corners[i].MonoIntensity = .125f * i;
            source.Corners[i].Intensity = new ColorF(.25f, .5f, .75f);
            source.Corners[i].Uv = new TexVertex(11 * i, 13 * i);
        }
        doc.Level.Sectors[0].Flags |= 0x1000;
        doc.Level.Sectors[0].HasCollideBox = true;
        doc.Level.Sectors[0].CollideBox = new Box(new(-.25, -.25, -.25), new(.25, .25, .25));
        string before = JklWriter.Build(doc);
        var history = new EditHistory(); history.Do(new ExtrudeSurfaceCommand(source, 1));
        string after = JklWriter.Build(doc);
        foreach (var original in source.Corners)
        {
            var corner = Assert.Single(source.Adjoin!.Corners, c => c.Vertex.Position == original.Vertex.Position);
            Assert.Equal(original.Uv, corner.Uv); Assert.Equal(original.MonoIntensity, corner.MonoIntensity);
            Assert.Equal(original.Intensity, corner.Intensity);
        }
        Assert.False(doc.Level.Sectors[1].HasCollideBox);
        Assert.Equal(0, doc.Level.Sectors[1].Flags & 0x1000);
        history.Undo(); Assert.Equal(before, JklWriter.Build(doc)); Assert.Equal(4, source.Geo); Assert.Null(source.Adjoin);
        history.Redo(); Assert.Equal(after, JklWriter.Build(doc)); AssertInwardClosed(doc.Level.Sectors[1]);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void CoreRefusesInvalidExtrusionDistanceWithoutMutation(double distance)
    {
        var doc = Scene(); string before = JklWriter.Build(doc);
        Assert.Throws<ArgumentException>(() => new ExtrudeSurfaceCommand(doc.Level.Sectors[0].Surfaces[0], distance).Apply());
        Assert.Equal(before, JklWriter.Build(doc));
    }

    [Fact]
    public void CoreRefusesReplacingAnExistingPortalWithoutMutation()
    {
        var doc = Scene(); var surface = doc.Level.Sectors[0].Surfaces[5];
        new ExtrudeSurfaceCommand(surface, 1).Apply(); string before = JklWriter.Build(doc);
        Assert.Throws<ArgumentException>(() => new ExtrudeSurfaceCommand(surface, 2).Apply());
        Assert.Equal(before, JklWriter.Build(doc));
    }

    [Theory]
    [InlineData(ProjectType.JediKnight)] [InlineData(ProjectType.MysteriesOfTheSith)]
    public void PlannedExtrusionReturnsCompleteBeforeAndSerializedGeometry(ProjectType game)
    {
        Save(Scene(game)); byte[] before = File.ReadAllBytes(_input);
        var plan = Plan("[{\"op\":\"extrudeSurface\",\"sector\":0,\"surface\":5,\"distance\":2}]");
        var change = Json(plan.Report).GetProperty("changes")[0];
        Assert.Equal(6, change.GetProperty("before").GetProperty("state").GetProperty("source").GetProperty("surfaces").GetArrayLength());
        var final = change.GetProperty("serializedFinal");
        Assert.Equal(1, final.GetProperty("target").GetProperty("createdSector").GetInt32());
        var appended = final.GetProperty("state").GetProperty("appended");
        Assert.Equal(8, appended.GetProperty("vertices").GetArrayLength());
        Assert.Equal(6, appended.GetProperty("surfaces").GetArrayLength());
        Assert.Equal(0, appended.GetProperty("surfaces")[0].GetProperty("geo").GetInt32());
        Assert.Equal(3, appended.GetProperty("surfaces")[0].GetProperty("adjoinFlags").GetInt64());
        Assert.Equal(2, appended.GetProperty("surfaces")[0].GetProperty("corners")[0].GetProperty("uv").GetArrayLength());
        Assert.Equal(before, File.ReadAllBytes(_input));
        string output = Path.Combine(_directory, "extruded.jkl");
        AgentWorkspace.Commit(plan, output);
        var reopened = AgentWorkspace.Load(output).Document;
        Assert.Equal(game, reopened.Level.Kind); Assert.All(reopened.Level.Sectors, AssertInwardClosed);
        Assert.All(reopened.Level.Sectors.SelectMany(s => s.Surfaces), s => Assert.Equal("synthetic-wall.mat", s.Material));
    }

    [Fact]
    public void AdjacentTexturedBoxesBecomeOpenReciprocalPortalsInOnePlan()
    {
        var plan = Plan("[{\"op\":\"addBox\",\"center\":[2,0,0],\"halfSize\":1}," +
            "{\"op\":\"adjoinSurfaces\",\"sector\":0,\"surface\":5,\"otherSector\":1,\"otherSurface\":4}]");
        var level = JklParser.ParseDocument(plan.OutputText).Level;
        var a = level.Sectors[0].Surfaces[5]; var b = level.Sectors[1].Surfaces[4];
        Assert.Same(b, a.Adjoin); Assert.Same(a, b.Adjoin);
        Assert.Equal(0, a.Geo); Assert.Equal(0, b.Geo); Assert.Equal(3, a.AdjoinFlags); Assert.Equal(3, b.AdjoinFlags);
        Assert.All(level.Sectors, AssertInwardClosed);
        var state = Json(plan.Report).GetProperty("changes")[1].GetProperty("serializedFinal").GetProperty("state");
        Assert.Equal(1, state.GetProperty("source").GetProperty("adjoinSector").GetInt32());
        Assert.Equal(0, state.GetProperty("other").GetProperty("adjoinSector").GetInt32());
    }

    [Theory]
    [InlineData(3, 1)] // parallel faces with a gap
    [InlineData(1.5, .5)] // coplanar but only a partial overlap
    [InlineData(0, 1)] // same volume, opposed faces are not coincident
    public void AdjoiningRequiresTheCompleteCoincidentBoundary(double center, double half)
    {
        var doc = Scene(); new CreateSectorCommand(doc.Level, SectorFactory.CreateBox(doc.Level, new(center, 0, 0), half, "synthetic-wall.mat", 0)).Apply();
        Save(doc); var before = File.ReadAllBytes(_input);
        var error = Assert.Throws<AgentRequestException>(() => Plan("[{\"op\":\"adjoinSurfaces\",\"sector\":0,\"surface\":5,\"otherSector\":1,\"otherSurface\":4}]"));
        Assert.Contains("same complete boundary", error.Message); Assert.Equal(before, File.ReadAllBytes(_input));
    }

    [Fact]
    public void AdjoinRejectsSameSectorAndAlreadyAdjoinedFaces()
    {
        Assert.Throws<AgentRequestException>(() => Plan("[{\"op\":\"adjoinSurfaces\",\"sector\":0,\"surface\":5,\"otherSector\":0,\"otherSurface\":5}]"));
        var doc = Scene(); new ExtrudeSurfaceCommand(doc.Level.Sectors[0].Surfaces[5], 2).Apply(); Save(doc);
        var error = Assert.Throws<AgentRequestException>(() => Plan("[{\"op\":\"adjoinSurfaces\",\"sector\":0,\"surface\":5,\"otherSector\":1,\"otherSurface\":0}]"));
        Assert.Contains("already adjoined", error.Message);
    }

    [Fact]
    public void ExtrusionRefusesOverlappingAnExistingRoomIncludingThinOverlap()
    {
        var doc = Scene(); new CreateSectorCommand(doc.Level, SectorFactory.CreateBox(doc.Level, new(2.9999, 0, 0), 1, "synthetic-wall.mat", 0)).Apply();
        Save(doc);
        var error = Assert.Throws<AgentRequestException>(() => Plan("[{\"op\":\"extrudeSurface\",\"sector\":0,\"surface\":5,\"distance\":1}]"));
        Assert.Contains("overlap existing sector 1", error.Message);
    }

    [Fact]
    public void ExtrusionRefusesAnInvertedOrOpenSourceSector()
    {
        var doc = Scene(); new FlipSurfaceCommand(doc.Level.Sectors[0].Surfaces[0]).Apply(); Save(doc);
        Assert.Contains("inward", Assert.Throws<AgentRequestException>(() => Plan("[{\"op\":\"extrudeSurface\",\"sector\":0,\"surface\":5,\"distance\":1}]")).Message);
        doc = Scene(); doc.Level.Sectors[0].Surfaces.RemoveAt(0); Save(doc);
        Assert.Contains("closed manifold", Assert.Throws<AgentRequestException>(() => Plan("[{\"op\":\"extrudeSurface\",\"sector\":0,\"surface\":0,\"distance\":1}]")).Message);
    }

    [Fact]
    public void AppendingAnExtrusionPreservesExistingGlobalSurfaceSelectorsAndCogValues()
    {
        var doc = Scene();
        new CreateSectorCommand(doc.Level, SectorFactory.CreateBox(doc.Level, new(-5, 0, 0), 1, "synthetic-wall.mat", 0)).Apply();
        var cog = new Cog { Name = "synthetic.cog" }; cog.Values.AddRange(["7", "1"]); doc.Level.Cogs.Add(cog);
        Save(doc); var before = AgentWorkspace.Load(_input).Document.Level;
        var positions = before.Sectors.SelectMany(s => s.Surfaces).Select(s => s.Corners.Select(c => c.Vertex.Position).ToArray()).ToArray();
        var result = JklParser.ParseDocument(Plan("[{\"op\":\"extrudeSurface\",\"sector\":0,\"surface\":5,\"distance\":1}]").OutputText).Level;
        Assert.Equal(3, result.Sectors.Count);
        var surfaces = result.Sectors.SelectMany(s => s.Surfaces).ToArray();
        for (int i = 0; i < positions.Length; i++) Assert.Equal(positions[i], surfaces[i].Corners.Select(c => c.Vertex.Position));
        Assert.Equal(new[] { "7", "1" }, result.Cogs[0].Values);
    }

    [Fact]
    public void OpaqueTopologySectionsRefuseGeometryEditsWithoutChangingInput()
    {
        File.AppendAllText(_input, "\nSECTION: UNKNOWN_TOPOLOGY\n7 9 2\nEND\n");
        var before = File.ReadAllBytes(_input);
        var error = Assert.Throws<AgentRequestException>(() => Plan("[{\"op\":\"extrudeSurface\",\"sector\":0,\"surface\":5,\"distance\":1}]"));
        Assert.Contains("UNKNOWN_TOPOLOGY", error.Message); Assert.Equal(before, File.ReadAllBytes(_input));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
