using System.Text.Json;
using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Formats.Automation;
using Sed.Formats.Jkl;

namespace Sed.Core.Tests;

/// <summary>Original synthetic fixtures cover exact selection through serialized source readback.</summary>
public sealed class AgentQueryFilterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sed-query-filter-" + Guid.NewGuid().ToString("N"));
    private readonly string _input;

    public AgentQueryFilterTests()
    {
        Directory.CreateDirectory(_directory);
        _input = Path.Combine(_directory, "original.jkl");
        File.WriteAllText(_input, JklWriter.Build(Fixture()));
    }

    private static JklDocument Fixture()
    {
        var level = new Level { Kind = ProjectType.MysteriesOfTheSith };
        level.Layers.AddRange(["Main", "Accent", "Mechanisms"]);
        long[] flags = [0, 1, 2, 3, 0x80000001L];
        int[] layers = [1, 0, 1, 2, 1];
        for (int i = 0; i < flags.Length; i++)
        {
            var sector = SectorFactory.CreateBox(level, new Vec3(i * 2, 0, 0), 1, string.Empty, -1);
            sector.Flags = flags[i]; sector.Layer = layers[i];
            level.Sectors.Add(sector);
            level.Lights.Add(new Light { Flags = flags[i], Layer = layers[i], Position = new Vec3(i * 2, 0, 0), Range = 1 });
        }
        string[] names = ["Guard", "guardian", "gUaRd", "Switch", "Guard"];
        string[] templates = ["Actor", "Actor", "actor", "Door", "ActorPlus"];
        int[] thingLayers = [1, 1, 2, 1, 1];
        for (int i = 0; i < names.Length; i++)
            level.Things.Add(new Thing { Name = names[i], Template = templates[i], Layer = thingLayers[i],
                Level = level, Sector = level.Sectors[0], Position = Vec3.Zero });
        var document = new JklDocument(level);
        // Duplicate names are intentional: material filtering must use the selected slot.
        document.Materials.AddRange(["wall.mat", "other.mat", "wall.mat"]);
        int[] materials = [-1, 0, 1, 2, 0, 2];
        long[] surfaceFlags = [0, 1, 2, 3, 0x80000001L, 0];
        long[] faceFlags = [2, 0, 1, 0, 0, 4];
        for (int i = 0; i < 6; i++)
        {
            var surface = level.Sectors[0].Surfaces[i];
            surface.MaterialIndex = materials[i];
            surface.Material = materials[i] < 0 ? string.Empty : document.Materials[materials[i]];
            surface.SurfFlags = surfaceFlags[i]; surface.FaceFlags = faceFlags[i];
        }
        var right = level.Sectors[0].Surfaces[5];
        var left = level.Sectors[1].Surfaces[4];
        right.Adjoin = left; left.Adjoin = right;
        right.AdjoinFlags = 0; left.AdjoinFlags = 3;
        return document;
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, AgentWorkspace.JsonOptions);
    private JsonElement Query(string request) => Json(AgentWorkspace.Query(_input, request));
    private JsonElement Filter(string collection, string filter, int offset = 0, int limit = 50, string? search = null)
    {
        string sector = collection == "surfaces" ? ",\"sector\":0" : "";
        string searchField = search is null ? "" : ",\"search\":" + JsonSerializer.Serialize(search);
        return Query($$"""{"schemaVersion":1,"collection":"{{collection}}","offset":{{offset}},"limit":{{limit}}{{sector}}{{searchField}},"filter":{{filter}}}""");
    }
    private static int[] Indices(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("index").GetInt32()).ToArray();

    [Fact]
    public void ExactNamePagesRetainSparseSourceIndicesAndProvenance()
    {
        byte[] before = File.ReadAllBytes(_input);
        var first = Filter("things", """{"name":"guard"}""", limit: 1);
        var second = Filter("things", """{"name":"guard"}""", offset: 1, limit: 1);
        var last = Filter("things", """{"name":"guard"}""", offset: 2, limit: 1);
        Assert.Equal([0], Indices(first)); Assert.Equal([2], Indices(second)); Assert.Equal([4], Indices(last));
        Assert.Equal(3, first.GetProperty("total").GetInt32());
        Assert.Equal(5, first.GetProperty("sourceTotal").GetInt32());
        Assert.Equal(1, first.GetProperty("nextOffset").GetInt32());
        Assert.Equal(2, second.GetProperty("nextOffset").GetInt32());
        Assert.Equal(JsonValueKind.Null, last.GetProperty("nextOffset").ValueKind);
        Assert.Equal(AgentWorkspace.Hash(before), first.GetProperty("sourceRevision").GetString());
        Assert.Equal(first.GetProperty("sourceRevision").GetString(), last.GetProperty("sourceRevision").GetString());
        Assert.Equal("guard", first.GetProperty("filter").GetProperty("name").GetString());
        Assert.Equal(before, File.ReadAllBytes(_input));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData("things")]
    [InlineData("thingContainment")]
    public void ExactNameTemplateLayerAndExistingSearchComposeWithAnd(string collection)
    {
        Assert.Equal([0, 1, 2], Indices(Filter(collection, """{"template":"aCtOr"}""")));
        Assert.Equal([0], Indices(Filter(collection, """{"template":"actor","name":"GUARD","layer":1}""")));
        Assert.Equal([0, 1, 2], Indices(Filter(collection, """{"template":"actor"}""", search: "ArD")));
        Assert.Equal([4], Indices(Filter(collection, """{"name":"guard"}""", search: "actorplus")));
        Assert.Empty(Indices(Filter(collection, """{"name":"gu"}""")));
        Assert.Empty(Indices(Filter(collection, """{"name":" guard"}""")));
        Assert.Empty(Indices(Filter(collection, """{"name":"guard "}""")));
    }

    [Fact]
    public void OriginalSubstringSearchAndUnfilteredOrderingRemainAvailable()
    {
        Assert.Equal([0, 1, 2, 3, 4], Indices(Query("""{"schemaVersion":1,"collection":"things"}""")));
        Assert.Equal([0, 1, 2, 4], Indices(Query("""{"schemaVersion":1,"collection":"things","search":"guard"}""")));
        var page = Filter("things", """{"name":"guard"}""", offset: int.MaxValue);
        Assert.Equal(3, page.GetProperty("total").GetInt32());
        Assert.Empty(Indices(page)); Assert.Equal(JsonValueKind.Null, page.GetProperty("nextOffset").ValueKind);
    }

    [Theory]
    [InlineData("sectors")]
    [InlineData("lights")]
    public void LayerAndFlagMasksAreIndependentAndKeepHighBits(string collection)
    {
        Assert.Equal([1, 2, 3, 4], Indices(Filter(collection, """{"flags":{"any":3}}""")));
        Assert.Equal([3], Indices(Filter(collection, """{"flags":{"all":3}}""")));
        Assert.Equal([0], Indices(Filter(collection, """{"flags":{"none":3}}""")));
        Assert.Equal([4], Indices(Filter(collection, """{"flags":{"all":2147483648}}""")));
        Assert.Equal([1, 4], Indices(Filter(collection, """{"flags":{"any":3,"all":1,"none":2}}""")));
        Assert.Equal([4], Indices(Filter(collection, """{"layer":1,"flags":{"any":1}}""")));
        Assert.Empty(Indices(Filter(collection, """{"layer":2147483647}""")));
    }

    [Theory]
    [InlineData("sectors", "flags", 5)]
    [InlineData("lights", "flags", 5)]
    [InlineData("surfaces", "surfFlags", 6)]
    [InlineData("surfaces", "faceFlags", 6)]
    public void ZeroMasksAndContradictoryMasksHaveExplicitSetSemantics(string collection, string field, int count)
    {
        Assert.Empty(Indices(Filter(collection, JsonSerializer.Serialize(new Dictionary<string, object> { [field] = new { any = 0 } }))));
        Assert.Equal(count, Indices(Filter(collection, JsonSerializer.Serialize(new Dictionary<string, object> { [field] = new { all = 0, none = 0 } }))).Length);
        Assert.Empty(Indices(Filter(collection, JsonSerializer.Serialize(new Dictionary<string, object> { [field] = new { all = 1, none = 1 } }))));
    }

    [Fact]
    public void SurfaceMaterialSlotsAndOwningSectorLayerAreExact()
    {
        Assert.Equal([1, 4], Indices(Filter("surfaces", """{"materialIndex":0}""")));
        Assert.Equal([3, 5], Indices(Filter("surfaces", """{"materialIndex":2}""")));
        Assert.Equal([0], Indices(Filter("surfaces", """{"materialIndex":-1}""")));
        Assert.Equal([4], Indices(Filter("surfaces", """{"materialIndex":0,"layer":1,"surfFlags":{"all":2147483648}}""")));
        Assert.Empty(Indices(Filter("surfaces", """{"materialIndex":0,"layer":0}""")));
        Assert.Equal(1, Filter("surfaces", """{"materialIndex":0}""").GetProperty("items")[0].GetProperty("layer").GetInt32());
    }

    [Fact]
    public void EachSurfaceFlagFieldIsDistinctAndMissingAdjoinsNeverMatch()
    {
        Assert.Equal([1, 2, 3, 4], Indices(Filter("surfaces", """{"surfFlags":{"any":3}}""")));
        Assert.Equal([0], Indices(Filter("surfaces", """{"faceFlags":{"any":2}}""")));
        Assert.Equal([2], Indices(Filter("surfaces", """{"faceFlags":{"all":1},"surfFlags":{"none":1}}""")));
        Assert.Equal([5], Indices(Filter("surfaces", """{"adjoinFlags":{"none":4294967295}}""")));
        Assert.Equal([5], Indices(Filter("surfaces", """{"adjoinFlags":{"all":0}}""")));
        Assert.Empty(Indices(Filter("surfaces", """{"adjoinFlags":{"any":1}}""")));
        var opposite = Query("""{"schemaVersion":1,"collection":"surfaces","sector":1,"filter":{"adjoinFlags":{"all":3}}}""");
        Assert.Equal([4], Indices(opposite));
    }

    [Fact]
    public void UntexturedSelectorWorksWhenMaterialTableIsEmpty()
    {
        var fixture = new JklDocument(LevelFactory.CreateUntexturedRoom(ProjectType.MysteriesOfTheSith));
        File.WriteAllText(_input, JklWriter.Build(fixture));
        Assert.Equal(6, Indices(Filter("surfaces", """{"materialIndex":-1}""")).Length);
        Assert.Throws<AgentRequestException>(() => Filter("surfaces", """{"materialIndex":0}"""));
    }

    [Theory]
    [InlineData("summary", "{\"layer\":0}")]
    [InlineData("materials", "{\"name\":\"wall.mat\"}")]
    [InlineData("templates", "{\"name\":\"actor\"}")]
    [InlineData("cogs", "{\"name\":\"test.cog\"}")]
    [InlineData("architecture", "{\"layer\":0}")]
    [InlineData("things", "null")]
    [InlineData("things", "[]")]
    [InlineData("things", "{}")]
    [InlineData("things", "{\"name\":\"Guard\",\"name\":\"Switch\"}")]
    [InlineData("things", "{\"Name\":\"Guard\"}")]
    [InlineData("things", "{\"name\":null}")]
    [InlineData("things", "{\"name\":12}")]
    [InlineData("things", "{\"name\":\" \"}")]
    [InlineData("things", "{\"template\":\"\"}")]
    [InlineData("things", "{\"layer\":-1}")]
    [InlineData("things", "{\"layer\":2147483648}")]
    [InlineData("things", "{\"layer\":1.5}")]
    [InlineData("things", "{\"flags\":{\"any\":1}}")]
    [InlineData("thingContainment", "{\"materialIndex\":0}")]
    [InlineData("sectors", "{\"name\":\"Main\"}")]
    [InlineData("sectors", "{\"surfFlags\":{\"all\":1}}")]
    [InlineData("lights", "{\"template\":\"Actor\"}")]
    [InlineData("lights", "{\"flags\":{}}")]
    [InlineData("lights", "{\"flags\":null}")]
    [InlineData("lights", "{\"flags\":1}")]
    [InlineData("lights", "{\"flags\":{\"any\":1,\"any\":2}}")]
    [InlineData("lights", "{\"flags\":{\"any\":1,\"other\":2}}")]
    [InlineData("lights", "{\"flags\":{\"any\":-1}}")]
    [InlineData("lights", "{\"flags\":{\"any\":4294967296}}")]
    [InlineData("lights", "{\"flags\":{\"any\":1.5}}")]
    [InlineData("lights", "{\"flags\":{\"any\":\"1\"}}")]
    [InlineData("lights", "{\"flags\":{\"all\":null}}")]
    [InlineData("surfaces", "{\"flags\":{\"any\":1}}")]
    [InlineData("surfaces", "{\"material\":\"wall.mat\"}")]
    [InlineData("surfaces", "{\"materialIndex\":-2}")]
    [InlineData("surfaces", "{\"materialIndex\":3}")]
    [InlineData("surfaces", "{\"materialIndex\":null}")]
    [InlineData("surfaces", "{\"materialIndex\":\"0\"}")]
    [InlineData("surfaces", "{\"layer\":null}")]
    [InlineData("surfaces", "{\"adjoinFlags\":{\"none\":-1}}")]
    public void InvalidOrInapplicableFiltersAreRejectedWithoutChangingInput(string collection, string filter)
    {
        byte[] before = File.ReadAllBytes(_input);
        Assert.Throws<AgentRequestException>(() => Filter(collection, filter));
        Assert.Equal(before, File.ReadAllBytes(_input));
    }

    [Fact]
    public void DuplicateRootFilterAndOversizedExactNamesAreRejected()
    {
        Assert.Throws<AgentRequestException>(() => Query("""{"schemaVersion":1,"collection":"things","filter":{"name":"Guard"},"filter":{"name":"Switch"}}"""));
        Assert.Throws<AgentRequestException>(() => Filter("things", JsonSerializer.Serialize(new { name = new string('x', 128) })));
        Assert.Throws<AgentRequestException>(() => Filter("things", JsonSerializer.Serialize(new { template = new string('x', 128) })));
    }

    [Fact]
    public void SchemaAdvertisesStrictCollectionSpecificFiltersAndMaskBounds()
    {
        var schema = Json(AgentQueries.Schema());
        var conditions = schema.GetProperty("allOf").EnumerateArray().ToArray();
        JsonElement FilterSchema(string collection) => conditions.Single(condition =>
            condition.GetProperty("if").GetProperty("properties").GetProperty("collection").TryGetProperty("const", out var c)
            && c.GetString() == collection).GetProperty("then").GetProperty("properties").GetProperty("filter");
        var surfaces = FilterSchema("surfaces");
        Assert.False(surfaces.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(1, surfaces.GetProperty("minProperties").GetInt32());
        Assert.Equal(new[] { "materialIndex", "layer", "surfFlags", "faceFlags", "adjoinFlags" }, surfaces.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        var flags = surfaces.GetProperty("properties").GetProperty("surfFlags");
        Assert.False(flags.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(1, flags.GetProperty("minProperties").GetInt32());
        Assert.Equal(0, flags.GetProperty("properties").GetProperty("any").GetProperty("minimum").GetInt32());
        Assert.Equal(uint.MaxValue, flags.GetProperty("properties").GetProperty("any").GetProperty("maximum").GetUInt32());
        Assert.Equal(new[] { "name", "template", "layer" }, FilterSchema("things").GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.Equal(new[] { "layer", "flags" }, FilterSchema("sectors").GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.Equal(new[] { "layer", "flags" }, FilterSchema("lights").GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.Contains(conditions, condition => condition.TryGetProperty("else", out var otherwise)
            && otherwise.GetProperty("properties").TryGetProperty("filter", out var unsupported) && unsupported.ValueKind == JsonValueKind.False);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
