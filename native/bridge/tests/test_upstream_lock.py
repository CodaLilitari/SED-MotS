"""Exercises the actual source verifier using tiny original trees, without Git."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

PARSER = argparse.ArgumentParser()
PARSER.add_argument("--verifier", type=Path, required=True)
PARSER.add_argument("--scratch", type=Path, required=True)
ARGS, REMAINING = PARSER.parse_known_args()
COMMIT = "b82255998fb733c8e8c87104eb7c17f27b48376f"


class UpstreamLockTests(unittest.TestCase):
    def setUp(self):
        ARGS.scratch.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="lock-", dir=ARGS.scratch)
        self.root = Path(self.temp.name) / "upstream"
        (self.root / "src/core").mkdir(parents=True)
        (self.root / "CMakeLists.txt").write_bytes(b"# original fixture\n")
        (self.root / "src/core/fixture.cpp").write_bytes(b"// original fixture\n")
        self.lock = Path(self.temp.name) / "lock.json"
        files = []
        for name in ("CMakeLists.txt", "src/core/fixture.cpp"):
            data = (self.root / name).read_bytes()
            files.append({"path": name, "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()})
        self.lock.write_text(json.dumps({
            "schemaVersion": 1, "upstreamCommit": COMMIT,
            "coverage": ["CMakeLists.txt", "src/core/"], "files": files}), encoding="utf-8")

    def tearDown(self):
        self.temp.cleanup()

    def run_verifier(self, expected=0):
        result = subprocess.run(
            [sys.executable, "-B", str(ARGS.verifier), "--root", str(self.root), "--lock", str(self.lock)],
            capture_output=True, timeout=15)
        self.assertEqual(result.returncode, expected, result.stderr.decode(errors="replace"))
        return result

    def test_exact_tree_passes_and_reports_lock_identity(self):
        reply = json.loads(self.run_verifier().stdout)
        self.assertEqual(reply["verifiedFiles"], 2)
        self.assertEqual(reply["upstreamCommit"], COMMIT)
        self.assertEqual(reply["sourceManifestSha256"], hashlib.sha256(self.lock.read_bytes()).hexdigest())

    def test_changed_file_is_refused(self):
        (self.root / "src/core/fixture.cpp").write_bytes(b"// changed fixture\n")
        self.run_verifier(2)

    def test_extra_compile_input_is_refused(self):
        (self.root / "src/core/extra.hpp").write_text("// extra", encoding="utf-8")
        self.run_verifier(2)

    def test_missing_input_is_refused(self):
        (self.root / "src/core/fixture.cpp").unlink()
        self.run_verifier(2)

    def test_linked_input_is_refused(self):
        original = self.root / "src/core/fixture.cpp"
        external = Path(self.temp.name) / "outside.cpp"
        external.write_bytes(original.read_bytes())
        original.unlink()
        try:
            original.symlink_to(external)
        except OSError:
            self.skipTest("Creating a symlink is unavailable in this environment")
        self.run_verifier(2)

    def test_unsafe_manifest_path_is_refused(self):
        value = json.loads(self.lock.read_text())
        value["files"][0]["path"] = "../outside"
        self.lock.write_text(json.dumps(value))
        self.run_verifier(2)


if __name__ == "__main__":
    unittest.main(argv=[__file__, *REMAINING])
