using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Formats.Game;
using Sed.Formats.Jkl;
using Sed.Formats.Material;
using Sed.Rendering;
using Sed.Rendering.Vulkan;

// Validates auto-resource-loading from a game install base directory.
//   Sed.GameProbe <baseDir> [level] [out.png]
if (args.Length < 1)
{
    Console.Error.WriteLine("usage: Sed.GameProbe <baseDir> [level] [out.png]");
    return 1;
}

var baseDir = args[0];
var levelName = args.FirstOrDefault(a => !a.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && a != baseDir);
var outPath = args.FirstOrDefault(a => a.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) ?? "/tmp/sed_game.png";

using var install = GameInstall.TryOpen(ProjectType.JediKnight, baseDir);
if (install is null) { Console.Error.WriteLine($"No Jedi Knight install found under {baseDir}"); return 2; }

var levels = install.Levels.ToList();
Console.WriteLine($"Install: {install.BaseDir}");
Console.WriteLine($"  level archive: {(install.LevelArchive is null ? "none" : "found")}, resource archives: {install.ResourceArchives.Count}");
Console.WriteLine($"  {levels.Count} levels");

var entry = levelName is null
    ? levels.FirstOrDefault()
    : levels.FirstOrDefault(e => e.NormalizedName.Contains(levelName.ToLowerInvariant())) ?? levels.FirstOrDefault();
if (entry is null) { Console.Error.WriteLine("No levels to render"); return 3; }

Console.WriteLine($"Rendering {entry.Name}");
Level level = JklParser.Parse(install.ReadLevel(entry));

var palette = MaterialLibrary.LoadPalette(level.ColorMaps, install.ResourceArchives.ToArray());
var library = new MaterialLibrary(palette, install.ResourceArchives.ToArray());
var models = new Sed.Formats.ThreeDo.ModelLibrary(install.ResourceArchives.ToArray());

var assembler = new SceneAssembler();
assembler.AddLevel(level);
var unmodeled = assembler.AddThings(level, models.Get);
var scene = assembler.Build();
Console.WriteLine($"  things with models: {level.Things.Count - unmodeled.Count}/{level.Things.Count}");

int ok = 0, total = 0;
IndexedTexture? Lookup(string m)
{
    total++;
    var t = library.GetIndexed(m);
    if (t is { } r) { ok++; return new IndexedTexture(r.Width, r.Height, r.Indices, r.IsSolid, r.HasColorKey); }
    return null;
}

const uint W = 960, H = 720;
var (center, radius) = Bounds(level);
var camera = Camera.LookingAt(center + new Vec3(radius * 0.5, -radius * 1.2, radius * 0.5), center);
var mvp = camera.ViewProjection((double)W / H);

using var ctx = VulkanContext.Create("SED GameProbe");
using var device = VulkanDevice.Create(ctx);
using var renderer = new SceneRenderer(device);
renderer.SetColormap(palette.PaletteRgb, palette.LightTable);
renderer.SetSky(level.Header.CeilingSky.Height, (float)level.Header.CeilingSky.Offset.X, (float)level.Header.CeilingSky.Offset.Y);
if (args.Contains("--bright")) renderer.SetBrightness(1f);
renderer.SetScene(scene, Lookup);
Console.WriteLine($"  materials resolved {ok}/{total}");

// Markers only for things that have no 3DO model.
double markerSize = radius * 0.012;
renderer.SetMarkers(SceneBuilder.BuildThingMarkers(unmodeled, markerSize, new ColorF(0.2f, 0.9f, 1f)));

PngWriter.Write(outPath, renderer.Render(mvp, camera.Position, W, H), (int)W, (int)H);
Console.WriteLine($"Rendered → {Path.GetFullPath(outPath)}");
return 0;

static (Vec3, double) Bounds(Level level)
{
    var box = Box.Empty;
    foreach (var s in level.Sectors)
        foreach (var v in s.Vertices)
            box.Encapsulate(v.Position);
    if (box.Max.X < box.Min.X) return (Vec3.Zero, 1);
    var c = box.Center;
    return (c, System.Math.Max(1, (box.Max - c).Length));
}
