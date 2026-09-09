using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Sed.Core.Math;
using Sed.Formats.Automation;
using Sed.Rendering;
using Sed.Rendering.Vulkan;

namespace Sed.Agent;

/// <summary>One bounded offscreen diagnostic view, with no window or game process.</summary>
public static class AgentSnapshotRunner
{
    private static readonly ColorF[] Colors = [new(0.55f, 0.72f, 0.85f), new(0.76f, 0.61f, 0.43f),
        new(0.47f, 0.72f, 0.56f), new(0.70f, 0.53f, 0.74f), new(0.73f, 0.73f, 0.48f), new(0.47f, 0.69f, 0.73f)];

    public static object Run(string path, string requestText, string outputPath)
    {
        var timer = Stopwatch.StartNew();
        var request = AgentSnapshotRequest.Parse(requestText);
        outputPath = AgentSnapshotRequest.PrepareOutputPath(outputPath); // before source parsing or any native/GPU operation
        var source = AgentWorkspace.LoadWithProvenance(path);
        request.VerifySource(source);
        var level = source.Document.Level; // independent parsed model, never the live GUI document
        var mesh = new Mesh();
        using var resources = request.Mode == "lit" ? new SnapshotResources(request.ResourceArchives) : null;
        RenderScene? scene = null;
        var renderWarnings = new List<string>();
        int omittedThings = level.Things.Count;
        int surfaceCount = 0, omittedPortals = 0, degenerateTriangles = 0;
        long triangleBudget = 0;
        if (resources is not null)
        {
            var assembler = new SceneAssembler(AgentSnapshotRequest.MaxTriangles);
            assembler.AddLevel(level);
            var unmodeled = assembler.AddThings(level, resources.Model);
            omittedThings = unmodeled.Count;
            scene = assembler.Build();
            if (request.LightingMode != "game") scene.LightingMode = request.LightingMode == "rgb" ? RenderLightingMode.Rgb : RenderLightingMode.Mono;
            scene.UsePaletteLightRamp = request.RenderProfile == "palette";
            mesh = scene.Mesh;
            foreach (var batch in scene.Submeshes)
            {
                _ = resources.Texture(batch.Material);
                _ = resources.Colormap(string.IsNullOrWhiteSpace(batch.Colormap) ? "dflt.cmp" : batch.Colormap);
            }
            renderWarnings.AddRange(scene.Diagnostics);
            var noModel = unmodeled.Where(t => string.IsNullOrWhiteSpace(level.GetThingModel(t))).ToList();
            if (noModel.Count > 0)
                renderWarnings.Add($"{noModel.Count} things have no resolved 3DO model and are omitted (including invisible controllers); "
                    + $"first indices: {string.Join(", ", noModel.Take(32).Select(t => level.Things.IndexOf(t)))}.");
            // Missing models/materials, malformed ARCH bindings and unsupported assets cannot silently certify a lit image.
            if (request.StrictAssets && (resources.Diagnostics.Count != 0 || scene.Diagnostics.Count != 0))
                throw new AgentRequestException("Lit snapshot asset validation failed: " + string.Join("; ", resources.Diagnostics.Concat(scene.Diagnostics).Take(12)));
            surfaceCount = level.Sectors.Sum(s => s.Surfaces.Count(f => f.Geo != 0 && f.Corners.Count >= 3 && !string.IsNullOrEmpty(f.Material)));
            omittedPortals = level.Sectors.Sum(s => s.Surfaces.Count(f => f.Geo == 0));
        }
        else
        {
        // Count before allocating the expanded per-triangle mesh.
        foreach (var sector in level.Sectors)
        foreach (var surface in sector.Surfaces)
        {
            if (AgentSnapshotRequest.IsOpenPortal(surface)) continue;
            triangleBudget += System.Math.Max(0, surface.Corners.Count - 2);
            if (triangleBudget > AgentSnapshotRequest.MaxTriangles)
                throw new AgentRequestException($"Snapshot exceeds the {AgentSnapshotRequest.MaxTriangles}-triangle budget.");
        }
        for (int sectorIndex = 0; sectorIndex < level.Sectors.Count; sectorIndex++)
        {
            var sector = level.Sectors[sectorIndex];
            for (int surfaceIndex = 0; surfaceIndex < sector.Surfaces.Count; surfaceIndex++)
            {
                var surface = sector.Surfaces[surfaceIndex];
                if (AgentSnapshotRequest.IsOpenPortal(surface)) { omittedPortals++; continue; }
                if (surface.Corners.Count < 3) continue;
                foreach (var corner in surface.Corners)
                {
                    var p = corner.Vertex.Position;
                    if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) || !double.IsFinite(p.Z)
                        || System.Math.Max(System.Math.Abs(p.X), System.Math.Max(System.Math.Abs(p.Y), System.Math.Abs(p.Z))) > 1_000_000)
                        throw new AgentRequestException($"Snapshot geometry contains invalid coordinates in sector {sectorIndex}, surface {surfaceIndex}.");
                }
                surfaceCount++;
                var color = Colors[(sectorIndex * 3 + surfaceIndex) % Colors.Length];
                var a = surface.Corners[0].Vertex.Position;
                for (int i = 1; i + 1 < surface.Corners.Count; i++)
                {
                    var b = surface.Corners[i].Vertex.Position; var c = surface.Corners[i + 1].Vertex.Position;
                    var normal = (b - a).Cross(c - a);
                    if (normal.LengthSquared < 1e-18) { degenerateTriangles++; continue; }
                    normal = normal.Normalized();
                    mesh.AddTriangle(new(a, normal, color), new(b, normal, color), new(c, normal, color));
                }
            }
        }
        }
        if (mesh.IsEmpty) throw new AgentRequestException("Snapshot has no drawable geometry for the requested mode.");
        var camera = Camera.LookingAt(request.Position, request.Target, request.FieldOfViewDegrees);
        camera.NearPlane = request.NearPlane; camera.FarPlane = request.FarPlane;
        object cameraDescription = new
        {
            position = new[] { request.Position.X, request.Position.Y, request.Position.Z },
            target = new[] { request.Target.X, request.Target.Y, request.Target.Z },
            request.FieldOfViewDegrees, request.NearPlane, request.FarPlane, up = "Z", projection = "perspective",
        };
        byte[] pixels;
        string deviceName;
        try
        {
            using var context = VulkanContext.Create("SED Agent Snapshot");
            using var device = VulkanDevice.Create(context);
            deviceName = device.DeviceName;
            using var renderer = new SceneRenderer(device);
            if (scene is not null && resources is not null)
            {
                renderer.SetSky(level.Header.CeilingSky.Height, (float)level.Header.CeilingSky.Offset.X, (float)level.Header.CeilingSky.Offset.Y,
                    (float)level.Header.HorizonSky.Offset.X, (float)level.Header.HorizonSky.Offset.Y);
                if (scene.Submeshes.Any(s => string.IsNullOrWhiteSpace(s.Colormap)) && resources.Colormap("dflt.cmp") is { } defaultCmp)
                    renderer.SetColormap(defaultCmp.PaletteRgb, defaultCmp.LightTable);
                renderer.SetScene(scene, resources.Texture, resources.Colormap);
                renderWarnings.AddRange(renderer.Diagnostics);
            }
            else renderer.SetMesh(mesh);
            pixels = renderer.Render(camera.ViewProjection((double)request.Width / request.Height), camera.Position,
                (uint)request.Width, (uint)request.Height);
        }
        catch (Exception error) when (error is VulkanException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new IOException("Offscreen Vulkan rendering is unavailable or failed; no snapshot was written. " + error.Message, error);
        }
        long nonBackgroundPixels = 0;
        for (int i = 0; i < pixels.Length; i += 4)
            if (System.Math.Abs(pixels[i] - 13) > 1 || System.Math.Abs(pixels[i + 1] - 13) > 1 || System.Math.Abs(pixels[i + 2] - 20) > 1)
                nonBackgroundPixels++;
        string temporary = Path.Combine(Path.GetDirectoryName(outputPath)!, ".sed-snapshot-" + Guid.NewGuid().ToString("N") + ".tmp");
        string outputHash; long outputBytes;
        try
        {
            PngWriter.Write(temporary, pixels, request.Width, request.Height);
            using (var stream = File.OpenRead(temporary))
            {
                outputHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
                outputBytes = stream.Length;
            }
            // Capture again after rendering/writing the temporary image. No output is promoted on a changed input.
            var current = AgentWorkspace.LoadWithProvenance(path);
            if (current.InputSha256 != source.InputSha256 || current.SourceRevision != source.SourceRevision)
                throw new AgentConflictException("Snapshot source or native dependency changed while rendering. No output was written.");
            resources?.VerifyUnchanged();
            try { File.Move(temporary, outputPath, overwrite: false); }
            catch (IOException) when (File.Exists(outputPath) || Directory.Exists(outputPath))
            { throw new AgentConflictException("Snapshot output already exists; no file was replaced."); }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        if (resources is not null) renderWarnings.AddRange(resources.Diagnostics);
        if (nonBackgroundPixels == 0) renderWarnings.Add("No geometry is visible from this camera; inspect camera position, target and clipping planes.");
        string[] contentLimitations = request.Mode == "lit" ? [
            "Static lighting preview: baked world mono/RGB, scalar 3DO base light, compatible ARCHLIGHTING, sector/surface extra light and per-sector CMP. No runtime dynamic lights, COG, animation or moving geometry.",
            "Indexed MAT first cel/mip and model geoset zero only; truecolor MATs are unsupported and reported. No fog, engine portal traversal, exact sky projection or retail transparency-table emulation.",
            "Model hierarchy is shown in its rest pose; absent models are omitted. Resource hashes identify all supplied archives and every used entry.",
        ] : [
            "Diagnostic polygon geometry only: untextured solid faces are included; reciprocal visible untextured or Geo=0 portal faces are omitted.",
            "Colors identify surfaces, not material or game lighting. MotS RGB and architectural lighting, sky, transparency, things and 3DO models are not represented.",
        ];
        var distinctWarnings = renderWarnings.Distinct().ToArray();
        return new
        {
            schemaVersion = 1, written = outputPath, outputSha256 = outputHash, outputBytes,
            source.InputSha256, source.SourceRevision, source.Dependencies,
            requestSha256 = AgentWorkspace.Hash(Encoding.UTF8.GetBytes(requestText)),
            camera = cameraDescription,
            cameraSha256 = AgentWorkspace.Hash(JsonSerializer.SerializeToUtf8Bytes(cameraDescription, AgentWorkspace.JsonOptions)),
            request.Width, request.Height, rgbaSha256 = AgentWorkspace.Hash(pixels),
            request.Mode, lightingMode = scene?.LightingMode.ToString().ToLowerInvariant(),
            renderProfile = scene is null ? null : request.RenderProfile,
            renderer = scene is null ? "Vulkan flat diagnostic geometry" : "Vulkan static lit scene", deviceName, guiLaunched = false,
            resourceArchives = resources?.Archives ?? Array.Empty<object>(), usedResources = resources?.UsedResources ?? Array.Empty<object>(),
            counts = new { sectors = level.Sectors.Count, renderedSurfaces = surfaceCount, triangles = mesh.TriangleCount,
                omittedPortalSurfaces = omittedPortals, skippedDegenerateTriangles = degenerateTriangles, omittedThings },
            nonBackgroundPixels, elapsedMilliseconds = timer.ElapsedMilliseconds,
            preservationWarnings = source.Document.PreservationWarnings,
            warningCount = distinctWarnings.Length, warnings = distinctWarnings.Take(200).ToArray(), warningsTruncated = distinctWarnings.Length > 200,
            limitations = contentLimitations.Concat(new[] {
                "Double-sided triangle fans without engine portal visibility or gameplay simulation; concave/non-planar polygons may differ from engine rendering.",
                "RGBA PNG only: no depth or object-ID image. Driver differences may change image bytes; use reported source and camera provenance.",
                "Requires a usable Vulkan loader/device. Callers should enforce a process timeout; existing driver fence waits cannot be cancelled here.",
            }).ToArray(),
        };
    }
}
