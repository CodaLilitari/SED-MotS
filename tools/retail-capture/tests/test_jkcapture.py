# Unit tests for jkcapture.py (stdlib only; no Wine, X server or game data needed).
# Part of SED-MotS (Red's claude branch). Finishes Wren's level-preview work: Wren (a Grok bot,
# one of Andrei's collaborators) wrote the original jk.sh/run.sh; recovered 2026-10-31.
# SPDX-License-Identifier: GPL-3.0-or-later
"""Run: python3 -B -m unittest discover -s tools/retail-capture/tests -v"""
from __future__ import annotations

import argparse
import contextlib
import hashlib
import io
import json
import os
import shutil
import struct
import subprocess
import sys
import tempfile
import threading
import unittest
import zlib
from pathlib import Path
from unittest import mock

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
import jkcapture as jk  # noqa: E402

STUB_ORDINALS = (14, 17, 18, 19, 21, 23, 26, 32, 38)


# ------------------------------------------------------------------------------------------------
# Synthetic inputs

def make_pe(dll_name="smackw32.dll", ordinals=STUB_ORDINALS, names=(), entry=0, imports=False, reloc=False,
            dll=True, machine=0x14C, export_outside_text=False):
    """A minimal PE32 image: .text with `xor eax,eax; ret 4` bodies, .rdata with an export table."""
    text = bytearray(0x200)
    rvas = {}
    for index, ordinal in enumerate(sorted(ordinals)):
        text[0x10 * index:0x10 * index + 5] = b"\x31\xc0\xc2\x04\x00"
        rvas[ordinal] = 0x1000 + 0x10 * index
    if export_outside_text and rvas:
        rvas[min(rvas)] = 0x2180
    rdata = bytearray(0x200)
    base = min(ordinals)
    count = max(ordinals) - base + 1
    functions, name_ptrs, name_ords, strings = 0x40, 0x100, 0x140, 0x160
    cursor = strings
    dll_name_rva = 0x2000 + cursor
    rdata[cursor:cursor + len(dll_name) + 1] = dll_name.encode() + b"\0"
    cursor += len(dll_name) + 1
    for ordinal, rva in rvas.items():
        struct.pack_into("<I", rdata, functions + 4 * (ordinal - base), rva)
    for index, (name, ordinal) in enumerate(names):
        struct.pack_into("<I", rdata, name_ptrs + 4 * index, 0x2000 + cursor)
        struct.pack_into("<H", rdata, name_ords + 2 * index, ordinal - base)
        rdata[cursor:cursor + len(name) + 1] = name.encode() + b"\0"
        cursor += len(name) + 1
    struct.pack_into("<IIHHIIIIIII", rdata, 0, 0, 0, 0, 0, dll_name_rva, base, count, len(names),
                     0x2000 + functions, 0x2000 + name_ptrs, 0x2000 + name_ords)
    sections = [(b".text", 0x1000, text, 0x60000020), (b".rdata", 0x2000, rdata, 0x40000040)]
    if reloc:
        sections.append((b".reloc", 0x3000, bytearray(0x200), 0x42000040))
    header = bytearray(0x200)
    header[0:2] = b"MZ"
    struct.pack_into("<I", header, 0x3C, 0x40)
    header[0x40:0x44] = b"PE\0\0"
    struct.pack_into("<HHIIIHH", header, 0x44, machine, len(sections), 0, 0, 0, 0xE0, 0x2102 if dll else 0x0102)
    optional = 0x58
    struct.pack_into("<H", header, optional, 0x10B)
    struct.pack_into("<I", header, optional + 16, entry)
    struct.pack_into("<I", header, optional + 28, 0x10000000)
    struct.pack_into("<II", header, optional + 32, 0x1000, 0x200)
    struct.pack_into("<I", header, optional + 56, 0x1000 * (len(sections) + 1))
    struct.pack_into("<I", header, optional + 60, 0x200)
    struct.pack_into("<I", header, optional + 92, 16)
    directories = optional + 96
    struct.pack_into("<II", header, directories, 0x2000, 0x40)
    if imports:
        struct.pack_into("<II", header, directories + 8, 0x2180, 20)
    table = optional + 0xE0
    raw = 0x200
    for index, (name, rva, payload, flags) in enumerate(sections):
        at = table + 40 * index
        header[at:at + 8] = name.ljust(8, b"\0")
        struct.pack_into("<IIIIIIHHI", header, at + 8, len(payload), rva, len(payload), raw, 0, 0, 0, 0, flags)
        raw += len(payload)
    return bytes(header) + b"".join(bytes(payload) for _, _, payload, _ in sections)


def make_gob(entries):
    blob, rows, offset = bytearray(), [], 12
    for name, payload in entries:
        rows.append(struct.pack("<II128s", offset + len(blob), len(payload), name.encode("ascii")))
        blob += payload
    return struct.pack("<4sII", b"GOB ", 0x14, 12 + len(blob)) + bytes(blob) + struct.pack("<I", len(rows)) + b"".join(rows)


SP_EPISODE = b'"Kell Riders - Canyon Outpost"\nTYPE 1\nSEQ 1\n\n# line cd level type file light dark gotoA gotoB\n10: 1 1 LEVEL kellwar.jkl 0 0 -1 -1\nend\n'
MP_EPISODE = (b'"Jedi Training"\nTYPE 2\n\n# comment\n10: 1 1 LEVEL m2.jkl 0 0 -1 -1\n20: 1 1 LEVEL m4.jkl 0 0 -1 -1\n'
              b'30: 1 1 LEVEL m5.jkl 0 0 -1 -1\n40: 1 1 LEVEL m10.jkl 0 0 -1 -1\nend\n')

SYSTEM_REG = "\n".join([
    "WINE REGISTRY Version 2",
    r"[Software\\Microsoft\\DirectPlay\\Service Providers\\Internet TCP/IP Connection For DirectPlay] 1",
    '"Path"="dpwsockx.dll"', "",
    r"[Software\\Microsoft\\DirectPlay\\Service Providers\\IPX Connection For DirectPlay] 1", "",
    r"[Software\\Microsoft\\DirectPlay\\Service Providers\\Serial Connection For DirectPlay] 1", "",
    r"[Software\\Microsoft\\DirectPlay\\Service Providers\\Modem Connection For DirectPlay] 1", "",
    r"[Software\\Microsoft\\DirectPlay\\Service Providers\\Virtual Loopback (jkcapture)] 1",
    '"Guid"="{7158F146-36BB-4070-AC35-D6456B940FDB}"', "",
    r"[Software\\Microsoft\\DirectPlay\\Service Providers\\Modem Connection For DirectPlay\\Sub] 1", "",
    r"[Software\\Microsoft\\DirectPlay8\\Service Providers\\DPNSPWinsockTCP] 1", ""])


class TempDir(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="jkcapture-test-"))

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def write(self, name, data):
        path = self.tmp / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data if isinstance(data, bytes) else data.encode())
        return path


# ------------------------------------------------------------------------------------------------

class PeTests(TempDir):
    def test_parser_reads_ordinal_exports(self):
        info = jk.parse_pe(make_pe())
        self.assertEqual(info["machine"], 0x14C)
        self.assertTrue(info["isDll"])
        self.assertEqual(sorted(info["exports"]["functions"]), list(STUB_ORDINALS))
        self.assertEqual(info["exports"]["dllName"], "smackw32.dll")
        self.assertEqual(info["importDirectory"], (0, 0))

    def test_synthetic_stub_passes_and_variants_are_refused(self):
        self.assertEqual(jk.verify_stub(self.write("ok.dll", make_pe()))["ordinals"], list(STUB_ORDINALS))
        cases = {"imports.dll": (make_pe(imports=True), "no import table"),
                 "entry.dll": (make_pe(entry=0x1000), "no entry point"),
                 "ordinals.dll": (make_pe(ordinals=STUB_ORDINALS[:-1]), "differ"),
                 "named.dll": (make_pe(names=(("SmackOpen", 14),)), "ordinal only"),
                 "outside.dll": (make_pe(export_outside_text=True), "outside .text"),
                 "exe.dll": (make_pe(dll=False), "not a DLL"),
                 "x64.dll": (make_pe(machine=0x8664), "PE32 i386"),
                 "name.dll": (make_pe(dll_name="other.dll"), "not smackw32.dll")}
        for name, (data, message) in cases.items():
            with self.subTest(name), self.assertRaisesRegex(jk.CaptureError, message):
                jk.verify_stub(self.write(name, data))
        with self.assertRaisesRegex(jk.CaptureError, "MZ"):
            jk.parse_pe(b"\0" * 128)

    def test_synthetic_loopback_provider(self):
        good = make_pe(dll_name="jkcap_loopsp.dll", ordinals=(1,), names=(("SPInit", 1),), reloc=True)
        self.assertEqual(jk.verify_loopsp(self.write("good.dll", good))["exports"], ["SPInit"])
        no_reloc = make_pe(dll_name="jkcap_loopsp.dll", ordinals=(1,), names=(("SPInit", 1),))
        with self.assertRaisesRegex(jk.CaptureError, "relocations"):
            jk.verify_loopsp(self.write("noreloc.dll", no_reloc))
        extra = make_pe(dll_name="jkcap_loopsp.dll", ordinals=(1, 2), names=(("SPInit", 1), ("Other", 2)), reloc=True)
        with self.assertRaisesRegex(jk.CaptureError, "exactly SPInit"):
            jk.verify_loopsp(self.write("extra.dll", extra))

    @unittest.skipUnless(shutil.which("clang") and shutil.which("lld-link"), "clang and lld-link are not installed")
    def test_built_stub_and_provider_verify(self):
        stub = jk.build_stub(self.tmp / "stub")
        self.assertEqual(stub["ordinals"], list(STUB_ORDINALS))
        self.assertEqual(stub["imports"], 0)
        self.assertEqual(jk.parse_pe((self.tmp / "stub" / "smackw32.dll").read_bytes())["exports"]["names"], [])
        loop = jk.build_loopsp(self.tmp / "loop")
        self.assertEqual(loop["exports"], ["SPInit"])
        self.assertIn(".reloc", loop["sections"])
        again = jk.build_stub(self.tmp / "stub2")
        self.assertEqual(again["sha256"], stub["sha256"], "the stub build is reproducible (/Brepro)")
        with self.assertRaisesRegex(jk.CaptureError, "create-only"):
            jk.build_stub(self.tmp / "stub")


class MenuAndStepTests(unittest.TestCase):
    def setUp(self):
        self.menus = jk.load_menus()

    def test_every_click_lies_in_its_rect_and_the_gui(self):
        for screen in self.menus["screens"].values():
            for element in screen["elements"].values():
                x, y, w, h = element["rect"]
                cx, cy = element["click"]
                self.assertTrue(x <= cx < x + w and y <= cy < y + h)
                self.assertTrue(0 <= cx < 640 and 0 <= cy < 480)

    def test_broken_menus_are_refused(self):
        broken = {"schemaVersion": 1, "gui": {"width": 640, "height": 480},
                  "screens": {"main": {"elements": {"x": {"rect": [0, 0, 10, 10], "click": [20, 5]}}}}}
        with self.assertRaisesRegex(jk.CaptureError, "outside its rect"):
            jk.validate_menus(broken)

    def test_rows(self):
        self.assertEqual(jk.row_point(self.menus, "mp_provider.list", 4), (260, 250))
        self.assertEqual(jk.row_point(self.menus, "mp_host.levels", 3), (400, 345))
        with self.assertRaisesRegex(jk.CaptureError, "not visible"):
            jk.row_point(self.menus, "mp_host.levels", 7)

    def test_valid_steps(self):
        steps = [{"op": "wait", "s": 1}, {"op": "click", "target": "main.single_player", "wait": 1},
                 {"op": "click", "x": 10, "y": 20}, {"op": "type", "text": "Red"}, {"op": "key", "keys": ["Escape", "equal"]},
                 {"op": "hold", "key": "Up", "s": 4}, {"op": "list_select", "list": "sp_episode.list", "index": 1, "count": 2},
                 {"op": "click_row", "list": "mp_provider.list", "row": 0}, {"op": "shot", "name": "a"},
                 {"op": "shot", "name": "b"}, {"op": "expect", "name": "b", "region": [0, 60, 640, 360]},
                 {"op": "expect_changed", "name": "b", "from": "a", "min_fraction": 0.5},
                 {"op": "await", "region": [0, 0, 640, 480], "timeout": 5, "note": "x"}]
        self.assertIs(jk.validate_steps(steps, self.menus), steps)

    def test_invalid_steps(self):
        cases = [([], "1..500"), ([{"op": "dance"}], "unknown op"), ([{"op": "wait"}], "lacks"),
                 ([{"op": "wait", "s": 1, "x": 2}], "unsupported"), ([{"op": "click", "x": 700, "y": 1}], "outside 640x480"),
                 ([{"op": "click", "target": "main.nothing"}], "Unknown menu target"),
                 ([{"op": "type", "text": "é"}], "printable ASCII"), ([{"op": "key", "keys": ["a b"]}], "X key names"),
                 ([{"op": "shot", "name": "a"}, {"op": "shot", "name": "a"}], "duplicate"),
                 ([{"op": "shot", "name": "../x"}], "letters"), ([{"op": "expect", "name": "a", "region": [0, 0, 10, 10]}], "not taken"),
                 ([{"op": "shot", "name": "a"}, {"op": "expect", "name": "a", "region": [0, 0, 700, 10]}], "inside the 640x480"),
                 ([{"op": "shot", "name": "a"}, {"op": "expect_changed", "name": "a", "from": "a"}], "two different"),
                 ([{"op": "hold", "key": "Up", "s": 99}], "hold|s must"), ([{"op": "wait", "s": True}], "number"),
                 ([{"op": "click", "x": 1, "y": 1, "retry": 9}], "retry"),
                 ([{"op": "await", "region": [0, 0, 640, 480], "not_flat": 1}], "not_flat")]
        for steps, message in cases:
            with self.subTest(message), self.assertRaisesRegex(jk.CaptureError, message):
                jk.validate_steps(steps, self.menus)

    def test_walk_script_is_wrens_demo(self):
        steps = jk.walk_steps()
        jk.validate_steps(steps, self.menus)
        self.assertEqual([s["op"] for s in steps if s["op"] in ("hold", "key")], ["hold", "hold", "hold", "hold", "key"])
        self.assertEqual([s.get("key") for s in steps if s["op"] == "hold"], ["Left", "Up", "Left", "Up"])


class ScreenTests(unittest.TestCase):
    def test_coordinate_scaling(self):
        screen = jk.Screen(":99", window=1, offset=(3, 4), scale=(2.0, 1.5))
        screen.geometry = lambda: (100, 50, 1280, 720)
        self.assertEqual(screen.to_screen(320, 240), (100 + 3 + 640, 50 + 4 + 360))
        self.assertEqual(screen.gui_region([0, 60, 640, 360], 1280, 720), (3, 94, 1277, 540))
        identity = jk.Screen(":99", window=1)
        identity.geometry = lambda: (0, 0, 640, 480)
        self.assertEqual(identity.to_screen(520, 450), (520, 450))

    def test_xwininfo_parser(self):
        text = "  Absolute upper-left X:  10\n  Absolute upper-left Y:  20\n  Width: 640\n  Height: 480\n"
        self.assertEqual(jk.parse_xwininfo(text), (10, 20, 640, 480))
        with self.assertRaises(jk.CaptureError):
            jk.parse_xwininfo("Width: 1")

    def test_display_selection(self):
        taken = {91, 92}
        self.assertEqual(jk.choose_display(range(91, 100), lambda n: n in taken), 93)
        with self.assertRaisesRegex(jk.CaptureError, "No free"):
            jk.choose_display(range(91, 93), lambda n: n in taken)


class EpisodeAndGobTests(TempDir):
    def test_episode_parser(self):
        sp = jk.parse_episode(SP_EPISODE.decode())
        self.assertEqual((sp["title"], sp["type"], sp["singlePlayer"], sp["levels"]),
                         ("Kell Riders - Canyon Outpost", 1, True, ["kellwar.jkl"]))
        mp = jk.parse_episode(MP_EPISODE.decode())
        self.assertTrue(mp["multiplayer"] and not mp["singlePlayer"])
        self.assertEqual(mp["levels"].index("m10.jkl"), 3, "row index = LEVEL order")
        with self.assertRaises(jk.CaptureError):
            jk.parse_episode("# nothing\n")

    def test_gob_round_trip_and_wrap(self):
        source = self.write("JK1MP.GOB", make_gob([("episode.jk", MP_EPISODE), ("jkl\\m10.jkl", b"SECTION: HEADER\n")]))
        self.assertEqual(sorted(jk.read_gob_directory(source)), ["episode.jk", "jkl/m10.jkl"])
        result = jk.wrap_level(source, "m10.jkl", self.tmp / "m10sp.gob")
        info = jk.episode_info(self.tmp / "m10sp.gob")
        self.assertEqual((info["type"], info["levels"]), (1, ["m10.jkl"]))
        self.assertEqual(jk.read_gob_entry(self.tmp / "m10sp.gob", "jkl/M10.JKL"), b"SECTION: HEADER\n")
        self.assertEqual(result["entries"], 2)
        with self.assertRaisesRegex(jk.CaptureError, "new .gob"):
            jk.wrap_level(source, "m10.jkl", self.tmp / "m10sp.gob")
        with self.assertRaisesRegex(jk.CaptureError, "has no"):
            jk.wrap_level(source, "m99.jkl", self.tmp / "other.gob")

    def test_damaged_gobs_are_refused(self):
        good = make_gob([("episode.jk", SP_EPISODE)])
        cases = {"magic": b"GOX " + good[4:], "version": good[:4] + struct.pack("<I", 0x15) + good[8:],
                 "short": good[:8], "past_end": good[:-136] + struct.pack("<II128s", 12, 10 ** 6, b"x")}
        for name, data in cases.items():
            with self.subTest(name), self.assertRaises(jk.CaptureError):
                jk.read_gob_directory(self.write(f"{name}.gob", data))


class PrefixAndStagingTests(TempDir):
    def test_prefix_refusals(self):
        home = self.tmp / "home"
        (home / ".wine").mkdir(parents=True)
        environ = {"HOME": str(home)}
        self.assertEqual(jk.check_new_prefix(self.tmp / "new", environ, home), (self.tmp / "new").resolve())
        with self.assertRaisesRegex(jk.CaptureError, "exists"):
            jk.check_new_prefix(home, environ, home)
        with self.assertRaisesRegex(jk.CaptureError, r"\.wine"):
            jk.check_new_prefix(home / ".wine" / "jk", environ, home)
        user = self.tmp / "userprefix"
        user.mkdir()
        with self.assertRaisesRegex(jk.CaptureError, "WINEPREFIX"):
            jk.check_new_prefix(user / "jk", {**environ, "WINEPREFIX": str(user)}, home)
        with self.assertRaisesRegex(jk.CaptureError, "parent"):
            jk.check_new_prefix(self.tmp / "missing" / "jk", environ, home)

    def test_executable_allowlist(self):
        fake = self.write("JK.EXE", make_pe(dll=False))
        digest = hashlib.sha256(fake.read_bytes()).hexdigest()
        with self.assertRaisesRegex(jk.CaptureError, "not the official JK.EXE"):
            jk.check_executable(fake)
        with self.assertRaisesRegex(jk.CaptureError, "does not match"):
            jk.check_executable(fake, "0" * 64)
        self.assertTrue(jk.check_executable(fake, digest)["vouched"])
        with self.assertRaisesRegex(jk.CaptureError, "not a 32-bit Windows executable"):
            dll = self.write("X.EXE", make_pe())
            jk.check_executable(dll, hashlib.sha256(dll.read_bytes()).hexdigest())
        self.assertIn("4509cd690634ba20e132bd16e713f91e144b17e2beaaad5e678f1ddb189b51d9", jk.KNOWN_EXECUTABLES)

    def test_staging_accepts_archives_and_cd_marker_only(self):
        res = self.tmp / "res"
        for name in ("Res1hi.gob", "RES2.GOB"):
            self.write(f"res/{name}", make_gob([("misc\\x.txt", b"x")]))
        self.write("res/JK_.CD", b"\x01\x00\x00\x00")
        episode = self.write("episode/kelltest.gob", make_gob([("episode.jk", SP_EPISODE)]))
        plan = jk.plan_staging(res, (), (episode,))
        self.assertEqual(sorted(d.lower() for _, d, _ in plan),
                         ["episode/kelltest.gob", "resource/jk_.cd", "resource/res1hi.gob", "resource/res2.gob"])
        for name in ("smackw32.dll", "intro.smk", "JK.EXE", "notes.txt"):
            with self.subTest(name), self.assertRaisesRegex(jk.CaptureError, "only .gob/.goo archives and JK_.CD"):
                jk.classify_input(self.write(name, b"MZ"))
        with self.assertRaisesRegex(jk.CaptureError, "Missing resource"):
            jk.plan_staging(None, (res / "Res1hi.gob",), ())
        with self.assertRaisesRegex(jk.CaptureError, "Two inputs"):
            jk.plan_staging(res, (res / "RES2.GOB",), ())
        with self.assertRaises(jk.CaptureError):
            jk.plan_staging(res, (), (self.write("episode/broken.gob", b"GOB junk"),))

    def test_registry_commands(self):
        commands = jk.registry_commands()
        joined = [" ".join(c) for c in commands]
        self.assertIn(r"reg add HKCU\Software\Wine\Direct3D /v renderer /t REG_SZ /d gl /f", joined)
        self.assertIn(r"reg add HKCU\Software\Wine\Explorer\Desktops /v Default /t REG_SZ /d 640x480 /f", joined)
        self.assertTrue(any("b3DAccel /t REG_DWORD /d 0" in c for c in joined))
        loop = [c for c in commands if jk.LOOPBACK_SP["name"] in " ".join(c)]
        self.assertEqual(sorted(c[4] for c in loop), ["Guid", "Path", "dwReserved1", "dwReserved2"])
        self.assertTrue(all(c[2].startswith(jk.DIRECTPLAY_SP_KEY) for c in loop))
        self.assertEqual(len(jk.registry_commands("800x600", "gdi", True, loopback=False)), 4)
        for desktop, wined3d in (("640x48", "gl"), ("320x200", "gl"), ("640x480", "vulkan")):
            with self.subTest(desktop + wined3d), self.assertRaises(jk.CaptureError):
                jk.registry_commands(desktop, wined3d)

    def test_wine_environment_is_private(self):
        env = jk.wine_environment(self.tmp / "p", ":95")
        self.assertEqual((env["WINEPREFIX"], env["WINEARCH"], env["DISPLAY"]), (str(self.tmp / "p"), "win32", ":95"))
        self.assertEqual(env["HOME"], str(self.tmp / "p" / "home"))
        self.assertEqual(env["WINEDEBUG"], jk.DEFAULT_WINE_DEBUG)


class FlowTests(TempDir):
    def make_prefix(self, episodes, players=(), characters=(), name="prefix"):
        prefix = self.tmp / name
        jkdir = prefix / "drive_c" / "JK"
        for sub in ("episode", "player", "resource"):
            (jkdir / sub).mkdir(parents=True, exist_ok=True)
        for name, text in episodes:
            (jkdir / "episode" / name).write_bytes(make_gob([("episode.jk", text)]))
        for player in players:
            (jkdir / "player" / player).mkdir()
            for character in characters:
                (jkdir / "player" / player / f"{character}.mpc").write_bytes(b"x")
        (prefix / "system.reg").write_text(SYSTEM_REG)
        return prefix

    def args(self, **extra):
        values = {"player": "Red", "difficulty": "medium", "startup_wait": 4.0, "allow_black": False, "walk": False,
                  "full_view": False, "episode": None, "level_wait": 30.0, "level": "m10.jkl", "provider_row": None,
                  "character": None, "spawn_wait": 40.0}
        values.update(extra)
        return argparse.Namespace(**values)

    def test_single_player_flow(self):
        prefix = self.make_prefix([("kelltest.gob", SP_EPISODE)])
        steps, plan = jk.sp_plan(prefix, self.args(walk=True, full_view=True))
        jk.validate_steps(steps, jk.load_menus())
        names = [s["name"] for s in steps if s["op"] == "shot"]
        self.assertEqual(names[:4], ["00_new_player", "01_main_menu", "02_single_player", "03_level"])
        self.assertIn("08_view", names)
        self.assertIn({"op": "expect_changed", "name": "03_level", "from": "02_single_player", "min_fraction": 0.5,
                       "note": "a level frame, not the last menu with an error dialog over it"}, steps)
        self.assertEqual(plan["episodeIndex"], 0)
        returning, _ = jk.sp_plan(self.make_prefix([("kelltest.gob", SP_EPISODE)], players=("Red",), name="second"),
                                  self.args(allow_black=True))
        self.assertIn("00_player_select", [s.get("name") for s in returning])
        self.assertNotIn("expect_changed", [s["op"] for s in returning])

    def test_episode_choice_needs_a_known_name(self):
        prefix = self.make_prefix([("a.gob", SP_EPISODE), ("b.gob", SP_EPISODE)])
        steps, plan = jk.sp_plan(prefix, self.args(episode="b.gob"))
        self.assertIn("list_select", [s["op"] for s in steps])
        with self.assertRaisesRegex(jk.CaptureError, "No single-player episode"):
            jk.sp_plan(prefix, self.args(episode="c.gob"))
        with self.assertRaisesRegex(jk.CaptureError, "no single-player episode"):
            jk.sp_plan(self.make_prefix([], name="empty"), self.args())

    def test_multiplayer_host_flow_uses_the_loopback_row(self):
        prefix = self.make_prefix([("JK1MP.GOB", MP_EPISODE)])
        steps, plan = jk.mp_plan(prefix, self.args())
        jk.validate_steps(steps, jk.load_menus())
        self.assertEqual((plan["providerRow"], plan["provider"]), (4, jk.LOOPBACK_SP["name"]))
        self.assertEqual(plan["levelRow"], 3)
        rows = [s for s in steps if s["op"] == "click_row"]
        self.assertEqual([(r["list"], r["row"]) for r in rows],
                         [("mp_provider.list", 4), ("mp_host.levels", 3), ("mp_load_char.list", 0)])
        self.assertIn("06_no_valid_characters", [s.get("name") for s in steps])
        explicit, plan = jk.mp_plan(prefix, self.args(provider_row=0))
        self.assertEqual(plan["provider"], "Internet TCP/IP Connection For DirectPlay")
        with self.assertRaisesRegex(jk.CaptureError, "outside"):
            jk.mp_plan(prefix, self.args(provider_row=9))
        with self.assertRaisesRegex(jk.CaptureError, "has no LEVEL"):
            jk.mp_plan(prefix, self.args(level="m99.jkl"))

    def test_existing_character_skips_creation(self):
        prefix = self.make_prefix([("JK1MP.GOB", MP_EPISODE)], players=("Red",), characters=("Red",))
        steps, plan = jk.mp_plan(prefix, self.args())
        self.assertEqual(plan["existingCharacters"], ["Red.mpc"])
        self.assertNotIn("06_no_valid_characters", [s.get("name") for s in steps])

    def test_provider_order_matches_wine_registry_order(self):
        self.assertEqual(jk.directplay_providers(SYSTEM_REG),
                         ["Internet TCP/IP Connection For DirectPlay", "IPX Connection For DirectPlay",
                          "Modem Connection For DirectPlay", "Serial Connection For DirectPlay",
                          "Virtual Loopback (jkcapture)"])

    def test_names_and_arguments(self):
        self.assertEqual(jk.check_player_name("Red2"), "Red2")
        for bad in ("", "2Red", "Red Five", "a" * 17):
            with self.subTest(bad), self.assertRaises(jk.CaptureError):
                jk.check_player_name(bad)
        self.assertEqual(jk.jk_arguments(["-windowgui", "-DISPSTATS"]), ["-windowGUI", "-dispStats"])
        with self.assertRaises(jk.CaptureError):
            jk.jk_arguments(["-cd"])
        self.assertEqual(jk.parse_offset("4,-23"), (4, -23))
        with self.assertRaises(jk.CaptureError):
            jk.parse_offset("4;5")


class FrameAndLogTests(TempDir):
    def test_png_and_region_stats(self):
        rgb = bytes([255, 0, 0] * 2 + [0, 0, 0] * 2)
        data = jk.png_bytes(2, 2, rgb)
        self.assertEqual(data[:8], b"\x89PNG\r\n\x1a\n")
        width, height = struct.unpack(">II", data[16:24])
        self.assertEqual((width, height), (2, 2))
        idat = data.index(b"IDAT")
        length = struct.unpack(">I", data[idat - 4:idat])[0]
        self.assertEqual(zlib.decompress(data[idat + 4:idat + 4 + length]), b"\0" + rgb[:6] + b"\0" + rgb[6:])
        stats = jk.region_stats(2, 2, rgb)
        self.assertEqual(stats["nonBlackFraction"], 0.5)
        self.assertEqual(jk.region_stats(2, 2, rgb, (0, 1, 2, 1))["meanLuma"], 0.0)

    def test_changed_fraction(self):
        a = (2, 2, bytes(12))
        b = (2, 2, bytes([200, 0, 0] + [0] * 9))
        self.assertEqual(jk.changed_fraction(a, b), 0.25)
        self.assertEqual(jk.changed_fraction(a, (2, 2, bytes([20] * 12))), 0.0)
        with self.assertRaises(jk.CaptureError):
            jk.changed_fraction(a, (1, 1, bytes(3)))

    def test_log_watch(self):
        log = self.write("wine.log", "ALSA lib x\n0024:err:winediag:wined3d_dll_init Using the OpenGL renderer.\n")
        watch = jk.LogWatch(log)
        self.assertIsNone(watch.poll())
        self.assertEqual(watch.renderer, "OpenGL")
        with log.open("a") as stream:
            stream.write("0024:err:dplay:DP_SecureOpen Unable to open session: DPERR_UNSUPPORTED\n"
                         "wine: Unhandled page fault on write access to 02580000 at address 7AF3E6D3 (thread 0024), "
                         "starting debugger...\n")
        self.assertIn("Unhandled page fault", watch.poll())
        summary = watch.summary()
        self.assertEqual(len(summary["directPlayErrors"]), 1)
        gdi = jk.LogWatch(self.write("gdi.log", "0024:err:winediag:wined3d_dll_init Disabling 3D support.\n"))
        self.assertEqual(gdi.summary()["wined3dRenderer"], "no 3D (GDI)")
        self.assertIsNone(jk.LogWatch(self.tmp / "missing.log").poll())

    def test_gl32_detection(self):
        good = self.write("lib32.so", b"\x7fELF\x01\x01\x01" + b"\0" * 11 + struct.pack("<H", 3) + b"\0" * 10)
        wide = self.write("lib64.so", b"\x7fELF\x02\x01\x01" + b"\0" * 11 + struct.pack("<H", 62) + b"\0" * 10)
        link = self.tmp / "libGL.so.1"
        os.symlink(good, link)
        self.assertTrue(jk.is_elf_i386(link))
        self.assertFalse(jk.is_elf_i386(wide))
        self.assertEqual(jk.find_gl32((str(wide), str(link))), str(link))
        self.assertIsNone(jk.find_gl32((str(wide), str(self.tmp / "none"))))


class FakeScreen:
    """Stands in for Screen: frames come from a list, inputs are recorded."""

    def __init__(self, frames):
        self.frames, self.events = list(frames), []

    def click(self, x, y):
        self.events.append(("click", x, y))

    def key(self, keys, delay_ms=100):
        self.events.append(("key", tuple(keys)))

    def grab(self):
        return self.frames.pop(0)

    def gui_region(self, region, width, height):
        return tuple(region)


class StepRunnerTests(TempDir):
    def menu_frame(self):
        return (640, 480, bytes([90, 40, 20]) * (640 * 480))

    def level_frame(self):
        return (640, 480, bytes([10, 120, 200]) * (640 * 480))

    def test_shots_and_expectations(self):
        screen = FakeScreen([self.menu_frame(), self.level_frame()])
        runner = jk.StepRunner(screen, self.tmp, jk.load_menus())
        runner.run([{"op": "click", "target": "sp.new_game"}, {"op": "shot", "name": "02_single_player"},
                    {"op": "shot", "name": "03_level"}, {"op": "expect", "name": "03_level", "region": [0, 60, 640, 360]},
                    {"op": "expect_changed", "name": "03_level", "from": "02_single_player"}])
        self.assertEqual(screen.events[0], ("click", 295, 110))
        self.assertEqual([s["file"] for s in runner.shots], ["02_single_player.png", "03_level.png"])
        self.assertTrue((self.tmp / "03_level.png").is_file())
        self.assertEqual(runner.results[-1]["changedFraction"], 1.0)

    def test_menu_with_dialog_is_not_a_level(self):
        screen = FakeScreen([self.menu_frame(), self.menu_frame()])
        runner = jk.StepRunner(screen, self.tmp, jk.load_menus())
        with self.assertRaisesRegex(jk.CaptureError, "still on the earlier screen"):
            runner.run([{"op": "shot", "name": "a"}, {"op": "shot", "name": "b"},
                        {"op": "expect_changed", "name": "b", "from": "a"}])
        self.assertEqual(runner.results[-1]["result"], "failed")

    def test_black_level_fails(self):
        screen = FakeScreen([(640, 480, bytes(640 * 480 * 3))])
        runner = jk.StepRunner(screen, self.tmp, jk.load_menus())
        with self.assertRaisesRegex(jk.CaptureError, "is black"):
            runner.run([{"op": "shot", "name": "a"}, {"op": "expect", "name": "a", "region": [0, 60, 640, 360]}])

    def test_lost_click_is_retried_until_the_screen_changes(self):
        menu, level = self.menu_frame(), self.level_frame()
        screen = FakeScreen([menu, menu, level])
        runner = jk.StepRunner(screen, self.tmp, jk.load_menus())
        runner.run([{"op": "click", "target": "player_select.ok", "wait": 0, "retry": 2}])
        self.assertEqual(runner.results[0]["attempts"], 2)
        self.assertEqual(runner.results[0]["changedFractions"], [0.0, 1.0])
        self.assertEqual([e for e in screen.events if e[0] == "click"], [("click", 560, 450)] * 2)

    def test_click_that_never_changes_anything_fails(self):
        menu = self.menu_frame()
        runner = jk.StepRunner(FakeScreen([menu] * 3), self.tmp, jk.load_menus())
        with self.assertRaisesRegex(jk.CaptureError, "changed nothing on screen in 2 attempts"):
            runner.run([{"op": "click", "x": 5, "y": 5, "wait": 0, "retry": 1}])

    def test_await_skips_the_flat_desktop_and_blank_frames(self):
        desktop = (640, 480, bytes([46, 117, 153]) * (640 * 480))
        blank = (640, 480, bytes(640 * 480 * 3))
        gui = (640, 480, bytes(range(0, 240, 1)) * (640 * 480 * 3 // 240))
        runner = jk.StepRunner(FakeScreen([desktop, blank, gui]), self.tmp, jk.load_menus())
        original = jk.time.sleep
        jk.time.sleep = lambda seconds: None
        try:
            runner.run([{"op": "await", "region": [0, 0, 640, 480], "not_flat": True, "timeout": 30}])
        finally:
            jk.time.sleep = original
        self.assertEqual(runner.results[0]["polls"], 3)
        self.assertEqual(jk.dominant_fraction(*desktop), 1.0)
        self.assertLess(jk.dominant_fraction(*gui), 0.1)

    def test_crash_stops_the_run_with_a_frame(self):
        screen = FakeScreen([self.menu_frame()])
        problems = iter([None, "JK.EXE crashed: wine: Unhandled page fault"])
        runner = jk.StepRunner(screen, self.tmp, jk.load_menus(), health=lambda: next(problems, "JK.EXE crashed"))
        with self.assertRaisesRegex(jk.CaptureError, "crashed"):
            runner.run([{"op": "wait", "s": 0}, {"op": "click", "x": 1, "y": 1}, {"op": "wait", "s": 0}])
        self.assertEqual([s["name"] for s in runner.shots], ["zz_crash"])
        self.assertEqual(runner.results[1]["result"], "failed")
        self.assertEqual(len(runner.results), 2)


class FakeDisplay:
    """Stands in for PrivateDisplay; counts starts instead of launching Xvfb."""
    entered = 0

    def __init__(self, *args, **kwargs):
        self.name = ":98"
        self.process = type("Process", (), {"pid": 424242})()

    def __enter__(self):
        FakeDisplay.entered += 1
        return self

    def __exit__(self, *exc):
        return False

    def stop(self):
        pass


def recorder(calls):
    """A subprocess.run stand-in that records the command and its WINEPREFIX."""
    def run(command, **kwargs):
        calls.append([str(c) for c in command] + [f"WINEPREFIX={(kwargs.get('env') or {}).get('WINEPREFIX')}"])
        return subprocess.CompletedProcess(command, 0, "", "")
    return run


class RuntimeSafetyTests(TempDir):
    """Starting, checking and stopping sessions, with subprocess calls recorded (no Wine or X server)."""

    def setUp(self):
        super().setUp()
        FakeDisplay.entered = 0

    def make_prepared_prefix(self, loopback=True, name="prefix"):
        prefix = self.tmp / name
        jkdir = prefix / "drive_c" / "JK"
        (jkdir / "episode").mkdir(parents=True)
        files = {"JK.EXE": b"MZ retail exe", "smackw32.dll": b"MZ stub"}
        if loopback:
            files["jkcap_loopsp.dll"] = b"MZ provider"
        for file_name, data in files.items():
            (jkdir / file_name).write_bytes(data)
        digest = {k: hashlib.sha256(v).hexdigest() for k, v in files.items()}
        receipt = {"schemaVersion": 1, "status": "prepared", "executable": {"sha256": digest["JK.EXE"]},
                   "stub": {"sha256": digest["smackw32.dll"]}, "settings": {"loopbackProvider": loopback},
                   "loopbackProvider": {"sha256": digest["jkcap_loopsp.dll"]} if loopback else None}
        (prefix / jk.PREFIX_RECEIPT).write_text(json.dumps(receipt))
        return prefix, jkdir

    def live_process(self, *marker):
        """A live child whose command line ends with marker (for example 'Xvfb', ':97')."""
        process = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)", *marker])

        def cleanup():
            process.kill()
            process.wait()
        self.addCleanup(cleanup)
        # Popen can return while the child is still inside execve, when /proc/<pid>/cmdline reads empty.
        for _ in range(200):
            if Path(f"/proc/{process.pid}/cmdline").read_bytes():
                break
            jk.time.sleep(0.01)
        return process

    def test_load_prefix_refuses_changed_missing_or_extra_executables(self):
        prefix, jkdir = self.make_prepared_prefix()
        self.assertEqual(jk.load_prefix(prefix)[0], prefix.resolve())
        for name in ("JK.EXE", "smackw32.dll", "jkcap_loopsp.dll"):
            with self.subTest(changed=name):
                original = (jkdir / name).read_bytes()
                (jkdir / name).write_bytes(original + b"!")
                with self.assertRaisesRegex(jk.CaptureError, "changed since prepare"):
                    jk.load_prefix(prefix)
                (jkdir / name).write_bytes(original)
        for extra in ("ddraw.dll", "episode/tool.EXE", "player/Red/helper.drv"):
            with self.subTest(extra=extra):
                path = jkdir / extra
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"MZ")
                with self.assertRaisesRegex(jk.CaptureError, "did not stage"):
                    jk.load_prefix(prefix)
                path.unlink()
        jk.load_prefix(prefix)
        (jkdir / "jkcap_loopsp.dll").unlink()
        with self.assertRaisesRegex(jk.CaptureError, "missing"):
            jk.load_prefix(prefix)
        without, jkdir2 = self.make_prepared_prefix(loopback=False, name="no-loopback")
        jk.load_prefix(without)
        (jkdir2 / "jkcap_loopsp.dll").write_bytes(b"MZ provider")
        with self.assertRaisesRegex(jk.CaptureError, "did not stage"):
            jk.load_prefix(without)
        plain = self.tmp / "plain"
        plain.mkdir()
        with self.assertRaisesRegex(jk.CaptureError, "not prepared"):
            jk.load_prefix(plain)

    def test_lock_owners_are_matched_by_pid_and_command_line(self):
        prefix, _ = self.make_prepared_prefix()
        lock = prefix / jk.LOCK_FILE
        dead = subprocess.Popen([sys.executable, "-c", "pass"])
        dead.wait()
        unrelated = self.live_process()          # alive, but not Xvfb, JK.EXE or jkcapture: a reused PID
        for owner in ({"xvfbPid": dead.pid, "winePid": dead.pid}, {"xvfbPid": unrelated.pid}):
            with self.subTest(stale=owner):
                lock.write_text(json.dumps(owner))
                path = jk.acquire_lock(prefix, {"tool": "jkcapture", "pid": 1234})
                self.assertEqual(json.loads(path.read_text())["pid"], 1234)
                path.unlink()
        xvfb = self.live_process("Xvfb", ":97")
        for owner in (json.dumps({"xvfbPid": xvfb.pid, "display": ":97"}), "not json"):
            with self.subTest(live=owner):
                lock.write_text(owner)
                with self.assertRaisesRegex(jk.CaptureError, "in use by another jkcapture session"):
                    jk.acquire_lock(prefix, {"pid": 1})
                self.assertEqual(lock.read_text(), owner)

    def test_refused_start_never_stops_the_owning_session(self):
        prefix, _ = self.make_prepared_prefix()
        owner = self.live_process("Xvfb", ":97")
        note = json.dumps({"tool": "jkcapture", "xvfbPid": owner.pid, "display": ":97"})
        (prefix / jk.LOCK_FILE).write_text(note)
        out = self.tmp / "out"
        out.mkdir()
        calls, launched = [], []

        def no_launch(command, **kwargs):           # never start a real process from this test
            launched.append(command)
            raise jk.CaptureError("launch attempted")
        session = jk.Session(prefix, out)
        with mock.patch.object(jk, "tool_path", lambda name: name), \
                mock.patch.object(jk.subprocess, "run", recorder(calls)), \
                mock.patch.object(jk.subprocess, "Popen", no_launch), \
                mock.patch.object(jk, "PrivateDisplay", FakeDisplay):
            with self.assertRaisesRegex(jk.CaptureError, "in use by another jkcapture session"):
                session.start()
            session.stop()                      # run_session's finally does this too
        self.assertEqual(launched, [], "a refused start must not launch Wine")
        self.assertEqual(calls, [], "a refused start must not run wineserver -k")
        self.assertEqual(FakeDisplay.entered, 0, "a refused start must not start Xvfb")
        self.assertEqual((prefix / jk.LOCK_FILE).read_text(), note)
        self.assertIsNone(owner.poll())

    def test_the_lock_owner_stops_its_prefix_once(self):
        prefix, _ = self.make_prepared_prefix()
        session = jk.Session(prefix, self.tmp)
        session.lock = jk.acquire_lock(prefix, {"tool": "jkcapture", "pid": os.getpid()})
        calls = []
        with mock.patch.object(jk, "tool_path", lambda name: name), mock.patch.object(jk.subprocess, "run", recorder(calls)):
            session.stop()
            session.stop()
        self.assertEqual(calls, [["wineserver", "-k", f"WINEPREFIX={prefix}"]])
        self.assertFalse((prefix / jk.LOCK_FILE).exists())

    def test_stop_acts_only_on_prepared_prefixes_and_valid_session_files(self):
        plain = self.tmp / "notprefix"
        plain.mkdir()
        good = {"schemaVersion": 1, "prefix": str(plain), "out": str(self.tmp), "display": ":97", "xvfbPid": 999999,
                "winePid": None, "desktopWindow": {"name": jk.DESKTOP_WINDOW, "id": 5}, "guiOffset": [0, 0]}
        calls = []
        with mock.patch.object(jk, "tool_path", lambda name: name), mock.patch.object(jk.subprocess, "run", recorder(calls)):
            with self.assertRaisesRegex(jk.CaptureError, "not prepared"):
                jk.stop_session(self.write("session.json", json.dumps(good)))
            for broken in ({"schemaVersion": 1}, {**good, "xvfbPid": "12"}, {**good, "display": "97"},
                           {**good, "winePid": -4}, [1]):
                with self.subTest(broken=broken), self.assertRaises(jk.CaptureError):
                    jk.stop_session(self.write("broken.json", json.dumps(broken)))
            prefix, _ = self.make_prepared_prefix()
            holder = self.live_process("Xvfb", ":96")
            (prefix / jk.LOCK_FILE).write_text(json.dumps({"xvfbPid": holder.pid, "display": ":96"}))
            with self.assertRaisesRegex(jk.CaptureError, "different live session"):
                jk.stop_session(self.write("stale.json", json.dumps({**good, "prefix": str(prefix)})))
        self.assertEqual(calls, [], "no wineserver -k for a refused stop")
        stderr = io.StringIO()
        with contextlib.redirect_stderr(stderr):
            self.assertEqual(jk.main(["stop", "--session", str(self.tmp / "broken.json")]), 2)
        self.assertEqual(json.loads(stderr.getvalue())["status"], "refused")

    def test_kill_pid_if_leaves_a_process_with_another_command_line(self):
        process = self.live_process("Xvfb", ":95")
        self.assertFalse(jk.kill_pid_if(process.pid, "JK.EXE"))
        self.assertFalse(jk.kill_pid_if(process.pid, "Xvfb :94"))
        self.assertIsNone(process.poll())
        reaper = threading.Thread(target=process.wait, daemon=True)     # reap promptly, as a parent would
        reaper.start()
        self.assertTrue(jk.kill_pid_if(process.pid, "Xvfb :95"))
        reaper.join(10)
        self.assertIsNotNone(process.returncode)

    def prepare_args(self, **extra):
        res = self.tmp / "res"
        for name in ("Res1hi.gob", "Res2.gob"):
            self.write(f"res/{name}", make_gob([("misc\\x.txt", b"x")]))
        self.write("res/JK_.CD", b"\x01\x00\x00\x00")
        exe = self.write("JK.EXE", make_pe(dll=False))
        values = {"prefix": str(self.tmp / "newprefix"), "exe": str(exe),
                  "exe_sha256": hashlib.sha256(exe.read_bytes()).hexdigest(), "resource_dir": str(res),
                  "resource": None, "episode": None, "copy": False, "desktop": "640x480", "wined3d": "gdi",
                  "accel": False, "no_loopback": False, "display": None}
        values.update(extra)
        return argparse.Namespace(**values)

    def test_prepare_checks_every_tool_before_the_prefix_exists(self):
        args = self.prepare_args()
        for missing in ("clang", "lld-link", "Xvfb", "wineboot"):
            with self.subTest(missing=missing), \
                    mock.patch.object(jk.shutil, "which", lambda name, m=missing: None if name == m else "/usr/bin/" + name):
                with self.assertRaisesRegex(jk.CaptureError, f"{missing} is not installed"):
                    jk.prepare(args)
                self.assertFalse(Path(args.prefix).exists())

    def test_failed_prepare_stops_its_wineserver_and_keeps_the_receipt(self):
        args = self.prepare_args()
        calls = []

        def failing(command, environment, timeout, what, log=None):
            raise jk.ToolFailure(f"{what} failed with exit code 1: boom")
        with mock.patch.object(jk, "tool_path", lambda name: name), mock.patch.object(jk, "PrivateDisplay", FakeDisplay), \
                mock.patch.object(jk, "run_checked", failing), mock.patch.object(jk.subprocess, "run", recorder(calls)):
            with self.assertRaisesRegex(jk.ToolFailure, "wineboot -i failed"):
                jk.prepare(args)
        prefix = Path(args.prefix).resolve()
        receipt = json.loads((prefix / jk.PREFIX_RECEIPT).read_text())
        self.assertEqual(receipt["status"], "failed")
        self.assertIn("wineboot -i failed", receipt["error"])
        self.assertEqual(receipt["cleanup"], "wineserver -k")
        self.assertEqual([c for c in calls if c[0] == "wineserver"], [["wineserver", "-k", f"WINEPREFIX={prefix}"]])

    def test_host_drive_letters_are_removed(self):
        devices = self.tmp / "p" / "dosdevices"
        devices.mkdir(parents=True)
        for name, target in (("c:", "../drive_c"), ("z:", "/"), ("d:", "/mnt"), ("d::", "/dev/sr0"), ("com1", "/dev/ttyS0")):
            os.symlink(target, devices / name)
        removed = jk.remove_host_drives(self.tmp / "p")
        self.assertEqual(sorted(p.name for p in devices.iterdir()), ["c:", "com1"])
        self.assertEqual({r["drive"]: r["target"] for r in removed}, {"d:": "/mnt", "d::": "/dev/sr0", "z:": "/"})

    def test_cli_reports_failures_and_refusals_as_json(self):
        for error, status, code in ((jk.ToolFailure("lld-link failed"), "failed", 1),
                                    (jk.CaptureError("bad input"), "refused", 2), (OSError("disk full"), "failed", 1),
                                    (subprocess.TimeoutExpired(["wineboot"], 600), "failed", 1)):
            def boom(out, error=error):
                raise error
            stderr = io.StringIO()
            with self.subTest(status=status, error=type(error).__name__), mock.patch.object(jk, "build_stub", boom), \
                    contextlib.redirect_stderr(stderr):
                self.assertEqual(jk.main(["build-stub", "--out", str(self.tmp)]), code)
            self.assertEqual(json.loads(stderr.getvalue())["status"], status)

    def test_prepare_accepts_no_prebuilt_stub(self):
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            jk.build_parser().parse_args(["prepare", "--prefix", "p", "--exe", "e", "--stub", "x.dll"])


if __name__ == "__main__":
    unittest.main()
