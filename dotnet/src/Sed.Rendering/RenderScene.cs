namespace Sed.Rendering;

/// <summary>
/// An 8-bit palette-indexed texture (one byte per pixel). The renderer applies
/// the level's master palette with scalar or RGB lighting, or a sector CMP ramp.
/// A 16/32-bit MAT instead supplies <paramref name="Rgba"/> (Width*Height*4 true-colour
/// bytes); <paramref name="Indices"/> is then ignored, and the texture is lit by
/// modulation like the accelerated engine, without palette or ramp.
/// </summary>
public readonly record struct IndexedTexture(int Width, int Height, byte[] Indices, bool IsSolid = false, bool HasColorKey = false,
    byte[]? Rgba = null)
{
    public bool IsTrueColor => Rgba is not null;
}

/// <summary>Resolves a material name (e.g. "wall01.mat") to indexed pixels, or null.</summary>
public delegate IndexedTexture? TextureLookup(string material);

/// <summary>Selects scalar or RGB lighting; the scene profile controls textured scalar shading.</summary>
public enum RenderLightingMode { Mono = 0, Rgb = 1 }

/// <summary>Flags packed into the existing 32-bit push word at byte 156.</summary>
public static class RenderLightingState
{
    public const int PushOffset = 156;
    public const int PushBytes = 160;
    public const uint RgbBit = 1;
    public const uint SolidBit = 2;
    public const uint ScalarModulationBit = 4;
    public const uint ColorKeyBit = 8;
    /// <summary>The material texture holds RGBA8 true colour rather than palette indices.</summary>
    public const uint TrueColorBit = 16;
    /// <summary>
    /// Draw only from in front of the surface plane. The engine draws a level surface only
    /// when the camera is in front of it (sithRender skips plane distance &lt;= 0); an adjoined
    /// surface's coplanar mirror covers the other side, so drawing both double-sided z-fights.
    /// </summary>
    public const uint SingleSidedBit = 32;
    public const int LightModeShift = 8;

    /// <summary>
    /// Whether a submesh is drawn only from in front of its plane: always for an adjoined world
    /// surface, and for every world surface under the back-face cutaway. Model faces never are
    /// (their vertex normals are per-triangle, not a surface's facing), nor are flat overlays.
    /// </summary>
    public static bool SingleSided(RenderScene scene, Submesh submesh, bool flat = false)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(submesh);
        return (submesh.Adjoined || scene.CullWorldBackFaces && submesh.WorldSurface) && !flat;
    }

    public static uint Pack(RenderLightingMode lighting, bool solid, int lightMode, bool usePaletteLightRamp = true, bool hasColorKey = false,
        bool trueColor = false, bool singleSided = false)
    {
        if (!Enum.IsDefined(lighting)) throw new ArgumentOutOfRangeException(nameof(lighting));
        if ((uint)lightMode > 255) throw new ArgumentOutOfRangeException(nameof(lightMode));
        return (uint)lighting | (solid ? SolidBit : 0) |
            (usePaletteLightRamp ? 0 : ScalarModulationBit) | (hasColorKey && !solid ? ColorKeyBit : 0) |
            (trueColor && !solid ? TrueColorBit : 0) | (singleSided ? SingleSidedBit : 0) | ((uint)lightMode << LightModeShift);
    }
}

/// <summary>The complete palette and monochrome ramp needed by an indexed-material draw.</summary>
public readonly record struct ColormapData(byte[] PaletteRgb, byte[] LightTable)
{
    public const int PaletteBytes = 256 * 3;
    public const int LightTableBytes = 64 * 256;

    public void Validate()
    {
        if (PaletteRgb is null || PaletteRgb.Length != PaletteBytes)
            throw new ArgumentException($"A colormap requires exactly {PaletteBytes} RGB palette bytes.");
        if (LightTable is null || LightTable.Length != LightTableBytes)
            throw new ArgumentException($"A colormap requires exactly {LightTableBytes} light-table bytes.");
    }
}

/// <summary>Resolves a named CMP without changing material texture indices.</summary>
public delegate ColormapData? ColormapLookup(string colormap);

/// <summary>A contiguous run of indices in a <see cref="Mesh"/> that share one material.</summary>
public sealed class Submesh
{
    public string Material { get; init; } = string.Empty;
    public string Colormap { get; init; } = string.Empty;
    /// <summary>Ordinary game things use the scalar pass even when world geometry uses MotS RGB.</summary>
    public bool UseMonochromeLighting { get; init; }
    /// <summary>0 fullbright, 1 unlit, 2 diffuse monochrome, 3 Gouraud.</summary>
    public int LightMode { get; init; } = 3;
    public int IndexOffset { get; init; }
    public int IndexCount { get; init; }
    /// <summary>Drawn in a separate alpha-blended pass after opaque geometry.</summary>
    public bool Translucent { get; init; }
    /// <summary>
    /// An adjoined world surface. Masked/translucent ones use the engine's deferred no-depth-write
    /// pass, and every adjoined surface is drawn only from in front of its plane (vertex normals
    /// must carry the surface normal), so it cannot z-fight with its coplanar mirror.
    /// </summary>
    public bool Adjoined { get; init; }
    /// <summary>
    /// A level surface (not a 3DO model face). Its vertices carry the surface normal, so the
    /// renderer can cull it from behind when <see cref="RenderScene.CullWorldBackFaces"/> is set.
    /// </summary>
    public bool WorldSurface { get; init; }
    /// <summary>0 = normal, 1 = ceiling sky, 2 = horizon sky (UV computed in shader).</summary>
    public int SkyMode { get; init; }

    /// <summary>
    /// Texture addressing: bit 0 clamps U, bit 1 clamps V (FF_TexClampX/Y).
    /// Surfaces that differ here batch separately, since addressing is per-draw.
    /// </summary>
    public int ClampMode { get; init; }
}

/// <summary>
/// A level's geometry batched for drawing: a single vertex/index buffer plus a
/// list of per-material index ranges. The renderer binds each material's texture
/// and issues one indexed draw per submesh.
/// </summary>
public sealed class RenderScene
{
    public RenderLightingMode LightingMode { get; set; } = RenderLightingMode.Mono;
    /// <summary>
    /// Textured scalar shading uses the CMP light table when true, or base palette
    /// times scalar light for the OpenJK accelerated preview when false. Solid MATs
    /// always retain their separate palette-cache rules. Defaults to the legacy tool profile.
    /// </summary>
    public bool UsePaletteLightRamp { get; set; } = true;
    /// <summary>
    /// Back-face cutaway: draw every world surface only from in front of its plane, as the
    /// engine does, so ceilings and outer walls facing away from an outside camera vanish
    /// (Wren's "doll's house" view). Models stay double-sided. Adjoined surfaces are always
    /// single-sided; this extends the rule to ordinary walls, floors and ceilings.
    /// </summary>
    public bool CullWorldBackFaces { get; set; }
    public List<string> Diagnostics { get; } = new();
    public Mesh Mesh { get; } = new();
    public List<Submesh> Submeshes { get; } = new();
}
