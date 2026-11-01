# Tests for Wren framing: the shared golden camera values (stdlib only).
# Part of SED-MotS (Red's claude branch). Finishes Wren's level-preview work: Wren (a Grok bot,
# one of Andrei's collaborators) wrote the original render.py; recovered 2026-10-31.
# Uses Coda's Blender-Sith fork (v1.0.1-coda.1), based on Blender-Sith by Crt Vavros (smlu).
# SPDX-License-Identifier: GPL-3.0-or-later
"""Golden values of Wren framing; ``sed-agent overview`` reproduces the same numbers (OverviewFramingTests)."""
import math
import unittest

import fixtures  # noqa: F401
from jk_level_importer import framing
from jk_level_importer.framing import Box, FramingError

BOX = Box((-10.0, -5.0, 0.0), (10.0, 5.0, 4.0))
TOL = 1e-9


class FramingTests(unittest.TestCase):
    def vec(self, actual, expected):
        for a, e in zip(actual, expected):
            self.assertAlmostEqual(a, e, delta=TOL)

    def test_overhead_landscape(self):
        cam = framing.frame(BOX, "overhead", 1280, 960)
        self.assertEqual(cam.projection, "orthographic")
        self.vec(cam.position, (0, 0, 24))
        self.vec(cam.target, (0, 0, 2))
        self.assertAlmostEqual(cam.orthographic_height, 15.75, delta=TOL)
        self.assertAlmostEqual(cam.orthographic_width, 21.0, delta=TOL)
        self.assertAlmostEqual(cam.blender_ortho_scale, 21.0, delta=TOL)
        self.assertAlmostEqual(cam.near, 10.0, delta=TOL)
        self.assertAlmostEqual(cam.far, 34.0, delta=TOL)

    def test_overhead_portrait(self):
        cam = framing.frame(BOX, "overhead", 600, 800)
        self.assertAlmostEqual(cam.orthographic_height, 28.0, delta=TOL)
        self.assertAlmostEqual(cam.orthographic_width, 21.0, delta=TOL)
        self.assertAlmostEqual(cam.blender_ortho_scale, 28.0, delta=TOL)

    def test_threequarter_wren(self):
        cam = framing.frame(BOX, "threequarter", 1280, 960)
        self.vec(cam.position, (11.925707526180574, -11.925707526180574, 12.222035022440492))
        self.assertAlmostEqual(cam.fov_y_degrees, 42.18467901165499, delta=TOL)
        self.assertAlmostEqual(cam.near, 4.181809173731455, delta=TOL)
        self.assertAlmostEqual(cam.far, 31.192829897580012, delta=TOL)

    def test_threequarter_contain(self):
        cam = framing.frame(BOX, "threequarter", 1280, 960, fit="contain")
        self.vec(cam.position, (16.75338631061249, -16.75338631061249, 16.360045409096422))
        self.assertAlmostEqual(cam.fov_y_degrees, 42.18467901165499, delta=TOL)
        self.assertAlmostEqual(cam.near, 8.1735536185632, delta=TOL)
        self.assertAlmostEqual(cam.far, 39.1763187872435, delta=TOL)

    def test_threequarter_portrait_fov(self):
        self.assertAlmostEqual(framing.frame(BOX, "threequarter", 600, 800).fov_y_degrees, 54.43222311461495, delta=TOL)

    def test_contain_puts_every_corner_inside_the_margin(self):
        margin = 0.1
        cam = framing.frame(BOX, "threequarter", 1280, 960, margin=margin, fit="contain")
        tan_v = math.tan(math.radians(cam.fov_y_degrees) / 2)
        tan_h = tan_v * 1280 / 960
        f = tuple((t - p) for t, p in zip(cam.target, cam.position))
        length = math.sqrt(sum(x * x for x in f))
        f = tuple(x / length for x in f)
        r = (f[1], -f[0], 0.0)
        rl = math.sqrt(r[0] ** 2 + r[1] ** 2)
        r = (r[0] / rl, r[1] / rl, 0.0)
        up = (r[1] * f[2] - r[2] * f[1], r[2] * f[0] - r[0] * f[2], r[0] * f[1] - r[1] * f[0])
        worst = 0.0
        for x in (BOX.min[0], BOX.max[0]):
            for y in (BOX.min[1], BOX.max[1]):
                for z in (BOX.min[2], BOX.max[2]):
                    q = (x - cam.position[0], y - cam.position[1], z - cam.position[2])
                    depth = sum(a * b for a, b in zip(q, f))
                    nx = sum(a * b for a, b in zip(q, r)) / depth / tan_h
                    ny = sum(a * b for a, b in zip(q, up)) / depth / tan_v
                    worst = max(worst, abs(nx), abs(ny))
        self.assertLessEqual(worst, 1 - 2 * margin + 1e-9)
        self.assertAlmostEqual(worst, 1 - 2 * margin, delta=1e-9)

    def test_refusals(self):
        with self.assertRaises(FramingError) as caught:
            framing.frame(None, "overhead", 64, 64)
        self.assertEqual(str(caught.exception), "Level has no drawn world surfaces to frame.")
        with self.assertRaises(FramingError) as caught:
            framing.frame(Box((1, 1, 0), (1, 1, 5)), "overhead", 64, 64)
        self.assertEqual(str(caught.exception), "Level bounds are degenerate in X and Y; nothing to frame.")
        for kwargs, text in (({"view": "side"}, "view must be one of"), ({"fit": "tight"}, "framing must be one of"),
                             ({"margin": 0.3}, "margin must be finite")):
            args = {"view": "overhead", "fit": "wren", "margin": 0.025}
            args.update(kwargs)
            with self.assertRaises(FramingError) as caught:
                framing.frame(BOX, args["view"], 64, 64, margin=args["margin"], fit=args["fit"])
            self.assertIn(text, str(caught.exception))

    def test_bounds(self):
        box = framing.bounds([(1, 2, 3), (-1, 5, 0)])
        self.assertEqual((box.min, box.max), ((-1, 2, 0), (1, 5, 3)))
        self.assertIsNone(framing.bounds([]))


if __name__ == "__main__":
    unittest.main()
