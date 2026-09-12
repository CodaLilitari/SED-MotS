using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Formats.Gob;
using Sed.Formats.Jkl;
using Sed.Formats.Material;
using Sed.Rendering;
using Sed.Rendering.Vulkan;

// Renders a textured JK level: level geometry from JK1.GOB, MAT/CMP from Res2.gob.
//   Sed.LevelRender <JK1.GOB> <Res2.gob> <level-name> [out.png]
if (args.Length < 3)
{
    Console.Error.WriteLine("usage: Sed.LevelRender <JK1.GOB> <Res2.gob> <level> [out.png]");
    return 1;
}

var levelGobPath = args[0];
var resGobPath = args[1];
var levelName = args[2];
var outPath = args.FirstOrDefault(a => a.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) ?? "/tmp/sed_textured.png";

using var levelGob = GobArchive.Open(levelGobPath);
using var resGob = GobArchive.Open(resGobPath);

var entry = levelGob.Find(levelName)
    ?? levelGob.FindByExtension(".jkl").FirstOrDefault(e => e.NormalizedName.Contains(levelName.ToLowerInvariant()))
    ?? throw new FileNotFoundException($"level '{levelName}' not found");

Console.WriteLine($"Level: {entry.Name}");
Level level = JklParser.Parse(levelGob.ReadText(entry));
Console.WriteLine($"  {level.Sectors.Count} sectors, colormaps=[{string.Join(", ", level.ColorMaps)}]");

var palette = MaterialLibrary.LoadPalette(level.ColorMaps, resGob);
var library = new MaterialLibrary(palette, resGob);

var scene = SceneBuilder.BuildScene(level);
int withTex = 0, total = 0;
IndexedTexture? Lookup(string material)
{
    total++;
    var t = library.GetIndexed(material);
    if (t is { } r) { withTex++; return new IndexedTexture(r.Width, r.Height, r.Indices, r.IsSolid, r.HasColorKey); }
    return null;
}

const uint W = 1024, H = 768;
var (center, radius) = Bounds(level);
var eye = center + new Vec3(radius * 0.6, -radius * 1.2, radius * 0.5);
var camera = Camera.LookingAt(eye, center);
var mvp = camera.ViewProjection((double)W / H);

using var ctx = VulkanContext.Create("SED LevelRender");
using var device = VulkanDevice.Create(ctx);
using var renderer = new SceneRenderer(device);
renderer.SetColormap(palette.PaletteRgb, palette.LightTable);
renderer.SetSky(level.Header.CeilingSky.Height, (float)level.Header.CeilingSky.Offset.X, (float)level.Header.CeilingSky.Offset.Y);
renderer.SetScene(scene, Lookup);
Console.WriteLine($"  submeshes={scene.Submeshes.Count}, materials resolved {withTex}/{total}");

var pixels = renderer.Render(mvp, camera.Position, W, H);
PngWriter.Write(outPath, pixels, (int)W, (int)H);
Console.WriteLine($"Rendered → {Path.GetFullPath(outPath)}");
return 0;

static (Vec3 center, double radius) Bounds(Level level)
{
    var box = Box.Empty;
    foreach (var s in level.Sectors)
        foreach (var v in s.Vertices)
            box.Encapsulate(v.Position);
    if (box.Max.X < box.Min.X) return (Vec3.Zero, 1);
    var c = box.Center;
    double r = (box.Max - c).Length;
    return (c, r <= 0 ? 1 : r);
}
