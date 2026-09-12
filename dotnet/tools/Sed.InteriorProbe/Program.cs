using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Formats.Game;
using Sed.Formats.Jkl;
using Sed.Formats.Material;
using Sed.Rendering;
using Sed.Rendering.Vulkan;

// Renders interior views from inside a level (camera placed at a thing position)
// to inspect geometry + texturing as a player would see it.
//   Sed.InteriorProbe <baseDir> <level> [thingIndex]
if (args.Length < 2) { Console.Error.WriteLine("usage: Sed.InteriorProbe <baseDir> <level> [thingIndex]"); return 1; }
var baseDir = args[0];
var levelName = args[1];
int thingIndex = args.Length > 2 && int.TryParse(args[2], out var ti) ? ti : -1;

using var install = GameInstall.TryOpen(ProjectType.JediKnight, baseDir)
    ?? throw new DirectoryNotFoundException(baseDir);
var entry = install.Levels.FirstOrDefault(e => e.NormalizedName.Contains(levelName.ToLowerInvariant()))
    ?? throw new FileNotFoundException(levelName);
var level = JklParser.Parse(install.ReadLevel(entry));
Console.WriteLine($"{entry.Name}: {level.Sectors.Count} sectors, {level.Things.Count} things");

// Pick a camera anchor: requested thing, else a 'player'/'start' thing, else thing 0.
Thing? anchor = thingIndex >= 0 && thingIndex < level.Things.Count
    ? level.Things[thingIndex]
    : level.Things.FirstOrDefault(t => t.Name.Contains("player", StringComparison.OrdinalIgnoreCase)
                                    || t.Name.Contains("start", StringComparison.OrdinalIgnoreCase))
      ?? level.Things.FirstOrDefault();
if (anchor is null) { Console.Error.WriteLine("no things to anchor camera"); return 2; }
Console.WriteLine($"camera at thing #{anchor.Num} '{anchor.Name}' {anchor.Position}");
foreach (var t in level.Things.Take(8))
    Console.WriteLine($"   thing {t.Num,3} '{t.Name}' sec={t.Sector?.Num}");

var palette = MaterialLibrary.LoadPalette(level.ColorMaps, install.ResourceArchives.ToArray());
var library = new MaterialLibrary(palette, install.ResourceArchives.ToArray());
var modelLib = new Sed.Formats.ThreeDo.ModelLibrary(install.ResourceArchives.ToArray());
var assembler = new SceneAssembler();
assembler.AddLevel(level);
var unmodeled = assembler.AddThings(level, modelLib.Get);
var scene = assembler.Build();
Console.WriteLine($"  things with models: {level.Things.Count - unmodeled.Count}/{level.Things.Count}, " +
                  $"submeshes={scene.Submeshes.Count}, tris={scene.Mesh.Indices.Count / 3}");
IndexedTexture? Lookup(string m) { var t = library.GetIndexed(m); return t is { } r ? new IndexedTexture(r.Width, r.Height, r.Indices, r.IsSolid, r.HasColorKey) : null; }

const uint W = 720, H = 540;
using var ctx = VulkanContext.Create("SED Interior");
using var device = VulkanDevice.Create(ctx);
using var renderer = new SceneRenderer(device);
renderer.SetColormap(palette.PaletteRgb, palette.LightTable);
renderer.SetSky(level.Header.CeilingSky.Height, (float)level.Header.CeilingSky.Offset.X, (float)level.Header.CeilingSky.Offset.Y);
renderer.SetScene(scene, Lookup);

// Look at the anchor thing from a few offset distances so nearby models are framed.
double[] dists = { 0.4, 0.9, 1.8 };
for (int i = 0; i < dists.Length; i++)
{
    double d = dists[i];
    var eye = anchor.Position + new Vec3(d * 0.8, -d, d * 0.6);
    var cam = Camera.LookingAt(eye, anchor.Position, 75);
    cam.NearPlane = 0.01; cam.FarPlane = 200;
    var deg = 180.0 / System.Math.PI;
    var pixels = renderer.Render(cam.ViewProjection((double)W / H), cam.Position,
        new Vec3(cam.Yaw * deg, cam.Pitch * deg, 0), W, H);
    var outPath = $"/tmp/interior_{levelName}_d{i}.png";
    PngWriter.Write(outPath, pixels, (int)W, (int)H);
    Console.WriteLine($"  dist {d} → {outPath}");
}
return 0;
