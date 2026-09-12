using System.Security.Cryptography;
using System.Text.Json;
using Sed.Core.Math;
using Sed.Rendering;
using Sed.Rendering.Vulkan;

// Original synthetic assets only. Exercises actual offscreen Vulkan drawing.
// Expected composition follows docs/audit/MOTS_TRANSPARENCY_CONTRACT.md;
// this is an accelerated albedo-pass check, not retail framebuffer equivalence.
if (args.Length != 1) throw new ArgumentException("Usage: Sed.TransparencyProbe <new-output-directory>");
string output = Path.GetFullPath(args[0]);
if (Directory.Exists(output)) throw new IOException("Probe output directory must not already exist.");
Directory.CreateDirectory(output);
using var context = VulkanContext.Create("SED transparency regression probe");
using var device = VulkanDevice.Create(context);
using var renderer = new SceneRenderer(device);
const int size = 32;
int assertions = 0, frames = 0;
var observations = new List<object>();
var checks = new List<object>();
var black = new[] { 0, 0, 0 };
var red = new[] { 255, 0, 0 };
var green = new[] { 0, 255, 0 };
var blue = new[] { 0, 0, 255 };
var magenta = new[] { 255, 0, 255 };
var yellow = new[] { 255, 255, 0 };
var palette = new byte[768];
foreach (var (index, color) in new[] { (0, magenta), (1, red), (2, green), (3, blue), (4, new[] { 255, 255, 255 }), (5, yellow) })
    for (int c = 0; c < 3; c++) palette[index * 3 + c] = (byte)color[c];
var ramp = new byte[16384];
for (int row = 0; row < 64; row++)
    for (int index = 0; index < 256; index++) ramp[row * 256 + index] = (byte)index;
renderer.SetColormap(palette, ramp);

var textures = new Dictionary<string, IndexedTexture>(StringComparer.OrdinalIgnoreCase)
{
    ["red.mat"] = new(1, 1, new byte[] { 1 }),
    ["green.mat"] = new(1, 1, new byte[] { 2 }),
    ["blue.mat"] = new(1, 1, new byte[] { 3 }),
    ["yellow.mat"] = new(1, 1, new byte[] { 5 }),
    ["zero.mat"] = new(1, 1, new byte[] { 0 }),
    ["masked-zero.mat"] = new(1, 1, new byte[] { 0 }, HasColorKey: true),
    ["masked-red.mat"] = new(1, 1, new byte[] { 1 }, HasColorKey: true),
    ["masked-green.mat"] = new(1, 1, new byte[] { 2 }, HasColorKey: true),
    ["solid-zero.mat"] = new(1, 1, new byte[] { 0 }, IsSolid: true, HasColorKey: true),
    ["cutout.mat"] = new(2, 1, new byte[] { 0, 1 }, HasColorKey: true),
};
IndexedTexture? Texture(string name) => textures.TryGetValue(name, out var texture) ? texture : null;
RenderScene NewScene(RenderLightingMode mode = RenderLightingMode.Rgb) => new() { LightingMode = mode, UsePaletteLightRamp = false };
MeshVertex Vertex(Vec3 p, double u = .5, double v = .5) => new(p, Vec3.Zero, new ColorF(1, 1, 1), u, v, mono: 1);

void Triangle(RenderScene scene, string material, Vec3 a, Vec3 b, Vec3 c, bool blend = false, bool adjoined = false, int lightMode = 0)
{
    int start = scene.Mesh.Indices.Count;
    scene.Mesh.AddTriangle(Vertex(a), Vertex(b), Vertex(c));
    scene.Submeshes.Add(new Submesh { Material = material, IndexOffset = start, IndexCount = 3,
        Translucent = blend, Adjoined = adjoined, LightMode = lightMode });
}
void Plane(RenderScene scene, string material, double z, bool blend = false, bool adjoined = false, int lightMode = 0)
    => Triangle(scene, material, new Vec3(-1, -1, z), new Vec3(3, -1, z), new Vec3(-1, 3, z), blend, adjoined, lightMode);
void Quad(RenderScene scene, string material, double z, double left = -1, double right = 1, bool blend = false, bool adjoined = false, double uvWidth = 1)
{
    int start = scene.Mesh.Indices.Count;
    var a = Vertex(new Vec3(left, -1, z), 0, .5);
    var b = Vertex(new Vec3(right, -1, z), uvWidth, .5);
    var c = Vertex(new Vec3(right, 1, z), uvWidth, .5);
    var d = Vertex(new Vec3(left, 1, z), 0, .5);
    scene.Mesh.AddTriangle(a, b, c); scene.Mesh.AddTriangle(a, c, d);
    scene.Submeshes.Add(new Submesh { Material = material, IndexOffset = start, IndexCount = 6,
        Translucent = blend, Adjoined = adjoined, LightMode = 0 });
}
byte[] Render(string name, Mat4? camera = null, Vec3? position = null)
{
    var pixels = renderer.Render(camera ?? Mat4.Identity, position ?? Vec3.Zero, size, size, 0, 0, 0);
    PngWriter.Write(Path.Combine(output, name + ".png"), pixels, size, size);
    frames++;
    observations.Add(new { name, pixelSha256 = Convert.ToHexString(SHA256.HashData(pixels)),
        centerRgba = pixels.AsSpan((size / 2 * size + size / 2) * 4, 4).ToArray().Select(v => (int)v).ToArray() });
    return pixels;
}
// Independent integer source-over oracle. Each UNORM attachment write rounds to
// a byte before a subsequent blend. The engine's inverted stored alpha gives 165.
int[] Over(int[] foreground, int[] background) => foreground.Zip(background,
    (f, b) => (int)Math.Round((f * 165.0 + b * 90.0) / 255.0, MidpointRounding.AwayFromZero)).ToArray();
void Pixel(byte[] pixels, int[] expected, string reason, int x = size / 2, int y = size / 2, int alpha = 255)
{
    int start = (y * size + x) * 4;
    int[] rgba = { expected[0], expected[1], expected[2], alpha };
    var actual = pixels.AsSpan(start, 4).ToArray().Select(v => (int)v).ToArray();
    checks.Add(new { reason, x, y, expectedRgba = rgba, actualRgba = actual });
    for (int c = 0; c < 4; c++)
    {
        assertions++;
        if (Math.Abs(actual[c] - rgba[c]) > 1)
            throw new InvalidOperationException($"{reason}: pixel ({x}, {y}), channel {c}: expected {rgba[c]}, got {actual[c]}.");
    }
}
void Check(bool condition, string reason)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(reason);
}

// Opaque background and one translucent surface pin effective opacity directly.
foreach (bool reverse in new[] { false, true })
{
    var scene = NewScene();
    if (reverse) Plane(scene, "red.mat", .2, blend: true);
    Plane(scene, "blue.mat", .8);
    if (!reverse) Plane(scene, "red.mat", .2, blend: true);
    renderer.SetScene(scene, Texture);
    Pixel(Render("red-over-blue-order-" + reverse), new[] { 165, 0, 90 }, "Effective source opacity is 165/255");
}
var swapped = NewScene();
Plane(swapped, "red.mat", .8); Plane(swapped, "blue.mat", .2, blend: true);
renderer.SetScene(swapped, Texture);
Pixel(Render("blue-over-red"), new[] { 90, 0, 165 }, "Colors must not be premultiplied twice");

// Both transparent surfaces must sort independently of submission/material order.
foreach (bool reverse in new[] { false, true })
{
    var scene = NewScene();
    foreach (var (material, z) in reverse ? new[] { ("blue.mat", .8), ("red.mat", .2) } : new[] { ("red.mat", .2), ("blue.mat", .8) })
        Plane(scene, material, z, blend: true, adjoined: true);
    renderer.SetScene(scene, Texture);
    Pixel(Render("two-blend-order-" + reverse), Over(red, Over(blue, black)), "Near red must draw over far blue for either material order");
}

// Actual opposite cameras with the same uploaded scene must reverse composition.
var cameraScene = NewScene();
foreach (var (material, z) in new[] { ("red.mat", .2), ("blue.mat", .8) })
    Triangle(cameraScene, material, new Vec3(-5, -5, z), new Vec3(15, -5, z), new Vec3(-5, 15, z), blend: true, adjoined: true);
renderer.SetScene(cameraScene, Texture);
var projection = Mat4.Perspective(Math.PI / 2, 1, .1, 10);
var front = new Vec3(0, 0, -2); var back = new Vec3(0, 0, 2);
var frontCamera = projection * Mat4.LookAt(front, new Vec3(0, 0, .5), new Vec3(0, 1, 0));
var backCamera = projection * Mat4.LookAt(back, new Vec3(0, 0, .5), new Vec3(0, 1, 0));
var firstView = Render("camera-front", frontCamera, front);
Pixel(firstView, Over(red, Over(blue, black)), "Front camera orders red nearest");
Pixel(Render("camera-back", backCamera, back), Over(blue, Over(red, black)), "Opposite camera orders blue nearest without geometry update");
var repeatedView = Render("camera-front-repeat", frontCamera, front);
Check(firstView.SequenceEqual(repeatedView), "Returning to the original camera must reproduce the original frame exactly.");

// Two depths in one MAT batch must interleave a different MAT, including after update.
RenderScene Interleaved(bool reverse)
{
    var scene = NewScene();
    foreach (double z in reverse ? new[] { .2, .8 } : new[] { .8, .2 })
        scene.Mesh.AddTriangle(Vertex(new Vec3(-1, -1, z)), Vertex(new Vec3(3, -1, z)), Vertex(new Vec3(-1, 3, z)));
    scene.Submeshes.Add(new Submesh { Material = "red.mat", IndexCount = 6, LightMode = 0, Translucent = true, Adjoined = true });
    Plane(scene, "blue.mat", .5, blend: true, adjoined: true);
    return scene;
}
int loads = 0;
renderer.SetScene(Interleaved(false), name => { loads++; return Texture(name); });
var interleavedExpected = Over(red, Over(blue, Over(red, black)));
Pixel(Render("same-mat-interleaved"), interleavedExpected, "Per-triangle ordering must interleave one MAT's near and far geometry");
renderer.UpdateGeometry(Interleaved(true));
Pixel(Render("same-mat-interleaved-update"), interleavedExpected, "Triangle source order must not survive as stale depth order after update");
Check(loads == 2, "Geometry/order update must reuse both material uploads.");

// Source index zero distinguishes ordinary texture, masking, blending and solid MAT.
foreach (var mode in new[] { RenderLightingMode.Rgb, RenderLightingMode.Mono })
foreach (var item in new[] {
    (Material: "zero.mat", Blend: false, Expected: magenta),
    (Material: "masked-zero.mat", Blend: false, Expected: blue),
    (Material: "zero.mat", Blend: true, Expected: blue),
    (Material: "masked-zero.mat", Blend: true, Expected: blue),
    (Material: "masked-green.mat", Blend: false, Expected: green),
    (Material: "masked-green.mat", Blend: true, Expected: Over(green, blue)),
    (Material: "solid-zero.mat", Blend: false, Expected: magenta),
    (Material: "solid-zero.mat", Blend: true, Expected: Over(magenta, blue)) })
{
    var scene = NewScene(mode);
    Plane(scene, "blue.mat", .8); Plane(scene, item.Material, .2, blend: item.Blend);
    renderer.SetScene(scene, Texture);
    Pixel(Render($"zero-matrix-{mode}-{item.Material}-{item.Blend}"), item.Expected,
        "MAT masking and face translucency must independently govern source index zero");
}

// The discard decision happens before CMP light remapping: mapped zero is visible.
var remappedRamp = (byte[])ramp.Clone();
for (int row = 0; row < 64; row++) { remappedRamp[row * 256] = 2; remappedRamp[row * 256 + 1] = 0; }
renderer.SetColormap(palette, remappedRamp);
foreach (var (material, expected) in new[] { ("zero.mat", green), ("masked-zero.mat", blue), ("masked-red.mat", magenta) })
{
    var scene = NewScene(RenderLightingMode.Mono); scene.UsePaletteLightRamp = true;
    Plane(scene, "blue.mat", .8); Plane(scene, material, .2, lightMode: 3);
    renderer.SetScene(scene, Texture);
    Pixel(Render("cmp-before-discard-" + material), expected, "Masking must inspect the source index before CMP remapping");
}
renderer.SetColormap(palette, ramp);

// A masked opaque texel writes depth; a discarded hole cannot block a later plane.
var holes = NewScene();
Plane(holes, "blue.mat", .9);
Quad(holes, "cutout.mat", .2, uvWidth: 2);
Plane(holes, "green.mat", .5);
renderer.SetScene(holes, Texture);
var holesImage = Render("masked-opaque-depth-holes");
Pixel(holesImage, green, "A discarded near texel must not write depth", x: size / 4);
Pixel(holesImage, red, "An opaque nonzero cutout texel must write depth and remain opaque", x: size * 3 / 4);

// Minimum-depth sorting deliberately draws A before B, although B is behind A
// at the sample. This exposes depth writes without pretending to solve crossing
// polygons: B has vertex depths .1/.9/.9 and center depth about .506, while A=.3.
foreach (bool maskedOpaque in new[] { false, true })
foreach (bool adjoined in new[] { false, true })
{
    var scene = NewScene();
    Plane(scene, "blue.mat", .95);
    Quad(scene, "yellow.mat", .15, left: -1, right: -.25);
    Plane(scene, maskedOpaque ? "masked-red.mat" : "red.mat", .3, blend: !maskedOpaque, adjoined: adjoined);
    Triangle(scene, "green.mat", new Vec3(-1, -1, .1), new Vec3(3, -1, .9), new Vec3(-1, 3, .9), blend: true, adjoined: true);
    renderer.SetScene(scene, Texture);
    var pixels = Render($"depth-crossing-mask-{maskedOpaque}-adjoin-{adjoined}");
    var first = maskedOpaque ? red : Over(red, blue);
    var expected = adjoined ? Over(green, first) : first;
    Pixel(pixels, expected, "Only a masked/translucent adjoin omits depth writes");
    Pixel(pixels, yellow, "Both read/write and read-only draws must retain depth testing against foreground", x: 4);
}

// One MAT shared across opaque/blended/adjoin states must not leak a pipeline.
RenderScene SharedMaterial(bool swap)
{
    var scene = NewScene(); Plane(scene, "blue.mat", .8);
    Quad(scene, "red.mat", .2, left: -1, right: 0, blend: swap, adjoined: false);
    Quad(scene, "red.mat", .2, left: 0, right: 1, blend: !swap, adjoined: true);
    return scene;
}
loads = 0;
renderer.SetScene(SharedMaterial(false), name => { loads++; return Texture(name); });
foreach (bool swap in new[] { false, true, false })
{
    renderer.UpdateGeometry(SharedMaterial(swap));
    var pixels = Render("shared-mat-state-" + frames + "-" + swap);
    Pixel(pixels, swap ? Over(red, blue) : red, "Shared MAT left draw keeps its own alpha/depth state", x: size / 4);
    Pixel(pixels, swap ? red : Over(red, blue), "Shared MAT right draw keeps its own alpha/depth state", x: size * 3 / 4);
}
Check(loads == 2, "Per-draw state changes must not reload the shared MAT.");

// Equal-depth ties are stable for read-only adjoined draws and repeat renders.
var ties = NewScene(); Plane(ties, "red.mat", .5, blend: true, adjoined: true); Plane(ties, "blue.mat", .5, blend: true, adjoined: true);
renderer.SetScene(ties, Texture);
var tieA = Render("equal-depth-tie-first"); var tieB = Render("equal-depth-tie-repeat");
Pixel(tieA, Over(blue, Over(red, black)), "Equal-depth ties retain source order");
Check(tieA.SequenceEqual(tieB), "Equal-depth repeats must be deterministic.");

// Invalid camera input must be rejected before recording GPU commands, leaving
// this renderer usable for the next frame rather than a partially open pass.
var invalidCamera = Mat4.Identity;
invalidCamera.M[0] = float.NaN;
bool refusedInvalidCamera = false;
try { renderer.Render(invalidCamera, Vec3.Zero, size, size, 0, 0, 0); }
catch (ArgumentException) { refusedInvalidCamera = true; }
Check(refusedInvalidCamera, "A nonfinite camera matrix must be refused before GPU submission.");
var afterInvalidCamera = Render("valid-after-invalid-camera");
Pixel(afterInvalidCamera, Over(blue, Over(red, black)), "A rejected camera must not poison the next valid draw");
Check(tieA.SequenceEqual(afterInvalidCamera), "A valid render after rejected input must reproduce the earlier frame exactly.");

var report = new { schemaVersion = 1, device = device.DeviceName, assertions, frames,
    contract = "Accelerated indexed albedo composition; effective opacity 165/255; depth routing keyed by adjoin and mask/blend",
    limitations = new[] { "Synthetic renderer-only data does not prove scene assembly or retail equivalence.",
        "Crossing-plane results exercise state routing, not globally correct transparency.", "True-color assets, emissive/gamma, software CMP blending and sky remain separate." },
    observations, checks, diagnostics = renderer.Diagnostics };
using (var file = new FileStream(Path.Combine(output, "receipt.json"), FileMode.CreateNew))
    JsonSerializer.Serialize(file, report, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine($"PASS: {assertions} assertions across {frames} Vulkan frames on {device.DeviceName}. {output}");
