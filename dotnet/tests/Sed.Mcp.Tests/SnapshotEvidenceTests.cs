using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sed.Formats.Automation;
using Sed.Formats.Resources;
using Sed.Mcp.Infrastructure;
using Sed.Rendering;

namespace Sed.Mcp.Tests;

public sealed class SnapshotEvidenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptsOwned64PixelImageAndRequestDerivedDefaults(bool lit)
    {
        using var fixture = new Fixture(lit);
        SnapshotEvidence.Validate(fixture.Result(), fixture.RequestText);
        Assert.Equal(fixture.OriginalFile, File.ReadAllBytes(fixture.OutputPath));
    }

    [Fact]
    public void AcceptsExplicitCameraClippingAndLitProfile()
    {
        using var fixture = new Fixture(lit: true, customCamera: true);
        SnapshotEvidence.Validate(fixture.Result(), fixture.RequestText);
    }

    [Theory]
    [InlineData("mono")]
    [InlineData("rgb")]
    public void GameSelectedLightingMayResolveToEitherDeclaredMode(string selected)
    {
        using var fixture = new Fixture(lit: true, lightingMode: "game");
        fixture.Receipt["lightingMode"] = selected;
        SnapshotEvidence.Validate(fixture.Result(), fixture.RequestText);
    }

    [Fact]
    public void RefusesReorderedArchivePriority()
    {
        using var fixture = new Fixture(lit: true, archiveCount: 2);
        SnapshotEvidence.Validate(fixture.Result(), fixture.RequestText);
        var archives = fixture.Receipt["resourceArchives"]!.AsArray();
        var first = archives[0]!.DeepClone();
        archives[0] = archives[1]!.DeepClone();
        archives[1] = first;
        fixture.Refused();
    }

    [Theory]
    [InlineData("inputSha256")]
    [InlineData("sourceRevision")]
    [InlineData("requestSha256")]
    [InlineData("cameraSha256")]
    [InlineData("rgbaSha256")]
    [InlineData("outputSha256")]
    public void RefusesWrongReceiptHashWithoutTouchingFile(string field)
    {
        using var fixture = new Fixture();
        fixture.Receipt[field] = new string('0', 64);
        fixture.Refused();
    }

    [Fact]
    public void SourceRevisionMustBeHashEvenWhenRequestOmitsIt()
    {
        using var fixture = new Fixture(includeRevision: false);
        SnapshotEvidence.Validate(fixture.Result(), fixture.RequestText);
        fixture.Receipt["sourceRevision"] = "not-a-sha256";
        fixture.Refused();
    }

    [Fact]
    public void JklRevisionMustEqualInputEvenWhenRequestOmitsIt()
    {
        using var fixture = new Fixture(includeRevision: false);
        fixture.Receipt["sourceRevision"] = new string('b', 64);
        fixture.Refused();
    }

    [Fact]
    public void JklReceiptCannotDeclareNativeTemplateDependencies()
    {
        using var fixture = new Fixture();
        fixture.Receipt["dependencies"] = new JsonArray(new JsonObject { ["role"] = "bundledTemplates" });
        fixture.Refused();
    }

    [Fact]
    public void RequestHashBindsExactJsonBytes()
    {
        using var fixture = new Fixture();
        Assert.Throws<SnapshotWorkerException>(() => SnapshotEvidence.Validate(fixture.Result(), fixture.RequestText + " "));
    }

    [Theory]
    [InlineData("width", "65")]
    [InlineData("height", "65")]
    [InlineData("outputBytes", "8")]
    [InlineData("mode", "\"lit\"")]
    [InlineData("renderer", "\"a different renderer\"")]
    [InlineData("guiLaunched", "true")]
    [InlineData("nonBackgroundPixels", "0")]
    [InlineData("lightingMode", "\"rgb\"")]
    [InlineData("renderProfile", "\"openjk\"")]
    public void RefusesContradictoryReceiptFields(string field, string value)
    {
        using var fixture = new Fixture();
        fixture.Receipt[field] = JsonNode.Parse(value);
        fixture.Refused();
    }

    [Theory]
    [InlineData("position", "[1,-2,0]")]
    [InlineData("target", "[0,1,0]")]
    [InlineData("fieldOfViewDegrees", "74")]
    [InlineData("nearPlane", "0.02")]
    [InlineData("farPlane", "999")]
    [InlineData("up", "\"Y\"")]
    [InlineData("projection", "\"orthographic\"")]
    [InlineData("extra", "1")]
    public void RefusesCameraMismatchEvenWithRecomputedCameraHash(string field, string value)
    {
        using var fixture = new Fixture();
        fixture.Receipt["camera"]![field] = JsonNode.Parse(value);
        fixture.Receipt["cameraSha256"] = Hash(Encoding.UTF8.GetBytes(fixture.Receipt["camera"]!.ToJsonString(AgentWorkspace.JsonOptions)));
        fixture.Refused();
    }

    [Theory]
    [InlineData("path")]
    [InlineData("sha256")]
    [InlineData("count")]
    [InlineData("revision")]
    [InlineData("entry-archive")]
    public void RefusesResourceInventoryMismatch(string problem)
    {
        using var fixture = new Fixture(lit: true);
        if (problem == "path") fixture.Receipt["resourceArchives"]![0]!["path"] = Path.Combine(fixture.Root, "unrequested.goo");
        if (problem == "sha256") fixture.Receipt["resourceArchives"]![0]!["sha256"] = new string('0', 64);
        if (problem == "count") fixture.Receipt["resourceArchives"]!.AsArray().Clear();
        if (problem == "revision") fixture.Receipt["resourceRevision"] = new string('0', 64);
        if (problem == "entry-archive") fixture.Receipt["usedResources"]![0]!["archive"] = Path.Combine(fixture.Root, "unrequested.goo");
        fixture.Refused();
    }

    [Theory]
    [InlineData("lightingMode", "mono")]
    [InlineData("renderProfile", "palette")]
    [InlineData("renderer", "Vulkan flat diagnostic geometry")]
    public void RefusesLitSettingsThatContradictExplicitRequest(string field, string value)
    {
        using var fixture = new Fixture(lit: true);
        fixture.Receipt[field] = value;
        fixture.Refused();
    }

    [Fact]
    public void RefusesUnexpectedDiagnosticResources()
    {
        using var fixture = new Fixture();
        fixture.Receipt["resourceArchives"] = new JsonArray(new JsonObject { ["path"] = Path.Combine(fixture.Root, "extra.goo"), ["sha256"] = new string('a', 64), ["bytes"] = 17 });
        fixture.Refused();
    }

    [Theory]
    [InlineData("signature-only")]
    [InlineData("truncated")]
    [InlineData("crc")]
    [InlineData("trailing")]
    [InlineData("huge-chunk")]
    [InlineData("wrong-dimensions")]
    [InlineData("order")]
    public void RefusesInvalidPngStructureEvenWhenFileDigestMatchesReceipt(string problem)
    {
        using var fixture = new Fixture();
        byte[] png = fixture.Image.ToArray();
        switch (problem)
        {
            case "signature-only": png = png[..8]; break;
            case "truncated": png = png[..^3]; break;
            case "crc": png[^1] ^= 1; break;
            case "trailing": png = [.. png, 0]; break;
            case "huge-chunk": BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(8, 4), uint.MaxValue); break;
            case "wrong-dimensions":
                var resized = Chunks(png);
                BinaryPrimitives.WriteInt32BigEndian(resized[0].Data.AsSpan(0, 4), 65);
                png = Encode(resized); break;
            case "order":
                var chunks = Chunks(png);
                png = Encode([chunks[1], chunks[0], chunks[2]]); break;
        }
        fixture.SetImage(png);
        fixture.Refused();
    }

    [Theory]
    [InlineData(8, 16)]
    [InlineData(9, 2)]
    [InlineData(10, 1)]
    [InlineData(11, 1)]
    [InlineData(12, 1)]
    public void RefusesUnsupportedOwnedPngModes(int field, int value)
    {
        using var fixture = new Fixture();
        var chunks = Chunks(fixture.Image);
        chunks[0].Data[field] = (byte)value;
        fixture.SetImage(Encode(chunks));
        fixture.Refused();
    }

    [Theory]
    [InlineData("short-raw")]
    [InlineData("long-raw")]
    [InlineData("filter")]
    [InlineData("pixels")]
    [InlineData("truncated-zlib")]
    [InlineData("adler")]
    [InlineData("zlib-header")]
    public void RefusesInvalidScanlinesZlibOrPixelEvidence(string problem)
    {
        using var fixture = new Fixture();
        var chunks = Chunks(fixture.Image);
        byte[] raw = fixture.RawScanlines();
        switch (problem)
        {
            case "short-raw": raw = raw[..^1]; break;
            case "long-raw": raw = [.. raw, 0]; break;
            case "filter": raw[3 * 257] = 1; break;
            case "pixels": raw[1] ^= 64; break;
        }
        // Mutations update the PNG CRC and output digest, so the refusal must
        // come from scanline/zlib/pixel validation rather than those envelopes.
        byte[] compressed = Compress(raw);
        if (problem == "truncated-zlib") compressed = compressed[..^4];
        if (problem == "adler") compressed[^1] ^= 1;
        if (problem == "zlib-header") compressed[0] = 0;
        chunks[1] = ("IDAT", compressed);
        fixture.SetImage(Encode(chunks));
        fixture.Refused();
    }

    [Fact]
    public void MinimalReceiptAndEightByteSignatureCannotClaimSuccessfulPreview()
    {
        using var fixture = new Fixture();
        fixture.SetImage(fixture.Image[..8]);
        fixture.Receipt = new JsonObject { ["schemaVersion"] = 1, ["written"] = fixture.OutputPath,
            ["outputBytes"] = 8, ["outputSha256"] = Hash(fixture.Image) };
        fixture.Refused();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static byte[] Compress(byte[] raw)
    {
        using var result = new MemoryStream();
        using (var stream = new ZLibStream(result, CompressionLevel.Optimal, leaveOpen: true)) stream.Write(raw);
        return result.ToArray();
    }
    private static List<(string Type, byte[] Data)> Chunks(byte[] png)
    {
        var result = new List<(string, byte[])>();
        int offset = 8;
        while (offset < png.Length)
        {
            int size = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            result.Add((Encoding.ASCII.GetString(png, offset + 4, 4), png.AsSpan(offset + 8, size).ToArray()));
            offset += size + 12;
        }
        return result;
    }
    private static byte[] Encode(List<(string Type, byte[] Data)> chunks)
    {
        using var stream = new MemoryStream();
        stream.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        byte[] word = new byte[4];
        foreach (var (name, data) in chunks)
        {
            BinaryPrimitives.WriteInt32BigEndian(word, data.Length); stream.Write(word);
            byte[] type = Encoding.ASCII.GetBytes(name); stream.Write(type); stream.Write(data);
            uint crc = uint.MaxValue;
            foreach (byte value in type.Concat(data))
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++) crc = (crc & 1) == 1 ? (crc >> 1) ^ 0xedb88320 : crc >> 1;
            }
            BinaryPrimitives.WriteUInt32BigEndian(word, crc ^ uint.MaxValue); stream.Write(word);
        }
        return stream.ToArray();
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "sed-snapshot-evidence-" + Guid.NewGuid().ToString("N"));
        public string OutputPath => Path.Combine(Root, "preview.png");
        public string RequestText { get; }
        public JsonObject Receipt { get; set; }
        public byte[] Pixels { get; } = new byte[64 * 64 * 4];
        public byte[] Image { get; private set; }
        public byte[] OriginalFile { get; }
        public SnapshotOutputState OutputState { get; }
        public Fixture(bool lit = false, bool customCamera = false, bool includeRevision = true, string lightingMode = "rgb", int archiveCount = 1)
        {
            Directory.CreateDirectory(Root);
            for (int i = 0; i < Pixels.Length; i += 4)
            { Pixels[i] = (byte)(i / 4 % 256); Pixels[i + 1] = 57; Pixels[i + 2] = 93; Pixels[i + 3] = 255; }
            PngWriter.Write(OutputPath, Pixels, 64, 64);
            OriginalFile = File.ReadAllBytes(OutputPath); Image = OriginalFile.ToArray();
            OutputState = new(OutputPath, "file", false, Image.Length, Hash(Image));
            var request = new JsonObject { ["schemaVersion"] = 1, ["inputSha256"] = new string('a', 64),
                ["position"] = new JsonArray(0, -2, 0), ["target"] = new JsonArray(0, 0, 0),
                ["fieldOfViewDegrees"] = 75, ["width"] = 64, ["height"] = 64 };
            if (includeRevision) request["sourceRevision"] = new string('a', 64);
            var archives = lit ? Enumerable.Range(0, archiveCount).Select(index =>
                new ResourceArchiveReceipt(Path.Combine(Root, $"original-{index}.goo"), new string((char)('c' + index), 64), 123)).ToArray() : [];
            var entries = lit ? new[] { new ResourceEntryReceipt("original.mat", archives[0].Path, "mat/original.mat", 17, new string('d', 64)) } : [];
            if (lit)
            {
                request["mode"] = "lit"; request["lightingMode"] = lightingMode; request["renderProfile"] = customCamera ? "palette" : "openjk";
                request["resourceArchives"] = JsonSerializer.SerializeToNode(archives.Select(a => new { a.Path, a.Sha256 }), AgentWorkspace.JsonOptions);
            }
            if (customCamera) { request["nearPlane"] = 0.02; request["farPlane"] = 77; request["fieldOfViewDegrees"] = 55; }
            RequestText = request.ToJsonString(AgentWorkspace.JsonOptions);
            var parsed = AgentSnapshotRequest.Parse(RequestText);
            var camera = new { position = new[] { 0.0, -2, 0 }, target = new[] { 0.0, 0, 0 },
                parsed.FieldOfViewDegrees, parsed.NearPlane, parsed.FarPlane, up = "Z", projection = "perspective" };
            Receipt = JsonSerializer.SerializeToNode(new {
                schemaVersion = 1, written = OutputPath, outputSha256 = Hash(Image), outputBytes = Image.Length,
                inputSha256 = parsed.InputSha256, sourceRevision = parsed.InputSha256, dependencies = Array.Empty<object>(), requestSha256 = Hash(Encoding.UTF8.GetBytes(RequestText)),
                camera, cameraSha256 = Hash(JsonSerializer.SerializeToUtf8Bytes(camera, AgentWorkspace.JsonOptions)), width = 64, height = 64,
                rgbaSha256 = Hash(Pixels), mode = parsed.Mode, lightingMode = lit ? "rgb" : null,
                renderProfile = lit ? parsed.RenderProfile : null,
                renderer = lit ? "Vulkan static lit scene" : "Vulkan flat diagnostic geometry", guiLaunched = false,
                resourceArchives = archives, usedResources = entries,
                resourceRevision = lit ? ExplicitArchiveResources.ComputeRevision(archives, entries) : null, nonBackgroundPixels = 4096,
            }, AgentWorkspace.JsonOptions)!.AsObject();
        }
        public void SetImage(byte[] image)
        { Image = image; Receipt["outputBytes"] = image.Length; Receipt["outputSha256"] = Hash(image); }
        public SnapshotWorkerResult Result() => new(JsonSerializer.SerializeToElement(Receipt), Image, Hash(Image), OutputState, "synthetic stderr");
        public byte[] RawScanlines()
        {
            var raw = new byte[(64 * 4 + 1) * 64];
            for (int y = 0; y < 64; y++) Pixels.AsSpan(y * 256, 256).CopyTo(raw.AsSpan(y * 257 + 1, 256));
            return raw;
        }
        public void Refused()
        {
            var result = Result();
            var error = Assert.Throws<SnapshotWorkerException>(() => SnapshotEvidence.Validate(result, RequestText));
            Assert.Equal("snapshot_evidence_mismatch", error.Code);
            Assert.True(error.IsHostFault);
            Assert.Equal(OutputState, error.OutputState);
            Assert.Equal("synthetic stderr", error.StandardError);
            Assert.True(JsonElement.DeepEquals(result.Receipt, error.Receipt!.Value));
            Assert.Equal(OriginalFile, File.ReadAllBytes(OutputPath));
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
