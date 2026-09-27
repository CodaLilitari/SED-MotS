using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sed.Formats.Automation;
using Sed.Formats.Resources;

namespace Sed.Mcp.Infrastructure;

/// <summary>
/// Verifies a returned fixed-worker artifact against its request and decoded pixels.
/// This accepts SED PngWriter's three-chunk, noninterlaced RGBA8/filter-zero format;
/// it is intentionally not a general-purpose PNG decoder or a rendering oracle.
/// BCL zlib decoding, exact decoded length and Adler checks do not establish a
/// canonical DEFLATE encoding or prove the absence of ignored compressed tails.
/// No file is reopened, changed or deleted, including on a failed evidence check.
/// </summary>
internal static class SnapshotEvidence
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    internal static void Validate(SnapshotWorkerResult result, string requestJson)
    {
        try
        {
            var request = AgentSnapshotRequest.Parse(requestJson);
            var receipt = result.Receipt;
            Require(receipt.ValueKind == JsonValueKind.Object, "Missing snapshot receipt object.");
            Require(Integer(receipt, "schemaVersion") == 1, "Unsupported receipt schema.");
            Require(Hash(receipt, "inputSha256") == request.InputSha256, "Receipt source hash differs from the request.");
            string sourceRevision = Hash(receipt, "sourceRevision");
            // The MCP dispatcher accepts only JKL previews. Its source revision
            // is exactly the file hash and it has no native template dependencies.
            Require(sourceRevision == request.InputSha256, "JKL source revision must equal the requested input hash.");
            Require(request.SourceRevision is null || request.SourceRevision == sourceRevision, "Receipt source revision differs from the request.");
            var dependencies = receipt.GetProperty("dependencies");
            Require(dependencies.ValueKind == JsonValueKind.Array && dependencies.GetArrayLength() == 0,
                "JKL snapshots cannot declare native template dependencies.");
            Require(Hash(receipt, "requestSha256") == AgentWorkspace.Hash(Encoding.UTF8.GetBytes(requestJson)), "Receipt does not bind the exact request bytes.");
            Require(Integer(receipt, "width") == request.Width && Integer(receipt, "height") == request.Height,
                "Receipt dimensions differ from the request.");
            Require(Text(receipt, "mode") == request.Mode, "Receipt mode differs from the request.");
            Require(receipt.GetProperty("guiLaunched").ValueKind == JsonValueKind.False, "Receipt must report no GUI launch.");

            var camera = new {
                position = new[] { request.Position.X, request.Position.Y, request.Position.Z },
                target = new[] { request.Target.X, request.Target.Y, request.Target.Z },
                request.FieldOfViewDegrees, request.NearPlane, request.FarPlane, up = "Z", projection = "perspective",
            };
            byte[] cameraBytes = JsonSerializer.SerializeToUtf8Bytes(camera, AgentWorkspace.JsonOptions);
            using var expectedCamera = JsonDocument.Parse(cameraBytes);
            Require(JsonElement.DeepEquals(receipt.GetProperty("camera"), expectedCamera.RootElement),
                "Receipt camera differs from the requested pose or default clipping planes.");
            Require(Hash(receipt, "cameraSha256") == AgentWorkspace.Hash(cameraBytes), "Receipt camera digest differs from the requested camera.");
            ValidateResourcesAndMode(receipt, request);

            byte[] png = result.ImageBytes;
            Require(png.Length is >= 8 and <= SnapshotWorker.MaxImageBytes, "PNG size exceeds the fixed-worker bounds.");
            string pngHash = AgentWorkspace.Hash(png);
            Require(Hash(receipt, "outputSha256") == pngHash &&
                string.Equals(result.Sha256, pngHash, StringComparison.OrdinalIgnoreCase) &&
                Integer(receipt, "outputBytes") == png.LongLength, "PNG bytes do not match the worker's output identity.");
            var (rgbaHash, nonBackground) = DecodeOwnedPng(png, request.Width, request.Height);
            Require(rgbaHash == Hash(receipt, "rgbaSha256"), "Decoded RGBA pixels differ from the receipt.");
            Require(Integer(receipt, "nonBackgroundPixels") == nonBackground, "Decoded non-background count differs from the receipt.");
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or AgentRequestException or
            KeyNotFoundException or InvalidOperationException or ArgumentException or OverflowException or IOException)
        {
            throw new SnapshotWorkerException("snapshot_evidence_mismatch",
                "Snapshot output could not be verified: " + error.Message, true, result.OutputState,
                exitCode: 0, receipt: result.Receipt, standardError: result.StandardError, inner: error);
        }
    }

    private static void ValidateResourcesAndMode(JsonElement receipt, AgentSnapshotRequest request)
    {
        var archives = receipt.GetProperty("resourceArchives");
        var entries = receipt.GetProperty("usedResources");
        Require(archives.ValueKind == JsonValueKind.Array && entries.ValueKind == JsonValueKind.Array, "Missing resource arrays.");
        if (request.Mode == "diagnostic")
        {
            Require(archives.GetArrayLength() == 0 && entries.GetArrayLength() == 0 &&
                receipt.GetProperty("resourceRevision").ValueKind == JsonValueKind.Null &&
                receipt.GetProperty("lightingMode").ValueKind == JsonValueKind.Null &&
                receipt.GetProperty("renderProfile").ValueKind == JsonValueKind.Null,
                "Diagnostic snapshot unexpectedly declares lit resources or settings.");
            Require(Text(receipt, "renderer") == "Vulkan flat diagnostic geometry", "Unexpected diagnostic renderer.");
            return;
        }
        Require(Text(receipt, "renderer") == "Vulkan static lit scene" && Text(receipt, "renderProfile") == request.RenderProfile,
            "Lit renderer or profile differs from the request.");
        string lighting = Text(receipt, "lightingMode");
        // The game-selected choice derives from source kind, which this API does
        // not reparse. Explicit mono/RGB requests are exactly checkable here.
        Require(lighting is "mono" or "rgb" && (request.LightingMode == "game" || lighting == request.LightingMode),
            "Receipt lighting mode differs from the explicit request.");
        Require(archives.GetArrayLength() == request.ResourceArchives.Count, "Receipt archive count differs from the request.");
        var capturedArchives = new List<ResourceArchiveReceipt>();
        int index = 0;
        foreach (var archive in archives.EnumerateArray())
        {
            var expected = request.ResourceArchives[index++];
            string path = Text(archive, "path"), sha = Hash(archive, "sha256");
            long bytes = Integer(archive, "bytes");
            Require(SamePath(path, expected.Path) && sha == expected.Sha256 && bytes is >= 0 and <= 536_870_912,
                "Receipt archive order, path, hash or size differs from the request contract.");
            capturedArchives.Add(new(path, sha, bytes));
        }
        var capturedEntries = new List<ResourceEntryReceipt>();
        foreach (var entry in entries.EnumerateArray())
        {
            string archive = Text(entry, "archive");
            long bytes = Integer(entry, "bytes");
            Require(capturedArchives.Any(item => SamePath(item.Path, archive)) && bytes >= 0,
                "Used resource does not belong to a requested archive or has invalid size.");
            capturedEntries.Add(new(Text(entry, "requested"), archive, Text(entry, "entry"), bytes, Hash(entry, "sha256")));
        }
        Require(Hash(receipt, "resourceRevision") == ExplicitArchiveResources.ComputeRevision(capturedArchives, capturedEntries),
            "Resource revision does not bind the returned resource inventory.");
    }

    private static (string Hash, long NonBackground) DecodeOwnedPng(byte[] png, int width, int height)
    {
        Require(png.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "Invalid PNG signature.");
        int offset = 8;
        var ihdr = Chunk(png, ref offset, "IHDR");
        Require(ihdr.Length == 13 && BinaryPrimitives.ReadUInt32BigEndian(ihdr[..4]) == width &&
            BinaryPrimitives.ReadUInt32BigEndian(ihdr.Slice(4, 4)) == height, "PNG dimensions differ from the requested image.");
        Require(ihdr[8] == 8 && ihdr[9] == 6 && ihdr[10] == 0 && ihdr[11] == 0 && ihdr[12] == 0,
            "PNG is not the owned noninterlaced RGBA8 format.");
        var compressed = Chunk(png, ref offset, "IDAT");
        Require(Chunk(png, ref offset, "IEND").Length == 0 && offset == png.Length, "Invalid IEND or trailing PNG bytes.");
        Require(compressed.Length >= 6 && compressed[0] == 0x78 && (compressed[1] & 0x20) == 0 &&
            ((compressed[0] << 8) + compressed[1]) % 31 == 0, "Invalid owned PNG zlib header.");
        uint declaredAdler = BinaryPrimitives.ReadUInt32BigEndian(compressed[^4..]);
        using var input = new MemoryStream(compressed.ToArray(), writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress, leaveOpen: true);
        using var pixels = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int stride = checked(width * 4);
        byte[] row = new byte[stride + 1];
        uint adlerA = 1, adlerB = 0;
        long nonBackground = 0;
        for (int y = 0; y < height; y++)
        {
            zlib.ReadExactly(row);
            Require(row[0] == 0, "Owned PNG scanlines must use filter zero.");
            pixels.AppendData(row, 1, stride);
            foreach (byte value in row) { adlerA = (adlerA + value) % 65521; adlerB = (adlerB + adlerA) % 65521; }
            for (int x = 1; x < row.Length; x += 4)
                if (Math.Abs(row[x] - 13) > 1 || Math.Abs(row[x + 1] - 13) > 1 || Math.Abs(row[x + 2] - 20) > 1) nonBackground++;
        }
        Require(zlib.ReadByte() == -1 && input.Position == input.Length, "Zlib pixels exceed the declared dimensions or leave unused input.");
        Require(((adlerB << 16) | adlerA) == declaredAdler, "Zlib trailer does not match decoded scanlines.");
        return (Convert.ToHexString(pixels.GetHashAndReset()).ToLowerInvariant(), nonBackground);
    }

    private static ReadOnlySpan<byte> Chunk(byte[] png, ref int offset, string expectedType)
    {
        Require(png.Length - offset >= 12, "Truncated PNG chunk.");
        uint length = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4));
        Require(length <= png.Length - offset - 12, "PNG chunk exceeds the available bytes.");
        int size = checked((int)length);
        var type = png.AsSpan(offset + 4, 4);
        Require(type.SequenceEqual(Encoding.ASCII.GetBytes(expectedType)), "Unexpected PNG chunk order or type.");
        var data = png.AsSpan(offset + 8, size);
        uint crc = uint.MaxValue;
        foreach (byte value in type) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
        foreach (byte value in data) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
        Require((crc ^ uint.MaxValue) == BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + size, 4)), "PNG chunk CRC differs.");
        offset += size + 12;
        return data;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < table.Length; n++)
        {
            uint value = n;
            for (int bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xedb88320 ^ (value >> 1) : value >> 1;
            table[n] = value;
        }
        return table;
    }
    private static bool SamePath(string actual, string expected) => Path.IsPathFullyQualified(actual) &&
        Path.GetFullPath(actual).Equals(Path.GetFullPath(expected), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static long Integer(JsonElement value, string name)
    {
        var field = value.GetProperty(name);
        Require(field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out _), "Invalid receipt integer: " + name);
        return field.GetInt64();
    }
    private static string Text(JsonElement value, string name)
    {
        var field = value.GetProperty(name);
        Require(field.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(field.GetString()), "Invalid receipt string: " + name);
        return field.GetString()!;
    }
    private static string Hash(JsonElement value, string name)
    {
        string hash = Text(value, name);
        Require(hash.Length == 64 && hash.All(Uri.IsHexDigit), "Invalid receipt SHA256: " + name);
        return hash.ToLowerInvariant();
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); }
}
