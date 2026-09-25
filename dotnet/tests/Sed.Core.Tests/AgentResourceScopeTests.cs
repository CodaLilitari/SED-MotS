using System.Text;
using System.Text.Json;
using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Formats.Automation;
using Sed.Formats.Gob;
using Sed.Formats.Jkl;
using Sed.Formats.Resources;
using Sed.Formats.ThreeDo;

namespace Sed.Core.Tests;

public sealed class AgentResourceScopeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sed-resource-scope-" + Guid.NewGuid().ToString("N"));
    private string Input => Path.Combine(_directory, "original.jkl");
    private string Archive => Path.Combine(_directory, "original.gob");
    private static readonly byte[] Script = Encoding.Latin1.GetBytes("symbols\nsector target=0\nend\ncode\nend\n");

    private void CreateFixture(bool includeArchitecture)
    {
        Directory.CreateDirectory(_directory);
        AgentWorkspace.Create(Input, "mots");
        var doc = AgentWorkspace.Load(Input).Document;
        var entries = new List<(string, byte[])>();
        foreach (string name in new[] { "first.cog", "second.cog" })
        {
            var cog = new Cog { Name = name };
            cog.Values.Add("0");
            doc.Level.Cogs.Add(cog);
            entries.Add(("cog/" + name, Script));
        }
        if (includeArchitecture)
        {
            var model = new ThreeDoModel();
            var mesh = new Mesh3do { Name = "synthetic" };
            mesh.Vertices.AddRange([new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0)]);
            var face = new Face3do { Geo = 3 };
            face.VertexIndices.AddRange([0, 1, 2]);
            mesh.Faces.Add(face);
            model.Meshes.Add(mesh);
            model.Nodes.Add(new HierarchyNode { Mesh = 0 });
            entries.Add(("3do/synthetic.3do", Encoding.ASCII.GetBytes(ThreeDoWriter.Build(model, 2.2))));

            var template = new Template { Name = "synthetic_arch", Parent = "none" };
            template.Values["model3d"] = "synthetic.3do";
            template.Values["thingflags"] = "0x8";
            doc.Level.Templates.Add(template.Name, template);
            var thing = doc.Level.NewThing();
            thing.Template = template.Name;
            thing.Sector = doc.Level.Sectors[0];
            thing.ArchLightingIndex = 0;
            thing.ArchLightingSourceModel = "synthetic.3do";
            var lightingMesh = new ArchitecturalLightMesh();
            for (int i = 0; i < 3; i++) lightingMesh.Vertices.Add(new(0, 0, 0, 0));
            var lighting = new ArchitecturalLightObject();
            lighting.Meshes.Add(lightingMesh);
            doc.Level.ArchitecturalLighting.Add(lighting);
        }
        File.WriteAllText(Input, JklWriter.Build(doc));
        GobWriter.Build(Archive, entries);
    }

    private Dictionary<string, object> Request() => new()
    {
        ["schemaVersion"] = 1,
        ["inputSha256"] = AgentWorkspace.Load(Input).Hash,
        ["resourceArchives"] = new[] { new ResourceArchiveRequest(Archive, AgentWorkspace.Hash(File.ReadAllBytes(Archive))) },
    };
    private static string Serialize(object value) => JsonSerializer.Serialize(value, AgentWorkspace.JsonOptions);
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, AgentWorkspace.JsonOptions);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BindingQueryAndCombinedPlanUseTheirOwnReviewedResourceSets(bool includeArchitecture)
    {
        CreateFixture(includeArchitecture);
        byte[] inputBefore = File.ReadAllBytes(Input), archiveBefore = File.ReadAllBytes(Archive);
        var query = Request();
        query["cog"] = 0;
        var bindings = Json(AgentWorkspace.Bindings(Input, Serialize(query)));
        string bindingRevision = bindings.GetProperty("bindingResourceRevision").GetString()!;
        Assert.False(bindings.TryGetProperty("resourceRevision", out _));
        Assert.Equal(1, bindings.GetProperty("usedResources").GetArrayLength());
        query["bindingResourceRevision"] = bindingRevision;
        Assert.Equal(bindings.GetRawText(), Json(AgentWorkspace.Bindings(Input, Serialize(query))).GetRawText());

        var patch = Request();
        patch["operations"] = Enumerable.Range(0, 2).Select(index => new
        {
            op = "rebindCogReference", cog = index, parameter = 0, symbol = "target", kind = "sector",
            targetIndex = -1, scriptSha256 = AgentWorkspace.Hash(Script),
        }).ToArray();
        var plan = AgentWorkspace.Plan(Input, Serialize(patch));
        Assert.NotNull(plan.ResourceRevision);
        Assert.NotEqual(bindingRevision, plan.ResourceRevision);
        var validation = Json(plan.Report).GetProperty("architectureValidation");
        Assert.Equal(includeArchitecture ? 1 : 0, validation.GetProperty("boundThings").GetInt32());
        Assert.Equal(includeArchitecture ? 3 : 2, validation.GetProperty("usedResources").GetArrayLength());
        Assert.All(JklParser.ParseDocument(plan.OutputText).Level.Cogs, cog => Assert.Equal("-1", Assert.Single(cog.Values)));

        patch["resourceRevision"] = bindingRevision;
        Assert.Throws<AgentConflictException>(() => AgentWorkspace.Plan(Input, Serialize(patch)));
        patch["resourceRevision"] = plan.ResourceRevision!;
        var repeated = AgentWorkspace.Plan(Input, Serialize(patch));
        Assert.Equal(plan.ResourceRevision, repeated.ResourceRevision);
        Assert.Equal(plan.OutputText, repeated.OutputText);
        Assert.Equal(inputBefore, File.ReadAllBytes(Input));
        Assert.Equal(archiveBefore, File.ReadAllBytes(Archive));
        Assert.Equal(2, Directory.GetFiles(_directory).Length);
    }

    [Fact]
    public void BindingsRejectsGenericRevisionWithGuidanceAndChecksItsScopedRevision()
    {
        CreateFixture(includeArchitecture: true);
        var query = Request();
        query["cog"] = 0;
        string revision = Json(AgentWorkspace.Bindings(Input, Serialize(query))).GetProperty("bindingResourceRevision").GetString()!;
        query["resourceRevision"] = revision;
        var error = Assert.Throws<AgentRequestException>(() => AgentWorkspace.Bindings(Input, Serialize(query)));
        Assert.Contains("bindingResourceRevision", error.Message);
        Assert.Contains("reviewed plan", error.Message);
        query.Remove("resourceRevision");
        query["bindingResourceRevision"] = new string('0', 64);
        Assert.Throws<AgentConflictException>(() => AgentWorkspace.Bindings(Input, Serialize(query)));
        query["bindingResourceRevision"] = revision;
        Assert.Equal(revision, Json(AgentWorkspace.Bindings(Input, Serialize(query))).GetProperty("bindingResourceRevision").GetString());
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
