"""Tests for openmots_capture.py. Synthetic files only; no engine or game data is used.

Set SED_TOOLS to a SED checkout's tools/ directory to also validate composed requests
with the capture runner's own validate_request.
"""
import contextlib
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import struct
import sys
import tempfile
import unittest
import zlib

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("openmots_capture", HERE / "openmots_capture.py")
oc = importlib.util.module_from_spec(spec)
spec.loader.exec_module(oc)

CODA_REGISTRY_SHA = "2de86bd073bcf26ab15ae3a6586fe59864b3c23d0fbb3ab025ca417eb5d6453f"
CODA_CVARS_SHA = "398bc22087e50c16a5de670ad32462c76425f40f1440babf96ddbb11068da3fa"


def run(argv):
    out, err = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        code = oc.main(argv)
    text = out.getvalue() or err.getvalue()
    return code, json.loads(text)


class Fixture(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="openmots-capture-")
        self.root = Path(self.temp.name)
        self.engine = self.root / "build" / "openjkdf2"
        self.engine.parent.mkdir()
        self.engine.write_bytes(b"\x7fELF" + b"synthetic engine")
        self.game = self.root / "MotS"
        (self.game / "Resource").mkdir(parents=True)
        for name in ("JKMRES.GOO", "JKMsndLO.goo", "jk_.cd"):
            (self.game / "Resource" / name).write_bytes(b"synthetic " + name.encode())
        self.level = self.root / "levels" / "red_room.jkl"
        self.level.parent.mkdir()
        self.level.write_bytes(b"# synthetic level\n")

    def tearDown(self):
        self.temp.cleanup()

    def bootstrap(self, *extra):
        return run(["bootstrap", "--engine", str(self.engine), "--game-dir", str(self.game),
                    "--output", str(self.root / "boot"), *extra])


class BootstrapTests(Fixture):
    def test_exact_names_case_insensitive_and_original_config(self):
        code, result = self.bootstrap("--engine-commit", "2c6008db")
        self.assertEqual(code, 0, result)
        manifest = json.loads((self.root / "boot" / "bootstrap.json").read_text())
        destinations = [item["destination"] for item in manifest["files"]]
        self.assertEqual(destinations, ["openjkdf2", "resource/JKMRES.GOO", "resource/JKMsndLO.goo",
                                        "resource/jk_.cd", "registry.json", "openjkdf2_cvars.json"])
        roles = {item["destination"]: item["role"] for item in manifest["files"]}
        self.assertEqual(roles["openjkdf2"], "engine")
        self.assertEqual(roles["registry.json"], "config")
        hashes = {item["destination"]: item["sha256"] for item in manifest["files"]}
        self.assertEqual(hashes["registry.json"], CODA_REGISTRY_SHA)
        self.assertEqual(hashes["openjkdf2_cvars.json"], CODA_CVARS_SHA)
        self.assertEqual(manifest["engineCommit"], "2c6008db")
        self.assertEqual(set(manifest["matchesEngine01Baseline"].values()), {False})
        for item in manifest["files"]:
            self.assertTrue(Path(item["source"]).is_absolute())
            self.assertEqual(hashlib.sha256(Path(item["source"]).read_bytes()).hexdigest(), item["sha256"])

    def test_missing_files_are_named_and_nothing_is_written(self):
        (self.game / "Resource" / "JKMsndLO.goo").unlink()
        code, result = self.bootstrap()
        self.assertEqual(code, 2)
        self.assertIn("resource/jkmsndlo.goo", result["error"])
        self.assertFalse((self.root / "boot").exists())

    def test_episodes_are_optional_but_complete_when_asked(self):
        code, result = self.bootstrap("--episodes")
        self.assertEqual(code, 2)
        self.assertIn("episode/jkm.goo", result["error"])
        (self.game / "episode").mkdir()
        for name in ("JKM.GOO", "JKM_KFY.GOO", "JKM_MP.GOO", "JKM_SABER.GOO"):
            (self.game / "episode" / name).write_bytes(name.encode())
        code, result = self.bootstrap("--episodes")
        self.assertEqual(code, 0, result)
        manifest = json.loads((self.root / "boot" / "bootstrap.json").read_text())
        self.assertIn("episode/JKM_SABER.GOO", [item["destination"] for item in manifest["files"]])

    def test_ambiguous_case_variants_are_refused(self):
        probe = self.root / "CaseProbe"
        probe.write_bytes(b"")
        if (self.root / "caseprobe").exists():
            self.skipTest("case-insensitive filesystem")
        (self.game / "Resource" / "JK_.CD").write_bytes(b"second marker")
        code, result = self.bootstrap()
        self.assertEqual(code, 2)
        self.assertIn("ambiguous", result["error"])

    def test_non_linux_engine_and_links_are_refused(self):
        windows = self.root / "openjkdf2-64.exe"
        windows.write_bytes(b"MZ synthetic")
        code, result = run(["bootstrap", "--engine", str(windows), "--game-dir", str(self.game),
                            "--output", str(self.root / "boot")])
        self.assertEqual(code, 2)
        self.assertIn("ELF", result["error"])
        link = self.root / "engine-link"
        link.symlink_to(self.engine)
        code, result = run(["bootstrap", "--engine", str(link), "--game-dir", str(self.game),
                            "--output", str(self.root / "boot")])
        self.assertEqual(code, 2)
        self.assertIn("regular file", result["error"])

    def test_existing_output_is_refused(self):
        (self.root / "boot").mkdir()
        code, result = self.bootstrap()
        self.assertEqual(code, 2)
        self.assertIn("output exists", result["error"])

    def test_check_detects_changed_inputs(self):
        self.assertEqual(self.bootstrap()[0], 0)
        boot = str(self.root / "boot" / "bootstrap.json")
        code, result = run(["check", "--bootstrap", boot])
        self.assertEqual((code, result["status"]), (0, "ok"))
        (self.game / "Resource" / "jk_.cd").write_bytes(b"changed")
        code, result = run(["check", "--bootstrap", boot])
        self.assertEqual((code, result["status"]), (1, "changed"))
        changed = [item["destination"] for item in result["files"] if not item["ok"]]
        self.assertEqual(changed, ["resource/jk_.cd"])


class RequestTests(Fixture):
    def setUp(self):
        super().setUp()
        self.assertEqual(self.bootstrap()[0], 0)
        self.boot = str(self.root / "boot" / "bootstrap.json")
        self.mat = self.root / "levels" / "panel.mat"
        self.mat.write_bytes(b"synthetic material")

    def compose(self, *extra, output="req"):
        return run(["request", "--bootstrap", self.boot, "--level", str(self.level), "--sector", "0",
                    "--position", "0", "1", "0.5", "--pyr", "0", "90", "0", "--size", "320", "240",
                    "--file", f"{self.mat}=mat/panel.mat", "--output", str(self.root / output), *extra])

    def test_request_contains_bootstrap_scenario_and_episode(self):
        code, result = self.compose("--title", 'Red "first" light')
        self.assertEqual(code, 0, result)
        body = json.loads((self.root / "req" / "request.json").read_text())
        self.assertEqual((body["episode"], body["map"]), ("REDTEST", "red_room.jkl"))
        self.assertEqual(body["camera"], {"width": 320, "height": 240, "sector": 0, "position": [0.0, 1.0, 0.5],
                                          "pitchYawRoll": [0.0, 90.0, 0.0], "horizontalFov": 90.0})
        destinations = {item["destination"]: item["role"] for item in body["files"]}
        self.assertEqual(destinations["episode/REDTEST/jkl/red_room.jkl"], "scenario")
        self.assertEqual(destinations["episode/REDTEST/mat/panel.mat"], "scenario")
        self.assertEqual(destinations["resource/jk_.cd"], "resource")
        episode = (self.root / "req" / "episode.jk").read_text()
        self.assertEqual(episode, "\"Red 'first' light\"\nTYPE 1\nSEQ 1\n0: 1 1 LEVEL red_room.jkl 0 0 -1 -1\nEND\n")
        self.assertEqual(body["runnerTimeoutMs"], body["engineTimeoutMs"] + 15000)

    @unittest.skipUnless(os.environ.get("SED_TOOLS"), "set SED_TOOLS to validate with SED's capture runner")
    def test_runner_accepts_the_request(self):
        self.assertEqual(self.compose()[0], 0)
        tools = Path(os.environ["SED_TOOLS"])
        sys.path.insert(0, str(tools))
        try:
            runner_spec = importlib.util.spec_from_file_location("sed_capture_runner", tools / "invoke_engine_capture.py")
            runner = importlib.util.module_from_spec(runner_spec)
            runner_spec.loader.exec_module(runner)
        finally:
            sys.path.remove(str(tools))
        body = json.loads((self.root / "req" / "request.json").read_text())
        validated = runner.validate_request(body)
        self.assertEqual(validated["engineProfile"], "linux")

    def test_bad_names_paths_and_camera_are_refused(self):
        cases = [
            (["--map", "has space.jkl"], "map name"),
            (["--episode", "../up"], "episode name"),
            (["--file", f"{self.mat}=../escape.mat"], "plain relative path"),
            (["--file", f"{self.mat}=panel.mat"], "resource folder"),
            (["--file", f"{self.mat}=mat/panel.mat"], "duplicate destination"),
            (["--fov", "150"], "fov"),
            (["--size", "2048", "2048"], "pixel budget"),
        ]
        for index, (extra, message) in enumerate(cases):
            with self.subTest(extra=extra):
                code, result = self.compose(*extra, output=f"bad-{index}")
                self.assertEqual(code, 2, result)
                self.assertIn(message, result["error"])
                self.assertFalse((self.root / f"bad-{index}").exists())


class ViewTests(unittest.TestCase):
    def test_ppm_to_png_round_trip(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            width, height = 5, 3
            pixels = bytes((x * 40 + c * 7 + y * 3) % 256 for y in range(height) for x in range(width) for c in range(3))
            (root / "capture.ppm").write_bytes(b"P6\n# engine capture\n5 3\n255\n" + pixels)
            code, result = run(["view", str(root / "capture.ppm"), "--output", str(root / "capture.png")])
            self.assertEqual(code, 0, result)
            data = (root / "capture.png").read_bytes()
            self.assertEqual(data[:8], b"\x89PNG\r\n\x1a\n")
            self.assertEqual(struct.unpack(">II", data[16:24]), (width, height))
            index, idat = 8, b""
            while index < len(data):
                length = struct.unpack(">I", data[index:index + 4])[0]
                kind = data[index + 4:index + 8]
                if kind == b"IDAT":
                    idat += data[index + 8:index + 8 + length]
                index += 12 + length
            raw = zlib.decompress(idat)
            stride = width * 3 + 1
            rows = b"".join(raw[r * stride + 1:(r + 1) * stride] for r in range(height))
            self.assertEqual(rows, pixels)
            code, result = run(["view", str(root / "capture.ppm"), "--output", str(root / "capture.png")])
            self.assertEqual(code, 2)

    def test_truncated_ppm_is_refused(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "short.ppm"
            path.write_bytes(b"P6\n4 4\n255\n" + b"\x00" * 10)
            code, result = run(["view", str(path), "--output", str(Path(temp) / "x.png")])
            self.assertEqual(code, 2)
            self.assertIn("wrong length", result["error"])


if __name__ == "__main__":
    unittest.main()
