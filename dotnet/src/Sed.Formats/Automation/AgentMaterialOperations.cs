using System.Text.Json;
using Sed.Core.Editing;
using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Formats.Jkl;

namespace Sed.Formats.Automation;

public static partial class AgentWorkspace
{
    private static bool TryPrepareMaterialOperation(JklDocument doc, JsonElement op, out IEditCommand? command)
    {
        command = null;
        Sector Sector() => At(doc.Level.Sectors, Integer(op, "sector"), "sector");
        Surface Surface() => At(Sector().Surfaces, Integer(op, "surface"), "surface");
        switch (String(op, "op"))
        {
            case "registerMaterial":
                Fields(op, "op", "name");
                command = new AppendResourceNameCommand(doc.Materials, ResourceName(op, ".mat"), "material");
                return true;
            case "registerColormap":
                Fields(op, "op", "name");
                command = new AppendResourceNameCommand(doc.Level.ColorMaps, ResourceName(op, ".cmp"), "colormap");
                return true;
            case "setSectorColormap":
                Fields(op, "op", "sector", "colormapIndex");
                int colormap = Integer(op, "colormapIndex");
                if ((uint)colormap >= (uint)doc.Level.ColorMaps.Count)
                    throw FieldError("colormapIndex is outside the current document.", "colormapIndex");
                command = new SetSectorColormapCommand(Sector(), doc.Level.ColorMaps[colormap], colormap);
                return true;
            case "fitSurfaceTexture":
                Fields(op, "op", "sector", "surface", "width", "height");
                int width = Integer(op, "width"), height = Integer(op, "height");
                if (width is < 1 or > 16384) throw FieldError("width must be between 1 and 16384 texels.", "width");
                if (height is < 1 or > 16384) throw FieldError("height must be between 1 and 16384 texels.", "height");
                var fittedSurface = Surface();
                fittedSurface.RecalcNormal(); // A preceding operation may have moved vertices.
                command = new AutoTextureCommand(fittedSurface, width, height);
                return true;
            case "setSurfaceUv":
                Fields(op, "op", "sector", "surface", "uv");
                var surface = Surface();
                var array = op.GetProperty("uv");
                if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() != surface.Corners.Count || array.GetArrayLength() is < 3 or > 64)
                    throw FieldError("uv must supply one [u,v] texel pair per surface corner, in query corner order (3..64 pairs).", "uv");
                var uv = new List<TexVertex>();
                foreach (var pair in array.EnumerateArray())
                {
                    if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() != 2)
                        throw new AgentRequestException("Each uv entry must be a two-number texel pair.", jsonPointer: $"/uv/{uv.Count}");
                    var coordinates = pair.EnumerateArray().ToArray();
                    double[] values = new double[2];
                    for (int i = 0; i < 2; i++)
                        if (coordinates[i].ValueKind != JsonValueKind.Number || !coordinates[i].TryGetDouble(out values[i]) || !double.IsFinite(values[i]) || System.Math.Abs(values[i]) > 1_000_000)
                            throw new AgentRequestException("UV coordinates must be finite and within +/-1000000 texels.", jsonPointer: $"/uv/{uv.Count}/{i}");
                    uv.Add(new TexVertex(values[0], values[1]));
                }
                command = new SetSurfaceUvCommand(surface, uv);
                return true;
            case "setSurfaceRenderModes":
                Fields(op, "op", "sector", "surface", "geometryMode", "lightMode", "textureMode");
                int geometry = Integer(op, "geometryMode"), light = Integer(op, "lightMode"), texture = Integer(op, "textureMode");
                if (geometry is < 0 or > 4) throw FieldError("geometryMode must be in 0..4.", "geometryMode");
                if (light is < 0 or > 3) throw FieldError("lightMode must be in 0..3.", "lightMode");
                if (texture is < 0 or > 3) throw FieldError("textureMode must be in 0..3.", "textureMode");
                var target = Surface();
                command = new CompositeCommand("Set surface rendering modes", [new SetSurfaceGeoCommand(target, geometry),
                    new SetSurfaceLightModeCommand(target, light), new SetSurfaceTexModeCommand(target, texture)]);
                return true;
            default: return false;
        }
    }

    private static string ResourceName(JsonElement op, string extension)
    {
        string name = Token(op, "name");
        if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) || name.Length <= extension.Length ||
            name.IndexOfAny(['/', '\\', ':', '<', '>', '|', '*', '?', ',', '=']) >= 0 || name.Contains("..", StringComparison.Ordinal))
            throw FieldError($"name must be a resource basename ending in {extension}; paths and traversal are not accepted.", "name");
        return name;
    }

    private static object MaterialOperationEffect(JklDocument doc, JsonElement op, int? created)
    {
        string name = String(op, "op");
        if (name is "registerMaterial" or "registerColormap")
        {
            var list = name == "registerMaterial" ? doc.Materials : doc.Level.ColorMaps;
            int index = created ?? list.FindIndex(n => n.Equals(String(op, "name"), StringComparison.OrdinalIgnoreCase));
            return new { target = new { kind = name == "registerMaterial" ? "material" : "colormap", index },
                state = new { name = list[index], count = list.Count } };
        }
        int sectorIndex = Integer(op, "sector");
        var sector = At(doc.Level.Sectors, sectorIndex, "sector");
        if (name == "setSectorColormap") return new { target = new { kind = "sector", index = sectorIndex },
            state = new { index = sector.ResolveColorMapIndex(), name = sector.ColorMap } };
        int surfaceIndex = Integer(op, "surface");
        var surface = At(sector.Surfaces, surfaceIndex, "surface");
        return new { target = new { kind = "surface", sector = sectorIndex, index = surfaceIndex }, state = new {
            surface.Material, surface.MaterialIndex, surface.Geo, surface.Light, surface.Tex,
            uv = surface.Corners.Select(c => new[] { c.Uv.U, c.Uv.V }).ToArray() } };
    }

    // The material table belongs to the document, outside the GUI's Level aggregate.
    // Registration appends names only; it never reads or copies game resources.
    private sealed class AppendResourceNameCommand : IEditCommand
    {
        private readonly List<string> _names;
        private readonly string _name;
        private readonly int _index;
        public string Name { get; }
        public AppendResourceNameCommand(List<string> names, string name, string kind)
        {
            if (names.Contains(name, StringComparer.OrdinalIgnoreCase))
                throw FieldError($"The {kind} is already registered; query and reuse its existing index.", "name");
            _names = names; _name = name; _index = names.Count; Name = $"Register {kind} {name}";
        }
        public void Apply() => _names.Add(_name);
        public void Revert() => _names.RemoveAt(_index);
    }
}
