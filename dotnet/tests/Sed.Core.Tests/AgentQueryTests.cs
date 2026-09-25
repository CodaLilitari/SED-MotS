using System.Text;
using System.Text.Json;
using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Formats.Automation;
using Sed.Formats.Jkl;

namespace Sed.Core.Tests;

public sealed class AgentQueryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sed-query-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _input;

    public AgentQueryTests()
    {
        Directory.CreateDirectory(_directory);
        _input = Path.Combine(_directory, "synthetic.jkl");
        SaveFixture(CreateFixture());
    }

    private static JklDocument CreateFixture(int things = 3)
    {
        var level = LevelFactory.CreateUntexturedRoom(ProjectType.MysteriesOfTheSith);
        level.Header.Gravity = 7.5f;
        level.Header.HorizonSky.Offset = new Vec2(2, 3);
        var sector = level.Sectors[0];
        sector.Flags = 4;
        sector.Ambient = new ColorF(.25f, .25f, .25f);
        sector.ExtraLight = new ColorF(.5f, .5f, .5f);
        sector.Tint = new ColorF(.125f, .25f, .5f);
        sector.Sound = "fixture.wav";
        sector.SoundVolume = .5;
        sector.Thrust = new Vec3(1, 2, 3);
        sector.Surfaces[0].SurfFlags = 4;
        sector.Surfaces[0].Corners[0].MonoIntensity = .125f;
        sector.Surfaces[0].Corners[0].Intensity = new ColorF(.25f, .5f, .75f);
        foreach (var (name, order) in new[] { ("Zeta", 2), ("Alpha", 0), ("Beta", 1) })
        {
            var template = new Template { Name = name, Parent = "none", Order = order };
            template.Values["health"] = "100";
            level.Templates.Add(name, template);
        }
        for (int i = 0; i < things; i++)
        {
            var thing = new Thing { Level = level, Sector = sector, Name = $"Fixture{i}", Template = i == 1 ? "Beta" : "Alpha",
                Position = new Vec3(.1, .2, .3), Pitch = 10 + i, Yaw = 20, Roll = 30 };
            thing.Values["health"] = (75 + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
            level.Things.Add(thing);
        }
        level.Lights.Add(new Light { Position = new Vec3(1, 2, 3), Color = new ColorF(.25f, .5f, .75f), Intensity = 2, Range = 4 });
        var doc = new JklDocument(level);
        doc.Materials.AddRange(["wall.mat", "floor.mat", "MetalWall.mat"]);
        return doc;
    }

    private void SaveFixture(JklDocument document) => File.WriteAllText(_input, JklWriter.Build(document));
    private JsonElement Query(string request, string? path = null) =>
        JsonSerializer.SerializeToElement(AgentWorkspace.Query(path ?? _input, request), AgentWorkspace.JsonOptions);
    private JsonElement QueryCollection(string collection) => Query($$"""{"schemaVersion":1,"collection":"{{collection}}"}""");

    [Fact]
    public void SummaryIsCompactReadOnlyAndBindsTheSource()
    {
        byte[] before = File.ReadAllBytes(_input);
        var page = QueryCollection("summary");
        Assert.Equal(AgentWorkspace.Hash(before), page.GetProperty("inputSha256").GetString());
        Assert.Equal(page.GetProperty("inputSha256").GetString(), page.GetProperty("sourceRevision").GetString());
        Assert.Empty(page.GetProperty("dependencies").EnumerateArray());
        Assert.Equal(1, page.GetProperty("total").GetInt32());
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal("MysteriesOfTheSith", item.GetProperty("game").GetString());
        Assert.Equal(7.5, item.GetProperty("header").GetProperty("gravity").GetDouble());
        Assert.Equal(new[] { 2.0, 3.0 }, Doubles(item.GetProperty("header").GetProperty("horizonSky").GetProperty("offset")));
        Assert.Equal(3, item.GetProperty("counts").GetProperty("things").GetInt32());
        Assert.False(item.TryGetProperty("things", out _));
        Assert.True(Encoding.UTF8.GetByteCount(page.GetRawText()) < 4096);
        Assert.Equal(before, File.ReadAllBytes(_input));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void SectorReadbackExposesWritableValuesWithoutGeometryArrays()
    {
        var item = Assert.Single(QueryCollection("sectors").GetProperty("items").EnumerateArray());
        Assert.Equal(4, item.GetProperty("flags").GetInt64());
        Assert.Equal(.25, item.GetProperty("ambient").GetDouble());
        Assert.Equal(.5, item.GetProperty("extraLight").GetDouble());
        Assert.Equal(new[] { .125, .25, .5 }, Doubles(item.GetProperty("tint")));
        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, Doubles(item.GetProperty("thrust")));
        Assert.Equal("FIXTURE.WAV", item.GetProperty("sound").GetString());
        Assert.Equal(8, item.GetProperty("vertices").GetInt32());
        Assert.Equal(6, item.GetProperty("surfaces").GetInt32());
    }

    [Fact]
    public void ThingReadbackIncludesOrientationParametersAndBinding()
    {
        var item = QueryCollection("things").GetProperty("items")[0];
        Assert.Equal("Fixture0", item.GetProperty("name").GetString());
        Assert.Equal(new[] { 10.0, 20.0, 30.0 }, Doubles(item.GetProperty("orientation")));
        Assert.Equal("75", item.GetProperty("parameters").GetProperty("health").GetString());
        Assert.Equal("health", item.GetProperty("sourceParameters")[0].GetProperty("key").GetString());
        Assert.Equal(-1, item.GetProperty("archLightingIndex").GetInt32());
        Assert.Equal(0, item.GetProperty("sector").GetInt32());
    }

    [Fact]
    public void LightReadbackIncludesCreationFields()
    {
        var light = Assert.Single(QueryCollection("lights").GetProperty("items").EnumerateArray());
        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, Doubles(light.GetProperty("position")));
        Assert.Equal(new[] { .25, .5, .75 }, Doubles(light.GetProperty("rgb")));
        Assert.Equal(2, light.GetProperty("intensity").GetDouble());
        Assert.Equal(4, light.GetProperty("range").GetDouble());
    }

    [Fact]
    public void ContainmentPagesPreserveIdentityAndDoNotReassignOutsideThings()
    {
        var fixture = CreateFixture();
        fixture.Level.ThingCapacity = 901;
        fixture.Level.Things[1].Position = new Vec3(1000, 1000, 1000);
        SaveFixture(fixture);
        byte[] before = File.ReadAllBytes(_input);
        var page = Query("""{"schemaVersion":1,"collection":"thingContainment","search":"Beta","limit":1}""");
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(1, item.GetProperty("index").GetInt32());
        Assert.Equal("NoContainingSector", item.GetProperty("status").GetString());
        Assert.Equal("NotContained", item.GetProperty("assignment").GetString());
        Assert.False(item.GetProperty("assignedSectorMatches").GetBoolean());
        Assert.Equal(0, item.GetProperty("assignedSectorIndex").GetInt32());
        Assert.Empty(item.GetProperty("candidates").EnumerateArray());
        Assert.Equal(901, QueryCollection("summary").GetProperty("items")[0].GetProperty("serializedThingCapacity").GetInt32());
        Assert.Equal(AgentWorkspace.Hash(before), page.GetProperty("sourceRevision").GetString());
        Assert.Equal(before, File.ReadAllBytes(_input));
    }

    [Fact]
    public void SurfaceAndVertexQueriesUseLocalIndicesAndPreserveCornerChannels()
    {
        var surfacePage = Query("""{"schemaVersion":1,"collection":"surfaces","sector":0,"offset":0,"limit":1}""");
        Assert.Equal(6, surfacePage.GetProperty("total").GetInt32());
        var surface = Assert.Single(surfacePage.GetProperty("items").EnumerateArray());
        Assert.Equal(0, surface.GetProperty("index").GetInt32());
        Assert.Equal(4, surface.GetProperty("surfFlags").GetInt64());
        var corner = surface.GetProperty("corners")[0];
        Assert.Equal(.125, corner.GetProperty("mono").GetDouble());
        Assert.Equal(new[] { .25, .5, .75 }, Doubles(corner.GetProperty("rgb")));
        int vertex = corner.GetProperty("vertex").GetInt32();
        var vertices = Query($$"""{"schemaVersion":1,"collection":"vertices","sector":0,"offset":{{vertex}},"limit":1}""");
        Assert.Equal(vertex, vertices.GetProperty("items")[0].GetProperty("index").GetInt32());
        Assert.Equal(3, vertices.GetProperty("items")[0].GetProperty("position").GetArrayLength());
        Assert.False(surfacePage.TryGetProperty("things", out _));
    }

    [Fact]
    public void CatalogSearchIsCaseInsensitiveAndIndicesAreNotRenumbered()
    {
        var materials = Query("""{"schemaVersion":1,"collection":"materials","search":"WALL","offset":1,"limit":1}""");
        Assert.Equal(2, materials.GetProperty("total").GetInt32());
        Assert.Equal(3, materials.GetProperty("sourceTotal").GetInt32());
        Assert.Equal(2, materials.GetProperty("items")[0].GetProperty("index").GetInt32());
        Assert.Equal("MetalWall.mat", materials.GetProperty("items")[0].GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, materials.GetProperty("nextOffset").ValueKind);
        var things = Query("""{"schemaVersion":1,"collection":"things","search":"bEtA"}""");
        Assert.Equal(1, things.GetProperty("items")[0].GetProperty("index").GetInt32());
        var templates = Query("""{"schemaVersion":1,"collection":"templates","search":"ZE"}""");
        Assert.Equal(2, templates.GetProperty("items")[0].GetProperty("index").GetInt32());
        Assert.Equal("100", templates.GetProperty("items")[0].GetProperty("parameters").GetProperty("health").GetString());
    }

    [Fact]
    public void DefaultAndMaximumPagesHaveStableContinuation()
    {
        SaveFixture(CreateFixture(205));
        var first = QueryCollection("things");
        Assert.Equal(205, first.GetProperty("total").GetInt32());
        Assert.Equal(50, first.GetProperty("items").GetArrayLength());
        Assert.Equal(50, first.GetProperty("nextOffset").GetInt32());
        var last = Query("""{"schemaVersion":1,"collection":"things","offset":50,"limit":200}""");
        Assert.Equal(155, last.GetProperty("returnedCount").GetInt32());
        Assert.Equal(50, last.GetProperty("items")[0].GetProperty("index").GetInt32());
        Assert.Equal(204, last.GetProperty("items")[154].GetProperty("index").GetInt32());
        Assert.Equal(JsonValueKind.Null, last.GetProperty("nextOffset").ValueKind);
        Assert.Equal(first.GetProperty("inputSha256").GetString(), last.GetProperty("inputSha256").GetString());
        var empty = Query("""{"schemaVersion":1,"collection":"things","offset":2147483647,"limit":200}""");
        Assert.Empty(empty.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("nextOffset").ValueKind);
    }

    [Fact]
    public void SearchWithoutMatchesHasAnEmptyTerminatedPage()
    {
        var page = Query("""{"schemaVersion":1,"collection":"materials","search":"absent"}""");
        Assert.Equal(0, page.GetProperty("total").GetInt32());
        Assert.Empty(page.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextOffset").ValueKind);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":2,\"collection\":\"summary\"}")]
    [InlineData("{\"schemaVersion\":\"1\",\"collection\":\"summary\"}")]
    [InlineData("{\"schemaVersion\":1,\"collection\":null}")]
    [InlineData("{\"schemaVersion\":1,\"collection\":\"unknown\"}")]
    [InlineData("{\"schemaVersion\":1,\"collection\":\"summary\",\"limit\":201}")]
    [InlineData("{\"schemaVersion\":1,\"collection\":\"summary\",\"limit\":0}")]
    [InlineData("{\"schemaVersion\":1,\"collection\":\"summary\",\"limit\":null}")]
    [InlineData("{\"schemaVersion\":1,\"collection\":\"summary\",\"limit\":1.5}")]
    [InlineData("{\"schemaVersion\":1,\"collection\":\"summary\",\"offset\":-1}")]
    [InlineData("{\"schemaVersion\":1,\"collection\":\"summary\",\"offset\":2147483648}")]
    [InlineData("{\"schemaVersion\":1,\"collection\":\"summary\",\"limit\":1,\"limit\":2}")]
    [InlineData("{\"schemaVersion\":1,\"collection\":\"summary\",\"shell\":\"ignored\"}")]
    [InlineData("{\"schemaVersion\":1,\"collection\":\"summary\",\"sector\":0}")]
    [InlineData("{\"schemaVersion\":1,\"collection\":\"surfaces\"}")]
    [InlineData("{\"schemaVersion\":1,\"collection\":\"vertices\",\"sector\":-1}")]
    [InlineData("{\"schemaVersion\":1,\"collection\":\"vertices\",\"sector\":999}")]
    [InlineData("{\"schemaVersion\":1,\"collection\":\"sectors\",\"search\":\"test\"}")]
    [InlineData("{\"schemaVersion\":1,\"collection\":\"templates\",\"search\":42}")]
    [InlineData("{\"schemaVersion\":1,\"collection\":\"templates\",\"search\":\"  \"}")]
    public void InvalidQueryFieldsAreRejectedWithoutChangingTheInput(string request)
    {
        byte[] before = File.ReadAllBytes(_input);
        Assert.Throws<AgentRequestException>(() => AgentWorkspace.Query(_input, request));
        Assert.Equal(before, File.ReadAllBytes(_input));
    }

    [Fact]
    public void OversizedRequestsAndSearchAreRejected()
    {
        Assert.Throws<AgentRequestException>(() => AgentWorkspace.Query(_input, new string(' ', AgentWorkspace.MaxRequestBytes + 1)));
        string request = JsonSerializer.Serialize(new { schemaVersion = 1, collection = "templates", search = new string('a', 128) });
        Assert.Throws<AgentRequestException>(() => Query(request));
    }

    [Fact]
    public void LargeParameterPagesShrinkWithoutDroppingFieldsOrLosingContinuation()
    {
        var fixture = CreateFixture();
        fixture.Level.Templates.Clear();
        for (int i = 0; i < 12; i++)
        {
            var template = new Template { Name = $"large{i}", Parent = "none", Order = i };
            template.Values["annotation"] = new string('a', 100_000);
            fixture.Level.Templates.Add(template.Name, template);
        }
        SaveFixture(fixture);
        var seen = new List<int>();
        int offset = 0;
        do
        {
            var page = Query($$"""{"schemaVersion":1,"collection":"templates","offset":{{offset}},"limit":12}""");
            Assert.True(Encoding.UTF8.GetByteCount(page.GetRawText()) <= AgentQueries.MaxResponseBytes);
            Assert.InRange(page.GetProperty("returnedCount").GetInt32(), 1, 11);
            foreach (var item in page.GetProperty("items").EnumerateArray())
            {
                seen.Add(item.GetProperty("index").GetInt32());
                Assert.Equal(100_000, item.GetProperty("parameters").GetProperty("annotation").GetString()!.Length);
            }
            var next = page.GetProperty("nextOffset");
            if (next.ValueKind == JsonValueKind.Null) break;
            Assert.True(next.GetInt32() > offset);
            offset = next.GetInt32();
        } while (true);
        Assert.Equal(Enumerable.Range(0, 12), seen);
    }

    [Fact]
    public void OneOversizedItemIsRefusedRatherThanTruncated()
    {
        var fixture = CreateFixture();
        fixture.Level.Templates["Alpha"].Values["annotation"] = new string('a', AgentQueries.MaxResponseBytes);
        SaveFixture(fixture);
        var error = Assert.Throws<AgentRequestException>(() => Query("""{"schemaVersion":1,"collection":"templates","limit":1}"""));
        Assert.Contains("1 MiB response limit", error.Message);
    }

    [Fact]
    public void NativeQueryTracksAbsentAndChangedCompanionTemplates()
    {
        string native = Path.Combine(_directory, "project.jed");
        File.WriteAllText(native, """
            SED 0.3.0
            SEC: MOTS
            END
            SEC: HEADER
            HDR 1 4 15 2 3 dflt.cmp 320
            HOR 100 768 0 0
            CEIL 0 0
            MIP 1 2 3 4
            LOD 1 2 3 4
            END
            """);
        const string request = """{"schemaVersion":1,"collection":"summary"}""";
        var absent = Query(request, native);
        Assert.Contains(absent.GetProperty("dependencies").EnumerateArray(), d =>
            d.GetProperty("role").GetString() == "companionTemplates" && !d.GetProperty("exists").GetBoolean());
        File.WriteAllText(Path.Combine(_directory, "mots.tpl"), "query_template none health=100\n");
        var present = Query(request, native);
        Assert.Equal(absent.GetProperty("inputSha256").GetString(), present.GetProperty("inputSha256").GetString());
        Assert.NotEqual(absent.GetProperty("sourceRevision").GetString(), present.GetProperty("sourceRevision").GetString());
        Assert.Contains(present.GetProperty("dependencies").EnumerateArray(), d =>
            d.GetProperty("role").GetString() == "companionTemplates" && d.GetProperty("exists").GetBoolean()
            && d.GetProperty("sha256").GetString() == AgentWorkspace.Hash(File.ReadAllBytes(Path.Combine(_directory, "mots.tpl"))));
        var templates = Query("""{"schemaVersion":1,"collection":"templates","search":"query_template"}""", native);
        Assert.Equal("100", Assert.Single(templates.GetProperty("items").EnumerateArray()).GetProperty("parameters").GetProperty("health").GetString());
    }

    private static double[] Doubles(JsonElement values) => values.EnumerateArray().Select(v => v.GetDouble()).ToArray();
    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
