using System.Text.Json;
using Sed.Core.Editing;
using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Formats.Automation;
using Sed.Formats.Jkl;

namespace Sed.Core.Tests;

public sealed class AgentTextureAlignmentTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sed-align-" + Guid.NewGuid().ToString("N"));
    public AgentTextureAlignmentTests() => Directory.CreateDirectory(_directory);
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, AgentWorkspace.JsonOptions);
    private string Input()
    {
        string path = Path.Combine(_directory, "original-room.jkl");
        AgentWorkspace.Create(path, "mots");
        var doc = AgentWorkspace.Load(path).Document;
        new AutoTextureCommand(doc.Level.Sectors[0].Surfaces[0], 64, 32).Apply();
        doc.Level.Sectors[0].Surfaces[5].Corners[0].MonoIntensity = .125f;
        doc.Level.Sectors[0].Surfaces[5].Corners[0].Intensity = new ColorF(.25f, .5f, .75f);
        File.WriteAllText(path, JklWriter.Build(doc));
        return path;
    }
    private static PreparedAgentEdit Plan(string path, params object[] operations) => AgentWorkspace.Plan(path,
        JsonSerializer.Serialize(new { schemaVersion = 1, inputSha256 = AgentWorkspace.Load(path).Hash, operations }));

    [Fact]
    public void AgentAlignmentPreservesReferenceAndLightingWithExactSeamAfterCommit()
    {
        string input = Input(); byte[] bytes = File.ReadAllBytes(input);
        var before = AgentWorkspace.Load(input).Document.Level.Sectors[0];
        var referenceUvs = before.Surfaces[0].Corners.Select(c => c.Uv).ToArray();
        var plan = Plan(input, new { op = "alignSurfaceTexture", sector = 0, surface = 5, otherSector = 0, otherSurface = 0 });
        Assert.Equal(bytes, File.ReadAllBytes(input));
        string output = Path.Combine(_directory, "aligned.jkl"); AgentWorkspace.Commit(plan, output);
        var after = AgentWorkspace.Load(output).Document.Level.Sectors[0];
        var target = after.Surfaces[5]; var reference = after.Surfaces[0];
        Assert.Equal(referenceUvs, reference.Corners.Select(c => c.Uv).ToArray());
        Assert.Equal(before.Vertices.Select(v => v.Position), after.Vertices.Select(v => v.Position));
        Assert.Equal(.125f, target.Corners[0].MonoIntensity);
        Assert.Equal(new ColorF(.25f, .5f, .75f), target.Corners[0].Intensity);
        var seam = target.Corners.Where(c => reference.Corners.Any(r => r.Vertex.Position == c.Vertex.Position)).ToArray();
        Assert.Equal(2, seam.Length);
        foreach (var c in seam) Assert.Equal(reference.Corners.Single(r => r.Vertex.Position == c.Vertex.Position).Uv, c.Uv);
        var reported = Json(plan.Report).GetProperty("changes")[0].GetProperty("serializedFinal").GetProperty("state").GetProperty("uv");
        Assert.Equal(target.Corners.Count, reported.GetArrayLength());
        for (int i = 0; i < target.Corners.Count; i++)
        {
            Assert.Equal(target.Corners[i].Uv.U, reported[i][0].GetDouble());
            Assert.Equal(target.Corners[i].Uv.V, reported[i][1].GetDouble());
        }
    }

    [Fact]
    public void PairedSourceBoundSelectionsAlignAndRejectStaleOrMixedAddressing()
    {
        string input = Input();
        JsonElement Select(int[] direction) => Json(AgentWorkspace.Select(input,
            JsonSerializer.Serialize(new { schemaVersion = 1, kind = "surface", sectorPoint = new[] { 0, 0, 0 }, outward = direction }))).GetProperty("selection");
        var target = Select([1, 0, 0]); var reference = Select([0, 0, -1]);
        var plan = Plan(input, new { op = "alignSurfaceTexture", target, otherTarget = reference });
        Assert.Equal(1, Json(plan.Report).GetProperty("changes").GetArrayLength());
        Assert.Throws<AgentRequestException>(() => Plan(input, new { op = "alignSurfaceTexture", target }));
        Assert.Throws<AgentRequestException>(() => Plan(input, new { op = "alignSurfaceTexture", target, otherTarget = reference, sector = 0 }));
        Assert.Throws<AgentConflictException>(() => Plan(input,
            new { op = "setVertexPosition", sector = 0, vertex = 0, position = new[] { -2, -1, -1 } },
            new { op = "alignSurfaceTexture", target, otherTarget = reference }));
    }

    [Theory]
    [InlineData(0)] // same surface
    [InlineData(1)] // opposite parallel face, no shared edge
    public void RefusalDoesNotChangeInputOrCreateOutput(int reference)
    {
        string input = Input(); byte[] before = File.ReadAllBytes(input);
        var error = Assert.Throws<AgentRequestException>(() => Plan(input,
            new { op = "alignSurfaceTexture", sector = 0, surface = 0, otherSector = 0, otherSurface = reference }));
        Assert.Equal(0, error.OperationIndex); Assert.StartsWith("/operations/0/", error.JsonPointer);
        Assert.Equal(before, File.ReadAllBytes(input)); Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void CoplanarWrapContinuesSignedGradientInsteadOfMirroringAndUndoIsExact()
    {
        var (reference, target) = Pair();
        var original = target.Corners.Select(c => c.Uv).ToArray();
        var history = new EditHistory(); history.Do(new AlignTextureToNeighbourCommand(target, reference));
        Assert.Equal(new TexVertex(128, 0), target.Corners[1].Uv);
        Assert.Equal(new TexVertex(128, 64), target.Corners[2].Uv);
        var aligned = target.Corners.Select(c => c.Uv).ToArray();
        history.Undo(); Assert.Equal(original, target.Corners.Select(c => c.Uv));
        history.Redo(); Assert.Equal(aligned, target.Corners.Select(c => c.Uv));
    }

    [Theory]
    [InlineData("zero-edge")]
    [InlineData("zero-area")]
    [InlineData("nonplanar")]
    [InlineData("nonfinite")]
    [InlineData("overflow")]
    public void InvalidGeometryOrUvRefusesAtomicallyWithoutChangingNormalCache(string defect)
    {
        var (reference, target) = Pair();
        switch (defect)
        {
            case "zero-edge": target.Corners[1].Vertex.Position = target.Corners[0].Vertex.Position; break;
            case "zero-area": foreach (var c in reference.Corners) c.Vertex.Position = new Vec3(0, c.Vertex.Position.Y, 0); break;
            case "nonplanar": target.Corners[2].Vertex.Position += new Vec3(0, 0, .1); break;
            case "nonfinite": reference.Corners[0].Uv = new TexVertex(double.NaN, 0); break;
            case "overflow": reference.Corners[0].Uv = new TexVertex(1e308, 0); break;
        }
        var before = target.Corners.Select(c => c.Uv).ToArray();
        var normal = target.Normal; var referenceNormal = reference.Normal;
        Assert.NotNull(AlignTextureToNeighbourCommand.Validate(target, reference));
        var history = new EditHistory();
        Assert.Throws<InvalidOperationException>(() => history.Do(new AlignTextureToNeighbourCommand(target, reference)));
        Assert.False(history.CanUndo); Assert.Equal(before, target.Corners.Select(c => c.Uv));
        Assert.Equal(normal, target.Normal); Assert.Equal(referenceNormal, reference.Normal);
    }

    private static (Surface Reference, Surface Target) Pair()
    {
        var level = new Level(); var sector = level.NewSector();
        Surface Quad(double left, double right)
        {
            var s = sector.NewSurface();
            foreach (var p in new[] { new Vec3(left, 0, 0), new Vec3(right, 0, 0), new Vec3(right, 1, 0), new Vec3(left, 1, 0) })
                s.Corners.Add(new Surface.Corner { Vertex = sector.AddVertex(p), Uv = new TexVertex(0, 0) });
            s.RecalcNormal(); return s;
        }
        var reference = Quad(-1, 0); var target = Quad(0, 1);
        for (int i = 0; i < 4; i++) reference.Corners[i].Uv = new TexVertex((reference.Corners[i].Vertex.Position.X + 1) * 64, reference.Corners[i].Vertex.Position.Y * 64);
        return (reference, target);
    }
    public void Dispose() => Directory.Delete(_directory, true);
}
