# Tests for the 3DO text helpers used before Blender-Sith imports a model (stdlib only).
# Part of SED-MotS (Red's claude branch). Finishes Wren's level-preview work: Wren (a Grok bot,
# one of Andrei's collaborators) wrote the original render.py; recovered 2026-10-31.
# Uses Coda's Blender-Sith fork (v1.0.1-coda.1), based on Blender-Sith by Crt Vavros (smlu).
# SPDX-License-Identifier: GPL-3.0-or-later
import unittest

import fixtures
from jk_level_importer import threedo


class ThreeDoTests(unittest.TestCase):
    def test_material_list(self):
        self.assertEqual(threedo.read_3do_materials(fixtures.cube_3do("Crate.MAT")), ["Crate.MAT"])
        text = "SECTION: MODELRESOURCE\n# comment\nMATERIALS 3\n0:\ta.mat  # first\n\n1: b.mat\n2: c.mat\nSECTION: GEOMETRYDEF\n"
        self.assertEqual(threedo.read_3do_materials(text), ["a.mat", "b.mat", "c.mat"])
        self.assertEqual(threedo.read_3do_materials("SECTION: HEADER\n3DO 2.1\n"), [])

    def test_latin1_is_reencoded(self):
        text, data, recoded = threedo.decode_3do("# caf\xe9\n3DO 2.1\n".encode("latin-1"))
        self.assertTrue(recoded)
        self.assertEqual(data.decode("utf-8"), text)
        self.assertFalse(threedo.decode_3do(b"3DO 2.1\n")[2])

    def test_modes_outside_blender_sith_enums(self):
        text = ("MESH 0\nGEOMETRYMODE\t5 # textured\nLIGHTINGMODE\t4\r\nTEXTUREMODE\t3\nVERTICES 1\n0: 0.5 5 5 0.3\n"
                "FACES 2\n0: 0 0x0 5 4 7 0.0000 3 0, 0 1, 1 2, 2\n1: -1 0x0 4 6 3 0.0000 3 0, 0 1, 1 2, 2\n"
                "FACE NORMALS\n0: 5 5 5\n")
        out, changed = threedo.sanitize_3do(text)
        self.assertEqual(changed, 3)
        self.assertEqual(out, text.replace("GEOMETRYMODE\t5", "GEOMETRYMODE\t4").replace("LIGHTINGMODE\t4", "LIGHTINGMODE\t3")
                         .replace("0: 0 0x0 5 4 7", "0: 0 0x0 4 3 3"))
        self.assertEqual(threedo.sanitize_3do(fixtures.cube_3do())[1], 0)

    def test_canonical_material_key(self):
        self.assertEqual(threedo.canonical("3DO\\MAT\\Wall.MAT"), "wall.mat")
        self.assertEqual(threedo.canonical("wall.mat.001"), "wall.mat")
        self.assertEqual(threedo.canonical("odd.name.001"), "odd.name.001")


if __name__ == "__main__":
    unittest.main()
