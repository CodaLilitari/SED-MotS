using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sed.Core.Model;
using Sed.Formats.ThreeDo;

namespace Sed.Core.Tests;

public sealed class ArchitectureBindingValidatorTests
{
    [Fact]
    public void PublicDiagnosticEnumsSerializeAsSemanticNamesWithoutGlobalOptions()
    {
        var (level, thing) = BoundLevel();
        var result = ArchitectureBindingValidator.Validate(level, thing, Resolve(new[] { new[] { 3, 4 } }));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.Equal("Missing", json.RootElement.GetProperty("BaselineStatus").GetString());
        var diagnostic = json.RootElement.GetProperty("Diagnostics")[0];
        Assert.Equal("MissingHistoricalBaseline", diagnostic.GetProperty("Code").GetString());
        Assert.Equal("Warning", diagnostic.GetProperty("Severity").GetString());
    }
    [Fact]
    public void CompatibleAllLodPrefixesWithoutHistoricalHashRemainCurrentTopologyOnly()
    {
        var (level, thing) = BoundLevel();
        var resolved = Resolve(new[] { new[] { 3, 4 }, new[] { 2, 3 }, new[] { 1 }, Array.Empty<int>() });
        var result = ArchitectureBindingValidator.Validate(level, thing, resolved);
        Assert.True(result.CanUseBinding);
        Assert.True(result.TopologyCompatible);
        Assert.False(result.HistoricalBaselineVerified);
        Assert.Equal(ArchitectureBaselineStatus.Missing, result.BaselineStatus);
        Assert.Equal(0, result.ThingIndex);
        Assert.Equal(0, result.ArchitectureIndex);
        Assert.Equal(resolved.ResourceIdentity, result.ResourceIdentity);
        Assert.Equal(resolved.RawSha256, result.RawSha256);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(ArchitectureDiagnosticCode.MissingHistoricalBaseline, diagnostic.Code);
        Assert.Equal(ArchitectureDiagnosticSeverity.Warning, diagnostic.Severity);
    }

    [Fact]
    public void MatchingNativeHashVerifiesHistoricalBaselineButRawHashRemainsSeparate()
    {
        var (level, thing) = BoundLevel();
        var resolved = Resolve(new[] { new[] { 3, 4 }, new[] { 2 } });
        thing.ArchLightingSourceModelHash = resolved.Topology!.ArchitectureContentHash!.ToUpperInvariant();
        Assert.NotEqual(resolved.RawSha256, resolved.Topology.ArchitectureContentHash);
        var result = ArchitectureBindingValidator.Validate(level, thing, resolved);
        Assert.True(result.CanUseBinding);
        Assert.True(result.HistoricalBaselineVerified);
        Assert.Equal(ArchitectureBaselineStatus.Matches, result.BaselineStatus);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void SameNameAndTopologyWithChangedContentRejectsBindingWithoutMutatingArrays()
    {
        var (level, thing) = BoundLevel();
        string source = ArchitectureTopologyFixture.Build(new[] { new[] { 3, 4 } });
        thing.ArchLightingSourceModelHash = ArchitectureModelFingerprint.Compute(source)!;
        var original = level.ArchitecturalLighting[0];
        var before = original.Meshes.Select(mesh => mesh.Vertices.ToArray()).ToArray();
        var resolved = Resolve(source.Replace("0.25", "0.5"));
        var result = ArchitectureBindingValidator.Validate(level, thing, resolved);
        Assert.True(result.TopologyCompatible);
        Assert.False(result.CanUseBinding);
        Assert.Equal(ArchitectureBaselineStatus.Mismatch, result.BaselineStatus);
        Assert.Contains(result.Diagnostics, d => d.Code == ArchitectureDiagnosticCode.ModelContentMismatch);
        Assert.Same(original, level.ArchitecturalLighting[0]);
        for (int i = 0; i < before.Length; i++) Assert.Equal(before[i], original.Meshes[i].Vertices);
        Assert.Equal(0, thing.ArchLightingIndex);
        Assert.Equal(ArchitectureModelFingerprint.Compute(source), thing.ArchLightingSourceModelHash);
    }

    [Fact]
    public void UnsupportedNormalizationIsUnverifiedRatherThanContentMismatch()
    {
        var (level, thing) = BoundLevel();
        string source = ArchitectureTopologyFixture.Build(new[] { new[] { 3, 4 } });
        thing.ArchLightingSourceModelHash = ArchitectureModelFingerprint.Compute(source)!;
        var result = ArchitectureBindingValidator.Validate(level, thing, Resolve("# \u00e9\n" + source));
        Assert.True(result.TopologyCompatible);
        Assert.False(result.CanUseBinding);
        Assert.Equal(ArchitectureBaselineStatus.Unsupported, result.BaselineStatus);
        Assert.Contains(result.Diagnostics, d => d.Code == ArchitectureDiagnosticCode.UnsupportedFingerprintNormalization);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == ArchitectureDiagnosticCode.ModelContentMismatch);
    }

    [Fact]
    public void UnknownAllLodShapeCannotAuthorizeEvenAPresentModel()
    {
        var (level, thing) = BoundLevel();
        var model = new ResolvedArchitectureModel("bridge.3do", "manual-preview", new string('a', 64), null);
        var result = ArchitectureBindingValidator.Validate(level, thing, model);
        Assert.False(result.TopologyCompatible);
        Assert.False(result.CanUseBinding);
        Assert.Contains(result.Diagnostics, d => d.Code == ArchitectureDiagnosticCode.MissingTopology && d.Severity == ArchitectureDiagnosticSeverity.Warning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InstanceFlagsReplaceNearestTemplateFlags(bool inheritedEligible)
    {
        var (level, thing) = BoundLevel();
        level.Templates["child"].Values["thingflags"] = inheritedEligible ? "0x8" : "0x4";
        Assert.Equal(inheritedEligible, ArchitectureBindingValidator.IsLevelGeometry(level, thing));
        thing.Values["thingflags"] = inheritedEligible ? "0x4" : "0x8";
        Assert.Equal(!inheritedEligible, ArchitectureBindingValidator.IsLevelGeometry(level, thing));
        var result = ArchitectureBindingValidator.Validate(level, thing, Resolve(new[] { new[] { 3, 4 } }));
        Assert.Equal(!inheritedEligible, result.CanUseBinding);
        Assert.Equal(inheritedEligible, result.Diagnostics.Any(d => d.Code == ArchitectureDiagnosticCode.IneligibleThingFlags));
    }

    [Fact]
    public void InvalidFlagsAreTypedDiagnosticsWhileTheRenderingHelperRetainsItsExceptionContract()
    {
        var (level, thing) = BoundLevel();
        thing.Values["thingflags"] = "invalid";
        Assert.Throws<FormatException>(() => ArchitectureBindingValidator.IsLevelGeometry(level, thing));
        var result = ArchitectureBindingValidator.Validate(level, thing, Resolve(new[] { new[] { 3, 4 } }));
        Assert.False(result.CanUseBinding);
        Assert.Contains(result.Diagnostics, d => d.Code == ArchitectureDiagnosticCode.InvalidThingFlags);
    }

    [Fact]
    public void PerThingArchitectureAndInheritedModelResolveIndependently()
    {
        var (level, first) = BoundLevel();
        var second = level.NewThing(); second.Template = "child"; second.ArchLightingIndex = 1;
        second.ArchLightingSourceModel = "bridge.3do";
        var alternate = new ArchitecturalLightObject();
        var mesh = new ArchitecturalLightMesh(); mesh.Vertices.AddRange(new ArchitecturalLightVertex[2]); alternate.Meshes.Add(mesh);
        level.ArchitecturalLighting.Add(alternate);
        var resolved = Resolve(new[] { new[] { 3, 4 } });
        Assert.True(ArchitectureBindingValidator.Validate(level, first, resolved).CanUseBinding);
        var result = ArchitectureBindingValidator.Validate(level, second, resolved);
        Assert.False(result.CanUseBinding);
        Assert.Equal(1, result.ThingIndex);
        Assert.Equal(1, result.ArchitectureIndex);
        Assert.Contains(result.Diagnostics, d => d.Code == ArchitectureDiagnosticCode.TopLevelMeshMismatch);
    }

    [Theory]
    [MemberData(nameof(IncompatibleShapes))]
    public void TopLevelMustMatchExactlyWhileLowerLodsMustFitExistingSlots(int[][] shapes, ArchitectureDiagnosticCode code, int lod, int? mesh)
    {
        var (level, thing) = BoundLevel();
        var result = ArchitectureBindingValidator.Validate(level, thing, Resolve(shapes));
        Assert.False(result.CanUseBinding);
        Assert.False(result.TopologyCompatible);
        Assert.Contains(result.Diagnostics, d => d.Code == code && d.Geoset == lod && d.Mesh == mesh);
    }

    public static IEnumerable<object?[]> IncompatibleShapes()
    {
        yield return new object?[] { new[] { new[] { 3 } }, ArchitectureDiagnosticCode.TopLevelMeshMismatch, 0, null };
        yield return new object?[] { new[] { new[] { 2, 4 } }, ArchitectureDiagnosticCode.TopLevelVertexMismatch, 0, 0 };
        yield return new object?[] { new[] { new[] { 3, 5 } }, ArchitectureDiagnosticCode.TopLevelVertexMismatch, 0, 1 };
        yield return new object?[] { new[] { new[] { 3, 4 }, new[] { 2, 3, 0 } }, ArchitectureDiagnosticCode.LowerLodMeshOverflow, 1, null };
        yield return new object?[] { new[] { new[] { 3, 4 }, new[] { 4 } }, ArchitectureDiagnosticCode.LowerLodVertexOverflow, 1, 0 };
    }

    [Fact]
    public void MissingResourceObjectAndIdentityChangesReturnDistinctDiagnostics()
    {
        var (level, thing) = BoundLevel();
        var resolved = Resolve(new[] { new[] { 3, 4 } });
        Assert.Contains(ArchitectureBindingValidator.Validate(level, thing, null).Diagnostics, d => d.Code == ArchitectureDiagnosticCode.MissingModel);
        thing.ArchLightingIndex = -2;
        Assert.Contains(ArchitectureBindingValidator.Validate(level, thing, resolved).Diagnostics, d => d.Code == ArchitectureDiagnosticCode.MissingArchitectureObject);
        thing.ArchLightingIndex = 0;
        thing.ArchLightingSourceModel = "before.3do";
        Assert.Contains(ArchitectureBindingValidator.Validate(level, thing, resolved).Diagnostics, d => d.Code == ArchitectureDiagnosticCode.SourceModelNameMismatch);
        thing.ArchLightingSourceModel = "bridge.3do";
        Assert.Contains(ArchitectureBindingValidator.Validate(level, thing, resolved with { ModelName = "another.3do" }).Diagnostics, d => d.Code == ArchitectureDiagnosticCode.ResolvedModelNameMismatch);
        thing.ArchLightingSourceModelHash = "not-a-hash";
        Assert.Contains(ArchitectureBindingValidator.Validate(level, thing, resolved).Diagnostics, d => d.Code == ArchitectureDiagnosticCode.InvalidHistoricalFingerprint);
    }

    [Fact]
    public void UnboundThingDoesNotAcquireABaselineOrDiagnostics()
    {
        var (level, thing) = BoundLevel();
        thing.ArchLightingIndex = -1;
        var result = ArchitectureBindingValidator.Validate(level, thing, null);
        Assert.False(result.CanUseBinding);
        Assert.Equal(ArchitectureBaselineStatus.NotBound, result.BaselineStatus);
        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(500_001, 500_000)]
    public void ManuallyConstructedTopologyCannotBypassDecodedBounds(int first, int second)
    {
        var (level, thing) = BoundLevel();
        var topology = new ThreeDoTopology(2.2, new[] { new ThreeDoGeosetTopology(new[] { first, second }) }, null);
        var result = ArchitectureBindingValidator.Validate(level, thing, new("bridge.3do", "manual", "", topology));
        Assert.False(result.CanUseBinding);
        Assert.Contains(result.Diagnostics, d => d.Code == ArchitectureDiagnosticCode.InvalidTopology);
    }

    private static (Level Level, Thing Thing) BoundLevel()
    {
        var level = new Level { Kind = ProjectType.MysteriesOfTheSith };
        level.Templates["base"] = new Template { Name = "base", Values = { ["thingflags"] = "0x8", ["model3d"] = "bridge.3do" } };
        level.Templates["child"] = new Template { Name = "child", Parent = "base" };
        var thing = level.NewThing(); thing.Template = "child"; thing.ArchLightingIndex = 0; thing.ArchLightingSourceModel = "bridge.3do";
        var architecture = new ArchitecturalLightObject();
        foreach (int count in new[] { 3, 4 })
        {
            var mesh = new ArchitecturalLightMesh();
            for (int i = 0; i < count; i++) mesh.Vertices.Add(new(.125, .25, .5, .75));
            architecture.Meshes.Add(mesh);
        }
        level.ArchitecturalLighting.Add(architecture);
        return (level, thing);
    }

    private static ResolvedArchitectureModel Resolve(int[][] shapes) => Resolve(ArchitectureTopologyFixture.Build(shapes));
    private static ResolvedArchitectureModel Resolve(string source) => new("bridge.3do", "synthetic.goo!/3do/bridge.3do",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant(), ThreeDoTopologyReader.Read(source));
}
