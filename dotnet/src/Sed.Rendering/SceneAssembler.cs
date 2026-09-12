using Sed.Core.Editing;
using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Formats.ThreeDo;

namespace Sed.Rendering;

/// <summary>
/// Accumulates renderable geometry from level surfaces and instanced 3DO models
/// into per-material meshes, then emits a single material-batched
/// <see cref="RenderScene"/>. UVs are kept in texel units (the renderer
/// normalizes per material), matching the original engine.
/// </summary>
public sealed class SceneAssembler
{
    private readonly record struct Batch(string Material, string Colormap, bool Translucent, int SkyMode, int ClampMode, int LightMode, bool UseMonochromeLighting, bool Adjoined);
    private readonly Dictionary<Batch, Mesh> _byBatch = new();
    private readonly List<string> _diagnostics = new();
    private RenderLightingMode _lightingMode;
    private ProjectType _gameKind;
    private readonly int _maxTriangles;
    private int _triangles;
    public SceneAssembler(int maxTriangles = int.MaxValue) => _maxTriangles = maxTriangles > 0
        ? maxTriangles : throw new ArgumentOutOfRangeException(nameof(maxTriangles));
    private void Triangle(Mesh mesh, MeshVertex a, MeshVertex b, MeshVertex c)
    {
        if (_triangles >= _maxTriangles) throw new InvalidOperationException($"Scene exceeds the {_maxTriangles}-triangle budget.");
        static void Check(MeshVertex vertex)
        {
            if (!float.IsFinite(vertex.Px) || !float.IsFinite(vertex.Py) || !float.IsFinite(vertex.Pz)
                || !float.IsFinite(vertex.U) || !float.IsFinite(vertex.V))
                throw new FormatException("Scene contains nonfinite geometry or texture coordinates.");
        }
        Check(a); Check(b); Check(c);
        mesh.AddTriangle(a, b, c);
        _triangles++;
    }

    private Mesh For(string material, bool translucent, int skyMode = 0, int clampMode = 0, string colormap = "", int lightMode = 3, bool mono = false, bool adjoined = false)
    {
        // Levels declare the same material under different casings — 01narshadda
        // has seven such pairs (01wgril1.mat / 01WGRIL1.mat). Material lookup is
        // case-insensitive, so keying the batch case-sensitively would upload the
        // same texture twice and add a redundant draw call per pair.
        var key = new Batch(material.ToLowerInvariant(), colormap.ToLowerInvariant(), translucent, skyMode, clampMode, lightMode, mono, adjoined);
        if (!_byBatch.TryGetValue(key, out var mesh))
            _byBatch[key] = mesh = new Mesh();
        return mesh;
    }

    /// <summary>
    /// Adds the level's textured surfaces (skipping no-material adjoin portals).
    /// Sectors on hidden layers are omitted when <paramref name="layers"/> is given.
    /// </summary>
    public void AddLevel(Level level, LayerVisibility? layers = null)
    {
        _gameKind = level.Kind;
        _lightingMode = level.Kind == ProjectType.MysteriesOfTheSith ? RenderLightingMode.Rgb : RenderLightingMode.Mono;
        foreach (var sector in level.Sectors)
        {
            if (layers is not null && !layers.IsVisible(sector)) continue;

        foreach (var surface in sector.Surfaces)
        {
            if (surface.Geo == 0 || surface.Corners.Count < 3 || string.IsNullOrEmpty(surface.Material)) continue;
            surface.RecalcNormal();
            var n = surface.Normal;
            int skyMode = (surface.SurfFlags & Surface.SfSkyCeiling) != 0 ? 1
                        : (surface.SurfFlags & Surface.SfSkyHorizon) != 0 ? 2 : 0;
            // Texture addressing is per-draw state, so clamp flags split the batch.
            int clampMode = TextureClampMode(surface.FaceFlags);
            var mesh = For(surface.Material, surface.IsTranslucent, skyMode, clampMode, ColormapName(level, sector), surface.IsSky ? 0 : surface.Light,
                adjoined: surface.Adjoin is not null);
            var c0 = surface.Corners[0];
            for (int i = 1; i + 1 < surface.Corners.Count; i++)
                Triangle(mesh, SurfVertex(c0, n, surface, sector), SurfVertex(surface.Corners[i], n, surface, sector), SurfVertex(surface.Corners[i + 1], n, surface, sector));
        }
        }
    }

    /// <summary>
    /// Instances each thing's 3DO model (resolved via templates) at its position
    /// and orientation. Returns the things that had no model (caller can mark them).
    /// </summary>
    public List<Thing> AddThings(Level level, Func<string, ThreeDoModel?> modelProvider,
        LayerVisibility? layers = null)
    {
        _gameKind = level.Kind;
        _lightingMode = level.Kind == ProjectType.MysteriesOfTheSith ? RenderLightingMode.Rgb : RenderLightingMode.Mono;
        var unmodeled = new List<Thing>();
        foreach (var thing in level.Things)
        {
            if (layers is not null && !layers.IsVisible(thing)) continue;

            var modelName = level.GetThingModel(thing);
            var model = string.IsNullOrEmpty(modelName) ? null : modelProvider(modelName);
            if (model is null || model.Meshes.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(modelName))
                    _diagnostics.Add($"Thing {level.Things.IndexOf(thing)}: named model '{modelName}' is missing or has no mesh data.");
                unmodeled.Add(thing); continue;
            }

            var world = Mat4.Translate(thing.Position) * Mat4.FromPyr(thing.Pitch, thing.Yaw, thing.Roll);
            bool levelGeometry = IsLevelGeometry(level, thing);
            var architecture = levelGeometry ? ResolveArchitecture(level, thing, model) : null;
            if (!levelGeometry && thing.ArchLightingIndex >= 0)
                _diagnostics.Add($"Thing {level.Things.IndexOf(thing)} has an ARCHLIGHTING binding but lacks resolved thingflags 0x8; ordinary things use model base mono lighting.");
            float ambient = Clamp((thing.Sector?.Ambient.R ?? 0) + (thing.Sector?.ExtraLight.R ?? 0));
            AddModel(model, world, architecture, ambient, ColormapName(level, thing.Sector), !levelGeometry);
        }
        return unmodeled;
    }

    /// <summary>Instances a 3DO model under a world transform, applying its node hierarchy.</summary>
    public void AddModel(ThreeDoModel model, Mat4 world)
        => AddModel(model, world, null, 0.8f, "", false); // Standalone asset preview has no sector context.

    private void AddModel(ThreeDoModel model, Mat4 world, ArchitecturalLightObject? architecture, float ambient, string colormap, bool mono)
    {
        if (model.Nodes.Count == 0)
        {
            for (int m = 0; m < model.Meshes.Count; m++) AddMesh(model.Meshes[m], world, model, architecture?.Meshes[m], ambient, colormap, mono);
            return;
        }

        var worlds = new Mat4?[model.Nodes.Count];
        Mat4 WorldOf(int i)
        {
            if (worlds[i] is { } cached) return cached;
            var pending = new List<int>();
            int current = i;
            var visited = new HashSet<int>();
            while (current >= 0 && current < model.Nodes.Count && worlds[current] is null)
            {
                if (!visited.Add(current)) throw new FormatException("3DO hierarchy contains a parent cycle.");
                pending.Add(current);
                current = model.Nodes[current].Parent;
            }
            var parentWorld = current >= 0 && current < model.Nodes.Count ? worlds[current]!.Value : world;
            for (int j = pending.Count - 1; j >= 0; j--)
            {
                var nd = model.Nodes[pending[j]];
                parentWorld *= Mat4.Translate(nd.Offset) * Mat4.FromPyr(nd.Pitch, nd.Yaw, nd.Roll);
                worlds[pending[j]] = parentWorld;
            }
            return worlds[i]!.Value;
        }

        for (int i = 0; i < model.Nodes.Count; i++)
        {
            var node = model.Nodes[i];
            if ((uint)node.Mesh < (uint)model.Meshes.Count)
                AddMesh(model.Meshes[node.Mesh], WorldOf(i), model, architecture?.Meshes[node.Mesh], ambient, colormap, mono);
        }
    }

    private void AddMesh(Mesh3do mesh, Mat4 world, ThreeDoModel model, ArchitecturalLightMesh? architecture, float ambient, string colormap, bool monoOnly)
    {
        if (mesh.GeometryMode == 0) return;
        if (mesh.LightingMode > 3)
            _diagnostics.Add($"Model '{model.Name}', mesh '{mesh.Name}' uses special lighting mode {mesh.LightingMode}; runtime cached mesh light is unavailable in this static preview.");
        foreach (var face in mesh.Faces)
        {
            if (face.Geo == 0 || face.VertexIndices.Count < 3) continue;
            if (face.VertexIndices.Any(i => (uint)i >= (uint)mesh.Vertices.Count))
                throw new FormatException("3DO face references a missing vertex.");
            int clampMode = TextureClampMode(face.FaceFlags);
            int mode = System.Math.Min(mesh.LightingMode == 6 ? 3 : mesh.LightingMode, face.Light);
            var target = For(model.GetMaterial(face.Material), (face.FaceFlags & Surface.FfTranslucent) != 0, clampMode: clampMode, colormap: colormap, lightMode: mode, mono: monoOnly);
            MeshVertex LitVertex(Vec3 position, Vec3 normal, int corner)
            {
                int vertex = face.VertexIndices[corner];
                float mono = vertex < mesh.VertexIntensities.Count ? mesh.VertexIntensities[vertex] : 0;
                var rgb = new ColorF(mono, mono, mono);
                if (architecture is not null)
                {
                    var baked = architecture.Vertices[vertex];
                    mono = (float)baked.Mono;
                    rgb = new ColorF((float)baked.Red, (float)baked.Green, (float)baked.Blue);
                }
                (mono, rgb) = StaticLight(mode, mono, rgb, face.ExtraLight, ambient);
                return new MeshVertex(position, normal, rgb, U(mesh, face, corner), V(mesh, face, corner), mono);
            }

            var p0 = world.TransformPoint(Vert(mesh, face, 0));
            for (int k = 1; k + 1 < face.VertexIndices.Count; k++)
            {
                var pi = world.TransformPoint(Vert(mesh, face, k));
                var pj = world.TransformPoint(Vert(mesh, face, k + 1));
                var n = (pi - p0).Cross(pj - p0).Normalized();
                Triangle(target,
                    LitVertex(p0, n, 0), LitVertex(pi, n, k), LitVertex(pj, n, k + 1));
            }
        }
    }

    private static Vec3 Vert(Mesh3do mesh, Face3do face, int corner)
    {
        int vi = face.VertexIndices[corner];
        return (uint)vi < (uint)mesh.Vertices.Count ? mesh.Vertices[vi] : Vec3.Zero;
    }

    private static double U(Mesh3do mesh, Face3do face, int corner) => Uv(mesh, face, corner).X;
    private static double V(Mesh3do mesh, Face3do face, int corner) => Uv(mesh, face, corner).Y;

    private static Vec2 Uv(Mesh3do mesh, Face3do face, int corner)
    {
        if (corner >= face.UvIndices.Count) return Vec2.Zero;
        int ti = face.UvIndices[corner];
        return (uint)ti < (uint)mesh.Uvs.Count ? mesh.Uvs[ti] : Vec2.Zero;
    }

    public RenderScene Build()
    {
        var scene = new RenderScene { LightingMode = _lightingMode, UsePaletteLightRamp = false };
        scene.Diagnostics.AddRange(_diagnostics);
        // Opaque first, then translucent, so the renderer can order passes by list order.
        foreach (var (batch, mesh) in _byBatch.OrderBy(kv => kv.Key.Translucent))
        {
            if (mesh.IsEmpty) continue;
            uint baseVertex = (uint)scene.Mesh.Vertices.Count;
            int indexOffset = scene.Mesh.Indices.Count;
            scene.Mesh.Vertices.AddRange(mesh.Vertices);
            foreach (var idx in mesh.Indices) scene.Mesh.Indices.Add(idx + baseVertex);
            scene.Submeshes.Add(new Submesh
            {
                Material = batch.Material,
                Colormap = batch.Colormap,
                UseMonochromeLighting = batch.UseMonochromeLighting,
                IndexOffset = indexOffset,
                IndexCount = mesh.Indices.Count,
                Translucent = batch.Translucent,
                Adjoined = batch.Adjoined,
                SkyMode = batch.SkyMode,
                ClampMode = batch.ClampMode,
                LightMode = batch.LightMode,
            });
        }
        return scene;
    }

    private static string ColormapName(Level level, Sector? sector) =>
        !string.IsNullOrWhiteSpace(sector?.ColorMap) ? sector.ColorMap : level.ColorMaps.FirstOrDefault() ?? "";

    private int TextureClampMode(long flags) => _gameKind == ProjectType.InfernalMachine
        ? ((flags & FaceFlags.TexClampX) != 0 ? 1 : 0) | ((flags & FaceFlags.TexClampY) != 0 ? 2 : 0) : 0;

    private static bool IsLevelGeometry(Level level, Thing thing)
    {
        string value = thing.Values.TryGetValue("thingflags", out var direct) ? direct : level.GetTemplateValue(thing.Template, "thingflags");
        if (string.IsNullOrEmpty(value)) return false;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
        if (!uint.TryParse(value, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint flags))
            throw new FormatException("Thing has invalid hexadecimal thingflags.");
        return (flags & 0x8) != 0;
    }

    private ArchitecturalLightObject? ResolveArchitecture(Level level, Thing thing, ThreeDoModel model)
    {
        if (thing.ArchLightingIndex < 0) return null;
        string? reason = null;
        ArchitecturalLightObject? architecture = null;
        if ((uint)thing.ArchLightingIndex >= (uint)level.ArchitecturalLighting.Count) reason = "missing ARCHLIGHTING object";
        else
        {
            architecture = level.ArchitecturalLighting[thing.ArchLightingIndex];
            if (!string.IsNullOrEmpty(thing.ArchLightingSourceModel) && !thing.ArchLightingSourceModel.Equals(level.GetThingModel(thing), StringComparison.OrdinalIgnoreCase))
                reason = "model name changed since architectural binding";
            else if (!string.IsNullOrEmpty(thing.ArchLightingSourceModelHash)
                && !thing.ArchLightingSourceModelHash.Equals(model.ArchitectureContentHash, StringComparison.OrdinalIgnoreCase))
                reason = "model content changed or its native architectural fingerprint cannot be verified";
            else if (architecture.Meshes.Count != model.Meshes.Count || architecture.Meshes.Where((m, i) => m.Vertices.Count != model.Meshes[i].Vertices.Count).Any())
                reason = "architectural mesh/vertex counts do not match model geoset zero";
        }
        if (reason is null) return architecture;
        _diagnostics.Add($"Thing {level.Things.IndexOf(thing)}: {reason}; showing model base/sector light without that binding.");
        return null;
    }

    // Static preview follows the hardware Gouraud composition without adding
    // runtime dynamic lights: base + surface extra, clamped, then ambient floor.
    // World faces use sector extra as their floor; model draws also use ambient.
    private static (float mono, ColorF rgb) StaticLight(int mode, float mono, ColorF rgb, float extra, float floor)
    {
        if (mode == 0) return (1, ColorF.White);
        if (mode is 1 or 2) { mono = 0; rgb = ColorF.Black; }
        float Apply(float value) => System.Math.Max(Clamp(Clamp(value) + extra), Clamp(floor));
        return (Apply(mono), new ColorF(Apply(rgb.R), Apply(rgb.G), Apply(rgb.B)));
    }
    private static float Clamp(float value) => float.IsFinite(value) ? System.Math.Clamp(value, 0f, 1f)
        : throw new FormatException("Scene lighting contains a nonfinite value.");

    private static MeshVertex SurfVertex(Surface.Corner c, Vec3 normal, Surface surface, Sector sector)
    {
        float baseMono = c.MonoIntensity ?? c.Intensity.R;
        // The engine derives its RGB flag from the JKL row width on load. The
        // editor already normalizes scalar rows and may author RGB before save.
        var baseRgb = c.Intensity;
        var (mono, rgb) = StaticLight(surface.IsSky ? 0 : surface.Light, baseMono,
            baseRgb, surface.ExtraLightIntensity, sector.ExtraLight.R);
        return new MeshVertex(c.Vertex.Position, normal, rgb, c.Uv.U, c.Uv.V, mono);
    }
}
