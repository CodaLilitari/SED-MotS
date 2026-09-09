using System.Security.Cryptography;
using System.Text.Json;
using Sed.Core.Math;
using Sed.Rendering;
using Sed.Rendering.Vulkan;

// Original synthetic data only. Exercises the real offscreen mesh shaders; no UI or game assets.
if (args.Length != 1) throw new ArgumentException("Usage: Sed.LightingProbe <new-output-directory>");
string output = Path.GetFullPath(args[0]);
if (Directory.Exists(output)) throw new IOException("Probe output directory must not already exist.");
Directory.CreateDirectory(output);
using var context = VulkanContext.Create("SED lighting regression probe");
using var device = VulkanDevice.Create(context);
using var renderer = new SceneRenderer(device);
const int size = 32;
int assertions = 0;
var observations = new List<object>();

ColormapData Map(byte red, byte green, byte blue)
{
    var palette = new byte[768];
    palette[3] = red; palette[4] = green; palette[5] = blue;
    palette[6] = 20; palette[7] = 80; palette[8] = 160;
    var ramp = new byte[16384];
    for (int row = 0; row < 64; row++)
        for (int index = 0; index < 256; index++) ramp[row * 256 + index] = (byte)(row < 32 ? 2 : index);
    return new ColormapData(palette, ramp);
}
var mapA = Map(200, 100, 40);
var mapB = Map(40, 200, 100);
renderer.SetColormap(mapA.PaletteRgb, mapA.LightTable);
IndexedTexture? Texture(string name) => new IndexedTexture(1, 1, new byte[] { 1 });
ColormapData? Colormap(string name) => name.Equals("b.cmp", StringComparison.OrdinalIgnoreCase) ? mapB : null;

RenderScene Scene(RenderLightingMode mode, float mono, ColorF rgb, string colormap = "", int lightMode = 3,
    bool translucent = false, bool usePaletteLightRamp = true)
{
    var scene = new RenderScene { LightingMode = mode, UsePaletteLightRamp = usePaletteLightRamp };
    scene.Mesh.AddTriangle(
        new MeshVertex(new Vec3(-1, -1, .5), Vec3.Zero, rgb, mono: mono),
        new MeshVertex(new Vec3(3, -1, .5), Vec3.Zero, rgb, mono: mono),
        new MeshVertex(new Vec3(-1, 3, .5), Vec3.Zero, rgb, mono: mono));
    scene.Submeshes.Add(new Submesh { Material = "synthetic.mat", Colormap = colormap, LightMode = lightMode, Translucent = translucent, IndexCount = 3 });
    return scene;
}
byte[] Render(string name, bool clearBlack = false)
{
    var pixels = clearBlack ? renderer.Render(Mat4.Identity, Vec3.Zero, size, size, 0, 0, 0)
        : renderer.Render(Mat4.Identity, Vec3.Zero, size, size);
    PngWriter.Write(Path.Combine(output, name + ".png"), pixels, size, size);
    var rgba = pixels.AsSpan((size / 2 * size + size / 2) * 4, 4).ToArray();
    observations.Add(new { name, centerRgba = rgba.Select(v => (int)v).ToArray(), pixelSha256 = Convert.ToHexString(SHA256.HashData(pixels)) });
    return rgba;
}
void Pixel(byte[] actual, byte r, byte g, byte b, string reason, byte alpha = 255)
{
    byte[] expected = { r, g, b, alpha };
    for (int i = 0; i < 4; i++)
    {
        assertions++;
        if (Math.Abs(actual[i] - expected[i]) > 1) throw new InvalidOperationException($"{reason}: channel{i} expected{expected[i]}, got{actual[i]}.");
    }
}
var colored = new ColorF(.2f, .5f, .9f);
renderer.SetScene(Scene(RenderLightingMode.Mono, .25f, colored), Texture, Colormap);
Pixel(Render("mono-ramp"), 20, 80, 160, "Mono must use its own channel and CMP ramp");
renderer.UpdateGeometry(Scene(RenderLightingMode.Rgb, .25f, colored));
Pixel(Render("rgb-independent"), 40, 50, 36, "RGB must use base palette and all three channels");
renderer.UpdateGeometry(Scene(RenderLightingMode.Rgb, .95f, colored));
Pixel(Render("rgb-mono-independent"), 40, 50, 36, "Changing mono must not affect RGB");
renderer.UpdateGeometry(Scene(RenderLightingMode.Rgb, .95f, colored, "b.cmp"));
Pixel(Render("changed-colormap"), 8, 100, 90, "A colormap-only edit must refresh descriptors");
renderer.UpdateGeometry(Scene(RenderLightingMode.Rgb, .95f, new ColorF(-.2f, 1.5f, .5f), "b.cmp"));
Pixel(Render("rgb-clamped"), 0, 200, 50, "Each RGB channel must clamp independently");
renderer.UpdateGeometry(Scene(RenderLightingMode.Rgb, .95f, colored, "b.cmp"));
renderer.SetBrightness(1);
Pixel(Render("full-bright"), 40, 200, 100, "Full bright retains selected palette");
renderer.SetBrightness(0);
renderer.UpdateGeometry(Scene(RenderLightingMode.Mono, .95f, colored, "missing.cmp"));
Pixel(Render("missing-colormap-fallback"), 200, 100, 40, "Missing palette fallback must be explicit");
assertions++;
if (!renderer.Diagnostics.Any(v => v.Contains("missing.cmp"))) throw new InvalidOperationException("Missing CMP diagnostic absent.");
renderer.SetColormap(mapB.PaletteRgb, mapB.LightTable);
Pixel(Render("changed-default-colormap"), 40, 200, 100, "Default replacement must refresh fallback descriptors");
try { renderer.SetColormap(new byte[767], new byte[16384]); throw new InvalidOperationException("Bad palette accepted."); }
catch (ArgumentException) { assertions++; }
Pixel(Render("rejected-colormap-retains-old"), 40, 200, 100, "Rejected palette must retain valid GPU resources");
renderer.SetMesh(Scene(RenderLightingMode.Rgb, .1f, colored).Mesh);
Pixel(Render("flat-overlay"), 51, 128, 230, "Flat drawing must remain independent of lighting mode");

var split = new RenderScene { LightingMode = RenderLightingMode.Rgb };
foreach (var (left, right, map) in new[] { (-1.0, 0.0, ""), (0.0, 1.0, "b.cmp") })
{
    int start = split.Mesh.Indices.Count;
    var a = new MeshVertex(new Vec3(left, -1, .5), Vec3.Zero, new ColorF(1, 1, 1));
    var b = new MeshVertex(new Vec3(right, -1, .5), Vec3.Zero, new ColorF(1, 1, 1));
    var c = new MeshVertex(new Vec3(right, 1, .5), Vec3.Zero, new ColorF(1, 1, 1));
    var d = new MeshVertex(new Vec3(left, 1, .5), Vec3.Zero, new ColorF(1, 1, 1));
    split.Mesh.AddTriangle(a, b, c);
    split.Mesh.AddTriangle(a, c, d);
    split.Submeshes.Add(new Submesh { Material = "synthetic.mat", Colormap = map, IndexOffset = start, IndexCount = 6 });
}
renderer.SetColormap(mapA.PaletteRgb, mapA.LightTable);
int textureLoads = 0;
renderer.SetScene(split, name => { textureLoads++; return Texture(name); }, Colormap);
var splitPixels = renderer.Render(Mat4.Identity, Vec3.Zero, size, size);
Pixel(splitPixels.AsSpan((size / 2 * size + size / 4) * 4, 4).ToArray(), 200, 100, 40, "Left batch default CMP");
Pixel(splitPixels.AsSpan((size / 2 * size + size * 3 / 4) * 4, 4).ToArray(), 40, 200, 100, "Right batch named CMP");
assertions++;
if (textureLoads != 1) throw new InvalidOperationException("Same material was uploaded more than once for distinct colormaps.");
PngWriter.Write(Path.Combine(output, "two-colormaps.png"), splitPixels, size, size);
observations.Add(new { name = "two-colormaps", textureLoads, pixelSha256 = Convert.ToHexString(SHA256.HashData(splitPixels)) });

// OpenJK's textured scalar path modulates the base palette. Explicit CMP mode
// remains available and must update correctly without reloading the material.
renderer.SetScene(Scene(RenderLightingMode.Mono, .25f, colored, usePaletteLightRamp: false), Texture);
Pixel(Render("jk-openjk-scalar-modulation"), 50, 25, 10, "OpenJK monochrome must modulate the base palette by scalar light");
renderer.UpdateGeometry(Scene(RenderLightingMode.Mono, .25f, colored));
Pixel(Render("jk-cmp-comparison"), 20, 80, 160, "CMP comparison must retain the independent ramp path");
renderer.UpdateGeometry(Scene(RenderLightingMode.Mono, .25f, colored, usePaletteLightRamp: false));
Pixel(Render("jk-profile-only-update"), 50, 25, 10, "Changing only the scene profile must refresh the draw state");
foreach (int lightMode in new[] { 1, 2 })
{
    renderer.UpdateGeometry(Scene(RenderLightingMode.Rgb, .25f, colored, lightMode: lightMode, usePaletteLightRamp: false));
    Pixel(Render("openjk-texture-mode" + lightMode + "-scalar"), 50, 25, 10, "OpenJK unlit/diffuse textured faces use scalar modulation in RGB scenes");
}
renderer.UpdateGeometry(Scene(RenderLightingMode.Mono, 0, ColorF.Black, lightMode: 0, usePaletteLightRamp: false));
Pixel(Render("openjk-texture-fullbright"), 200, 100, 40, "OpenJK fullbright retains the source palette");

// Ordinary model and world draws can share a MAT/CMP descriptor while selecting
// different lighting channels. Draw state must not leak through the material cache.
var modelAndWorld = new RenderScene { LightingMode = RenderLightingMode.Rgb, UsePaletteLightRamp = false };
foreach (var (left, right, useMono) in new[] { (-1.0, 0.0, true), (0.0, 1.0, false) })
{
    int start = modelAndWorld.Mesh.Indices.Count;
    var a = new MeshVertex(new Vec3(left, -1, .5), Vec3.Zero, colored, mono: .25f);
    var b = new MeshVertex(new Vec3(right, -1, .5), Vec3.Zero, colored, mono: .25f);
    var c = new MeshVertex(new Vec3(right, 1, .5), Vec3.Zero, colored, mono: .25f);
    var d = new MeshVertex(new Vec3(left, 1, .5), Vec3.Zero, colored, mono: .25f);
    modelAndWorld.Mesh.AddTriangle(a, b, c);
    modelAndWorld.Mesh.AddTriangle(a, c, d);
    modelAndWorld.Submeshes.Add(new Submesh {
        Material = "synthetic.mat", UseMonochromeLighting = useMono, IndexOffset = start, IndexCount = 6
    });
}
textureLoads = 0;
renderer.SetScene(modelAndWorld, name => { textureLoads++; return Texture(name); });
foreach (bool useRamp in new[] { false, true })
{
    modelAndWorld.UsePaletteLightRamp = useRamp;
    renderer.UpdateGeometry(modelAndWorld);
    var pixels = renderer.Render(Mat4.Identity, Vec3.Zero, size, size);
    var ordinaryModel = pixels.AsSpan((size / 2 * size + size / 4) * 4, 4).ToArray();
    var rgbWorld = pixels.AsSpan((size / 2 * size + size * 3 / 4) * 4, 4).ToArray();
    if (useRamp) Pixel(ordinaryModel, 20, 80, 160, "Ordinary model selects scalar CMP comparison within an RGB scene");
    else Pixel(ordinaryModel, 50, 25, 10, "Ordinary model selects scalar engine shading within an RGB scene");
    Pixel(rgbWorld, 40, 50, 36, "World RGB must remain independent of the ordinary model's scalar draw and profile");
    string name = useRamp ? "same-material-model-mono-world-rgb-cmp" : "same-material-model-mono-world-rgb-openjk";
    PngWriter.Write(Path.Combine(output, name + ".png"), pixels, size, size);
    observations.Add(new { name, ordinaryModelRgba = ordinaryModel.Select(v => (int)v).ToArray(),
        worldRgba = rgbWorld.Select(v => (int)v).ToArray(), pixelSha256 = Convert.ToHexString(SHA256.HashData(pixels)) });
}
assertions++;
if (textureLoads != 1) throw new InvalidOperationException("Per-draw monochrome selection/profile edit must reuse the shared material upload.");

// Solid MAT uses a separate hardware-cache lighting rule. Row0 supplies a colored baseline.
IndexedTexture? Solid(string name) => new IndexedTexture(1, 1, new byte[] { 1 }, IsSolid: true);
renderer.SetColormap(mapA.PaletteRgb, mapA.LightTable);
renderer.SetScene(Scene(RenderLightingMode.Rgb, .99f, new ColorF(.2f, .5f, .1f)), Solid);
Pixel(Render("solid-rgb-colored-baseline"), 70, 207, 185, "Solid RGB must add independent row0 palette channels, then truncate bytes");
renderer.UpdateGeometry(Scene(RenderLightingMode.Rgb, .01f, new ColorF(.2f, .5f, .1f)));
Pixel(Render("solid-rgb-mono-independent"), 70, 207, 185, "Solid RGB must ignore alternate mono");
renderer.UpdateGeometry(Scene(RenderLightingMode.Rgb, .1f, new ColorF(-1f, 2f, .1f)));
Pixel(Render("solid-rgb-clamped"), 19, 255, 185, "Solid RGB clamps channels before byte conversion");
renderer.SetScene(Scene(RenderLightingMode.Mono, .5f, colored), Solid);
Pixel(Render("solid-mono-floor-row"), 20, 80, 160, "Mono .5 must use floor31, not rounded32");
renderer.SetScene(Scene(RenderLightingMode.Mono, .5f, colored), Texture);
Pixel(Render("texture-mono-floor-row"), 20, 80, 160, "Textured mono uses the same floor-row selection");
foreach (int lightMode in new[] { 1, 2 })
{
    renderer.SetScene(Scene(RenderLightingMode.Rgb, .25f, colored, lightMode: lightMode), Solid);
    Pixel(Render("solid-mode" + lightMode + "-mono"), 20, 80, 160, "Solid unlit/diffuse uses mono even in RGB display mode");
}
renderer.SetScene(Scene(RenderLightingMode.Mono, .5f, colored, usePaletteLightRamp: false), Solid);
Pixel(Render("openjk-solid-mono-retains-cmp"), 20, 80, 160, "The engine textured scalar profile must not change solid CMP shading");
renderer.UpdateGeometry(Scene(RenderLightingMode.Rgb, .99f, new ColorF(.2f, .5f, .1f), usePaletteLightRamp: false));
Pixel(Render("openjk-solid-rgb-retains-cache-rule"), 70, 207, 185, "The engine textured scalar profile must not change solid RGB shading");

// A deliberately nonidentity row63 must not affect a fullbright surface or editor override.
var fullbrightMap = Map(200, 100, 40);
fullbrightMap.PaletteRgb[0] = 17; fullbrightMap.PaletteRgb[1] = 73; fullbrightMap.PaletteRgb[2] = 149;
fullbrightMap.LightTable[63 * 256 + 1] = 2;
renderer.SetColormap(fullbrightMap.PaletteRgb, fullbrightMap.LightTable);
foreach (var (isSolid, lookup) in new (bool, TextureLookup)[] { (false, Texture), (true, Solid) })
{
    renderer.SetScene(Scene(RenderLightingMode.Mono, 0, ColorF.Black, lightMode: 0), lookup);
    Pixel(Render(isSolid ? "solid-fullbright-raw-palette" : "texture-fullbright-raw-palette"), 200, 100, 40, "Fullbright mode0 bypasses the ramp");
    renderer.UpdateGeometry(Scene(RenderLightingMode.Mono, .25f, colored));
    renderer.SetBrightness(1);
    Pixel(Render(isSolid ? "solid-editor-fullbright" : "texture-editor-fullbright"), 200, 100, 40, "Editor fullbright bypasses a nonidentity top ramp row");
    renderer.SetBrightness(0);
}
renderer.SetScene(Scene(RenderLightingMode.Rgb, 0, ColorF.Black, lightMode: 0),
    _ => new IndexedTexture(1, 1, new byte[] { 0 }, IsSolid: true));
Pixel(Render("solid-zero-opaque"), 17, 73, 149, "Solid palette index0 is a visible color");
renderer.SetScene(Scene(RenderLightingMode.Rgb, 0, ColorF.Black, lightMode: 0, translucent: true),
    _ => new IndexedTexture(1, 1, new byte[] { 0 }, IsSolid: true));
Pixel(Render("solid-zero-translucent", clearBlack: true), 6, 26, 53, "Solid index0 must not be discarded; hardware translucency is90/255", alpha: 90);

renderer.SetColormap(mapA.PaletteRgb, mapA.LightTable);
foreach (var mode in new[] { RenderLightingMode.Rgb, RenderLightingMode.Mono })
{
    var gradient = Scene(mode, 0, ColorF.Black);
    var bright = gradient.Mesh.Vertices[1];
    bright.Mono = 1; bright.R = 1; bright.G = 1; bright.B = 1;
    gradient.Mesh.Vertices[1] = bright;
    renderer.SetScene(gradient, Solid);
    // Center pixel x=16.5/32 gives vertex1 barycentric weight33/128 on this triangle.
    // Resolve each vertex first; interpolation of saturated/quantized results differs
    // substantially from looking up/interpreting an interpolated light value.
    if (mode == RenderLightingMode.Rgb)
        Pixel(Render("solid-rgb-vertex-gradient"), 80, 124, 184, "Solid RGB must clamp/truncate before raster interpolation");
    else
        Pixel(Render("solid-mono-vertex-gradient"), 66, 85, 129, "Solid mono must resolve the CMP before raster interpolation");
}

var report = new { schemaVersion = 1, device = device.DeviceName, assertions, observations, diagnostics = renderer.Diagnostics };
using (var file = new FileStream(Path.Combine(output, "receipt.json"), FileMode.CreateNew))
    JsonSerializer.Serialize(file, report, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine($"PASS: {assertions} GPU pixel assertions on {device.DeviceName}. {output}");
