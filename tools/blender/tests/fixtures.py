# Synthetic GOB, MAT, CMP, 3DO and JKL builders for the JK level importer tests (stdlib only).
# Part of SED-MotS (Red's claude branch). Finishes Wren's level-preview work: Wren (a Grok bot,
# one of Andrei's collaborators) wrote the original render.py; recovered 2026-10-31.
# Uses Coda's Blender-Sith fork (v1.0.1-coda.1), based on Blender-Sith by Crt Vavros (smlu).
# SPDX-License-Identifier: GPL-3.0-or-later
"""No game data: every archive, texture, palette, model and level here is built from code."""
from __future__ import annotations

import struct
import sys
from dataclasses import dataclass
from pathlib import Path

BLENDER_TOOLS = Path(__file__).resolve().parents[1]
if str(BLENDER_TOOLS) not in sys.path:
    sys.path.insert(0, str(BLENDER_TOOLS))


# ---- GOB ---------------------------------------------------------------------------------------
def gob_bytes(entries, version=0x14, magic=b"GOB ") -> bytes:
    """``entries``: ``[(name, bytes)]``; the directory follows the 12-byte header like retail GOBs."""
    header = 12
    directory = 4 + 136 * len(entries)
    offset = header + directory
    rows, blobs = [], []
    for name, data in entries:
        raw = name.encode("ascii") if isinstance(name, str) else name
        rows.append(struct.pack("<II", offset, len(data)) + raw.ljust(128, b"\0")[:128])
        blobs.append(data)
        offset += len(data)
    return magic + struct.pack("<II", version, header) + struct.pack("<I", len(entries)) + b"".join(rows) + b"".join(blobs)


def write_gob(path, entries, **kw) -> Path:
    path = Path(path)
    path.write_bytes(gob_bytes(entries, **kw))
    return path


# ---- MAT ---------------------------------------------------------------------------------------
@dataclass
class Layout:
    color_mode: int
    bpp: int
    r_bits: int = 0
    g_bits: int = 0
    b_bits: int = 0
    r_shift: int = 0
    g_shift: int = 0
    b_shift: int = 0
    a_bits: int = 0
    a_shift: int = 0


INDEXED = Layout(0, 8)
RGB565 = Layout(1, 16, 5, 6, 5, 11, 5, 0)
RGB555 = Layout(1, 16, 5, 5, 5, 10, 5, 0)          # Con Krape's grs3.mat: no declared alpha
ARGB8888 = Layout(2, 32, 8, 8, 8, 16, 8, 0, 8, 24)
LEGACY16 = Layout(1, 16)                            # JKPaint: no channel fields


@dataclass
class Texture:
    width: int
    height: int
    flags: int
    mips: list
    color_key: int = 0


def mat_bytes(layout: Layout, selected: int, *textures: Texture, cels: int = 1) -> bytes:
    """A texture MAT like SED's test builder: ``cels`` cel headers, the first selecting ``selected``."""
    out = bytearray(b"MAT ")
    out += struct.pack("<iiii", 0x32, 2, cels, len(textures))
    out += struct.pack("<ii", layout.color_mode, layout.bpp)
    out += struct.pack("<iii", layout.r_bits, layout.g_bits, layout.b_bits)
    out += struct.pack("<iii", layout.r_shift, layout.g_shift, layout.b_shift)
    out += struct.pack("<iii", 8 - layout.r_bits, 8 - layout.g_bits, 8 - layout.b_bits)
    out += struct.pack("<iii", layout.a_bits, layout.a_shift, 8 - layout.a_bits)
    for cel in range(cels):
        out += struct.pack("<i", 8) + bytes(32) + struct.pack("<i", selected if cel == 0 else 0)
    for texture in textures:
        out += struct.pack("<6i", texture.width, texture.height, texture.flags, 0, texture.color_key, len(texture.mips))
        for mip in texture.mips:
            out += mip
    return bytes(out)


def color_mat_bytes(index: int) -> bytes:
    out = bytearray(b"MAT ")
    out += struct.pack("<iiii", 0x32, 0, 1, 0)
    out += struct.pack("<ii", 0, 8) + bytes(48)
    out += struct.pack("<6i", 3, index, 0, 0, 0, 0)
    return bytes(out)


def u16(*values) -> bytes:
    return struct.pack("<%dH" % len(values), *values)


def u32(*values) -> bytes:
    return struct.pack("<%dI" % len(values), *values)


# ---- CMP ---------------------------------------------------------------------------------------
def cmp_bytes(colors: dict, version=0x1E) -> bytes:
    """64-byte header plus a 256-entry palette; ``colors`` maps index -> (r, g, b)."""
    palette = bytearray(768)
    for index, (r, g, b) in colors.items():
        palette[index * 3:index * 3 + 3] = bytes((r, g, b))
    return b"CMP " + struct.pack("<ii", version, 0) + bytes(52) + bytes(palette) + bytes(64 * 256)


# ---- 3DO ---------------------------------------------------------------------------------------
def cube_3do(material: str = "cube.mat", insert_offset=(0.1, 0.2, 0.3), half: float = 0.5, child_offset=(0.0, 0.0, 1.0),
             face_light: int = 3, vertex_intensity: float = 0.25, face_extra: float = 0.0) -> str:
    """A 3DO 2.1 with a two-node hierarchy: node 0 a cube mesh, node 1 a smaller cube above it."""
    def mesh(index, name, size):
        v = [(-size, -size, -size), (size, -size, -size), (size, size, -size), (-size, size, -size),
             (-size, -size, size), (size, -size, size), (size, size, size), (-size, size, size)]
        faces = [(0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)]
        normals = [(0, 0, -1), (0, 0, 1), (0, -1, 0), (1, 0, 0), (0, 1, 0), (-1, 0, 0)]
        lines = [f"MESH {index}", f"NAME {name}", f"RADIUS {size * 1.8:.6f}",
                 "GEOMETRYMODE\t4", "LIGHTINGMODE\t3", "TEXTUREMODE\t3", "", f"VERTICES {len(v)}"]
        for i, p in enumerate(v):
            lines.append(f"{i}: {p[0]:.6f} {p[1]:.6f} {p[2]:.6f} {vertex_intensity:.6f}")
        lines += ["", "TEXTURE VERTICES 4", "0: 0.000000 0.000000", "1: 16.000000 0.000000",
                  "2: 16.000000 16.000000", "3: 0.000000 16.000000", "", "VERTEX NORMALS"]
        for i, p in enumerate(v):
            lines.append(f"{i}: {p[0] / size * 0.57735:.6f} {p[1] / size * 0.57735:.6f} {p[2] / size * 0.57735:.6f}")
        lines += ["", f"FACES {len(faces)}"]
        for i, f in enumerate(faces):
            corners = " ".join(f"{a}, {k}" for k, a in enumerate(f))
            lines.append(f"{i}: 0 0x0 4 {face_light} 3 {face_extra:.6f} 4 {corners}")
        lines += ["", "FACE NORMALS"]
        for i, n in enumerate(normals):
            lines.append(f"{i}: {n[0]:.6f} {n[1]:.6f} {n[2]:.6f}")
        return lines

    ox, oy, oz = insert_offset
    cx, cy, cz = child_offset
    text = ["# synthetic 3DO for tests", "SECTION: HEADER", "3DO 2.1", "",
            "SECTION: MODELRESOURCE", "MATERIALS 1", f"0: {material}", "",
            "SECTION: GEOMETRYDEF", "RADIUS 1.000000", f"INSERT OFFSET {ox:.6f} {oy:.6f} {oz:.6f}",
            "GEOSETS 1", "GEOSET 0", "MESHES 2", ""]
    text += mesh(0, "base", half) + [""] + mesh(1, "top", half / 2) + [""]
    text += ["SECTION: HIERARCHYDEF", "HIERARCHY NODES 2",
             "0: 0x0 0x1 0 -1 1 -1 1 0.000000 0.000000 0.000000 0.000000 0.000000 0.000000 0.000000 0.000000 0.000000 base",
             f"1: 0x0 0x1 1 0 -1 -1 0 {cx:.6f} {cy:.6f} {cz:.6f} 0.000000 30.000000 0.000000 0.000000 0.000000 0.000000 top",
             ""]
    return "\n".join(text)


# ---- JKL ---------------------------------------------------------------------------------------
def jkl_text(*, materials, colormaps=("dflt.cmp",), vertices, texverts, surfaces, normals, sectors,
             templates=(), things=(), header_extra=(), material_capacity=None, thing_capacity=None,
             template_capacity=None, adjoins=()) -> str:
    """A JKL from rows: ``surfaces`` are the text after ``i:``; ``sectors`` are lists of key lines."""
    out = ["# synthetic level for tests", "SECTION: JK", "", "SECTION: HEADER", "Version 1", "World Gravity 4.00",
           "Ceiling Sky Z 3.000000", "Horizon Distance 100.000000", "Horizon Pixels per Rev 768.000000",
           "Horizon Sky Offset 0.000000 0.000000", "Ceiling Sky Offset 0.000000 0.000000", *header_extra, "",
           "SECTION: MATERIALS", f"World materials {material_capacity or len(materials)}"]
    out += [f"{i}: {name} 1.000000 1.000000" for i, name in enumerate(materials)]
    out += ["end", "", "SECTION: GEORESOURCE", f"World Colormaps {len(colormaps)}"]
    out += [f"{i}: {name}" for i, name in enumerate(colormaps)]
    out += [f"World vertices {len(vertices)}"] + [f"{i}: {x:.6f} {y:.6f} {z:.6f}" for i, (x, y, z) in enumerate(vertices)]
    out += [f"World texture vertices {len(texverts)}"] + [f"{i}: {u:.6f} {v:.6f}" for i, (u, v) in enumerate(texverts)]
    out += [f"World adjoins {len(adjoins)}"] + [f"{i}: {row}" for i, row in enumerate(adjoins)]
    out += [f"World surfaces {len(surfaces)}"] + [f"{i}: {row}" for i, row in enumerate(surfaces)]
    out += [f"{i}: {x:.6f} {y:.6f} {z:.6f}" for i, (x, y, z) in enumerate(normals)]
    out += ["", "SECTION: SECTORS", f"World sectors {len(sectors)}"]
    for i, lines in enumerate(sectors):
        out += [f"SECTOR {i}", *lines]
    out += ["", "SECTION: TEMPLATES", f"World templates {template_capacity or len(templates)}", *templates, "end",
            "", "SECTION: THINGS", f"World things {thing_capacity or len(things)}"]
    out += [f"{i}: {row}" for i, row in enumerate(things)]
    out += ["end", ""]
    return "\n".join(out)


def box_room(x0, y0, z0, x1, y1, z1):
    """Eight corners of an axis-aligned room, JK vertex order used by ``room_faces``."""
    return [(x0, y0, z0), (x1, y0, z0), (x1, y1, z0), (x0, y1, z0), (x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1)]


# Faces of a closed room seen from inside: corners ordered so the stored normals point inward.
ROOM_FACES = [((0, 1, 2, 3), (0, 0, 1)),        # floor, normal up
              ((7, 6, 5, 4), (0, 0, -1)),       # ceiling, normal down
              ((0, 4, 5, 1), (0, 1, 0)),        # south wall (y0), normal +y
              ((1, 5, 6, 2), (-1, 0, 0)),       # east wall (x1), normal -x
              ((2, 6, 7, 3), (0, -1, 0)),       # north wall (y1), normal -y
              ((3, 7, 4, 0), (1, 0, 0))]        # west wall (x0), normal +x
