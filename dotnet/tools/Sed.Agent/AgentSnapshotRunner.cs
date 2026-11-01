using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Sed.Core.IO;
using Sed.Core.Math;
using Sed.Formats.Automation;
using Sed.Rendering;
using Sed.Rendering.Vulkan;

namespace Sed.Agent;

/// <summary>
/// One bounded offscreen diagnostic view, with no window or game process. <see cref="Run"/>
/// takes an explicit camera; <see cref="RunOverview"/> computes Wren's level-preview cameras
/// (an orthographic overhead plan or a three-quarter perspective, after the Blender previews
/// Wren, a Grok bot and one of Andrei's AI collaborators, made; recovered 2026-10-31) and
/// renders the equivalent lit snapshot through the same path.
/// </summary>
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
        return RenderCore(path, source, request, requestText, outputPath, overview: null, timer);
    }

    /// <summary>
    /// Wren's level-preview cameras: frames the level's drawn world surfaces for the requested
    /// view and renders the equivalent lit snapshot. The receipt's <c>overview.snapshotRequest</c>
    /// is that exact snapshot request, so <c>snapshot</c> reproduces its camera and pixels.
    /// </summary>
    public static object RunOverview(string path, string requestText, string outputPath)
    {
        var timer = Stopwatch.StartNew();
        var overview = AgentOverviewRequest.Parse(requestText);
        outputPath = AgentSnapshotRequest.PrepareOutputPath(outputPath); // before source parsing or any native/GPU operation
        var source = AgentWorkspace.LoadWithProvenance(path);
        overview.VerifySource(source);
        if (OverviewFraming.WorldSurfaceBounds(source.Document.Level) is not { } bounds)
            throw new AgentRequestException("Level has no drawn world surfaces to frame.");
        OverviewCamera camera;
        try { camera = OverviewFraming.Compute(bounds, overview.View, overview.Framing, overview.Width, overview.Height, overview.Margin); }
        catch (InvalidOperationException error) { throw new AgentRequestException(error.Message); }
        var request = overview.ToSnapshotRequest(camera.Position, camera.Target, camera.Projection, camera.FieldOfViewDegrees,
            camera.OrthographicHeight, camera.Near, camera.Far);
        var info = new
        {
            view = overview.View, framing = overview.Framing, margin = overview.Margin,
            bounds = new
            {
                min = new[] { bounds.Min.X, bounds.Min.Y, bounds.Min.Z },
                max = new[] { bounds.Max.X, bounds.Max.Y, bounds.Max.Z },
            },
            snapshotRequest = request.ToJson(),
        };
        return RenderCore(path, source, request, requestText, outputPath, info, timer);
    }

    /// <summary>
    /// The camera a request describes. A view whose horizontal component is under 1e-6 of its
    /// length (only orthographic requests may have one) is built exactly vertical, yaw 0: looking
    /// down, screen up is +Y (north) and screen right +X; looking up, screen up is -Y.
    /// </summary>
    internal static Camera SnapshotCamera(AgentSnapshotRequest request)
    {
        var delta = request.Target - request.Position;
        var camera = delta.Cross(Camera.Up).Length / delta.Length < 0.000001
            ? new Camera { Position = request.Position, Yaw = 0, Pitch = delta.Z < 0 ? -System.Math.PI / 2 : System.Math.PI / 2 }
            : Camera.LookingAt(request.Position, request.Target, request.FieldOfViewDegrees);
        camera.NearPlane = request.NearPlane; camera.FarPlane = request.FarPlane;
        if (request.IsOrthographic)
        {
            camera.Projection = CameraProjection.Orthographic;
            camera.OrthographicHeight = request.OrthographicHeight;
        }
        else camera.FieldOfViewDegrees = request.FieldOfViewDegrees;
        return camera;
    }

    private static object RenderCore(string path, AgentDocumentSnapshot source, AgentSnapshotRequest request, string requestText,
        string outputPath, object? overview, Stopwatch timer)
    {
        var level = source.Document.Level; // independent parsed model, never the live GUI document
        var mesh = new Mesh();
        using var resources = request.Mode == "lit" ? new SnapshotResources(request.ResourceArchives) : null;
        RenderScene? scene = null;
        Sed.Formats.Resources.ArchitectureResourceValidationResult? architectureValidation = null;
        var renderWarnings = new List<string>();
        int omittedThings = level.Things.Count;
        ColormapData? masterColormap = null;
        int surfaceCount = 0, omittedPortals = 0, degenerateTriangles = 0;
        long triangleBudget = 0;
        if (resources is not null)
        {
            architectureValidation = Sed.Formats.Resources.ArchitectureResourceValidation.Evaluate(level, resources.Capture);
            var assembler = new SceneAssembler(AgentSnapshotRequest.MaxTriangles);
            assembler.AddLevel(level);
            var unmodeled = assembler.AddThings(level, resources.Model);
            omittedThings = unmodeled.Count;
            scene = assembler.Build();
            if (request.LightingMode != "game") scene.LightingMode = request.LightingMode == "rgb" ? RenderLightingMode.Rgb : RenderLightingMode.Mono;
            scene.UsePaletteLightRamp = request.RenderProfile == "palette";
            scene.CullWorldBackFaces = request.CullBackFaces;
            mesh = scene.Mesh;
            // JK keeps one display palette for textures, World Colormap 0 (OpenJKDF2 resolves
            // every indexed texture through colormaps[0]); a texture takes only its sector CMP's
            // light ramp. Solid colours use the sector CMP's own palette and ramp (rdCache).
            var master = MasterColormap.Resolve(level.ColorMaps, resources.Colormap);
            var masterProblems = master.Colormap is null
                ? master.Problems.Append("Without a master palette, textured indexed materials use a grayscale placeholder palette.").ToArray()
                : master.Problems;
            masterColormap = master.Colormap;
            foreach (var batch in scene.Submeshes)
            {
                _ = resources.Texture(batch.Material);
                if (!string.IsNullOrWhiteSpace(batch.Colormap)) _ = resources.Colormap(batch.Colormap);
            }
            renderWarnings.AddRange(masterProblems);
            renderWarnings.AddRange(scene.Diagnostics);
            // Notices (such as the retail mip fallback) are reported but do not fail strict mode:
            // the drawn pixels are a complete mip 0, which retail JK shows on faces nearer than
            // the level's MipMap Distances (SED never draws later mips).
            renderWarnings.AddRange(resources.Notices);
            var noModel = unmodeled.Where(t => string.IsNullOrWhiteSpace(level.GetThingModel(t))).ToList();
            if (noModel.Count > 0)
                renderWarnings.Add($"{noModel.Count} things have no resolved 3DO model and are omitted (including invisible controllers); "
                    + $"first indices: {string.Join(", ", noModel.Take(32).Select(t => level.Things.IndexOf(t)))}.");
            // Missing models/materials, malformed ARCH bindings and unsupported assets cannot silently certify a lit image.
            if (request.StrictAssets && (architectureValidation.HasErrors || resources.Diagnostics.Count != 0 || scene.Diagnostics.Count != 0 || masterProblems.Count != 0))
                throw new AgentRequestException("Lit snapshot asset validation failed: " + string.Join("; ", masterProblems.Concat(resources.Diagnostics).Concat(scene.Diagnostics).Take(12)));
            surfaceCount = level.Sectors.Sum(s => s.Surfaces.Count(SceneAssembler.IsDrawnWorldSurface));
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
        var camera = SnapshotCamera(request);
        object cameraDescription = request.CameraDescription();
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
                if (masterColormap is { } masterPalette) renderer.SetColormap(masterPalette.PaletteRgb, masterPalette.LightTable);
                renderer.SetScene(scene, resources.Texture, resources.Colormap);
                // The shader already bypasses light for indexed, solid and true-colour draws at full brightness.
                if (request.Fullbright) renderer.SetBrightness(1f);
                renderWarnings.AddRange(renderer.Diagnostics);
            }
            else renderer.SetMesh(mesh);
            pixels = renderer.Render(camera.ViewProjection((double)request.Width / request.Height), camera.Eye,
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
            try { AtomicFilePromotion.MoveNew(temporary, outputPath); }
            catch (IOException) when (File.Exists(outputPath) || Directory.Exists(outputPath))
            { throw new AgentConflictException("Snapshot output already exists; no file was replaced."); }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        if (resources is not null) renderWarnings.AddRange(resources.Diagnostics);
        if (nonBackgroundPixels == 0) renderWarnings.Add("No geometry is visible from this camera; inspect camera position, target and clipping planes.");
        string[] modeLimitations = request.Mode == "lit" ? [
            "Static lighting preview: baked world mono/RGB, scalar 3DO base light, compatible ARCHLIGHTING, sector/surface extra light, and per-sector CMP light ramps over the level's single master palette (World Colormap 0). No runtime dynamic lights, COG, animation or moving geometry.",
            "First MAT cel/mip and model geoset zero only. 8-bit textures resolve through the master palette; solid-colour MATs use their sector CMP's own palette and ramp, as the accelerated engine does, without its tint; 16/32-bit MATs are decoded to true colour and lit by modulation without a CMP ramp. No fog, engine portal traversal, exact sky projection or retail transparency-table emulation.",
            "Model hierarchy is shown in its rest pose; absent models are omitted. Resource hashes identify all supplied archives and every used entry.",
        ] : [
            "Diagnostic polygon geometry only: untextured solid faces are included; reciprocal visible untextured or Geo=0 portal faces are omitted.",
            "Colors identify surfaces, not material or game lighting. MotS RGB and architectural lighting, sky, transparency, things and 3DO models are not represented.",
        ];
        var contentLimitations = new List<string>(modeLimitations);
        if (scene is not null && request.Fullbright)
            contentLimitations.Add("Fullbright: lighting is ignored; indexed texels show their master-palette colour, solid colours their palette entry and true colour its texel.");
        string facing = scene is not null && request.CullBackFaces
            ? "Every world surface is drawn only from in front of its plane (cutaway); models stay double-sided."
            : "Surfaces are double-sided except adjoined ones, which are drawn only from in front of their plane, as the engine draws level surfaces.";
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
            fullbright = scene is null ? (bool?)null : request.Fullbright,
            cullBackFaces = scene is null ? (bool?)null : request.CullBackFaces,
            renderer = scene is null ? "Vulkan flat diagnostic geometry" : "Vulkan static lit scene", deviceName, guiLaunched = false,
            resourceArchives = resources?.Archives ?? [], usedResources = resources?.UsedResources ?? [],
            assetNotices = resources?.Notices ?? [],
            resourceRevision = resources?.Capture.ResourceRevision, architectureValidation,
            counts = new { sectors = level.Sectors.Count, renderedSurfaces = surfaceCount, triangles = mesh.TriangleCount,
                omittedPortalSurfaces = omittedPortals, skippedDegenerateTriangles = degenerateTriangles, omittedThings },
            nonBackgroundPixels, elapsedMilliseconds = timer.ElapsedMilliseconds,
            preservationWarnings = source.Document.PreservationWarnings,
            warningCount = distinctWarnings.Length, warnings = distinctWarnings.Take(200).ToArray(), warningsTruncated = distinctWarnings.Length > 200,
            limitations = contentLimitations.Concat(new[] {
                "Triangle fans without engine portal visibility or gameplay simulation; concave/non-planar polygons may differ from engine rendering. " + facing,
                "RGBA PNG only: no depth or object-ID image. Driver differences may change image bytes; use reported source and camera provenance.",
                "Requires a usable Vulkan loader/device. Callers should enforce a process timeout; existing driver fence waits cannot be cancelled here.",
            }).ToArray(),
            overview,
        };
    }
}
