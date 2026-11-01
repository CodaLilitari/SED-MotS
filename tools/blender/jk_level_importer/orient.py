# JK pitch/yaw/roll to rotation matrices and Blender Euler angles (stdlib only).
# Part of SED-MotS (Red's claude branch). Finishes Wren's level-preview work: Wren (a Grok bot,
# one of Andrei's collaborators) wrote the original render.py; recovered 2026-10-31.
# Uses Coda's Blender-Sith fork (v1.0.1-coda.1), based on Blender-Sith by Crt Vavros (smlu).
# SPDX-License-Identifier: GPL-3.0-or-later
"""Exact JK thing orientation.

The engine builds a thing's matrix with OpenJKDF2 ``rdMatrix_Build34``, which equals
``Rz(yaw) * Rx(pitch) * Ry(roll)`` (SED ``Mat4.FromPyr``, Blender-Sith ``makeRotationMatrix``).
JK and Blender are both right-handed, Z up, +Y forward, so no axis swap is needed. Blender's Euler
order ``'YXZ'`` applies Y (roll) first, then X (pitch), then Z (yaw): the same product. Wren's
script used ``'ZXY'``, an approximation that is exact only when at most one angle is non-zero.
"""
from __future__ import annotations

import math

EULER_ORDER = "YXZ"


def _rz(a):
    c, s = math.cos(a), math.sin(a)
    return ((c, -s, 0.0), (s, c, 0.0), (0.0, 0.0, 1.0))


def _rx(a):
    c, s = math.cos(a), math.sin(a)
    return ((1.0, 0.0, 0.0), (0.0, c, -s), (0.0, s, c))


def _ry(a):
    c, s = math.cos(a), math.sin(a)
    return ((c, 0.0, s), (0.0, 1.0, 0.0), (-s, 0.0, c))


def matmul(a, b):
    return tuple(tuple(sum(a[i][k] * b[k][j] for k in range(3)) for j in range(3)) for i in range(3))


def pyr_matrix(pitch: float, yaw: float, roll: float):
    """3x3 rotation (rows) for JK angles in degrees: ``Rz(yaw) Rx(pitch) Ry(roll)``."""
    p, y, r = math.radians(pitch), math.radians(yaw), math.radians(roll)
    return matmul(_rz(y), matmul(_rx(p), _ry(r)))


def columns(matrix):
    """``(rvec, lvec, uvec)``: the engine's right, look and up vectors (matrix columns)."""
    return tuple(tuple(matrix[i][j] for i in range(3)) for j in range(3))


def blender_euler(pitch: float, yaw: float, roll: float):
    """``(order, (x, y, z))`` for ``obj.rotation_mode``/``obj.rotation_euler`` in radians."""
    return EULER_ORDER, (math.radians(pitch), math.radians(roll), math.radians(yaw))


def world_matrix(position, pyr):
    """4x4 row-major thing transform: translation then rotation (no 3DO insert offset)."""
    m = pyr_matrix(*pyr)
    x, y, z = position
    return ((m[0][0], m[0][1], m[0][2], x),
            (m[1][0], m[1][1], m[1][2], y),
            (m[2][0], m[2][1], m[2][2], z),
            (0.0, 0.0, 0.0, 1.0))
