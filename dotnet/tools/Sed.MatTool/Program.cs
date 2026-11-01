using Sed.Formats.Gob;
using Sed.Formats.Material;
using Sed.Rendering;

// Decodes a MAT texture against a CMP palette (both from a GOB) and writes a PNG.
//   Sed.MatTool <res.gob> <mat-name> [cmp-name] [out.png]
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: Sed.MatTool <res.gob> <mat-name> [cmp-name] [out.png]");
    return 1;
}

var gobPath = args[0];
var matName = args[1];
var cmpName = args.Length > 2 && args[2].EndsWith(".cmp", StringComparison.OrdinalIgnoreCase) ? args[2] : "dflt.cmp";
var outPath = args.FirstOrDefault(a => a.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) ?? "/tmp/sed_mat.png";

using var gob = GobArchive.Open(gobPath);

var cmpEntry = gob.Find(cmpName) ?? throw new FileNotFoundException($"CMP '{cmpName}' not found");
var palette = Colormap.Read(gob.ReadBytes(cmpEntry));
Console.WriteLine($"Palette: {cmpEntry.Name}");

var matEntry = gob.Find(matName) ?? gob.FindByExtension(".mat")
    .FirstOrDefault(e => e.NormalizedName.Contains(matName.ToLowerInvariant()))
    ?? throw new FileNotFoundException($"MAT '{matName}' not found");

var mat = MatFile.Read(gob.ReadBytes(matEntry));
Console.WriteLine($"MAT: {matEntry.Name} — {mat.Width}x{mat.Height} {(mat.IsColor ? $"color #{mat.ColorIndex}" : "texture")}");
if (mat.MipFallbackNote is { } note) Console.WriteLine($"Note: {note}");

var rgba = mat.ToRgba(palette);
PngWriter.Write(outPath, rgba, mat.Width, mat.Height);
Console.WriteLine($"Wrote {Path.GetFullPath(outPath)}");
return 0;
