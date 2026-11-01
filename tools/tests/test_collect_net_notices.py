"""Offline cache-selection regressions for the Windows notice collector.

The PowerShell harness creates original tiny packages in TEMP, never restores
dependencies, and replaces both HTTP entry points with local fixture functions.
"""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


COLLECTOR = Path(__file__).resolve().parents[1] / "Collect-NetNotices.ps1"
HARNESS = Path(__file__).with_name("collect_net_notices_fixtures.ps1")
PWSH = shutil.which("pwsh") if os.name == "nt" else None
DEADLINE_SECONDS = 180
EXPECTED_CASES = {
    "ordinary-partial-primary-fallback", "runtime-partial-primary-fallback",
    "signed-content-hash-differs-from-archive", "missing-marker-fallback",
    "corrupt-marker-fallback", "wrong-content-hash-fallback",
    "missing-ordinary-file-fallback", "missing-runtime-file-fallback",
    "wrong-nuspec-id-fallback", "wrong-nuspec-version-fallback",
    "all-incomplete-refused", "missing-marker-refused", "corrupt-marker-refused",
    "wrong-content-hash-refused", "missing-sidecar-refused",
    "bad-sidecar-refused", "wrong-nuspec-refused",
    "missing-runtime-payload-refused", "nuspec-only-no-license-refused",
    "pinned-notice-only-refused", "pinned-license-accepted",
    "nested-notice-only-refused", "nested-license-accepted",
}


class FixtureInfrastructureError(RuntimeError):
    """A fixture setup/timeout failure, distinct from a regression assertion."""


@unittest.skipUnless(os.name == "nt", "Windows notice-collector regression harness")
@unittest.skipUnless(PWSH, "PowerShell 7 (pwsh) is required")
class CollectNetNoticesTests(unittest.TestCase):
    def test_complete_cache_selection_and_notice_coverage(self):
        scratch_parent = Path(tempfile.gettempdir()).resolve()
        if any((ancestor / ".git").exists()
               for ancestor in (scratch_parent, *scratch_parent.parents)):
            raise FixtureInfrastructureError("TEMP must be outside every Git checkout")
        with tempfile.TemporaryDirectory(prefix="sed-notice-fixtures-",
                                         dir=scratch_parent) as temporary:
            scratch = Path(temporary) / "cases"
            command = [PWSH, "-NoProfile", "-NonInteractive", "-File", str(HARNESS),
                       "-CollectorPath", str(COLLECTOR), "-ScratchPath", str(scratch)]
            try:
                result = subprocess.run(command, capture_output=True, text=True,
                                        encoding="utf-8", errors="replace",
                                        timeout=DEADLINE_SECONDS, check=False)
            except subprocess.TimeoutExpired as exc:
                # subprocess.run kills and reaps this owned child. The offline
                # PowerShell harness runs the collector in-process, without any
                # builds, restores, native children or external HTTP requests.
                raise FixtureInfrastructureError(
                    f"Notice fixture infrastructure exceeded {DEADLINE_SECONDS}s; "
                    f"stdout={exc.stdout!r}; stderr={exc.stderr!r}") from exc
            receipt_path = scratch / "receipt.json"
            if not receipt_path.is_file():
                raise FixtureInfrastructureError(
                    f"Fixture harness produced no receipt (exit {result.returncode}):\n"
                    f"{result.stdout}\n{result.stderr}")
            receipt = json.loads(receipt_path.read_text(encoding="utf-8-sig"))
            records = receipt["cases"]
            self.assertEqual({record["case"] for record in records}, EXPECTED_CASES)
            self.assertEqual(len(records), len(EXPECTED_CASES))
            self.assertEqual(receipt["sourceSha256"],
                             hashlib.sha256(COLLECTOR.read_bytes()).hexdigest())
            self.assertFalse(receipt["networkPerformed"])
            self.assertFalse(receipt["buildOrRestorePerformed"])
            for record in records:
                with self.subTest(case=record["case"]):
                    self.assertTrue(record["passed"], record)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertEqual(receipt["passed"], len(EXPECTED_CASES))
            self.assertEqual(receipt["failed"], 0)


if __name__ == "__main__":
    unittest.main()
