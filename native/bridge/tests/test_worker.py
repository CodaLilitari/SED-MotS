"""Black-box contract tests: real worker, original fixtures, no source writes."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
import unittest

PARSER = argparse.ArgumentParser()
PARSER.add_argument("--binary", type=Path, required=True)
PARSER.add_argument("--upstream", type=Path, required=True)
PARSER.add_argument("--scratch", type=Path, required=True)
ARGS, REMAINING = PARSER.parse_known_args()
REQUEST_ID = "0123456789abcdef0123456789abcdef"
COMMIT = "b82255998fb733c8e8c87104eb7c17f27b48376f"
VECTORS = ARGS.upstream / "src/core/tests/assets/fixtures/sed_vectors"
LOCK = Path(__file__).resolve().parents[1] / "upstream.lock.json"


class WorkerContractTests(unittest.TestCase):
    def run_worker(self, arguments, payload=b"", expected=0):
        ARGS.scratch.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="worker-", dir=ARGS.scratch) as directory:
            # No input path is supplied. A sentinel also catches accidental cwd writes.
            sentinel = Path(directory) / "keep.bin"
            sentinel.write_bytes(b"original synthetic sentinel\x00")
            before = {p.name: p.read_bytes() for p in Path(directory).iterdir()}
            result = subprocess.run(
                [str(ARGS.binary.resolve()), *arguments], input=payload,
                cwd=directory, capture_output=True, timeout=30)
            after = {p.name: p.read_bytes() for p in Path(directory).iterdir()}
            self.assertEqual(before, after, "read-only worker changed its working directory")
        self.assertEqual(result.returncode, expected, result.stderr.decode(errors="replace"))
        self.assertLessEqual(len(result.stdout), 65536)
        self.assertTrue(result.stdout.endswith(b"\n"), result.stdout)
        self.assertLessEqual(len(result.stderr), 4096)
        reply = json.loads(result.stdout.decode("utf-8"))
        self.assertEqual(reply["schemaVersion"], 1)
        self.assertEqual(reply["name"], "sed-cpp-bridge")
        self.assertEqual(reply["version"], "0.1.0")
        self.assertEqual(reply["upstreamCommit"], COMMIT)
        self.assertEqual(reply["sourceManifestSha256"], hashlib.sha256(LOCK.read_bytes()).hexdigest())
        self.assertIs(reply["readOnly"], True)
        return reply

    def inspect(self, operation, kind, payload, expected=0):
        reply = self.run_worker(
            ["--protocol", "1", operation, "--project-kind", kind,
             "--request-id", REQUEST_ID], payload, expected)
        self.assertEqual(reply["operation"], operation)
        self.assertEqual(reply["requestId"], REQUEST_ID)
        return reply

    def test_capabilities_are_explicit_and_need_no_input(self):
        reply = self.run_worker(["--protocol", "1", "capabilities"])
        self.assertEqual(reply["status"], "ok")
        self.assertEqual(reply["capabilities"], ["inspect-native-sed", "inspect-jkl"])
        self.assertEqual(reply["projectKinds"], ["jk", "mots", "ijim"])
        self.assertFalse(reply["writesFiles"])
        self.assertIsNone(reply["requestId"])
        self.assertEqual(reply["inputBytes"], 0)

    def test_fixed_argument_contract_rejects_wrong_version_extra_path_and_bad_id(self):
        cases = [
            [], ["--protocol", "2", "capabilities"],
            ["--protocol", "1", "capabilities", "extra"],
            ["--protocol", "1", "inspect-jkl", "--project-kind", "mots"],
            ["--protocol", "1", "inspect-jkl", "--project-kind", "other", "--request-id", REQUEST_ID],
            ["--protocol", "1", "inspect-jkl", "--project-kind", "mots", "--request-id", "A" * 32],
            ["--protocol", "1", "inspect-jkl", "--project-kind", "mots", "--request-id", REQUEST_ID, "file.jkl"],
        ]
        for arguments in cases:
            with self.subTest(arguments=arguments):
                reply = self.run_worker(arguments, expected=2)
                self.assertEqual(reply["error"]["code"], "invalid_request")
                self.assertEqual(reply["status"], "rejected")
                self.assertEqual(reply["inputBytes"], 0)

    def test_jkl_original_minimal_header_is_read_only_and_resource_incomplete(self):
        payload = b"# Original bridge fixture\nSECTION: HEADER\nVERSION 1\nEND\n"
        reply = self.inspect("inspect-jkl", "mots", payload)
        self.assertEqual(reply["projectKind"], "mots")
        self.assertEqual(reply["inputBytes"], len(payload))
        self.assertEqual(reply["summary"]["sectors"], 0)
        self.assertEqual(reply["summary"]["things"], 0)
        self.assertEqual(reply["resourceValidation"]["status"], "not-performed")
        self.assertFalse(reply["resourceValidation"]["complete"])
        self.assertEqual(reply["integrity"]["status"], "not-applicable")
        self.assertEqual(reply["optionalSections"], [])

    def test_jkl_golden_triangle_counts_cover_all_three_kinds(self):
        for kind, stem, extension in (("jk", "jkdf2", "jkl"), ("mots", "mots", "jkl"), ("ijim", "ijim", "ndy")):
            with self.subTest(kind=kind):
                payload = (VECTORS / f"{stem}_input.{extension}").read_bytes()
                reply = self.inspect("inspect-jkl", kind, payload)
                self.assertEqual(reply["projectKind"], kind)
                self.assertEqual(reply["summary"]["sectors"], 1)
                self.assertEqual(reply["summary"]["surfaces"], 1)
                self.assertEqual(reply["summary"]["vertices"], 3)

    def test_jkl_does_not_call_arbitrary_or_nul_text_a_valid_empty_level(self):
        for payload in (b"", b"hello", b"SECTION: HEADER\nEND\n",
                        b"SECTION: HEADER\nVERSION 1\x00\nEND\n",
                        b"SECTION: HEADER\nVERSION 99\nEND\n"):
            with self.subTest(payload=payload):
                reply = self.inspect("inspect-jkl", "jk", payload, 3)
                self.assertEqual(reply["error"]["code"], "parse_error")
                self.assertNotIn("summary", reply)

    def test_explicit_kind_mismatch_is_document_rejection(self):
        reply = self.inspect("inspect-jkl", "jk", b"SECTION: HEADER\nVERSION 3\nEND\n", 3)
        self.assertEqual(reply["error"]["code"], "project_kind_mismatch")
        reply = self.inspect("inspect-native-sed", "mots", (VECTORS / "jkdf2_native.sed").read_bytes(), 3)
        self.assertEqual(reply["error"]["code"], "project_kind_mismatch")

    def test_native_goldens_report_real_integrity_and_triangle_counts(self):
        for kind, stem in (("jk", "jkdf2"), ("mots", "mots"), ("ijim", "ijim")):
            with self.subTest(kind=kind):
                source = VECTORS / f"{stem}_native.sed"
                payload = source.read_bytes()
                reply = self.inspect("inspect-native-sed", kind, payload)
                self.assertEqual(reply["projectKind"], kind)
                self.assertEqual(reply["summary"]["sectors"], 1)
                self.assertEqual(reply["summary"]["surfaces"], 1)
                self.assertEqual(reply["summary"]["vertices"], 3)
                self.assertEqual(reply["integrity"]["status"], "clean")
                self.assertFalse(reply["integrity"]["hadCorruption"])
                self.assertEqual(source.read_bytes(), payload)

    def test_native_repair_is_observable_without_emitting_payload_or_saving(self):
        payload = (VECTORS / "hist_small_1_damage.sed").read_bytes()
        reply = self.inspect("inspect-native-sed", "ijim", payload)
        self.assertEqual(reply["integrity"]["status"], "repaired")
        self.assertTrue(reply["integrity"]["hadCorruption"])
        self.assertGreaterEqual(reply["integrity"]["repairedChunks"], 1)
        self.assertIn("hist", reply["integrity"]["repairedSections"])
        history = next(section for section in reply["optionalSections"] if section["type"] == "hist")
        self.assertGreater(history["payloadBytes"], 0)
        self.assertNotIn("payload", history)

    def test_native_damage_beyond_budget_is_not_salvaged_as_success(self):
        for payload in (b"not a native file", (VECTORS / "jkdf2_sect_unrecoverable.sed").read_bytes()):
            reply = self.inspect("inspect-native-sed", "jk", payload, 3)
            self.assertEqual(reply["error"]["code"], "parse_error")
            self.assertNotIn("summary", reply)
            self.assertNotIn("integrity", reply)

    def test_input_limit_refuses_before_parsing(self):
        reply = self.inspect("inspect-jkl", "jk", b"x" * (16777216 + 1), 2)
        self.assertEqual(reply["error"]["code"], "input_limit")
        self.assertEqual(reply["inputBytes"], 16777217)

    def test_jkl_admission_bounds_lines_tokens_and_repeated_geometry(self):
        header = b"SECTION: HEADER\nVERSION 1\nEND\n"
        cases = [
            header + b"#" * 65537,
            header + b"\n" * 131072,
            header + b"SECTION: SECTORS\nWORLD SECTORS 1\nSECTOR 0\nSURFACES 0 2147483647\nEND\n",
            # A source row with many tokens and repeated surface references
            # must be rejected before the codec can clone geometry.
            header + b"SECTION: SECTORS\n" + b"x " * 1000 + b"\nSURFACES 0 2000\nEND\n",
            # Opaque material/colormap names can also expand when cloned.
            header + b"SECTION: SECTORS\n" + b"x" * 40000 + b"\nSURFACES 0 2000\nEND\n",
            header + (b"x " * 1024 + b"\n") * 1025,
        ]
        for payload in cases:
            with self.subTest(length=len(payload)):
                reply = self.inspect("inspect-jkl", "jk", payload, 2)
                self.assertEqual(reply["error"]["code"], "input_limit")
                self.assertNotIn("summary", reply)

    def test_declared_world_count_is_checked_against_real_rows(self):
        payload = (b"SECTION: HEADER\nVERSION 1\nEND\nSECTION: GEORESOURCE\n"
                   b"WORLD COLORMAPS 0\nWORLD VERTICES 2147483647\nEND\n")
        reply = self.inspect("inspect-jkl", "jk", payload, 3)
        self.assertEqual(reply["error"]["code"], "parse_error")


if __name__ == "__main__":
    unittest.main(argv=[__file__, *REMAINING])
