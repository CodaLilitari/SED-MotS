# Tests for the GOB/GOO reader of the JK level importer (stdlib only).
# Part of SED-MotS (Red's claude branch). Finishes Wren's level-preview work: Wren (a Grok bot,
# one of Andrei's collaborators) wrote the original render.py; recovered 2026-10-31.
# Uses Coda's Blender-Sith fork (v1.0.1-coda.1), based on Blender-Sith by Crt Vavros (smlu).
# SPDX-License-Identifier: GPL-3.0-or-later
import hashlib
import struct
import tempfile
import unittest
from pathlib import Path

import fixtures
from jk_level_importer.gob import GobArchive, GobError, normalize


class GobTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def write(self, data: bytes, name="a.gob") -> Path:
        path = self.root / name
        path.write_bytes(data)
        return path

    def test_round_trip_with_backslashes_and_case(self):
        path = fixtures.write_gob(self.root / "x.gob", [("JKL\\Level.JKL", b"level"), ("mat\\Wall.mat", b"MAT data")])
        gob = GobArchive(path)
        self.assertEqual(gob.order, ["jkl/level.jkl", "mat/wall.mat"])
        self.assertEqual(gob.read("mat/wall.mat"), b"MAT data")
        self.assertEqual(gob.raw_name("jkl/level.jkl"), "JKL\\Level.JKL")
        self.assertEqual(gob.sha256, hashlib.sha256(path.read_bytes()).hexdigest())
        self.assertEqual(gob.names(".jkl"), ["jkl/level.jkl"])
        self.assertEqual(normalize("3DO\\MAT\\X.MAT"), "3do/mat/x.mat")

    def test_duplicate_rows_keep_the_first(self):
        gob = GobArchive(fixtures.write_gob(self.root / "d.gob", [("mat\\a.mat", b"one"), ("MAT\\A.MAT", b"two")]))
        self.assertEqual(gob.read("mat/a.mat"), b"one")
        self.assertEqual(gob.duplicates, ["MAT\\A.MAT"])

    def test_refusals(self):
        good = fixtures.gob_bytes([("mat\\a.mat", b"x" * 10)])
        cases = {
            "bad magic": (b"GOX " + good[4:], "bad magic"),
            "version": (fixtures.gob_bytes([("a", b"")], version=0x15), "Unsupported GOB version"),
            "directory": (good[:8] + struct.pack("<I", len(good)) + good[12:], "directory offset"),
            "short header": (b"GOB ", "Truncated GOB header"),
        }
        row_past_end = bytearray(good)
        struct.pack_into("<I", row_past_end, 16 + 4, 10_000)
        cases["row past EOF"] = (bytes(row_past_end), "outside the archive")
        unterminated = fixtures.gob_bytes([(b"m" * 128, b"")])
        cases["unterminated"] = (unterminated, "unterminated name")
        too_many = bytearray(good)
        struct.pack_into("<I", too_many, 12, 100001)
        cases["count"] = (bytes(too_many), "100000-entry limit")
        escape = fixtures.gob_bytes([("..\\evil.mat", b"")])
        cases["escape"] = (escape, "unsupported path")
        for label, (data, text) in cases.items():
            with self.subTest(label):
                with self.assertRaises(GobError) as caught:
                    GobArchive(self.write(data, label.replace(" ", "_") + ".gob"))
                self.assertIn(text, str(caught.exception))

    def test_missing_file_is_a_gob_error(self):
        with self.assertRaises(GobError):
            GobArchive(self.root / "absent.gob")


if __name__ == "__main__":
    unittest.main()
