# Tests for JK orientation (pitch/yaw/roll) against rdMatrix_Build34 (stdlib only).
# Part of SED-MotS (Red's claude branch). Finishes Wren's level-preview work: Wren (a Grok bot,
# one of Andrei's collaborators) wrote the original render.py; recovered 2026-10-31.
# Uses Coda's Blender-Sith fork (v1.0.1-coda.1), based on Blender-Sith by Crt Vavros (smlu).
# SPDX-License-Identifier: GPL-3.0-or-later
import math
import random
import unittest

import fixtures  # noqa: F401  (sys.path for the package)
from jk_level_importer import orient


def build34(pitch, yaw, roll):
    """Independent transcription of OpenJKDF2 rdMatrix_Build34: the rvec, lvec, uvec columns."""
    p, y, r = (math.radians(a) for a in (pitch, yaw, roll))
    sp, cp, sy, cy, sr, cr = math.sin(p), math.cos(p), math.sin(y), math.cos(y), math.sin(r), math.cos(r)
    rvec = (cy * cr - sr * sp * sy, sy * cr + sr * sp * cy, -sr * cp)
    lvec = (-sy * cp, cy * cp, sp)
    uvec = (sr * cy + cr * sp * sy, sr * sy - cr * sp * cy, cr * cp)
    return rvec, lvec, uvec


class OrientationTests(unittest.TestCase):
    def assert_close(self, a, b, tol=1e-12):
        for u, v in zip(a, b):
            for x, y in zip(u, v):
                self.assertAlmostEqual(x, y, delta=tol)

    def test_golden(self):
        rvec, lvec, uvec = orient.columns(orient.pyr_matrix(30, 45, 60))
        self.assert_close((rvec, lvec, uvec), ((0.047367172745, 0.659739608441, -0.75),
                                               (-0.612372435696, 0.612372435696, 0.5),
                                               (0.789149130992, 0.435595740399, 0.433012701892)), 1e-11)

    def test_random_angles_match_build34(self):
        rng = random.Random(1997)
        for _ in range(500):
            angles = tuple(rng.uniform(-360, 360) for _ in range(3))
            self.assert_close(orient.columns(orient.pyr_matrix(*angles)), build34(*angles))

    def test_blender_euler_order_and_values(self):
        order, euler = orient.blender_euler(30, 45, 60)
        self.assertEqual(order, "YXZ")
        self.assertEqual(euler, (math.radians(30), math.radians(60), math.radians(45)))

    def test_world_matrix_has_no_insert_offset(self):
        m = orient.world_matrix((1, 2, 3), (0, 90, 0))
        self.assertEqual([row[3] for row in m[:3]], [1, 2, 3])
        self.assertAlmostEqual(m[0][1], -1.0)   # yaw 90: +Y (look) turns to -X


if __name__ == "__main__":
    unittest.main()
