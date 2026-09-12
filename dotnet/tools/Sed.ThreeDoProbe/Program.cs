using Sed.Core.Math;
using Sed.Formats.Gob;
using Sed.Formats.Material;
using Sed.Formats.ThreeDo;
using Sed.Rendering;
using Sed.Rendering.Vulkan;

// Parses a single .3do model from a resource GOB and renders it textured.
//   Sed.ThreeDoProbe <Res2.gob> <model.3do> [cmp] [out.png]
if (args.Length < 2) { Console.Error.WriteLine("usage: Sed.ThreeDoProbe <Res2.gob> <model.3do> [cmp] [out.png]"); return 1; }
var gobPath = args[0];
var modelName = args[1];
var cmpName = args.FirstOrDefault(a => a.EndsWith(".cmp", StringComparison.OrdinalIgnoreCase)) ?? "dflt.cmp";
var outPath = args.FirstOrDefault(a => a.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) ?? "/tmp/sed_3do.png";

using var gob = GobArchive.Open(gobPath);
var entry = gob.Find(modelName) ?? gob.FindByExtension(".3do").FirstOrDefault(e => e.NormalizedName.Contains(modelName.ToLowerInvariant()))
    ?? throw new FileNotFoundException(modelName);

var model = ThreeDoParser.Parse(gob.ReadText(entry));
Console.WriteLine($"{entry.Name}: v{model.Version}, {model.Meshes.Count} meshes, {model.Nodes.Count} nodes, {model.Materials.Count} materials");
Console.WriteLine($"  verts={model.Meshes.Sum(m => m.Vertices.Count)}, faces={model.Meshes.Sum(m => m.Faces.Count)}");

var palette = Colormap.Read(gob.ReadBytes(gob.Find(cmpName)!));
var library = new MaterialLibrary(palette, gob);

var assembler = new SceneAssembler();
assembler.AddModel(model, Mat4.Identity);
var scene = assembler.Build();
Console.WriteLine($"  submeshes={scene.Submeshes.Count}, tris={scene.Mesh.Indices.Count / 3}");
if (scene.Mesh.IsEmpty) { Console.Error.WriteLine("empty model mesh"); return 3; }

IndexedTexture? Lookup(string m) { var t = library.GetIndexed(m); return t is { } r ? new IndexedTexture(r.Width, r.Height, r.Indices, r.IsSolid, r.HasColorKey) : null; }

// Frame the model.
var box = Box.Empty;
foreach (var v in scene.Mesh.Vertices) box.Encapsulate(new Vec3(v.Px, v.Py, v.Pz));
var center = box.Center;
double radius = System.Math.Max(0.1, (box.Max - center).Length);
var camera = Camera.LookingAt(center + new Vec3(radius * 0.9, -radius * 1.4, radius * 0.7), center);

const uint W = 640, H = 640;
using var ctx = VulkanContext.Create("SED 3DO Probe");
using var device = VulkanDevice.Create(ctx);
using var renderer = new SceneRenderer(device);
renderer.SetColormap(palette.PaletteRgb, palette.LightTable);
renderer.SetScene(scene, Lookup);
PngWriter.Write(outPath, renderer.Render(camera.ViewProjection((double)W / H), camera.Position, W, H, 0.1f, 0.1f, 0.12f), (int)W, (int)H);
Console.WriteLine($"Rendered → {Path.GetFullPath(outPath)}");
return 0;
