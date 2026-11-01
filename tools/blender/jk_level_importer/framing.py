# Wren framing: auto-framed overhead and three-quarter cameras (stdlib only).
# Part of SED-MotS (Red's claude branch). Finishes Wren's level-preview work: Wren (a Grok bot,
# one of Andrei's collaborators) wrote the original render.py; recovered 2026-10-31.
# Uses Coda's Blender-Sith fork (v1.0.1-coda.1), based on Blender-Sith by Crt Vavros (smlu).
# SPDX-License-Identifier: GPL-3.0-or-later
"""The shared camera rule ("Wren framing") used by this importer and by ``sed-agent overview``.

Wren framed an orthographic overhead and a 35 mm three-quarter perspective on the level's bounds.
This keeps Wren's directions and numbers and fixes them for both tools:

* bounds are the drawn world surfaces only (``geo != 0``, three or more corners, a material; sky
  included; things excluded);
* overhead: orthographic, straight down, screen up = +Y, vertical extent
  ``max(s.y, s.x / aspect) * (1 + 2 * margin)``;
* three-quarter: from ``(0.7, -0.7, 0.6)``, Blender lens 35 mm on a 36 mm sensor (``AUTO`` fit);
  ``wren`` places the eye at ``0.75 * |size|`` like Wren, ``contain`` backs off until all eight box
  corners fit inside the margin.
"""
from __future__ import annotations

import math
from dataclasses import dataclass

LENS_MM = 35.0
SENSOR_MM = 36.0
WREN_DIRECTION = (0.7, -0.7, 0.6)
VIEWS = ("overhead", "threequarter")
FITS = ("wren", "contain")


class FramingError(ValueError):
    pass


@dataclass(frozen=True)
class Box:
    min: tuple
    max: tuple

    @property
    def center(self):
        return tuple((a + b) / 2 for a, b in zip(self.min, self.max))

    @property
    def size(self):
        return tuple(b - a for a, b in zip(self.min, self.max))


@dataclass(frozen=True)
class ViewCamera:
    view: str
    projection: str              # "orthographic" or "perspective"
    position: tuple
    target: tuple
    near: float
    far: float
    width: int
    height: int
    orthographic_height: float = 0.0
    orthographic_width: float = 0.0
    blender_ortho_scale: float = 0.0
    fov_y_degrees: float = 0.0
    distance: float = 0.0
    lens_mm: float = LENS_MM
    sensor_mm: float = SENSOR_MM

    def report(self) -> dict:
        out = {"name": self.view, "projection": self.projection, "position": list(self.position),
               "target": list(self.target), "near": self.near, "far": self.far,
               "width": self.width, "height": self.height}
        if self.projection == "orthographic":
            out["orthographicHeight"] = self.orthographic_height
            out["orthoScale"] = self.blender_ortho_scale
        else:
            out["fieldOfViewDegrees"] = self.fov_y_degrees
            out["lensMillimetres"] = self.lens_mm
        return out


def _dot(a, b):
    return sum(x * y for x, y in zip(a, b))


def _cross(a, b):
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def _normalize(v):
    length = math.sqrt(_dot(v, v))
    return tuple(x / length for x in v)


def bounds(points) -> Box | None:
    """Axis-aligned box of points, or None when there are none."""
    lo = [math.inf] * 3
    hi = [-math.inf] * 3
    any_point = False
    for p in points:
        any_point = True
        for k in range(3):
            if p[k] < lo[k]:
                lo[k] = p[k]
            if p[k] > hi[k]:
                hi[k] = p[k]
    return Box(tuple(lo), tuple(hi)) if any_point else None


def frame(box: Box | None, view: str, width: int, height: int, margin: float = 0.025, fit: str = "wren") -> ViewCamera:
    """Camera for ``view`` (``overhead`` or ``threequarter``) on ``box`` for a ``width`` x ``height`` image."""
    if view not in VIEWS:
        raise FramingError("view must be one of: overhead, threequarter.")
    if fit not in FITS:
        raise FramingError("framing must be one of: wren, contain.")
    if not (0.0 <= margin <= 0.25) or margin != margin:
        raise FramingError("margin must be finite and from 0 through 0.25.")
    if width < 1 or height < 1:
        raise FramingError("width and height must be positive.")
    if box is None:
        raise FramingError("Level has no drawn world surfaces to frame.")
    s = box.size
    if max(s[0], s[1]) < 1e-6:
        raise FramingError("Level bounds are degenerate in X and Y; nothing to frame.")
    a = width / height
    c = box.center
    big = math.sqrt(_dot(s, s))
    radius = big / 2
    if view == "overhead":
        h = max(s[0], s[1])
        eye = (c[0], c[1], box.max[2] + h)
        ortho_h = max(s[1], s[0] / a) * (1 + 2 * margin)
        ortho_w = ortho_h * a
        near = min(max(0.5 * h, 0.001), 1000.0)
        far = (eye[2] - box.min[2]) + near
        return ViewCamera(view, "orthographic", eye, c, near, far, width, height,
                          orthographic_height=ortho_h, orthographic_width=ortho_w,
                          blender_ortho_scale=max(ortho_h, ortho_w), distance=eye[2] - c[2])
    tan_v = (SENSOR_MM / 2 / LENS_MM) / max(a, 1.0)
    fov_y = 2 * math.degrees(math.atan(tan_v))
    tan_h = tan_v * a
    u = _normalize(WREN_DIRECTION)
    if fit == "wren":
        d = 0.75 * big
        eye = tuple(ci + k * d for ci, k in zip(c, WREN_DIRECTION))
        dist = math.sqrt(sum((e - ci) ** 2 for e, ci in zip(eye, c)))
    else:
        f = tuple(-x for x in u)
        r = _normalize(_cross(f, (0.0, 0.0, 1.0)))
        up = _cross(r, f)
        k_h = (1 - 2 * margin) * tan_h
        k_v = (1 - 2 * margin) * tan_v
        dist = 0.0
        for x in (box.min[0], box.max[0]):
            for y in (box.min[1], box.max[1]):
                for z in (box.min[2], box.max[2]):
                    q = (x - c[0], y - c[1], z - c[2])
                    dist = max(dist, abs(_dot(r, q)) / k_h - _dot(f, q), abs(_dot(up, q)) / k_v - _dot(f, q))
        eye = tuple(ci + ui * dist for ci, ui in zip(c, u))
    if dist > radius:
        near = min(max(0.5 * (dist - radius), 0.001), 1000.0)
    else:
        near = max(0.001, 0.001 * radius)
    far = dist + 1.01 * radius
    return ViewCamera(view, "perspective", eye, c, near, far, width, height, fov_y_degrees=fov_y, distance=dist)
