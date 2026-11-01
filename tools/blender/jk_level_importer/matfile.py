# MAT inspection and normalisation with SED's rules, for Blender-Sith (stdlib only).
# Part of SED-MotS (Red's claude branch). Finishes Wren's level-preview work: Wren (a Grok bot,
# one of Andrei's collaborators) wrote the original render.py; recovered 2026-10-31.
# Uses Coda's Blender-Sith fork (v1.0.1-coda.1), based on Blender-Sith by Crt Vavros (smlu).
# SPDX-License-Identifier: GPL-3.0-or-later
"""Decide what a MAT shows, exactly as SED's ``MatFile`` does, and rewrite it for Blender-Sith.

Rules (SED ``dotnet/src/Sed.Formats/Material/MatFile.cs`` plus the retail mip fallback):

* header 76 bytes: ``"MAT "``, version ``0x32``, type (0 colour, 2 texture), cel count, texture
  count, colour mode, bpp, r/g/b bits @28/32/36, shifts @40/44/48, bit differences @52/56/60,
  alpha bits/shift/difference @64/68/72;
* the first cel's texture is shown (texture index at offset 76+36); every earlier texture must be
  complete, because its mip chain locates the selected one;
* retail mip fallback: when the selected texture's mip 0 is complete but a later mip is short, mip 0
  is shown with a note (retail JK reads mips unchecked and draws mip 0 on faces nearer than the
  level's MipMap Distances; farther faces use the short level); a short mip 0 is refused;
* 8-bit indices go through the master palette, and alpha flag bit 0 keys index 0;
* 16/32-bit pixels decode with the declared channel layout, or OpenJKDF2's fixed RGB565/ARGB1555
  when a 16-bit header declares no channels, or bit 15 as the mask of a masked 15-bit texture.

``normalize`` writes a single-cel, single-texture, single-mip MAT that Blender-Sith's ``importMat``
decodes to exactly those pixels: 8-bit keeps its indices (stored colour key 0, alpha flag bit 0
only), true colour is pre-expanded to 32-bit ``A<<24|R<<16|G<<8|B`` with SED's rounding.
"""
from __future__ import annotations

import struct
from dataclasses import dataclass, field

HEADER = 76
CEL = 40
TEXTURE = 24

MSG_TRUNCATED_MIP = "Truncated MAT mip pixel data."
MSG_MIP_DIMENSIONS = "MAT mip count exceeds its dimensions."


class MatError(ValueError):
    """The MAT cannot be shown (same texts as SED)."""


def _i32(data: bytes, offset: int) -> int:
    return struct.unpack_from("<i", data, offset)[0]


@dataclass(frozen=True)
class Channel:
    bits: int
    shift: int

    def valid(self, bpp: int) -> bool:
        return 1 <= self.bits <= 8 and 0 <= self.shift <= bpp - self.bits

    @property
    def mask(self) -> int:
        return 0 if self.bits == 0 else ((1 << self.bits) - 1) << self.shift

    def expand(self, pixel: int, absent: int) -> int:
        """Eight bits with rounding, ``(v*255 + max/2) / max`` (SED, as GL does for packed formats)."""
        if self.bits == 0:
            return absent
        top = (1 << self.bits) - 1
        value = (pixel >> self.shift) & top
        return (value * 255 + top // 2) // top


@dataclass(frozen=True)
class TrueColorLayout:
    bpp: int
    r: Channel
    g: Channel
    b: Channel
    a: Channel

    @property
    def has_alpha(self) -> bool:
        return self.a.bits != 0

    @staticmethod
    def from_header(data: bytes, bpp: int, alpha_enabled: bool) -> "TrueColorLayout":
        r = Channel(_i32(data, 28), _i32(data, 40))
        g = Channel(_i32(data, 32), _i32(data, 44))
        b = Channel(_i32(data, 36), _i32(data, 48))
        a = Channel(_i32(data, 64), _i32(data, 68))
        if r.bits == 0 and g.bits == 0 and b.bits == 0:
            # Legacy JKPaint 16-bit MATs omit the channel fields; OpenJKDF2 uploads them as
            # RGB565, or ARGB1555 when alpha flag bit 0 is set.
            if bpp != 16:
                raise MatError(f"MAT {bpp}-bit pixels declare no RGB channel layout.")
            if alpha_enabled:
                return TrueColorLayout(16, Channel(5, 10), Channel(5, 5), Channel(5, 0), Channel(1, 15))
            return TrueColorLayout(16, Channel(5, 11), Channel(6, 5), Channel(5, 0), Channel(0, 0))
        if (not r.valid(bpp) or not g.valid(bpp) or not b.valid(bpp)
                or r.mask & g.mask or r.mask & b.mask or g.mask & b.mask):
            raise MatError(f"MAT {bpp}-bit pixels declare an invalid or overlapping RGB channel layout.")
        rgb = r.mask | g.mask | b.mask
        if a.bits != 0:
            if not a.valid(bpp) or a.mask & rgb:
                raise MatError(f"MAT {bpp}-bit pixels declare an invalid or overlapping alpha channel.")
        elif bpp == 16 and alpha_enabled and not rgb & 0x8000:
            # A masked 15-bit texture: OpenJKDF2 uploads it as ARGB1555, so free bit 15 is its mask.
            a = Channel(1, 15)
        return TrueColorLayout(bpp, r, g, b, a)

    def decode(self, pixels: bytes, count: int) -> bytes:
        out = bytearray(count * 4)
        fmt = "<%dH" % count if self.bpp == 16 else "<%dI" % count
        values = struct.unpack_from(fmt, pixels, 0)
        r, g, b, a = self.r, self.g, self.b, self.a
        for i, pixel in enumerate(values):
            o = i * 4
            out[o] = r.expand(pixel, 0)
            out[o + 1] = g.expand(pixel, 0)
            out[o + 2] = b.expand(pixel, 0)
            out[o + 3] = a.expand(pixel, 255)
        return bytes(out)


@dataclass
class MatInfo:
    kind: str | None = None          # "color", "indexed", "truecolor"; None when refused
    bpp: int = 0
    width: int = 0
    height: int = 0
    has_color_key: bool = False
    selected: int = 0
    mip_count: int = 0
    mip_fallback_note: str | None = None
    error: str | None = None
    color_index: int = 0
    cel_count: int = 0
    texture_count: int = 0
    layout: TrueColorLayout | None = None
    alpha_flags: int = 0
    pixel_offset: int = 0            # start of the selected texture's mip 0
    cel_offset: int = HEADER         # first cel header
    texture_offset: int = 0          # selected texture's 24-byte header
    notes: list = field(default_factory=list)

    @property
    def ok(self) -> bool:
        return self.error is None

    @property
    def uv_size(self) -> tuple:
        """Mip 0 size that divides texel UVs (SED uses 64x64 for colour MATs)."""
        return (self.width, self.height)

    @property
    def has_alpha(self) -> bool:
        if self.kind == "indexed":
            return self.has_color_key
        if self.kind == "truecolor" and self.layout is not None:
            return self.layout.has_alpha
        return False


def inspect(data: bytes) -> MatInfo:
    """What SED shows for this MAT, or the refusal text in ``error``."""
    info = MatInfo()
    try:
        _inspect(data, info)
    except MatError as exc:
        info.kind = None
        info.error = str(exc)
    return info


def _inspect(d: bytes, info: MatInfo):
    n = len(d)
    if n < HEADER or d[0:3] != b"MAT":
        raise MatError("Not a MAT file (bad magic)")
    version = _i32(d, 4)
    if version != 0x32:
        raise MatError(f"Unsupported MAT version 0x{version & 0xFFFFFFFF:X}")
    mat_type = _i32(d, 8)
    cel_count = _i32(d, 12)
    color_mode = _i32(d, 20)
    bpp = _i32(d, 24)
    info.cel_count = cel_count
    if mat_type == 0:
        if n < HEADER + 8:
            raise MatError("Truncated color MAT header.")
        info.kind, info.color_index, info.width, info.height = "color", _i32(d, HEADER + 4), 64, 64
        info.bpp = 8
        return
    if mat_type != 2:
        raise MatError(f"Unsupported MAT type {mat_type}.")
    if bpp in (16, 32):
        if color_mode not in (1, 2):
            raise MatError(f"MAT {bpp}-bit pixels require an RGB or RGBA colour mode, not mode {color_mode}.")
    elif bpp != 8:
        raise MatError(f"MAT {bpp}-bit pixels are not supported; expected 8, 16 or 32.")
    info.bpp = bpp
    per_pixel = bpp // 8
    if cel_count < 1 or cel_count > (n - HEADER) // CEL:
        raise MatError("MAT cel headers are out of range.")
    td = HEADER + cel_count * CEL
    texture_count = _i32(d, 16)
    info.texture_count = texture_count
    if texture_count < 1 or texture_count > (n - td) // TEXTURE:
        raise MatError("MAT texture count is out of range.")
    selected = _i32(d, HEADER + 36)
    if selected < 0 or selected >= texture_count:
        raise MatError("MAT first cel references an out-of-range texture.")
    info.selected = selected
    for texture in range(selected + 1):
        if td > n - TEXTURE:
            raise MatError("Truncated MAT texture header.")
        w, h = _i32(d, td), _i32(d, td + 4)
        if w < 1 or h < 1 or w > 16384 or h > 16384 or w * h > 16_777_216:
            raise MatError("MAT dimensions exceed the supported 16-million-pixel budget.")
        alpha_flags = _i32(d, td + 8)
        mip_count = _i32(d, td + 20)
        if mip_count < 1 or mip_count > 15:
            raise MatError("MAT mip count is out of range.")
        header_at = td
        pixels = td + TEXTURE
        td = pixels
        if texture != selected:
            mw, mh = w, h
            for _ in range(mip_count):
                if mw == 0 or mh == 0:
                    raise MatError(MSG_MIP_DIMENSIONS)
                size = mw * mh * per_pixel
                if size > n - td:
                    raise MatError(MSG_TRUNCATED_MIP)
                td += size
                mw >>= 1
                mh >>= 1
            continue
        if (w >> (mip_count - 1)) < 1 or (h >> (mip_count - 1)) < 1:
            raise MatError(MSG_MIP_DIMENSIONS)
        for level in range(mip_count):
            mw, mh = w >> level, h >> level
            size = mw * mh * per_pixel
            if size > n - td:
                if level == 0:
                    raise MatError(MSG_TRUNCATED_MIP)
                remaining = max(0, n - td)
                # Retail picks a mip per face by depth against the JKL's MipMap Distances, so it shows
                # mip 0 only on nearby faces; farther faces sample the level this file never filled.
                info.mip_fallback_note = (
                    f"Retail mip fallback: mip level {level} of {mip_count} levels ({mw}x{mh}) needs {size} bytes "
                    f"but {remaining} remain; showing complete mip level 0, as retail JK does on faces nearer "
                    f"than the level's MipMap Distances; retail faces far enough to use mip level {level} or later "
                    f"show data the file never filled.")
                break
            td += size
        info.width, info.height, info.mip_count = w, h, mip_count
        info.alpha_flags = alpha_flags
        info.has_color_key = bool(alpha_flags & 1)
        info.pixel_offset = pixels
        info.texture_offset = header_at
        if bpp == 8:
            info.kind = "indexed"
        else:
            info.layout = TrueColorLayout.from_header(d, bpp, info.has_color_key)
            info.kind = "truecolor"
        return
    raise MatError("MAT selected texture was not found.")


def decode_rgba(data: bytes, info: MatInfo | None = None, palette=None) -> bytes:
    """RGBA8 of mip 0, top row first: true colour as SED decodes it; indexed or colour via ``palette``.

    ``palette`` is a sequence of 256 ``(r, g, b)`` tuples (the master CMP).
    """
    info = info or inspect(data)
    if not info.ok:
        raise MatError(info.error)
    count = info.width * info.height
    if info.kind == "truecolor":
        return info.layout.decode(data[info.pixel_offset:info.pixel_offset + count * info.bpp // 8], count)
    if palette is None:
        raise MatError("An indexed MAT needs a palette.")
    out = bytearray(count * 4)
    if info.kind == "color":
        r, g, b = palette[info.color_index & 0xFF]
        for i in range(count):
            out[i * 4:i * 4 + 4] = bytes((r, g, b, 255))
        return bytes(out)
    indices = data[info.pixel_offset:info.pixel_offset + count]
    for i, index in enumerate(indices):
        r, g, b = palette[index]
        out[i * 4:i * 4 + 4] = bytes((r, g, b, 0 if info.has_color_key and index == 0 else 255))
    return bytes(out)


def _header(color_mode: int, bpp: int, rgb_bits=(0, 0, 0), rgb_shift=(0, 0, 0), alpha=(0, 0),
            mat_type: int = 2, cels: int = 1, textures: int = 1) -> bytes:
    return (b"MAT " + struct.pack("<iiii", 0x32, mat_type, cels, textures)
            + struct.pack("<ii", color_mode, bpp)
            + struct.pack("<iii", *rgb_bits) + struct.pack("<iii", *rgb_shift) + struct.pack("<iii", 0, 0, 0)
            + struct.pack("<iii", alpha[0], alpha[1], 0))


def normalize(data: bytes, info: MatInfo | None = None) -> bytes:
    """A MAT that Blender-Sith decodes to the pixels SED shows; raises MatError when refused."""
    info = info or inspect(data)
    if not info.ok:
        raise MatError(info.error)
    if info.kind == "color":
        # One colour record; tex_format fields reset so Blender-Sith's header checks always pass,
        # and the index kept inside the 256-entry palette Blender-Sith indexes directly.
        record = struct.pack("<6i", 0, info.color_index & 0xFF, 0, 0, 0, 0)
        return _header(0, 8, mat_type=0, cels=1, textures=0) + record
    cel = bytearray(d_slice(data, info.cel_offset, CEL))
    struct.pack_into("<i", cel, 36, 0)
    w, h = info.width, info.height
    key_flag = info.alpha_flags & 1
    if info.kind == "indexed":
        texture = struct.pack("<6i", w, h, key_flag, 0, 0, 1)
        pixels = data[info.pixel_offset:info.pixel_offset + w * h]
        return _header(0, 8) + bytes(cel) + texture + pixels
    rgba = decode_rgba(data, info)
    alpha = info.layout.has_alpha
    out = bytearray(w * h * 4)
    for i in range(w * h):
        r, g, b, a = rgba[i * 4:i * 4 + 4]
        struct.pack_into("<I", out, i * 4, (a << 24) | (r << 16) | (g << 8) | b)
    header = _header(2 if alpha else 1, 32, (8, 8, 8), (16, 8, 0), (8, 24) if alpha else (0, 0))
    texture = struct.pack("<6i", w, h, key_flag, 0, 0, 1)
    return header + bytes(cel) + texture + bytes(out)


def d_slice(data: bytes, offset: int, size: int) -> bytes:
    chunk = data[offset:offset + size]
    return chunk + bytes(size - len(chunk))


def describe(info: MatInfo) -> str:
    if not info.ok:
        return f"refused: {info.error}"
    if info.kind == "color":
        return f"colour index {info.color_index}"
    text = f"{info.kind} {info.bpp}-bit {info.width}x{info.height}"
    if info.selected:
        text += f" (first cel shows texture {info.selected})"
    if info.mip_fallback_note:
        text += " [mip fallback]"
    return text
