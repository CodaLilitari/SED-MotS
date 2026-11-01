# Tests for the engine light composition used by baked previews (stdlib only).
# Part of SED-MotS (Red's claude branch). Finishes Wren's level-preview work: Wren (a Grok bot,
# one of Andrei's collaborators) wrote the original render.py; recovered 2026-10-31.
# Uses Coda's Blender-Sith fork (v1.0.1-coda.1), based on Blender-Sith by Crt Vavros (smlu).
# SPDX-License-Identifier: GPL-3.0-or-later
import unittest

import fixtures  # noqa: F401
from jk_level_importer import light


class Close(unittest.TestCase):
    def assertEqual(self, first, second, msg=None):  # numeric tuples compare to 1e-12
        if isinstance(first, (tuple, float)) and isinstance(second, (tuple, float)):
            a = first if isinstance(first, tuple) else (first,)
            b = second if isinstance(second, tuple) else (second,)
            self.assertEqual(len(a), len(b), msg)
            for x, y in zip(a, b):
                self.assertAlmostEqual(x, y, delta=1e-12, msg=msg)
        else:
            super().assertEqual(first, second, msg)


class WorldLightTests(Close):
    def test_fully_lit_and_sky(self):
        self.assertEqual(light.world_corner(0, False, (0.1, 0.1, 0.1), 0.0, 0.0), (1.0, 1.0, 1.0))
        self.assertEqual(light.world_corner(3, True, (0.0, 0.0, 0.0), 0.0, 0.0), (1.0, 1.0, 1.0))

    def test_gouraud_adds_surface_extra_and_floors_at_sector_extra(self):
        self.assertEqual(light.world_corner(3, False, (0.5, 0.5, 0.5), 0.25, 0.0), (0.75, 0.75, 0.75))
        self.assertEqual(light.world_corner(3, False, (0.1, 0.1, 0.1), 0.0, 0.3), (0.3, 0.3, 0.3))
        self.assertEqual(light.world_corner(3, False, (0.9, 0.9, 0.9), 0.5, 0.0), (1.0, 1.0, 1.0))

    def test_not_lit_and_diffuse_ignore_intensity(self):
        for mode in (1, 2):
            self.assertEqual(light.world_corner(mode, False, (0.8, 0.8, 0.8), 0.2, 0.1), (0.2, 0.2, 0.2))
            self.assertEqual(light.world_corner(mode, False, (0.8, 0.8, 0.8), 0.0, 0.0), (0.0, 0.0, 0.0))

    def test_clamping_order(self):
        # clamp(clamp(I) + E): an intensity above 1 is clamped before the extra is added.
        self.assertEqual(light.world_corner(3, False, (1.7, 1.7, 1.7), -0.5, 0.0), (0.5, 0.5, 0.5))
        self.assertEqual(light.world_corner(3, False, (-0.4, -0.4, -0.4), 0.3, -2.0), (0.3, 0.3, 0.3))
        with self.assertRaises(ValueError):
            light.world_corner(3, False, (float("nan"),) * 3, 0.0, 0.0)

    def test_mots_rgb_channels(self):
        self.assertEqual(light.world_corner(3, False, (0.2, 0.5, 0.9), 0.1, 0.25), (0.3, 0.6, 1.0))


class ThingLightTests(Close):
    def test_modes(self):
        self.assertEqual(light.model_corner(0, (0.1, 0.1, 0.1), (0.0, 0.0, 0.0)), (1.0, 1.0, 1.0))
        self.assertEqual(light.model_corner(3, (0.4, 0.4, 0.4), (0.1, 0.1, 0.1)), (0.5, 0.5, 0.5))
        self.assertEqual(light.model_corner(1, (0.4, 0.4, 0.4), (0.1, 0.1, 0.1)), (0.1, 0.1, 0.1))
        self.assertEqual(light.model_corner(2, (0.4, 0.4, 0.4), (1.5, 1.5, 1.5)), (1.0, 1.0, 1.0))

    def test_effective_mode_and_floor(self):
        self.assertEqual(light.model_mode(6, 3), 3)
        self.assertEqual(light.model_mode(3, 0), 0)
        self.assertEqual(light.model_mode(1, 3), 1)
        self.assertEqual(light.model_mode(5, 4), 3)
        self.assertEqual(light.thing_floor(0.88, 0.04), 0.92)
        self.assertEqual(light.thing_floor(0.9, 0.4), 1.0)


if __name__ == "__main__":
    unittest.main()
