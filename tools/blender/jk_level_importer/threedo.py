# 3DO text helpers for the JK level importer: material list, encoding, enum-safe modes (stdlib only).
# Part of SED-MotS (Red's claude branch). Finishes Wren's level-preview work: Wren (a Grok bot,
# one of Andrei's collaborators) wrote the original render.py; recovered 2026-10-31.
# Uses Coda's Blender-Sith fork (v1.0.1-coda.1), based on Blender-Sith by Crt Vavros (smlu).
# SPDX-License-Identifier: GPL-3.0-or-later
"""Small 3DO text helpers used before Blender-Sith imports a model.

Blender-Sith parses the full model; these only read the ``MATERIALS`` list (so every MAT is imported
once, with SED's rules, before the model), make the text UTF-8 (Blender-Sith opens 3DOs as UTF-8),
and rewrite mode values its strict enums reject to the modes the engine draws them as.
"""
from __future__ import annotations

import re


def canonical(name: str) -> str:
    """Case-insensitive MAT key: lower-case basename (Blender's ``.001`` suffix removed)."""
    base = name.replace("\\", "/").rsplit("/", 1)[-1].strip().lower()
    return re.sub(r"\.\d{3}$", "", base) if re.search(r"\.mat\.\d{3}$", base) else base


def read_3do_materials(text: str) -> list:
    """The ``MATERIALS`` list of a 3DO (any version), read without a full parse."""
    lines = [line.split("#", 1)[0].strip() for line in text.splitlines()]
    for i, line in enumerate(lines):
        match = re.match(r"^materials\s+(\d+)\s*$", line, re.I)
        if not match:
            continue
        count, names = int(match.group(1)), []
        for row in lines[i + 1:]:
            if len(names) >= count:
                break
            if not row:
                continue
            entry = re.match(r"^\d+\s*:\s*(\S+)", row)
            if not entry:
                break
            names.append(entry.group(1))
        return names
    return []


def decode_3do(data: bytes):
    """3DO text as UTF-8 for Blender-Sith (which opens files as UTF-8); Latin-1 bytes are re-encoded."""
    try:
        return data.decode("utf-8"), data, False
    except UnicodeDecodeError:
        text = data.decode("latin-1")
        return text, text.encode("utf-8"), True


_SITH_GEO = range(0, 5)          # Blender-Sith's GeometryMode, LightingMode and TextureMode enums
_SITH_LIGHT = (0, 1, 2, 3, 6)
_SITH_TEX = range(0, 4)
_FACE_ROW = re.compile(r"^(\s*\d+\s*:\s*-?\d+\s+\S+\s+)(-?\d+)(\s+)(-?\d+)(\s+)(-?\d+)(\s.*)$")
_MESH_MODE = re.compile(r"^(\s*(GEOMETRYMODE|LIGHTINGMODE|TEXTUREMODE)\s+)(-?\d+)(.*)$", re.I)


def _clamp_modes(geo: int, light_mode: int, texture: int):
    """Engine-equivalent values inside Blender-Sith's enums: geometry 5 draws as textured (4), lighting modes
    other than 0-3 and 6 as Gouraud (3), texture modes above 3 as perspective-corrected (3)."""
    geo = geo if geo in _SITH_GEO else (4 if geo > 4 else 0)
    light_mode = light_mode if light_mode in _SITH_LIGHT else 3
    texture = texture if texture in _SITH_TEX else 3
    return geo, light_mode, texture


def sanitize_3do(text: str):
    """Rewrite mode values Blender-Sith's strict enums reject (for example geometry mode 5) to the values the
    engine draws them as. Returns ``(text, changed_count)``; only the cache copy is rewritten."""
    out, changed, in_faces = [], 0, False
    for line in text.splitlines(keepends=True):
        body = line.split("#", 1)[0].rstrip("\r\n")
        upper = body.strip().upper()
        if upper.startswith("FACE NORMALS"):
            in_faces = False
        elif re.match(r"^FACES\s+\d+", upper):
            in_faces = True
        match = _MESH_MODE.match(body)
        if match:
            value = int(match.group(3))
            kind = match.group(2).upper()
            fixed = _clamp_modes(value if kind == "GEOMETRYMODE" else 4, value if kind == "LIGHTINGMODE" else 3,
                                 value if kind == "TEXTUREMODE" else 3)
            new = fixed[0] if kind == "GEOMETRYMODE" else fixed[1] if kind == "LIGHTINGMODE" else fixed[2]
            if new != value:
                line = f"{match.group(1)}{new}{match.group(4)}" + line[len(body):]
                changed += 1
        elif in_faces:
            match = _FACE_ROW.match(body)
            if match:
                values = (int(match.group(2)), int(match.group(4)), int(match.group(6)))
                fixed = _clamp_modes(*values)
                if fixed != values:
                    line = (f"{match.group(1)}{fixed[0]}{match.group(3)}{fixed[1]}{match.group(5)}{fixed[2]}"
                            f"{match.group(7)}" + line[len(body):])
                    changed += 1
        out.append(line)
    return "".join(out), changed
