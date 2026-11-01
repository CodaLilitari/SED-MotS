# Tests for engine-style resource lookup over folders and archives (stdlib only).
# Part of SED-MotS (Red's claude branch). Finishes Wren's level-preview work: Wren (a Grok bot,
# one of Andrei's collaborators) wrote the original render.py; recovered 2026-10-31.
# Uses Coda's Blender-Sith fork (v1.0.1-coda.1), based on Blender-Sith by Crt Vavros (smlu).
# SPDX-License-Identifier: GPL-3.0-or-later
import tempfile
import unittest
from pathlib import Path

import fixtures
from jk_level_importer import resources
from jk_level_importer.gob import GobError
from jk_level_importer.resources import LevelLookupError, Resolver, open_source


class ResolverTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def gob(self, name, entries):
        return open_source(fixtures.write_gob(self.root / name, entries))

    def test_path_major_order_across_sources(self):
        episode = self.gob("episode.gob", [("3do\\mat\\wall.mat", b"episode 3do/mat"), ("mat\\other.mat", b"episode")])
        retail = self.gob("res2.gob", [("mat\\wall.mat", b"retail mat"), ("3do\\mat\\only.mat", b"retail 3do/mat")])
        resolver = Resolver([episode, retail])
        wall = resolver.resolve("mat", "Wall.mat")
        # mat/ is searched in every source before 3do/mat/ in any source, like the engine.
        self.assertEqual(wall.data, b"retail mat")
        self.assertEqual(wall.source_index, 1)
        self.assertEqual(wall.entry, "mat\\wall.mat")
        self.assertEqual(len(wall.shadowed), 1)
        self.assertEqual(wall.shadowed[0]["entry"], "3do\\mat\\wall.mat")
        self.assertEqual(resolver.resolve("mat", "only.mat").data, b"retail 3do/mat")
        self.assertEqual(resolver.resolve("mat", "other.mat").source_index, 0)

    def test_first_source_wins_for_the_same_path_and_identical_copies_are_not_shadowed(self):
        a = self.gob("a.gob", [("misc\\cmp\\dflt.cmp", b"A"), ("3do\\x.3do", b"same")])
        b = self.gob("b.gob", [("misc\\cmp\\dflt.cmp", b"B"), ("3do\\x.3do", b"same")])
        resolver = Resolver([a, b])
        cmp = resolver.resolve("cmp", "DFLT.CMP")
        self.assertEqual(cmp.data, b"A")
        self.assertEqual([s["sha256"] for s in cmp.shadowed], [__import__("hashlib").sha256(b"B").hexdigest()])
        self.assertEqual(resolver.resolve("3do", "x.3do").shadowed, [])

    def test_loose_folder_is_case_insensitive(self):
        loose = self.root / "loose"
        (loose / "Mat").mkdir(parents=True)
        (loose / "Mat" / "Brick.MAT").write_bytes(b"loose brick")
        (loose / "3DO").mkdir()
        (loose / "3DO" / "Crate.3do").write_bytes(b"crate")
        resolver = Resolver([open_source(loose)])
        self.assertEqual(resolver.resolve("mat", "brick.mat").data, b"loose brick")
        self.assertEqual(resolver.resolve("3do", "CRATE.3DO").entry, "3DO/Crate.3do")

    def test_basename_fallback_warns_and_reports_ambiguity(self):
        a = self.gob("a.gob", [("odd\\place\\stone.mat", b"one")])
        b = self.gob("b.gob", [("elsewhere\\stone.mat", b"two")])
        resolver = Resolver([a, b])
        stone = resolver.resolve("mat", "stone.mat")
        self.assertEqual(stone.data, b"one")
        self.assertTrue(stone.by_basename)
        self.assertIn("ambiguous", stone.notes)
        self.assertTrue(any("resolved by basename to" in w and "odd\\place\\stone.mat" in w for w in resolver.warnings))

    def test_missing_resources_are_listed_once(self):
        resolver = Resolver([self.gob("a.gob", [("mat\\a.mat", b"a")])])
        self.assertIsNone(resolver.resolve("mat", "absent.mat"))
        self.assertIsNone(resolver.resolve("mat", "ABSENT.MAT"))
        self.assertEqual(resolver.missing, {"mat": ["absent.mat"]})

    def test_cache_layout(self):
        cache = self.root / "cache"
        resolver = Resolver([self.gob("a.gob", [("3do\\Crate.3do", b"crate"), ("misc\\cmp\\dflt.cmp", b"cmp")])], cache)
        crate = resolver.resolve("3do", "crate.3do")
        path = resolver.extract(crate)
        self.assertEqual(path, cache / "src" / "0" / "3do" / "crate.3do")
        self.assertEqual(path.read_bytes(), b"crate")
        self.assertEqual(resolver.material_path("Sub\\Wall.MAT"), cache / "mat" / "wall.mat")
        self.assertEqual(resolver.records[0]["entry"], "3do\\Crate.3do")
        self.assertEqual(resolver.records[0]["bytes"], 5)

    def test_cache_paths_never_leave_the_cache(self):
        # Entry names come from archives and level files. The GOB reader refuses '.'/'..' rows when it
        # opens an archive; the cache itself refuses any path that would leave it, as a second line.
        with self.assertRaises(GobError):
            self.gob("hostile.gob", [("3do\\..\\..\\..\\..\\escape.3do", b"x")])
        cache = self.root / "deep" / "cache"
        resolver = Resolver([self.gob("a.gob", [("3do\\crate.3do", b"crate")])], cache)
        found = resolver.resolve("3do", "crate.3do")
        found.key = "3do/../../../../escape.3do"          # as if a source had produced such a key
        with self.assertRaises(resources.CacheError):
            resolver.extract(found)
        self.assertEqual([p for p in self.root.rglob("escape.3do")], [])
        for name in ("..", ".", "sub\\..", "", "   "):
            with self.subTest(name=name), self.assertRaises(resources.CacheError):
                resolver.material_path(name)
        with self.assertRaises(resources.CacheError):
            resolver.cache_path("src", "..", "x")

    def test_cache_folder_must_be_new_and_outside_every_input(self):
        level = self.root / "lvl"
        (level / "jkl").mkdir(parents=True)
        (level / "mat").mkdir()
        (level / "jkl" / "room.jkl").write_bytes(b"SECTION: HEADER\n")
        (self.root / "res").mkdir()
        archive = fixtures.write_gob(self.root / "res" / "res2.gob", [("mat\\x.mat", b"x")])
        extra = self.root / "extra"
        extra.mkdir()
        sith = self.root / "bsith" / "sith"
        sith.mkdir(parents=True)
        level_input = resources.open_level(str(level / "jkl" / "room.jkl"), [str(archive)], [str(extra)])
        roots = resources.protected_roots(level_input, sith)
        self.assertEqual({root for _, root in roots},
                         {level.resolve(), (level / "jkl").resolve(), extra.resolve(), (self.root / "res").resolve(),
                          (self.root / "bsith").resolve()})
        refused = [level, level / "new-cache", level / "mat", level / "jkl", self.root / "res" / "cache",
                   extra / "cache", self.root / "bsith" / "cache", self.root]
        for path in refused:
            with self.subTest(path=str(path)), self.assertRaisesRegex(resources.CacheError, "overlaps"):
                resources.check_cache_dir(path, roots)
        fresh = self.root / "fresh"
        self.assertEqual(resources.check_cache_dir(fresh, roots), fresh.resolve())
        fresh.mkdir()
        self.assertEqual(resources.check_cache_dir(fresh, roots), fresh.resolve())     # empty is fine
        (fresh / "left.mat").write_bytes(b"x")
        with self.assertRaisesRegex(resources.CacheError, "not empty"):
            resources.check_cache_dir(fresh, roots)
        (self.root / "plain").write_bytes(b"x")
        with self.assertRaisesRegex(resources.CacheError, "not a folder"):
            resources.check_cache_dir(self.root / "plain", roots)

    def test_find_level_entry(self):
        a = self.gob("a.gob", [("jkl\\one.jkl", b""), ("jkl\\two.jkl", b"")])
        b = self.gob("b.gob", [("jkl\\two.jkl", b""), ("levels\\three.jkl", b""), ("x\\three.jkl", b"")])
        sources = [a, b]
        self.assertEqual(resources.find_level_entry(sources, "jkl\\ONE.jkl"), (0, "jkl/one.jkl"))
        self.assertEqual(resources.find_level_entry(sources, "two.jkl"), (0, "jkl/two.jkl"))
        with self.assertRaises(LevelLookupError) as caught:
            resources.find_level_entry(sources, "three.jkl")
        self.assertEqual(len(caught.exception.candidates), 2)
        with self.assertRaises(LevelLookupError):
            resources.find_level_entry(sources, "four.jkl")
        self.assertEqual(resources.level_entries(b), ["jkl/two.jkl", "levels/three.jkl", "x/three.jkl"])


if __name__ == "__main__":
    unittest.main()
