# Engine static light composition for baked previews (stdlib only).
# Part of SED-MotS (Red's claude branch). Finishes Wren's level-preview work: Wren (a Grok bot,
# one of Andrei's collaborators) wrote the original render.py; recovered 2026-10-31.
# Uses Coda's Blender-Sith fork (v1.0.1-coda.1), based on Blender-Sith by Crt Vavros (smlu).
# SPDX-License-Identifier: GPL-3.0-or-later
"""Per-corner light the way JK's hardware path composes it.

Evidence: OpenJKDF2 ``sithRender.c`` sets a world face's ambient floor from the sector's extra
light and its extra light from the surface; ``rdCache.c`` draws ``clamp(clamp(I + extra), floor, 1)``.
SED ``SceneAssembler.StaticLight`` implements the same composition. This is "baked" light from the
JKL's own vertex intensities, not a new lighting solution; dynamic lights are not added.

World surfaces: ``L = 1`` for light mode 0 or sky; else ``max(clamp(clamp(I) + E_surface),
clamp(E_sector))`` with ``I = 0`` for light modes 1 and 2. The sector ``AMBIENT LIGHT`` is used only
for things. Things: ``L`` per corner from the 3DO (Gouraud ``clamp(clamp(I) + faceExtra)``, fully lit
``1``, not lit or diffuse ``clamp(faceExtra)``), floored per instance at
``clamp(ambient + extra)`` of the thing's sector.
"""
from __future__ import annotations

import math


def clamp01(value: float) -> float:
    if not math.isfinite(value):
        raise ValueError("Scene lighting contains a nonfinite value.")
    return 0.0 if value < 0.0 else 1.0 if value > 1.0 else value


def world_corner(light_mode: int, sky: bool, intensity, surface_extra: float, sector_extra: float):
    """RGB light of one world-surface corner; ``intensity`` is ``(r, g, b)`` (mono repeated for JK)."""
    if sky or light_mode == 0:
        return (1.0, 1.0, 1.0)
    floor = clamp01(sector_extra)
    out = []
    for channel in intensity:
        base = 0.0 if light_mode in (1, 2) else channel
        out.append(max(clamp01(clamp01(base) + surface_extra), floor))
    return tuple(out)


def model_mode(mesh_mode: int, face_mode: int) -> int:
    """SED's effective 3DO light mode: ``min(mesh (6 read as 3), face)``, anything above 3 as 3."""
    mesh = 3 if mesh_mode == 6 else mesh_mode
    mode = min(mesh, face_mode)
    return 3 if mode > 3 else max(mode, 0)


def model_corner(mode: int, intensity, face_extra):
    """RGB light of one 3DO face corner before the sector floor; ``face_extra`` is ``(r, g, b)``."""
    if mode == 0:
        return (1.0, 1.0, 1.0)
    out = []
    for channel, extra in zip(intensity, face_extra):
        base = 0.0 if mode in (1, 2) else channel
        out.append(clamp01(clamp01(base) + extra))
    return tuple(out)


def thing_floor(sector_ambient: float, sector_extra: float) -> float:
    """Per-instance floor ``clamp(ambient + extra)`` of the thing's sector."""
    return clamp01(sector_ambient + sector_extra)
