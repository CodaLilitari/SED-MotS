using System.Text;
using System.Text.Json;
using Sed.Core.Model;
using Sed.Formats.Automation;
using Sed.Formats.Gob;
using Sed.Formats.Jkl;
using Sed.Formats.Resources;

namespace Sed.Core.Tests;

public sealed class AgentPackageTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "sed-package-" + Guid.NewGuid().ToString("N"));
    private readonly string input;
    private int archiveNumber;

    public AgentPackageTests()
    {
        Directory.CreateDirectory(directory);
        input = Path.Combine(directory, "original room.jkl");
        Room();
    }

    private void Room(Action<JklDocument>? configure = null)
    {
        var doc = new JklDocument(LevelFactory.CreateUntexturedRoom(ProjectType.MysteriesOfTheSith));
        configure?.Invoke(doc);
        // The parser explicitly recognizes empty-table colormap slot zero as
        // an editor scaffold, not a claim of a playable textured level.
        File.WriteAllText(input, JklWriter.Build(doc).Replace("\n", "\r\n"), new UTF8Encoding(false));
    }

    private ResourceArchiveRequest Archive(params (string Name, string Text)[] entries)
    {
        string path = Path.Combine(directory, "source " + archiveNumber++ + ".goo");
        GobWriter.Build(path, entries.Select(e => (e.Name, Encoding.ASCII.GetBytes(e.Text))));
        return new(path, AgentWorkspace.Hash(File.ReadAllBytes(path)));
    }

    private string Request(ResourceArchiveRequest[]? archives = null, Action<Dictionary<string, object>>? change = null)
    {
        var request = new Dictionary<string, object>
        {
            ["schemaVersion"] = 1, ["inputSha256"] = AgentWorkspace.Hash(File.ReadAllBytes(input)),
            ["levelFileName"] = "original.jkl", ["episodeTitle"] = "Original dependency fixture",
            ["resourceArchives"] = archives ?? []
        };
        change?.Invoke(request);
        return JsonSerializer.Serialize(request, AgentWorkspace.JsonOptions);
    }

    private PreparedAgentPackage Plan(string request) => AgentWorkspace.PlanPackage(input, request);
    private static JsonElement Report(PreparedAgentPackage plan) => plan.Report;
    private static string Hash(JsonElement report, string property) => report.GetProperty(property).GetString()!;
    private static object Commit(PreparedAgentPackage plan, string output, string outputHash, string manifestHash, CancellationToken token = default)
        => AgentWorkspace.CommitPackage(plan, output, outputHash, manifestHash, token);

    [Fact]
    public void TransitiveModelMaterialIsPackagedAndStockRootIsOmittedWithExactReadback()
    {
        Room(doc =>
        {
            doc.Materials.Add("stock.mat");
            var template = new Template { Name = "fixture", Parent = "none" };
            template.Values["model3d"] = "fixture.3do";
            doc.Level.Templates[template.Name] = template;
        });
        var custom = Archive(("3do/fixture.3do", "SECTION: MODELRESOURCE\nMATERIALS 1\n0: custom.mat\nSECTION: GEOMETRYDEF\n"),
            ("mat/custom.mat", "original material payload"));
        var stock = Archive(("mat/stock.mat", "original baseline payload"));
        byte[] original = File.ReadAllBytes(input);
        string request = Request([custom, stock], r => r["stockArchives"] = new[] { stock.Path });
        var plan = Plan(request); var report = Report(plan);
        Assert.True(report.GetProperty("canBuild").GetBoolean(), report.ToString());
        Assert.False(report.GetProperty("runtimeClosureVerified").GetBoolean());
        Assert.Single(report.GetProperty("stockOmissions").EnumerateArray());
        Assert.Equal(3, Directory.GetFiles(directory).Length);
        string output = Path.Combine(directory, "package with spaces.goo");
        Commit(plan, output, Hash(report, "outputSha256"), Hash(report, "manifestSha256"));
        using var gob = GobArchive.Open(output);
        Assert.Equal(new[] { "3do/fixture.3do", "episode.jk", "jkl/original.jkl", "mat/custom.mat" }, gob.Entries.Select(e => e.NormalizedName));
        Assert.Equal(original, gob.ReadBytes(gob.Find("jkl/original.jkl")!));
        Assert.Equal("original material payload", Encoding.ASCII.GetString(gob.ReadBytes(gob.Find("mat/custom.mat")!)));
        Assert.DoesNotContain(gob.Entries, e => e.NormalizedName == "mat/stock.mat");
        Assert.Equal(original, File.ReadAllBytes(input));
        Assert.Equal(Hash(report, "outputSha256"), AgentWorkspace.Hash(File.ReadAllBytes(output)));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrAmbiguousDependenciesNeverPublishEvenWhenUnverifiedIsAcknowledged(bool ambiguous)
    {
        Room(doc => doc.Materials.Add("missing.mat"));
        var first = Archive(ambiguous ? [("a/missing.mat", "a"), ("b/missing.mat", "b")] : [("unrelated", "x")]);
        var lower = Archive(("mat/missing.mat", "fallback"));
        var plan = Plan(Request(ambiguous ? [first, lower] : [first], r => r["allowUnverifiedReferences"] = true));
        var report = Report(plan);
        Assert.False(report.GetProperty("canBuild").GetBoolean());
        Assert.Contains(report.GetProperty("issues").EnumerateArray(), issue => issue.GetProperty("code").GetString() == (ambiguous ? "ambiguousResource" : "missingResource"));
        Assert.False(report.GetProperty("staticScanComplete").GetBoolean());
        Assert.Equal(JsonValueKind.Null, report.GetProperty("outputSha256").ValueKind);
        string output = Path.Combine(directory, "must-not-exist.goo");
        Assert.Throws<AgentValidationException>(() => Commit(plan, output, new string('0', 64), Hash(report, "manifestSha256")));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void UnusedStockRoleChangesManifestEvenWhenArchiveBytesStayEqual()
    {
        var unused = Archive(("mat/unused.mat", "original"));
        var first = Plan(Request([unused])); var before = Report(first);
        var second = Plan(Request([unused], r => r["stockArchives"] = new[] { unused.Path })); var after = Report(second);
        Assert.Equal(Hash(before, "outputSha256"), Hash(after, "outputSha256"));
        Assert.NotEqual(Hash(before, "manifestSha256"), Hash(after, "manifestSha256"));
        string output = Path.Combine(directory, "stale-review.goo");
        Assert.Throws<AgentConflictException>(() => Commit(second, output, Hash(before, "outputSha256"), Hash(before, "manifestSha256")));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void ChangedInputOrArchiveRefusesCapturedPlan()
    {
        var unused = Archive(("mat/unused.mat", "original"));
        var plan = Plan(Request([unused])); var report = Report(plan);
        File.AppendAllText(input, "\r\n# changed source\r\n");
        string output = Path.Combine(directory, "changed.goo");
        Assert.Throws<AgentConflictException>(() => Commit(plan, output, Hash(report, "outputSha256"), Hash(report, "manifestSha256")));
        Room(); plan = Plan(Request([unused])); report = Report(plan);
        GobWriter.Build(unused.Path, [("mat/unused.mat", Encoding.ASCII.GetBytes("changed"))]);
        Assert.Throws<AgentConflictException>(() => Commit(plan, output, Hash(report, "outputSha256"), Hash(report, "manifestSha256")));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void ExistingOutputAndCancellationPreserveFilesAndLeaveNoStaging()
    {
        var plan = Plan(Request()); var report = Report(plan);
        string output = Path.Combine(directory, "existing.goo"); File.WriteAllText(output, "keep me");
        Assert.Throws<AgentConflictException>(() => Commit(plan, output, Hash(report, "outputSha256"), Hash(report, "manifestSha256")));
        Assert.Equal("keep me", File.ReadAllText(output));
        string cancelled = Path.Combine(directory, "cancelled.goo");
        Assert.Throws<OperationCanceledException>(() => Commit(plan, cancelled, Hash(report, "outputSha256"), Hash(report, "manifestSha256"), new CancellationToken(true)));
        Assert.False(File.Exists(cancelled)); Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void GraphAndArchiveOverheadBudgetsBlockPublication()
    {
        Room(doc => doc.Materials.AddRange(["one.mat", "two.mat"]));
        var resources = Archive(("mat/one.mat", "one"), ("mat/two.mat", "two"));
        var limited = Report(Plan(Request([resources], r => r["maxNodes"] = 1)));
        Assert.False(limited.GetProperty("canBuild").GetBoolean());
        Assert.Contains(limited.GetProperty("issues").EnumerateArray(), i => i.GetProperty("code").GetString() == "nodeLimit");
        Room(); var normal = Report(Plan(Request()));
        long bytes = normal.GetProperty("outputBytes").GetInt64();
        var tooSmall = Report(Plan(Request(change: r => r["maxPackageBytes"] = bytes - 1)));
        Assert.False(tooSmall.GetProperty("canBuild").GetBoolean());
        Assert.Contains(tooSmall.GetProperty("issues").EnumerateArray(), i => i.GetProperty("code").GetString() == "packageByteLimit");
    }

    [Fact]
    public void ExhaustedGraphBudgetCannotAcquireModelsThroughArchitectureValidation()
    {
        Room(doc => {
            for (int i = 0; i < 2; i++)
            {
                string name = "fixture" + i;
                doc.Level.Templates[name] = new Template { Name = name, Parent = "none",
                    Values = { ["thingflags"] = "0x8", ["model3d"] = name + ".3do" } };
                var thing = doc.Level.NewThing(); thing.Template = name; thing.Sector = doc.Level.Sectors[0]; thing.ArchLightingIndex = i;
                thing.ArchLightingSourceModel = name + ".3do";
                var allocation = new ArchitecturalLightObject(); var mesh = new ArchitecturalLightMesh();
                mesh.Vertices.AddRange(new ArchitecturalLightVertex[3]); allocation.Meshes.Add(mesh); doc.Level.ArchitecturalLighting.Add(allocation);
            }
        });
        string model = ArchitectureTopologyFixture.Build(new[] { new[] { 3 } });
        var archive = Archive(("3do/fixture0.3do", model), ("3do/fixture1.3do", model));
        using var provider = new ExplicitArchiveResources([archive]);
        var plan = LevelPackagePlanner.Prepare(File.ReadAllBytes(input), provider, new LevelPackageOptions("original.jkl", MaxNodes: 1));
        Assert.False(plan.CanBuild);
        Assert.Contains(plan.Report.GetProperty("issues").EnumerateArray(), i => i.GetProperty("code").GetString() == "nodeLimit");
        Assert.Empty(provider.ReadRequests);
    }

    [Theory]
    [InlineData("../secret.mat")]
    [InlineData("/absolute.mat")]
    [InlineData("caf\u00e9.mat")]
    [InlineData("a//b.mat")]
    public void UnsafeResourceNamesAreRefusedBeforeLookup(string name)
    {
        Room(doc => doc.Materials.Add(name));
        var report = Report(Plan(Request()));
        Assert.False(report.GetProperty("canBuild").GetBoolean());
        Assert.Contains(report.GetProperty("issues").EnumerateArray(), i => i.GetProperty("code").GetString() == "invalidResourcePath");
    }

    [Fact]
    public void SourceAliasIsCopiedToRequestedFolderAndReadbackRemainsResolvable()
    {
        Room(doc => doc.Materials.Add("aliased.mat"));
        var archive = Archive(("alternate/aliased.mat", "exact original bytes"));
        var plan = Plan(Request([archive])); var report = Report(plan);
        Assert.True(report.GetProperty("canBuild").GetBoolean(), report.ToString());
        string output = Path.Combine(directory, "alias.goo");
        Commit(plan, output, Hash(report, "outputSha256"), Hash(report, "manifestSha256"));
        using var gob = GobArchive.Open(output);
        var match = Assert.Single(gob.FindResourceEntries("aliased.mat", "mat"));
        Assert.Equal("mat/aliased.mat", match.NormalizedName);
        Assert.Equal("exact original bytes", Encoding.ASCII.GetString(gob.ReadBytes(match)));
    }

    [Fact]
    public void EqualCustomOverrideIsIncludedAndRepeatedPlanningHasIdenticalIdentities()
    {
        Room(doc => doc.Materials.Add("shared.mat"));
        var custom = Archive(("mat/shared.mat", "same original bytes"));
        var stock = Archive(("mat/shared.mat", "same original bytes"));
        string request = Request([custom, stock], r => r["stockArchives"] = new[] { stock.Path });
        var first = Plan(request); var second = Plan(request);
        Assert.True(first.CanBuild, first.Report.ToString());
        Assert.Equal(first.OutputSha256, second.OutputSha256);
        Assert.Equal(first.ManifestSha256, second.ManifestSha256);
        Assert.Empty(first.Report.GetProperty("stockOmissions").EnumerateArray());
        Assert.Contains(first.Report.GetProperty("entries").EnumerateArray(), e => e.GetProperty("path").GetString() == "mat/shared.mat");
        var reversed = Plan(Request([stock, custom], r => r["stockArchives"] = new[] { stock.Path }));
        Assert.Single(reversed.Report.GetProperty("stockOmissions").EnumerateArray());
        Assert.NotEqual(first.ManifestSha256, reversed.ManifestSha256);
    }

    [Fact]
    public void DistinctRequestsCannotShareDestinationOrProviderCacheIdentity()
    {
        var archive = Archive(("key/overlap", "SECTION: MODELRESOURCE\nMATERIALS 0\n"), ("else/overlap", "different original bytes"));
        var plan = Plan(Request([archive], r => r["extraResources"] = new[] {
            new { name = "key/overlap", folder = "3do" }, new { name = "overlap", folder = "3do/key" } }));
        Assert.False(plan.CanBuild);
        Assert.Contains(plan.Report.GetProperty("issues").EnumerateArray(), i => i.GetProperty("code").GetString() == "destinationCollision");
    }

    [Fact]
    public void PreviouslyReadProviderCannotChangeAResourceLookupIntoAFalseResolution()
    {
        var archive = Archive(("key/overlap", "SECTION: MODELRESOURCE\nMATERIALS 0\n"), ("else/overlap", "different original bytes"));
        using var provider = new ExplicitArchiveResources([archive]);
        Assert.NotNull(provider.Read("key/overlap", "3do"));
        var plan = LevelPackagePlanner.Prepare(File.ReadAllBytes(input), provider,
            new LevelPackageOptions("original.jkl", ExtraResources: [new("overlap", "3do/key")]));
        Assert.False(plan.CanBuild);
        Assert.Contains(plan.Report.GetProperty("issues").EnumerateArray(), i => i.GetProperty("code").GetString() == "ambiguousResource");
        Assert.Equal(2, provider.ReadRequests.Count);
    }

    [Fact]
    public void OmittedStockModelStillIncludesItsCustomMaterialOverride()
    {
        var custom = Archive(("mat/shared.mat", "original custom override"));
        var stock = Archive(("3do/original.3do", "SECTION: MODELRESOURCE\nMATERIALS 1\n0: shared.mat\n"), ("mat/shared.mat", "original stock material"));
        var plan = Plan(Request([custom, stock], r => {
            r["stockArchives"] = new[] { stock.Path };
            r["extraResources"] = new[] { new { name = "original.3do", folder = "3do" } };
        }));
        Assert.True(plan.CanBuild, plan.Report.ToString());
        Assert.Equal("original.3do", Assert.Single(plan.Report.GetProperty("stockOmissions").EnumerateArray()).GetProperty("name").GetString());
        var included = Assert.Single(plan.Report.GetProperty("entries").EnumerateArray(), e => e.GetProperty("path").GetString() == "mat/shared.mat");
        Assert.Equal(AgentWorkspace.Hash(Encoding.ASCII.GetBytes("original custom override")), included.GetProperty("sha256").GetString());
    }

    [Fact]
    public void CanonicalPackageAliasCannotSilentlyShadowDifferentStockFallback()
    {
        var custom = Archive(("custom/item", "SECTION: MODELRESOURCE\nMATERIALS 0\n"));
        var stock = Archive(("baseline/item", "original keyframe bytes"));
        var plan = Plan(Request([stock, custom], r => {
            r["stockArchives"] = new[] { stock.Path };
            r["extraResources"] = new[] { new { name = "custom/item", folder = "3do" }, new { name = "item", folder = "3do/key" } };
        }));
        Assert.False(plan.CanBuild);
        Assert.Contains(plan.Report.GetProperty("issues").EnumerateArray(), i => i.GetProperty("code").GetString() == "packagedLookupCollision");
    }

    [Fact]
    public void PlacedCogValuesUseTheSameCommentNormalizedTypedSchemaAsDefaults()
    {
        Room(doc => {
            var cog = new Cog { Name = "fixture.cog" }; cog.Values.Add("placed.wav"); doc.Level.Cogs.Add(cog);
        });
        var resources = Archive(("cog/fixture.cog", "symbols\n; original comment\nsound cue\nsound localCue=default.wav local\ncog controller=17 local\nend\ncode\nend\n"),
            ("sound/placed.wav", "original placed sound"), ("sound/default.wav", "original default sound"));
        var unacknowledged = Plan(Request([resources]));
        Assert.False(unacknowledged.CanBuild);
        var acknowledged = Plan(Request([resources], r => r["allowUnverifiedReferences"] = true));
        Assert.True(acknowledged.CanBuild, acknowledged.Report.ToString());
        Assert.False(acknowledged.Report.GetProperty("runtimeClosureVerified").GetBoolean());
        var edges = acknowledged.Report.GetProperty("edges").EnumerateArray().ToArray();
        Assert.Contains(edges, e => e.GetProperty("relation").GetString() == "placedCogResource" && e.GetProperty("symbolName").GetString() == "cue");
        Assert.Contains(edges, e => e.GetProperty("relation").GetString() == "cogDefault" && e.GetProperty("symbolName").GetString() == "localCue");
        Assert.DoesNotContain(acknowledged.Report.GetProperty("nodes").EnumerateArray(), n => n.GetProperty("name").GetString() == "17");
    }

    [Fact]
    public void CogParameterCountMismatchIsNotAcknowledgable()
    {
        Room(doc => doc.Level.Cogs.Add(new Cog { Name = "fixture.cog" }));
        var resources = Archive(("cog/fixture.cog", "symbols\nsound cue\nend\n"));
        var plan = Plan(Request([resources], r => r["allowUnverifiedReferences"] = true));
        Assert.False(plan.CanBuild);
        Assert.Contains(plan.Report.GetProperty("issues").EnumerateArray(), i => i.GetProperty("code").GetString() == "cogParameterCount");
    }

    [Fact]
    public void AcknowledgingUnsupportedCogDefaultCannotHideKnownMissingPlacedResource()
    {
        Room(doc => { var cog = new Cog { Name = "fixture.cog" }; cog.Values.Add("missing.wav"); doc.Level.Cogs.Add(cog); });
        var resource = Archive(("cog/fixture.cog", "symbols\nsound fallback=\"unused.wav\" local\nsound required\nend\n"));
        var plan = Plan(Request([resource], r => r["allowUnverifiedReferences"] = true));
        Assert.False(plan.CanBuild);
        Assert.Contains(plan.Report.GetProperty("nodes").EnumerateArray(), n => n.GetProperty("name").GetString() == "missing.wav" && n.GetProperty("lookupStatus").GetString() == "missing");
        Assert.Contains(plan.Report.GetProperty("issues").EnumerateArray(), i => i.GetProperty("code").GetString() == "missingResource");
    }

    [Fact]
    public void MultipleTypedResourceChainsAreIncludedWithTheirOrigins()
    {
        var resources = Archive(("misc/pup/original.pup", "stand idle.key\n"), ("3do/key/idle.key", "original key"),
            ("misc/snd/original.snd", "create tone.wav\n"), ("sound/tone.wav", "original sound"),
            ("misc/spr/original.spr", "shared.mat 0 1 2 4 3 1 0 0 0 0\n"),
            ("misc/par/original.par", "SECTION: HEADER\nPAR 1.0\nSIZE 0.1\nMATERIAL shared.mat\nLIGHTINGMODE 3\nSECTION: GEOMETRYDEF\n"),
            ("mat/shared.mat", "original material"));
        var plan = Plan(Request([resources], r => r["extraResources"] = new[] {
            new { name = "original.pup", folder = "misc/pup" }, new { name = "original.snd", folder = "misc/snd" },
            new { name = "original.spr", folder = "misc/spr" }, new { name = "original.par", folder = "misc/par" } }));
        Assert.True(plan.CanBuild, plan.Report.ToString());
        Assert.Equal(7, plan.Report.GetProperty("nodes").GetArrayLength());
        Assert.Equal(9, plan.Report.GetProperty("entries").GetArrayLength());
        Assert.Equal(2, plan.Report.GetProperty("edges").EnumerateArray().Count(e => e.GetProperty("relation").GetString() is "spriteMaterial" or "particleMaterial"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MalformedKnownDependencyTableAlwaysBlocks(bool acknowledge)
    {
        var resource = Archive(("3do/broken.3do", "SECTION: MODELRESOURCE\nMATERIALS 1\n"));
        var plan = Plan(Request([resource], r => {
            r["extraResources"] = new[] { new { name = "broken.3do", folder = "3do" } };
            r["allowUnverifiedReferences"] = acknowledge;
        }));
        Assert.False(plan.CanBuild);
        Assert.False(plan.Report.GetProperty("staticScanComplete").GetBoolean());
        Assert.Contains(plan.Report.GetProperty("issues").EnumerateArray(), i => i.GetProperty("code").GetString() == "truncatedMaterialTable");
    }

    [Fact]
    public void RetainedResourceTablesBecomeRootsAndUnknownSectionsRequireAcknowledgement()
    {
        File.AppendAllText(input, "\r\nSECTION: SOUNDS\r\nWorld sounds 8\r\ntable.wav\r\nend\r\n");
        var resource = Archive(("sound/table.wav", "original table sound"));
        var plan = Plan(Request([resource]));
        Assert.True(plan.CanBuild, plan.Report.ToString());
        Assert.Contains(plan.Report.GetProperty("nodes").EnumerateArray(), n => n.GetProperty("name").GetString() == "table.wav");
        File.AppendAllText(input, "\r\nSECTION: FUTURE_RESOURCE_TABLE\r\ncustom-value\r\nend\r\n");
        Assert.False(Plan(Request([resource])).CanBuild);
        var acknowledged = Plan(Request([resource], r => r["allowUnverifiedReferences"] = true));
        Assert.True(acknowledged.CanBuild, acknowledged.Report.ToString());
        Assert.False(acknowledged.Report.GetProperty("staticScanComplete").GetBoolean());
        Assert.False(acknowledged.Report.GetProperty("allReferencesResolved").GetBoolean());
    }

    [Theory]
    [InlineData("maxDepth", 0, "depthLimit")]
    [InlineData("maxEdges", 1, "edgeLimit")]
    public void TransitiveWorkLimitsNeverReportCompleteClosure(string option, int limit, string issue)
    {
        var resource = Archive(("3do/original.3do", "SECTION: MODELRESOURCE\nMATERIALS 1\n0: child.mat\n"), ("mat/child.mat", "original child"));
        var plan = Plan(Request([resource], r => {
            r["extraResources"] = new[] { new { name = "original.3do", folder = "3do" } };
            r[option] = limit;
        }));
        Assert.False(plan.CanBuild);
        Assert.False(plan.Report.GetProperty("allReferencesResolved").GetBoolean());
        Assert.Contains(plan.Report.GetProperty("issues").EnumerateArray(), i => i.GetProperty("code").GetString() == issue);
    }

    [Theory]
    [InlineData(123, true)]
    [InlineData(124, false)]
    public void FullArchiveNameMustFitWithoutTruncation(int nameLength, bool accepted)
    {
        string name = new('a', nameLength);
        Room(doc => doc.Materials.Add(name));
        // The fixture writer must not truncate the boundary we are exercising.
        var resources = accepted ? new[] { Archive(("mat/" + name, "original bytes")) } : [];
        var plan = Plan(Request(resources));
        Assert.Equal(accepted, plan.CanBuild);
        if (accepted) Assert.Contains(plan.Report.GetProperty("entries").EnumerateArray(), e => e.GetProperty("path").GetString() == "mat/" + name);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1}")]
    [InlineData("{\"schemaVersion\":1,\"files\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"resourceArchives\":null}")]
    [InlineData("{\"schemaVersion\":1,\"allowUnverifiedReferences\":\"true\"}")]
    [InlineData("{\"schemaVersion\":1,\"maxNodes\":0}")]
    [InlineData("{\"schemaVersion\":1,\"maxDepth\":33}")]
    [InlineData("{\"schemaVersion\":1,\"maxPackageBytes\":134217729}")]
    [InlineData("{\"schemaVersion\":1,\"episodeTitle\":\"bad#title\"}")]
    [InlineData("{\"schemaVersion\":1,\"extraResources\":[{\"name\":\"a\",\"folder\":\"other\"}]}")]
    public void InvalidRequestsFailBeforeArchiveAcquisition(string fields)
    {
        string body = fields[..^1] + ",\"inputSha256\":\"" + AgentWorkspace.Hash(File.ReadAllBytes(input)) + "\",\"levelFileName\":\"original.jkl\"}";
        Assert.Throws<AgentRequestException>(() => Plan(body));
    }

    [Fact]
    public void StockMembershipIsValidatedBeforeOpeningAnyArchive()
    {
        var absent = new ResourceArchiveRequest(Path.Combine(directory, "absent.goo"), new string('0', 64));
        Assert.Throws<AgentRequestException>(() => Plan(Request([absent], r => r["stockArchives"] = new[] { Path.Combine(directory, "undeclared.goo") })));
    }

    [Fact]
    public void WrongGameExtensionAndOutputHashRefuseWithoutStaging()
    {
        var plan = Plan(Request());
        Assert.Throws<AgentRequestException>(() => Commit(plan, Path.Combine(directory, "wrong.gob"), plan.OutputSha256!, plan.ManifestSha256));
        Assert.Throws<AgentConflictException>(() => Commit(plan, Path.Combine(directory, "wrong.goo"), new string('0', 64), plan.ManifestSha256));
        Assert.Single(Directory.GetFiles(directory));
    }

    [Fact]
    public void SavedNullSentinelsRemainNullAfterParserCaseNormalization()
    {
        Room(doc => {
            doc.Level.Sectors[0].Sound = "none";
            var template = new Template { Name = "fixture", Parent = "NONE" };
            template.Values["soundclass"] = "NONE"; doc.Level.Templates[template.Name] = template;
        });
        Assert.Equal("NONE", JklParser.ParseDocument(File.ReadAllText(input)).Level.Sectors[0].Sound);
        var plan = Plan(Request());
        Assert.True(plan.CanBuild, plan.Report.ToString());
        Assert.Empty(plan.Report.GetProperty("nodes").EnumerateArray());
    }

    [Fact]
    public void RepeatedResourceParameterCannotSilentlyOmitAnEarlierLoadedAsset()
    {
        File.AppendAllText(input, "\r\nSECTION: TEMPLATES\r\nWorld templates 1\r\nfixture none model3d=first.3do model3d=second.3do\r\nend\r\n");
        var resources = Archive(("3do/first.3do", "SECTION: MODELRESOURCE\nMATERIALS 0\n"), ("3do/second.3do", "SECTION: MODELRESOURCE\nMATERIALS 0\n"));
        var plan = Plan(Request([resources], r => r["allowUnverifiedReferences"] = true));
        Assert.False(plan.CanBuild);
        Assert.Contains(plan.Report.GetProperty("issues").EnumerateArray(), i => i.GetProperty("code").GetString() == "repeatedResourceParameter");
    }

    [Fact]
    public void DuplicateTemplateDeclarationCannotSilentlyOmitFirstDefinition()
    {
        File.AppendAllText(input, "\r\nSECTION: TEMPLATES\r\nWorld templates 2\r\nfixture none model3d=first.3do\r\nfixture none model3d=second.3do\r\nend\r\n");
        var resources = Archive(("3do/first.3do", "SECTION: MODELRESOURCE\nMATERIALS 0\n"), ("3do/second.3do", "SECTION: MODELRESOURCE\nMATERIALS 0\n"));
        var plan = Plan(Request([resources], r => r["allowUnverifiedReferences"] = true));
        Assert.False(plan.CanBuild);
        Assert.False(plan.Report.GetProperty("staticScanComplete").GetBoolean());
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
