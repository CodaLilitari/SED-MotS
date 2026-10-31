"""Linux engine-profile tests for the capture runner. Synthetic engine only; no game is launched."""
import copy
import importlib.util
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import time
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / "invoke_engine_capture.py"
sys.path.insert(0, str(SCRIPT.parent))
spec = importlib.util.spec_from_file_location("invoke_engine_capture_linux", SCRIPT)
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)

LINUX = sys.platform.startswith("linux")
XVFB = shutil.which("Xvfb") is not None

# An original stand-in engine: it honours only the capture-v1 request file and
# writes the image and receipt the real engine would, plus what it observed.
FAKE_ENGINE = r'''#!PYTHON
import json, os, sys
lines = open(os.environ["OPENJKDF2_CAPTURE_REQUEST"], encoding="ascii").read().split("\n")
assert lines[0] == "sed-engine-capture-v1"
width, height, sector, frame, timeout = (int(float(v)) for v in lines[1].split())
x, y, z, pitch, yaw, roll, fov = (float(v) for v in lines[2].split())
out = lines[3]
with open(out, "xb") as image:
    image.write(f"P6\n{width} {height}\n255\n".encode("ascii") + bytes([40, 90, 160]) * (width * height))
receipt = {"schemaVersion": 1, "mode": "hidden-rendered-inspection", "format": "ppm-rgb8", "width": width,
           "height": height, "renderedFrame": frame, "sector": sector, "position": [x, y, z],
           "pitchYawRoll": [pitch, yaw, roll], "horizontalFov": fov, "deterministicSimulation": False,
           "resourceHashesVerifiedByEngine": False}
with open(out + ".json", "x", encoding="ascii") as stream:
    json.dump(receipt, stream)
print("fake-engine", os.environ.get("DISPLAY"), os.environ.get("HOME") == os.getcwd(),
      os.environ.get("SDL_VIDEODRIVER"), os.environ.get("ALSOFT_DRIVERS"), sys.argv[1:])
'''


class LinuxProfileTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="sed-capture-linux-")
        self.root = Path(self.temp.name)
        self.sources = self.root / "sources"
        self.sources.mkdir()
        self.output = self.root / "session"
        self.request = {"schemaVersion": 1, "episode": "SEDARCH", "map": "archcheck.jkl",
                        "camera": {"width": 64, "height": 64, "sector": 0, "position": [0, 1, 2],
                                   "pitchYawRoll": [0, 90, 0], "horizontalFov": 90},
                        "frame": 3, "engineTimeoutMs": 5000, "runnerTimeoutMs": 20000,
                        "logLimitBytes": 4096, "files": []}
        engine = FAKE_ENGINE.replace("#!PYTHON", "#!" + sys.executable).encode()
        for destination, role, data in [
            ("openjkdf2", "engine", engine),
            ("resource/jk_.cd", "resource", b"Synthetic marker"),
            ("registry.json", "config", b'{"sfxVolume":0,"musicVolume":0,"cutsceneVolume":0}'),
            ("openjkdf2_cvars.json", "config", b'{"net_disableUpdates":true}'),
            ("episode/SEDARCH/episode.jk", "scenario", b"Original synthetic episode"),
            ("episode/SEDARCH/jkl/archcheck.jkl", "scenario", b"Original synthetic level"),
        ]:
            source = self.sources / destination.replace("/", "_")
            source.write_bytes(data)
            self.request["files"].append({"source": str(source), "destination": destination,
                                          "sha256": runner.sha256(source), "role": role})

    def tearDown(self):
        self.temp.cleanup()

    def test_linux_profile_validates_and_selects_its_executable(self):
        request = runner.validate_request(copy.deepcopy(self.request))
        self.assertEqual(request["engineProfile"], "linux")
        self.assertEqual(runner.command_for(request, self.output)[0], str(self.output / "openjkdf2"))

    def test_mixed_or_incomplete_profiles_are_refused(self):
        mixed = copy.deepcopy(self.request)
        dll = self.sources / "OpenAL32.dll"
        dll.write_bytes(b"Synthetic runtime")
        mixed["files"].append({"source": str(dll), "destination": "OpenAL32.dll",
                               "sha256": runner.sha256(dll), "role": "runtime"})
        with self.assertRaisesRegex(runner.CaptureError, "linux engine profile needs exactly"):
            runner.validate_request(mixed)
        none = copy.deepcopy(self.request)
        none["files"] = [f for f in none["files"] if f["role"] != "engine"]
        with self.assertRaisesRegex(runner.CaptureError, "exactly one engine profile"):
            runner.validate_request(none)

    @unittest.skipUnless(LINUX, "Linux process-group integration")
    def test_process_group_closes_descendant_holding_the_log_pipe(self):
        self.output.mkdir()
        program = "import subprocess,sys; subprocess.Popen([sys.executable,'-c','import time; time.sleep(30)']); print('parent-done')"
        started = time.monotonic()
        result = runner.supervise([sys.executable, "-c", program], self.output, os.environ.copy(), 5000, 1024, {})
        self.assertEqual(result["status"], "completed", result)
        self.assertLess(time.monotonic() - started, 5)
        self.assertEqual((self.output / "stdout.log").read_text().strip(), "parent-done")
        self.assertTrue(result["processContainment"]["assignmentAtomicWithLaunch"])

    @unittest.skipUnless(LINUX and XVFB, "needs Linux and Xvfb")
    def test_private_display_starts_and_stops(self):
        with runner.PrivateDisplay() as display:
            socket = Path(f"/tmp/.X11-unix/X{display.display[1:]}")
            self.assertTrue(socket.exists())
            self.assertFalse(display.receipt()["visibleToUser"])
        self.assertIsNotNone(display.process.poll())

    @unittest.skipUnless(LINUX and XVFB, "needs Linux and Xvfb")
    def test_full_capture_with_synthetic_linux_engine(self):
        path = self.root / "request.json"
        path.write_text(json.dumps(self.request), encoding="utf-8")
        receipt = runner.run_capture(path, self.output)
        self.assertEqual(receipt["status"], "completed", receipt.get("error"))
        self.assertTrue(receipt["inputsUnchanged"])
        self.assertTrue(receipt["validPpm"])
        self.assertTrue(receipt["privateDisplay"]["display"].startswith(":"))
        log = (self.output / "stdout.log").read_text()
        self.assertIn(f"fake-engine {receipt['privateDisplay']['display']} True x11 null", log)
        self.assertIn("'-capture'", log)


if __name__ == "__main__":
    unittest.main()
