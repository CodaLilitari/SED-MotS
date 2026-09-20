from pathlib import Path
import hashlib
import json
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).parents[1]))
import test_engine_lighting as workflow


class WorkflowFailureReceiptTests(unittest.TestCase):
    @staticmethod
    def inputs(root):
        fixture_path, inputs_path = root / "fixture.json", root / "inputs.json"
        names = ("baked", "reset", "unbound", "rgb-swapped", "white-only-change", "extensions-omitted", "metadata-interleaved")
        fixture_path.write_text(json.dumps({"status": "prepared", "engineLaunched": False,
            "agentVersion": {}, "episode": "SEDARCH", "map": "archcheck.jkl",
            "cases": [{"name": name, "files": []} for name in names],
            "camera": {"width": 640, "height": 480, "sector": 0, "position": [0, 0, 0],
                       "pitchYawRoll": [0, 0, 0], "horizontalFov": 80}}), encoding="utf-8")
        inputs_path.write_text('{"files":[]}', encoding="utf-8")
        return fixture_path, inputs_path

    def test_missing_final_input_does_not_erase_primary_failure_receipt(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            fixture_path, inputs_path, output = root/"fixture.json", root/"inputs.json", root/"run"
            names = ("baked", "reset", "unbound", "rgb-swapped", "white-only-change", "extensions-omitted", "metadata-interleaved")
            fixture_path.write_text(json.dumps({"status": "prepared", "engineLaunched": False,
                "agentVersion": {}, "episode": "SEDARCH", "map": "archcheck.jkl",
                "cases": [{"name": name, "files": []} for name in names],
                "camera": {"width": 640, "height": 480, "sector": 0, "position": [0,0,0],
                           "pitchYawRoll": [0,0,0], "horizontalFov": 80}}))
            inputs_path.write_text('{"files":[]}')
            def fail_capture(*args):
                inputs_path.unlink()
                raise RuntimeError("primary synthetic capture failure")
            with patch.object(workflow, "run_capture", fail_capture):
                result = workflow.run(fixture_path, inputs_path, output, only_case="baked")
            stored = json.loads((output/"acceptance.json").read_text())
            self.assertEqual(result["status"], "failed")
            self.assertEqual(stored["error"], "primary synthetic capture failure")
            self.assertFalse(stored["preparationAndBootstrapReceiptsUnchanged"])
            self.assertIn("finalIdentityError", stored)


    def test_receipt_hashes_the_bytes_parsed_even_if_manifest_replaced_during_parse(self):
        for target_name, identity_key in (("fixture.json", "fixtureReceiptSha256"),
                                          ("inputs.json", "bootstrapManifestSha256")):
            with self.subTest(target=target_name), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                fixture_path, inputs_path = self.inputs(root)
                target = root / target_name
                original = target.read_bytes()
                original_loads = json.loads
                replacement = dict(original_loads(original), changedAfterRead=True)
                replaced = False

                def replace_after_parse(text, *args, **kwargs):
                    nonlocal replaced
                    result = original_loads(text, *args, **kwargs)
                    if text == original.decode("utf-8") and not replaced:
                        target.write_text(json.dumps(replacement), encoding="utf-8")
                        replaced = True
                    return result

                def complete_capture(request_path, destination):
                    destination.mkdir()
                    receipt = {"status": "completed"}
                    workflow.write_json(destination / "launcher-receipt.json", receipt)
                    return receipt

                with patch.object(workflow.json, "loads", side_effect=replace_after_parse), \
                        patch.object(workflow, "run_capture", side_effect=complete_capture):
                    result = workflow.run(fixture_path, inputs_path, root / "run", only_case="baked")
                self.assertTrue(replaced)
                self.assertEqual(result[identity_key], hashlib.sha256(original).hexdigest())
                self.assertNotEqual(result[identity_key], workflow.sha256(target))
                self.assertFalse(result["preparationAndBootstrapReceiptsUnchanged"])
                self.assertEqual(result["status"], "failed")
                stored = json.loads((root / "run/acceptance.json").read_text())
                self.assertEqual(stored[identity_key], hashlib.sha256(original).hexdigest())
                self.assertEqual(stored["status"], "failed")


class ExistingOutputControlTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.destination = self.root / "baked-1"
        self.destination.mkdir()
        (self.destination / "capture.ppm").write_bytes(b"original capture")
        (self.destination / "launcher-receipt.json").write_bytes(b'{"status":"completed"}')
        (self.destination / "empty").mkdir()
        (self.destination / "resources").mkdir()
        (self.destination / "resources/model.3do").write_bytes(b"original model")
        self.request = self.root / "request.json"

    def test_expected_refusal_proves_all_files_and_empty_directories_unchanged(self):
        with patch.object(workflow, "run_capture", side_effect=workflow.CaptureError("refused")):
            result = workflow.check_existing_output_refusal(self.request, self.destination)
        self.assertTrue(result["passed"])
        self.assertEqual(result["before"], result["after"])
        self.assertEqual({item["path"] for item in result["before"]},
                         {".", "capture.ppm", "launcher-receipt.json", "empty", "resources", "resources/model.3do"})

    def test_refusal_after_image_mutation_fails_even_if_launcher_receipt_unchanged(self):
        prior_receipt_hash = workflow.sha256(self.destination / "launcher-receipt.json")

        def destructive_refusal(*args):
            (self.destination / "capture.ppm").write_bytes(b"corrupted capture")
            raise workflow.CaptureError("refused after mutation")

        with patch.object(workflow, "run_capture", side_effect=destructive_refusal):
            result = workflow.check_existing_output_refusal(self.request, self.destination)
        self.assertEqual(workflow.sha256(self.destination / "launcher-receipt.json"), prior_receipt_hash)
        self.assertTrue(result["refused"])
        self.assertFalse(result["passed"])

    def test_added_file_or_removed_empty_directory_cannot_hide_in_hashes(self):
        for mutate in (lambda: (self.destination / "unexpected.txt").write_bytes(b"new"),
                       lambda: (self.destination / "empty").rmdir()):
            with self.subTest(mutate=mutate):
                def refuse(*args):
                    mutate()
                    raise workflow.CaptureError("refused")
                with patch.object(workflow, "run_capture", side_effect=refuse):
                    self.assertFalse(workflow.check_existing_output_refusal(self.request, self.destination)["passed"])

    def test_success_without_refusal_does_not_pass_unchanged_output_control(self):
        with patch.object(workflow, "run_capture", return_value={"status": "completed"}):
            result = workflow.check_existing_output_refusal(self.request, self.destination)
        self.assertFalse(result["refused"])
        self.assertFalse(result["passed"])

    def test_reparse_point_is_rejected_without_following_or_launching(self):
        with patch.object(workflow, "is_link", side_effect=lambda path: Path(path).name == "resources"), \
                patch.object(workflow, "run_capture") as capture:
            with self.assertRaisesRegex(workflow.CaptureError, "reparse"):
                workflow.check_existing_output_refusal(self.request, self.destination)
            capture.assert_not_called()


class ModelMismatchControlTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.destination = self.root / "refusal"
        self.destination.mkdir()
        source = self.root / "archpanel.3do"
        source.write_bytes(b"substituted model")
        self.model = {"source": str(source), "destination": "episode/SEDARCH/3do/archpanel.3do",
                      "role": "scenario", "sha256": hashlib.sha256(b"original model").hexdigest()}
        self.manifest = [{**self.model, "sourceBeforeSha256": workflow.sha256(source),
                          "sourceBeforeMatchesDeclared": False, "stagedBeforeSha256": None}]
        self.receipt = {"status": "failed", "engineStarted": False, "failureStage": "staging",
                        "error": "The control does not match on this wording."}
        self.write_manifest()

    def write_manifest(self):
        (self.destination / "manifest-before.json").write_text(json.dumps(self.manifest), encoding="utf-8")

    def check(self):
        return workflow.check_model_mismatch_refusal(self.receipt, self.destination, self.model)

    def test_targeted_observed_source_hash_mismatch_passes(self):
        self.assertTrue(self.check())

    def test_unrelated_prelaunch_failure_does_not_pass(self):
        for stage in ("preflight", "resource-closure", "final-staging-identity"):
            with self.subTest(stage=stage):
                self.receipt["failureStage"] = stage
                self.assertFalse(self.check())
        self.receipt["failureStage"] = "staging"
        self.manifest = []
        self.write_manifest()
        self.assertFalse(self.check())

    def test_declared_match_wrong_observed_hash_or_staged_target_refuses(self):
        for key, value in (("sourceBeforeMatchesDeclared", True),
                           ("sourceBeforeSha256", "0" * 64),
                           ("sha256", "1" * 64),
                           ("stagedBeforeSha256", self.model["sha256"]),
                           ("destination", "episode/SEDARCH/3do/other.3do")):
            with self.subTest(key=key):
                old = self.manifest[0][key]
                self.manifest[0][key] = value
                self.write_manifest()
                self.assertFalse(self.check())
                self.manifest[0][key] = old
        self.write_manifest()
        self.assertTrue(self.check())

    def test_engine_start_or_image_or_duplicate_record_refuses(self):
        self.receipt["engineStarted"] = True
        self.assertFalse(self.check())
        self.receipt["engineStarted"] = False
        (self.destination / "capture.ppm").write_bytes(b"unexpected output")
        self.assertFalse(self.check())
        (self.destination / "capture.ppm").unlink()
        self.manifest.append(dict(self.manifest[0]))
        self.write_manifest()
        self.assertFalse(self.check())


if __name__ == "__main__":
    unittest.main()
