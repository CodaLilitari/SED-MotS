using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Sed.Agent;
using Sed.Formats.Automation;
using Sed.Formats.Gob;

namespace Sed.Core.Tests;

/// <summary>
/// The lit snapshot's MAT/CMP adapter and the checks the runner makes before any GPU work.
/// A damaged or unsupported MAT or CMP is a diagnostic that strict mode refuses, never an
/// uncaught exception, and the master palette comes from World Colormap 0. A MAT shown through
/// the retail mip fallback is a notice, not a diagnostic. Only the strict mip-fallback test may
/// reach the Vulkan stage (and succeeds or reports Vulkan as unavailable); Sed.LightingProbe and
/// tools/Test-AgentLitSnapshot.ps1 check pixels.
/// </summary>
public sealed class AgentLitSnapshotAssetTests : IDisposable
{
    private static readonly int[] Rgb565 = [5, 6, 5, 11, 5, 0];
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sed-lit-assets-" + Guid.NewGuid().ToString("N"));

    public AgentLitSnapshotAssetTests() => Directory.CreateDirectory(_directory);
    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void AdapterDecodesTrueColourAndReportsDamagedAssetsWithoutThrowing()
    {
        string archive = Archive("resources.gob",
            ("mat/rgb565.mat", Mat(16, Rgb565, 2, 1, U16(0xF800, 0x001F))),
            ("mat/plain.mat", Mat(8, [], 2, 1, [3, 4])),
            ("mat/truncated.mat", Mat(16, Rgb565, 2, 2, U16(0xF800, 0x001F))), // 2x2 needs eight bytes
            ("mat/deep.mat", Mat(24, [8, 8, 8, 16, 8, 0], 1, 1, [1, 2, 3])),
            ("misc/cmp/broken.cmp", Encoding.ASCII.GetBytes("not a colormap")),
            ("misc/cmp/sector.cmp", Cmp(20)));
        using var resources = new SnapshotResources([new SnapshotArchive(archive, Sha(archive))]);

        var rgb565 = resources.Texture("rgb565.mat")!.Value;
        Assert.True(rgb565.IsTrueColor);
        Assert.False(rgb565.IsSolid);
        Assert.Empty(rgb565.Indices);
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 0, 255, 255 }, rgb565.Rgba);
        var plain = resources.Texture("plain.mat")!.Value;
        Assert.False(plain.IsTrueColor);
        Assert.Equal(new byte[] { 3, 4 }, plain.Indices);

        // MatFile and Colormap report damage with InvalidDataException, which is not an IOException.
        // Before the adapter caught it, the first of these crashed sed-agent snapshot.
        Assert.Null(resources.Texture("truncated.mat"));
        Assert.Null(resources.Texture("deep.mat"));
        Assert.Null(resources.Colormap("broken.cmp"));
        Assert.NotNull(resources.Colormap("sector.cmp"));
        Assert.Contains("MAT 'truncated.mat': Truncated MAT mip pixel data.", resources.Diagnostics);
        Assert.Contains("MAT 'deep.mat': MAT 24-bit pixels are not supported; expected 8, 16 or 32.", resources.Diagnostics);
        Assert.Contains("CMP 'broken.cmp': Not a CMP colormap (bad magic)", resources.Diagnostics);
        // Failures are cached: a repeated lookup neither throws nor repeats its diagnostic.
        Assert.Null(resources.Texture("truncated.mat"));
        Assert.Single(resources.Diagnostics, diagnostic => diagnostic.StartsWith("MAT 'truncated.mat'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("truncated.mat", "master.cmp", "MAT 'truncated.mat': Truncated MAT mip pixel data.")]
    [InlineData("deep.mat", "master.cmp", "MAT 'deep.mat': MAT 24-bit pixels are not supported; expected 8, 16 or 32.")]
    // custom.cmp is in no archive. The runner must ask for World Colormap 0, not the sector's CMP,
    // and strict mode must refuse the dflt.cmp fallback rather than certify a different palette.
    [InlineData("wall.mat", "custom.cmp", "Master colormap 'custom.cmp' (World Colormap 0) is unavailable; using dflt.cmp's palette instead.")]
    public void StrictLitSnapshotRefusesBeforeRendering(string material, string worldColormap0, string expected)
    {
        string archive = Archive("lit.gob",
            ("mat/wall.mat", Mat(8, [], 2, 1, [1, 1])),
            ("mat/truncated.mat", Mat(16, Rgb565, 2, 2, U16(0xF800, 0x001F))),
            ("mat/deep.mat", Mat(24, [8, 8, 8, 16, 8, 0], 1, 1, [1, 2, 3])),
            ("misc/cmp/master.cmp", Cmp(10)), ("misc/cmp/sector.cmp", Cmp(20)), ("misc/cmp/dflt.cmp", Cmp(30)));
        string level = Path.Combine(_directory, "room.jkl");
        File.WriteAllText(level, Room(material, worldColormap0), new UTF8Encoding(false));
        string output = Path.Combine(_directory, "must-not-exist.png");

        var error = Assert.Throws<AgentRequestException>(() => AgentSnapshotRunner.Run(level, Request(level, archive), output));
        Assert.StartsWith("Lit snapshot asset validation failed: ", error.Message, StringComparison.Ordinal);
        Assert.Contains(expected, error.Message);
        Assert.False(File.Exists(output));
    }

    private const string FallbackNote = "Retail mip fallback: mip level 1 of 2 levels (1x1) needs 2 bytes but 0 remain; showing complete mip level 0, as retail JK does on faces nearer than the level's MipMap Distances; retail faces far enough to use mip level 1 or later show data the file never filled.";

    [Fact]
    public void MipFallbackMatIsANoticeRecordedOnceNotADiagnostic()
    {
        // A 2x2 RGB565 texture declaring two mips whose 1x1 mip is absent (DBZ zgrass pattern).
        string archive = Archive("fallback.gob", ("mat/x.mat", Mat(16, Rgb565, 2, 2, U16(0xF800, 0x001F, 0x07E0, 0xFFFF), mipCount: 2)));
        using var resources = new SnapshotResources([new SnapshotArchive(archive, Sha(archive))]);
        var first = resources.Texture("x.mat");
        var second = resources.Texture("X.MAT");
        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.True(first!.Value.IsTrueColor);
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 0, 255, 255, 0, 255, 0, 255, 255, 255, 255, 255 }, first.Value.Rgba);
        Assert.Empty(resources.Diagnostics);
        Assert.Equal(new[] { $"MAT 'x.mat': {FallbackNote}" }, resources.Notices);
    }

    [Fact]
    public void StrictLitSnapshotDoesNotRefuseAMipFallbackMat()
    {
        string archive = Archive("strict-fallback.gob",
            ("mat/wall.mat", Mat(16, Rgb565, 2, 2, U16(0xF800, 0xF800, 0xF800, 0xF800), mipCount: 2)),
            ("misc/cmp/master.cmp", Cmp(10)), ("misc/cmp/sector.cmp", Cmp(20)));
        string level = Path.Combine(_directory, "fallback-room.jkl");
        File.WriteAllText(level, Room("wall.mat", "master.cmp"), new UTF8Encoding(false));
        string output = Path.Combine(_directory, "fallback.png");
        object? receipt = null;
        try { receipt = AgentSnapshotRunner.Run(level, Request(level, archive), output); }
        catch (IOException error) when (error.Message.StartsWith("Offscreen Vulkan rendering is unavailable or failed", StringComparison.Ordinal))
        {
            // No usable Vulkan device here: the run got past strict asset validation, which is the point.
            Assert.False(File.Exists(output));
            return;
        }
        var json = JsonSerializer.SerializeToElement(receipt, AgentWorkspace.JsonOptions);
        Assert.True(File.Exists(output));
        Assert.Equal(new[] { $"MAT 'wall.mat': {FallbackNote}" }, json.GetProperty("assetNotices").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains($"MAT 'wall.mat': {FallbackNote}", json.GetProperty("warnings").EnumerateArray().Select(e => e.GetString()));
        Assert.DoesNotContain(json.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("placeholder", StringComparison.Ordinal));
        Assert.False(json.GetProperty("fullbright").GetBoolean());
        Assert.False(json.GetProperty("cullBackFaces").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("overview").ValueKind);
    }

    private static string Request(string level, string archive) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["schemaVersion"] = 1, ["inputSha256"] = Sha(level),
        ["position"] = new[] { 0.0, -3.0, 4.0 }, ["target"] = new[] { 0.0, 0.0, 0.0 },
        ["fieldOfViewDegrees"] = 65, ["width"] = 64, ["height"] = 64, ["mode"] = "lit",
        ["resourceArchives"] = new[] { new Dictionary<string, object> { ["path"] = archive, ["sha256"] = Sha(archive) } },
    });

    // One MotS sector on CMP 1 under World Colormap 0; one fullbright floor surface.
    private static string Room(string material, string worldColormap0) => $"""
        SECTION: HEADER
        VERSION 1
        END
        SECTION: MATERIALS
        World materials 1
        0: {material} 1 1
        END
        SECTION: GEORESOURCE
        World colormaps 2
        0: {worldColormap0}
        1: sector.cmp
        World vertices 4
        0: -2 -2 0
        1: 2 -2 0
        2: 2 2 0
        3: -2 2 0
        World texture vertices 4
        0: 0 0
        1: 2 0
        2: 2 2
        3: 0 2
        World adjoins 0
        World surfaces 1
        0: 0 0x0 0x0 4 0 3 -1 0 4 0,0 1,1 2,2 3,3 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1
        0: 0 0 1
        END
        SECTION: SECTORS
        World sectors 1
        SECTOR 0
        FLAGS 0
        AMBIENT LIGHT 0
        EXTRA LIGHT 0
        COLORMAP 1
        TINT 0 0 0
        VERTICES 4
        0: 0
        1: 1
        2: 2
        3: 3
        SURFACES 0 1
        END

        """;

    /// <summary>
    /// A one-cel textured MAT; channels are r/g/b bits then r/g/b shifts. It declares
    /// <paramref name="mipCount"/> mips but stores only <paramref name="pixels"/>.
    /// </summary>
    private static byte[] Mat(int bpp, int[] channels, int width, int height, byte[] pixels, int mipCount = 1)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("MAT "));
        foreach (int value in new[] { 0x32, 2, 1, 1, bpp == 8 ? 0 : 1, bpp }) writer.Write(value);
        for (int i = 0; i < 6; i++) writer.Write(i < channels.Length ? channels[i] : 0);
        writer.Write(new byte[24]); // bit differences and alpha fields complete the 76-byte header
        writer.Write(new byte[40]); // one cel header selecting texture zero
        foreach (int value in new[] { width, height, 0, 0, 0, mipCount }) writer.Write(value);
        writer.Write(pixels);
        return stream.ToArray();
    }

    private static byte[] Cmp(byte marker)
    {
        var bytes = new byte[64 + 768 + 16384];
        Encoding.ASCII.GetBytes("CMP ").CopyTo(bytes, 0);
        bytes[64 + 3] = marker;
        for (int row = 0; row < 64; row++)
            for (int index = 0; index < 256; index++) bytes[832 + row * 256 + index] = (byte)index;
        return bytes;
    }

    private static byte[] U16(params ushort[] values)
    {
        var bytes = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), values[i]);
        return bytes;
    }

    private string Archive(string name, params (string Name, byte[] Bytes)[] entries)
    {
        string path = Path.Combine(_directory, name);
        GobWriter.Build(path, entries);
        return path;
    }

    private static string Sha(string path) => AgentWorkspace.Hash(File.ReadAllBytes(path));
}
