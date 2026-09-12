using System.Text;
using Sed.Formats.Automation;
using Sed.Formats.Material;
using Sed.Formats.Resources;
using Sed.Formats.ThreeDo;
using Sed.Rendering;

namespace Sed.Agent;

/// <summary>Rendering adapters over the shared explicit resource capture used by export.</summary>
internal sealed class SnapshotResources : IDisposable
{
    public ExplicitArchiveResources Capture { get; }
    private readonly Dictionary<string, ThreeDoModel?> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IndexedTexture?> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ColormapData?> _colormaps = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _diagnostics = new();
    public IReadOnlyList<string> Diagnostics => Capture.Diagnostics.Concat(_diagnostics).ToArray();
    public IReadOnlyList<ResourceEntryReceipt> UsedResources => Capture.UsedResources;
    public IReadOnlyList<ResourceArchiveReceipt> Archives => Capture.Archives;
    public SnapshotResources(IReadOnlyList<SnapshotArchive> requests) => Capture = new(requests);
    public void VerifyUnchanged() => Capture.VerifyUnchanged();

    public IndexedTexture? Texture(string name)
    {
        if (_textures.TryGetValue(name, out var cached)) return cached;
        try
        {
            if (Capture.Read(name, "mat") is not { } bytes) return _textures[name] = null;
            var mat = MatFile.Read(bytes.Span);
            return _textures[name] = mat.IsColor ? new IndexedTexture(1, 1, [(byte)mat.ColorIndex], IsSolid: true)
                : new(mat.Width, mat.Height, mat.Indices, HasColorKey: mat.HasColorKey);
        }
        catch (Exception error) when (error is IOException or FormatException or ArgumentException or OverflowException)
        { _diagnostics.Add($"MAT '{name}': {error.Message}"); return _textures[name] = null; }
    }

    public ColormapData? Colormap(string name)
    {
        if (_colormaps.TryGetValue(name, out var cached)) return cached;
        try
        {
            if (Capture.Read(name, "misc/cmp") is not { } bytes) return _colormaps[name] = null;
            var cmp = Sed.Formats.Material.Colormap.Read(bytes.Span);
            if (bytes.Length < 64 + 768 + 16384) _diagnostics.Add($"CMP '{name}' omits its light ramp; using an identity ramp.");
            return _colormaps[name] = new ColormapData(cmp.PaletteRgb, cmp.LightTable);
        }
        catch (Exception error) when (error is IOException or ArgumentException)
        { _diagnostics.Add($"CMP '{name}': {error.Message}"); return _colormaps[name] = null; }
    }

    public ThreeDoModel? Model(string name)
    {
        if (_models.TryGetValue(name, out var cached)) return cached;
        try
        {
            if (Capture.Read(name, "3do") is not { } bytes) return _models[name] = null;
            var model = ThreeDoParser.Parse(Encoding.Latin1.GetString(bytes.Span));
            if (model.Version is not (2.1 or 2.2) || model.Meshes.Count == 0
                || !model.Meshes.Any(m => m.Faces.Any(f => f.VertexIndices.Count >= 3)))
                throw new FormatException("Expected a drawable JK/MotS 3DO 2.1 or 2.2 model; content is empty, incomplete or unsupported.");
            return _models[name] = model;
        }
        catch (Exception error) when (error is IOException or FormatException or ArgumentException or OverflowException)
        { _diagnostics.Add($"3DO '{name}': {error.Message}"); return _models[name] = null; }
    }
    public void Dispose() => Capture.Dispose();
}
