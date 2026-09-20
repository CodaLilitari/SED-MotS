"""Original runner contract tests. Synthetic children only; no game is launched."""
import copy
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import struct
import sys
import tempfile
import time
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / "invoke_engine_capture.py"
sys.path.insert(0, str(SCRIPT.parent))
spec = importlib.util.spec_from_file_location("invoke_engine_capture", SCRIPT)
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)


class SyntheticJob:
    def __init__(self, process):
        self.process = process

    def kill(self):
        if self.process.poll() is None:
            self.process.kill()

    def close(self):
        self.kill()


class CaptureRunnerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="sed-capture-tests-")
        self.root = Path(self.temp.name)
        self.sources = self.root / "sources"
        self.sources.mkdir()
        self.output = self.root / "session"
        self.request_path = self.root / "request.json"
        self.request = {"schemaVersion": 1, "episode": "SEDARCH", "map": "archcheck.jkl",
                        "camera": {"width": 64, "height": 64, "sector": 0,
                                   "position": [0, 1, 2], "pitchYawRoll": [0, 90, 0], "horizontalFov": 90},
                        "frame": 3, "engineTimeoutMs": 1000, "runnerTimeoutMs": 2000,
                        "logLimitBytes": 1024, "files": []}
        for destination, role, data in [
            ("openjkdf2-64.exe", "engine", b"Original synthetic binary identity, never executed."),
            ("OpenAL32.dll", "runtime", b"Synthetic runtime one"),
            ("libGameNetworkingSockets.dll", "runtime", b"Synthetic runtime two"),
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

    def save_request(self):
        self.request_path.write_text(json.dumps(self.request), encoding="utf-8")

    def run_request(self, executor=None):
        self.save_request()
        return runner.run_capture(self.request_path, self.output,
                                  executor=executor or self.synthetic_capture)

    def synthetic_capture(self, command, output, environment, timeout_ms, log_limit, staged, **kwargs):
        self.assertEqual(command[1:], ["-capture", "-autostart", "-sp", "-motsCompat", "-noHUD",
                                     "-episode", "SEDARCH", "-map", "archcheck.jkl"])
        self.assertEqual(Path(environment["OPENJKDF2_CAPTURE_REQUEST"]), output / "capture-request.txt")
        self.assertNotIn("SDL_VIDEODRIVER", environment)
        self.assertTrue((output / ".openjkdf2-capture-session").is_file())
        self.assertIn("episode/SEDARCH/jkl/archcheck.jkl", staged)
        c = self.request["camera"]
        (output / "capture.ppm").write_bytes(f"P6\n{c['width']} {c['height']}\n255\n".encode() + b"\x19\x34\x56" * c["width"] * c["height"])
        engine = {"schemaVersion": 1, "mode": "hidden-rendered-inspection", "format": "ppm-rgb8",
                  "width": c["width"], "height": c["height"], "renderedFrame": self.request["frame"],
                  "sector": c["sector"], "position": c["position"], "pitchYawRoll": c["pitchYawRoll"],
                  "horizontalFov": c["horizontalFov"], "deterministicSimulation": False,
                  "resourceHashesVerifiedByEngine": False}
        (output / "capture.ppm.json").write_text(json.dumps(engine), encoding="utf-8")
        return {"status": "completed", "processExitCode": 0}

    def test_complete_staging_receipt_preserves_identity_without_runtime_claims(self):
        prior = {item["source"]: runner.sha256(item["source"]) for item in self.request["files"]}
        result = self.run_request()
        self.assertEqual(result["status"], "completed")
        self.assertTrue(result["inputsUnchanged"])
        self.assertFalse(result["simulationDeterministic"])
        self.assertFalse(result["effectiveCameraObserved"])
        self.assertFalse(result["runtimeResourceResolutionObserved"])
        self.assertEqual(result["lightingMode"]["requested"], "native-default")
        self.assertIsNone(result["lightingMode"]["observed"])
        self.assertEqual(result["requestedCamera"]["sector"], result["engineReportedReceipt"]["sector"])
        after = json.loads((self.output / "manifest-after.json").read_text())
        self.assertEqual(len(after), len(prior))
        for record in after:
            self.assertTrue(record["sourceUnchanged"])
            self.assertTrue(record["stagedUnchanged"])
            self.assertEqual(runner.sha256(record["source"]), prior[record["source"]])

    def test_arbitrary_command_options_refused_with_failure_receipt(self):
        for key in ("command", "arguments", "executable", "environment"):
            with self.subTest(key=key):
                self.output = self.root / ("refusal-" + key)
                self.request[key] = ["-path", "somewhere"]
                result = self.run_request(executor=lambda *args: self.fail("Refused request launched."))
                self.assertEqual(result["status"], "failed")
                self.assertIn("Unsupported request fields", result["error"])
                self.assertTrue((self.output / "launcher-receipt.json").is_file())
                del self.request[key]

    def test_invalid_numeric_or_path_contract_refused(self):
        changes = [("camera", "width", True), ("camera", "sector", -1), ("camera", "horizontalFov", float("nan")),
                   ("camera", "position", [0, 0, float("inf")]), (None, "episode", "../../outside"),
                   (None, "map", "x.jkl -path elsewhere"), (None, "frame", 121), (None, "logLimitBytes", 0)]
        for parent, key, value in changes:
            with self.subTest(key=key):
                request = copy.deepcopy(self.request)
                (request[parent] if parent else request)[key] = value
                with self.assertRaises(runner.CaptureError):
                    runner.validate_request(request)

    def test_duplicate_json_key_refused(self):
        self.request_path.write_text('{"schemaVersion":1,"schemaVersion":1}', encoding="utf-8")
        result = runner.run_capture(self.request_path, self.output, executor=lambda *args: self.fail("Launched."))
        self.assertEqual(result["status"], "failed")
        self.assertIn("Duplicate JSON key", result["error"])

    def test_existing_output_never_overwritten(self):
        self.save_request()
        self.output.mkdir()
        sentinel = self.output / "keep.txt"
        sentinel.write_text("preserved")
        with self.assertRaisesRegex(runner.CaptureError, "new directory"):
            runner.run_capture(self.request_path, self.output)
        self.assertEqual(sentinel.read_text(), "preserved")
        self.assertEqual(list(self.output.iterdir()), [sentinel])

    def test_output_inside_input_tree_refused_before_creation(self):
        self.save_request()
        target = self.sources / "unsafe"
        with self.assertRaisesRegex(runner.CaptureError, "input file directory"):
            runner.run_capture(self.request_path, target)
        self.assertFalse(target.exists())

    def test_case_collision_traversal_and_reserved_destinations_refused(self):
        for destination in ("OPENJKDF2-64.EXE", "../outside", "/outside", "episode\\SEDARCH\\a", "capture.ppm",
                            "episode/SEDARCH/NUL.txt", "episode/SEDARCH/file."):
            with self.subTest(destination=destination):
                request = copy.deepcopy(self.request)
                request["files"].append(dict(request["files"][-1], destination=destination))
                with self.assertRaises(runner.CaptureError):
                    runner.validate_request(request)

    def test_hash_mismatch_never_launches_and_has_after_manifest(self):
        self.request["files"][-1]["sha256"] = "0" * 64
        result = self.run_request(executor=lambda *args: self.fail("Wrong identity launched."))
        self.assertEqual(result["status"], "failed")
        self.assertFalse(result["engineStarted"])
        self.assertEqual(result["failureStage"], "staging")
        self.assertIn("Source SHA256 mismatch", result["error"])
        after = json.loads((self.output / "manifest-after.json").read_text())
        self.assertFalse(after[-1]["sourceBeforeMatchesDeclared"])
        self.assertTrue(after[-1]["sourceUnchanged"])
        self.assertTrue((self.output / "manifest-after.json").is_file())
        self.assertTrue((self.output / "launcher-receipt.json").is_file())

    def add_archive(self, entries, raw=None):
        source = self.sources / "bootstrap.goo"
        if raw is None:
            offset = 16 + 136 * len(entries)
            raw = b"GOB " + struct.pack("<III", 0x14, 12, len(entries))
            raw += b"".join(struct.pack("<II", offset, 0) + name.encode("ascii").ljust(128, b"\0") for name in entries)
        source.write_bytes(raw)
        self.request["files"].append({"source": str(source), "destination": "resource/bootstrap.goo",
                                      "sha256": runner.sha256(source), "role": "resource"})

    def test_bootstrap_archive_custom_path_collision_is_refused(self):
        self.add_archive(["JKL\\ARCHCHECK.JKL"])
        result = self.run_request(executor=lambda *args, **kwargs: self.fail("Colliding resource launched."))
        self.assertFalse(result["engineStarted"])
        self.assertEqual(result["failureStage"], "resource-closure")
        self.assertIn("episode/SEDARCH/jkl/archcheck.jkl", result["resourceClosure"]["duplicateCustomResources"])
        custom = next(x for x in result["resourceClosure"]["customResourceCandidates"] if x["logicalPath"] == "jkl/archcheck.jkl")
        self.assertEqual(len(custom["candidates"]), 2)

    def test_bootstrap_archive_custom_basename_alias_is_refused(self):
        self.add_archive(["different/path/ARCHCHECK.JKL"])
        result = self.run_request(executor=lambda *args, **kwargs: self.fail("Aliased resource launched."))
        self.assertEqual(result["status"], "failed")
        self.assertFalse(result["engineStarted"])
        self.assertFalse(result["resourceClosure"]["valid"])

    def test_other_staged_loose_alias_is_refused(self):
        source = self.sources / "alias.jkl"
        source.write_bytes(b"Original synthetic unrelated content")
        self.request["files"].append({"source": str(source), "destination": "resource/other/ARCHCHECK.JKL",
                                      "sha256": runner.sha256(source), "role": "resource"})
        result = self.run_request(executor=lambda *args, **kwargs: self.fail("Loose alias launched."))
        self.assertFalse(result["engineStarted"])
        self.assertEqual(result["failureStage"], "resource-closure")

    def test_unsupported_archive_is_refused_not_ignored(self):
        self.add_archive([], b"An unsupported archive container")
        result = self.run_request(executor=lambda *args, **kwargs: self.fail("Opaque archive launched."))
        self.assertFalse(result["engineStarted"])
        self.assertEqual(result["failureStage"], "resource-closure")
        self.assertIn("Unsupported GOB", result["resourceClosure"]["unsupportedArchives"][0]["error"])

    def test_disjoint_archive_records_index_only_and_passes(self):
        self.add_archive(["3do/synthetic_other.3do", "jkl/other.jkl"])
        result = self.run_request()
        self.assertEqual(result["status"], "completed", result)
        self.assertTrue(result["resourceClosure"]["valid"])
        self.assertFalse(result["resourceClosure"]["archivePayloadsRead"])
        self.assertEqual(result["resourceClosure"]["archives"][0]["entryCount"], 2)
        self.assertTrue(all(item["exactlyOneIntendedCandidate"] for item in result["resourceClosure"]["customResourceCandidates"]))

    def test_content_changed_at_final_staging_seam_never_launches(self):
        self.save_request()
        def mutate(output):
            path = output / "episode/SEDARCH/jkl/archcheck.jkl"
            path.write_bytes(b"Substituted after ordinary staging")
        result = runner.run_capture(self.request_path, self.output,
                                    executor=lambda *args, **kwargs: self.fail("Changed final stage launched."),
                                    _before_final_stage=mutate)
        self.assertFalse(result["engineStarted"])
        self.assertEqual(result["failureStage"], "final-staging-identity")
        self.assertFalse(result["inputsUnchanged"])
        self.assertFalse(result["finalStagingIdentity"][-1]["matchesDeclared"])
        self.assertTrue((self.output / "manifest-prelaunch.json").exists())

    def test_unsafe_config_never_launches(self):
        item = next(x for x in self.request["files"] if x["destination"] == "registry.json")
        Path(item["source"]).write_text('{"sfxVolume":0,"musicVolume":0,"cutsceneVolume":0,"InstallPath":"outside"}')
        item["sha256"] = runner.sha256(item["source"])
        result = self.run_request(executor=lambda *args: self.fail("Unsafe config launched."))
        self.assertEqual(result["status"], "failed")
        self.assertIn("Unsupported registry fields", result["error"])

    def test_execution_exception_still_has_failure_receipt_and_source_evidence(self):
        def fail(*args, **kwargs):
            raise OSError("Synthetic launch refusal")
        result = self.run_request(executor=fail)
        self.assertEqual(result["status"], "failed")
        self.assertIn("Synthetic launch refusal", result["error"])
        self.assertTrue(result["inputsUnchanged"])
        self.assertTrue((self.output / "manifest-before.json").is_file())

    def test_success_exit_without_image_fails(self):
        result = self.run_request(executor=lambda *args, **kwargs: {"status": "completed", "processExitCode": 0})
        self.assertEqual(result["status"], "failed")
        self.assertIn("image is missing", result["error"])

    def test_changed_immutable_input_after_capture_fails(self):
        def mutate(*args, **kwargs):
            result = self.synthetic_capture(*args, **kwargs)
            (self.output / "episode/SEDARCH/jkl/archcheck.jkl").write_bytes(b"Changed fixture")
            return result
        result = self.run_request(executor=mutate)
        self.assertEqual(result["status"], "failed")
        self.assertFalse(result["inputsUnchanged"])

    def test_engine_configuration_rewrites_are_recorded_not_source_changes(self):
        def mutate(*args, **kwargs):
            result = self.synthetic_capture(*args, **kwargs)
            (self.output / "registry.json").write_text('{"engineWroteDefaults":true}')
            return result
        result = self.run_request(executor=mutate)
        self.assertEqual(result["status"], "completed")
        record = next(x for x in json.loads((self.output / "manifest-after.json").read_text()) if x["destination"] == "registry.json")
        self.assertTrue(record["sourceUnchanged"])
        self.assertFalse(record["stagedUnchanged"])
        self.assertTrue(record["stagedMutationAllowed"])

    def test_engine_receipt_camera_mismatch_is_not_accepted(self):
        def mismatch(*args, **kwargs):
            result = self.synthetic_capture(*args, **kwargs)
            path = self.output / "capture.ppm.json"
            value = json.loads(path.read_text())
            value["sector"] = 1
            path.write_text(json.dumps(value))
            return result
        result = self.run_request(executor=mismatch)
        self.assertEqual(result["status"], "failed")
        self.assertIn("requested sector", result["error"])

    @staticmethod
    def world_state():
        return {"engineWorldStateAvailable": True, "isMultiplayer": False, "motsCompat": True,
                "world": "archcheck.jkl", "localPlayerModel": "original-player.3do",
                "localPlayerSoundClass": "original.snd", "hostRequestedModel": None,
                "localPlayerHierarchyNodes": 1}

    def receipt_fixture(self):
        result = self.run_request()
        self.assertEqual(result["status"], "completed")
        return copy.deepcopy(result["engineReportedReceipt"])

    def validate_receipt(self, value):
        (self.output / "capture.ppm.json").write_text(json.dumps(value), encoding="utf-8")
        return runner.validate_capture(self.output, self.request)

    def test_legacy_engine_receipt_remains_accepted_without_world_observation(self):
        value = self.receipt_fixture()
        self.assertNotIn("engineWorldStateAvailable", value)
        self.assertEqual(self.validate_receipt(value)["engineReportedReceipt"], value)

    def test_complete_current_engine_world_receipt_is_preserved(self):
        def observed(*args, **kwargs):
            result = self.synthetic_capture(*args, **kwargs)
            path = self.output / "capture.ppm.json"
            value = json.loads(path.read_text())
            value.update(self.world_state())
            path.write_text(json.dumps(value), encoding="utf-8")
            return result
        result = self.run_request(executor=observed)
        self.assertEqual(result["status"], "completed", result.get("error"))
        self.assertTrue(result["engineReportedReceipt"]["engineWorldStateAvailable"])
        self.assertEqual(result["engineReportedReceipt"]["localPlayerModel"], "original-player.3do")
        self.assertFalse(result["runtimeResourceResolutionObserved"])
        self.assertFalse(result["effectiveCameraObserved"])
        value = dict(result["engineReportedReceipt"], world="ARCHCHECK.JKL", localPlayerModel=None,
                     localPlayerSoundClass=None, localPlayerHierarchyNodes=0)
        self.assertTrue(self.validate_receipt(value)["validPpm"])

    def test_partial_world_receipt_extension_refused_for_each_missing_field(self):
        base = self.receipt_fixture()
        complete = dict(base, **self.world_state())
        for key in self.world_state():
            with self.subTest(missing=key):
                value = dict(complete)
                del value[key]
                with self.assertRaisesRegex(runner.CaptureError, "missing required fields"):
                    self.validate_receipt(value)
        with self.assertRaisesRegex(runner.CaptureError, "missing required fields"):
            self.validate_receipt(dict(base, engineWorldStateAvailable=False))

    def test_malformed_or_mismatched_world_state_refused(self):
        complete = dict(self.receipt_fixture(), **self.world_state())
        changes = [("engineWorldStateAvailable", False), ("engineWorldStateAvailable", 1),
                   ("isMultiplayer", True), ("isMultiplayer", 0), ("motsCompat", False), ("motsCompat", 1),
                   ("world", "another.jkl"), ("world", None), ("localPlayerModel", 42),
                   ("localPlayerModel", ""), ("localPlayerModel", "a"*33),
                   ("localPlayerSoundClass", "line\nbreak.snd"), ("localPlayerSoundClass", []),
                   ("hostRequestedModel", "host.3do"), ("localPlayerHierarchyNodes", True),
                   ("localPlayerHierarchyNodes", 1.0), ("localPlayerHierarchyNodes", -1),
                   ("localPlayerHierarchyNodes", 4097), ("localPlayerModel", None)]
        for key, value in changes:
            with self.subTest(field=key, value=value):
                with self.assertRaises(runner.CaptureError):
                    self.validate_receipt(dict(complete, **{key: value}))

    def test_unknown_engine_receipt_fields_refused_with_or_without_extension(self):
        base = self.receipt_fixture()
        for value in (base, dict(base, **self.world_state())):
            with self.subTest(extended="world" in value):
                with self.assertRaisesRegex(runner.CaptureError, "Unsupported engine receipt fields"):
                    self.validate_receipt(dict(value, unexpectedObservation=True))

    def supervise_synthetic(self, program, timeout=2000, observer=lambda _: (0, 0, 0)):
        self.output.mkdir()
        return runner.supervise([sys.executable, "-c", program], self.output, os.environ.copy(), timeout, 1024, {},
                                observer=observer, job_factory=SyntheticJob)

    def test_synthetic_child_drains_both_streams(self):
        result = self.supervise_synthetic("import os; os.write(1,b'hello'); os.write(2,b'diagnostic')")
        self.assertEqual(result["status"], "completed")
        self.assertEqual((self.output / "stdout.log").read_bytes(), b"hello")
        self.assertEqual((self.output / "stderr.log").read_bytes(), b"diagnostic")

    @unittest.skipUnless(os.name == "nt", "Windows background-process integration")
    def test_windows_job_and_owned_window_observer_with_synthetic_child(self):
        self.output.mkdir()
        started = []
        program = "import os,time; os.write(1,b'windows-job-tested'); time.sleep(0.15)"
        result = runner.supervise([sys.executable, "-c", program], self.output, os.environ.copy(), 2000, 1024, {},
                                  started_callback=started.append)
        self.assertEqual(result["status"], "completed", result)
        self.assertTrue(result["engineStarted"])
        self.assertEqual(started, [result["processId"]])
        self.assertEqual(result["windowObservation"]["maximumVisibleOwnedWindows"], 0)
        self.assertEqual(result["windowObservation"]["maximumForegroundOwnedWindows"], 0)
        self.assertEqual((self.output / "stdout.log").read_bytes(), b"windows-job-tested")

    @unittest.skipUnless(os.name == "nt", "Windows owned-process-tree integration")
    def test_windows_job_closes_descendant_inherited_log_pipe(self):
        self.output.mkdir()
        program = "import subprocess,sys; subprocess.Popen([sys.executable,'-c','import time; time.sleep(30)']); print('parent-done')"
        started = time.monotonic()
        result = runner.supervise([sys.executable, "-c", program], self.output, os.environ.copy(), 2000, 1024, {})
        self.assertEqual(result["status"], "completed", result)
        self.assertLess(time.monotonic() - started, 5)
        self.assertEqual((self.output / "stdout.log").read_text().strip(), "parent-done")

    def test_synthetic_child_log_overflow_is_capped_and_fails(self):
        result = self.supervise_synthetic("import os; [os.write(1,b'x'*4096) for _ in range(500)]")
        self.assertEqual(result["status"], "failed")
        self.assertLessEqual((self.output / "stdout.log").stat().st_size, 1024)
        self.assertTrue(result["stdout"]["truncated"])

    def test_synthetic_child_timeout_is_bounded(self):
        started = time.monotonic()
        result = self.supervise_synthetic("import time; time.sleep(30)", timeout=150)
        self.assertEqual(result["status"], "failed")
        self.assertTrue(result["timedOut"])
        self.assertLess(time.monotonic() - started, 5)

    def test_observed_visible_owned_window_terminates_child(self):
        result = self.supervise_synthetic("import time; time.sleep(30)", observer=lambda _: (1, 1, 0))
        self.assertEqual(result["status"], "failed")
        self.assertIn("visible or foreground", result["error"])
        self.assertIsNotNone(result["processExitCode"])

    def test_generated_output_budget_is_enforced(self):
        self.output.mkdir()
        path = self.output / "oversized-output"
        with path.open("wb") as stream:
            stream.truncate(runner.MAX_GENERATED_BYTES + 1)
        with self.assertRaisesRegex(runner.CaptureError, "64MiB"):
            runner.check_generated_budget(self.output, {})


if __name__ == "__main__":
    unittest.main()
