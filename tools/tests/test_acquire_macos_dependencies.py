"""Original fixtures for the pinned Mac dependency acquisition boundary."""
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import sys
import tarfile
import tempfile
import unittest

TOOLS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(TOOLS))
SCRIPT = TOOLS / "acquire_macos_dependencies.py"
dep = None
if SCRIPT.exists():
    spec = importlib.util.spec_from_file_location("acquire_macos_dependencies", SCRIPT)
    dep = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(dep)


def identity(data):
    return {"bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}


def archive_bytes(entries):
    result = io.BytesIO()
    with tarfile.open(fileobj=result, mode="w") as archive:
        for name, value in entries:
            info = tarfile.TarInfo(name)
            if isinstance(value, tuple):
                info.type, info.linkname = value
                archive.addfile(info)
            else:
                info.size = len(value)
                archive.addfile(info, io.BytesIO(value))
    return result.getvalue()


class AcquisitionTests(unittest.TestCase):
    def setUp(self):
        self.assertIsNotNone(dep, "Mac dependency acquisition helper is not implemented")
        self.temp = tempfile.TemporaryDirectory(prefix="sed-mac-acquisition-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.library = b"original synthetic native-library fixture"
        self.license = b"original synthetic license text\n"
        self.extra = b"original synthetic dependency notice\n"
        self.archive = archive_bytes([("Package/lib.dylib", self.library), ("Package/LICENSE", self.license)])
        self.lock = {
            "schemaVersion": 1, "version": "1.2.3", "upstreamCommit": "1" * 40,
            "artifact": {"path": "dependency.tar", "url": "https://example.invalid/release.tar", **identity(self.archive)},
            "library": {"path": "libMoltenVK.dylib", "member": "Package/lib.dylib", **identity(self.library)},
            "notices": [
                {"path": "notices/main/LICENSE", "member": "Package/LICENSE", "destination": "licenses/moltenvk/main/LICENSE", "url": "https://example.invalid/LICENSE", **identity(self.license)},
                {"path": "notices/extra/LICENSE", "destination": "licenses/moltenvk/extra/LICENSE", "url": "https://example.invalid/extra/LICENSE", **identity(self.extra)},
            ],
        }
        self.responses = {self.lock["artifact"]["url"]: self.archive, self.lock["notices"][1]["url"]: self.extra}

    def acquire(self, output=None, archive_path=None):
        def open_url(url, timeout):
            return io.BytesIO(self.responses[url])
        return dep.acquire(output or self.root / "new output", self.lock, open_url=open_url, archive_path=archive_path)

    def test_complete_manifest_binds_exact_artifact_library_and_notice_bytes(self):
        path = self.acquire()
        manifest = json.loads(path.read_text())
        self.assertEqual("1.2.3", manifest["version"])
        for row in [manifest["artifact"], manifest["library"], *manifest["notices"]]:
            self.assertFalse(Path(row["path"]).is_absolute())
            self.assertEqual(identity((path.parent / row["path"]).read_bytes()), {key: row[key] for key in ("bytes", "sha256")})
        self.assertEqual(self.library, (path.parent / "libMoltenVK.dylib").read_bytes())
        self.assertEqual(2, len(manifest["notices"]))
        self.assertNotIn("member", manifest["library"])

    def test_cached_archive_has_same_provenance_and_is_never_modified(self):
        cached = self.root / "cached.tar"
        cached.write_bytes(self.archive)
        del self.responses[self.lock["artifact"]["url"]]
        path = self.acquire(archive_path=cached)
        self.assertEqual(self.archive, cached.read_bytes())
        self.assertEqual(self.lock["artifact"], json.loads(path.read_text())["artifact"])

    def test_existing_output_is_preserved_and_refused(self):
        target = self.root / "existing"
        target.mkdir()
        (target / "keep").write_bytes(b"retained")
        with self.assertRaises(RuntimeError):
            self.acquire(target)
        self.assertEqual(["keep"], [p.name for p in target.iterdir()])
        self.assertEqual(b"retained", (target / "keep").read_bytes())

    def test_output_below_git_worktree_is_refused_before_creation(self):
        (self.root / ".git").mkdir()
        with self.assertRaises(RuntimeError):
            self.acquire()
        self.assertFalse((self.root / "new output").exists())

    def test_archive_hash_or_size_mismatch_never_emits_manifest(self):
        for value in (self.archive[:-1], self.archive + b"x", b"X" + self.archive[1:]):
            with self.subTest(size=len(value)):
                self.responses[self.lock["artifact"]["url"]] = value
                target = self.root / str(len(list(self.root.iterdir())))
                with self.assertRaises(RuntimeError):
                    self.acquire(target)
                self.assertFalse((target / "moltenvk-manifest.json").exists())

    def test_wrong_external_notice_prevents_success_manifest(self):
        self.responses[self.lock["notices"][1]["url"]] = b"wrong notice"
        with self.assertRaises(RuntimeError):
            self.acquire()
        self.assertFalse((self.root / "new output/moltenvk-manifest.json").exists())

    def test_bad_selected_member_cannot_be_extracted(self):
        cases = [
            [("Package/lib.dylib", self.library), ("Package/lib.dylib", self.library)],
            [("Package/lib.dylib", (tarfile.SYMTYPE, "elsewhere"))],
            [("Package/lib.dylib", (tarfile.LNKTYPE, "elsewhere"))],
            [("../outside", b"escape"), ("Package/lib.dylib", self.library)],
            [("/absolute", b"escape"), ("Package/lib.dylib", self.library)],
            [("Package\\escape", b"escape"), ("Package/lib.dylib", self.library)],
            [("Package/lib.dylib", b"changed fixture")],
            [("Package/unselected", b"only unrelated file")],
        ]
        for index, entries in enumerate(cases):
            with self.subTest(index=index):
                data = archive_bytes(entries + [("Package/LICENSE", self.license)])
                self.lock["artifact"].update(identity(data))
                self.responses[self.lock["artifact"]["url"]] = data
                output = self.root / str(index)
                with self.assertRaises(RuntimeError):
                    self.acquire(output)
                self.assertFalse((output / "libMoltenVK.dylib").exists())
                self.assertFalse((output / "moltenvk-manifest.json").exists())
        self.assertFalse((self.root / "outside").exists())

    def test_unselected_archive_symlinks_are_never_materialized(self):
        data = archive_bytes([("Package/lib.dylib", self.library), ("Package/LICENSE", self.license),
                              ("Package/framework-link", (tarfile.SYMTYPE, "../../outside"))])
        self.lock["artifact"].update(identity(data))
        self.responses[self.lock["artifact"]["url"]] = data
        path = self.acquire()
        self.assertFalse((path.parent / "Package").exists())
        self.assertEqual(self.library, (path.parent / "libMoltenVK.dylib").read_bytes())

    def test_bad_manifest_paths_are_rejected_before_output_exists(self):
        for value in ("../escape", "/absolute", "C:/absolute", "nested\\file", "a/./b", "a//b"):
            with self.subTest(value=value):
                self.lock["library"]["path"] = value
                with self.assertRaises(RuntimeError):
                    self.acquire()
                self.assertFalse((self.root / "new output").exists())

    def test_case_colliding_output_paths_are_rejected_on_every_host(self):
        self.lock["notices"][0]["path"] = "LIBMOLTENVK.DYLIB"
        with self.assertRaises(RuntimeError):
            self.acquire()
        self.assertFalse((self.root / "new output").exists())

    def test_notice_destination_must_remain_inside_moltenvk_notice_tree(self):
        self.lock["notices"][0]["destination"] = "licenses/something-else/LICENSE"
        with self.assertRaises(RuntimeError):
            self.acquire()
        self.assertFalse((self.root / "new output").exists())

    def test_plain_http_is_not_accepted_for_pinned_downloads(self):
        self.lock["artifact"]["url"] = "http://example.invalid/release.tar"
        with self.assertRaises(RuntimeError):
            self.acquire()
        self.assertFalse((self.root / "new output").exists())

    def test_linked_output_parent_is_refused(self):
        actual = self.root / "actual"
        actual.mkdir()
        link = self.root / "link"
        try:
            link.symlink_to(actual, target_is_directory=True)
        except OSError as error:
            self.skipTest(f"This host cannot create the symlink fixture: {error}")
        with self.assertRaises(RuntimeError):
            self.acquire(link / "new")
        self.assertEqual([], list(actual.iterdir()))


if __name__ == "__main__":
    unittest.main()
