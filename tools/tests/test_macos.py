"""Original distribution fixtures; these tests do not execute Mac binaries."""
import importlib.util
import json
import os
from pathlib import Path
import plistlib
import shutil
import stat
import struct
import subprocess
import sys
import tempfile
import unittest
import zipfile

TOOLS = Path(__file__).resolve().parents[1]
sys.path.append(str(TOOLS))
DRIVER = TOOLS / "macos.py"


def thin(cpu):
    return struct.pack("<IIIIIIII", 0xFEEDFACF, cpu, 0, 6, 0, 0, 0, 0)


def universal():
    a, b = thin(0x01000007), thin(0x0100000C)
    return (struct.pack(">II", 0xCAFEBABE, 2)
            + struct.pack(">IIIII", 0x01000007, 0, 48, len(a), 0)
            + struct.pack(">IIIII", 0x0100000C, 0, 48 + len(a), len(b), 0) + a + b)


class MacPackageTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue(DRIVER.is_file(), "Mac bundle assembler is not implemented")
        spec = importlib.util.spec_from_file_location("sed_macos_tests_driver", DRIVER)
        self.mac = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.mac)
        self.temp = tempfile.TemporaryDirectory(prefix="sed mac packaging ")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def file(self, relative, data=b"original fixture", mode=0o644):
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
        path.chmod(mode)
        return path

    def package(self):
        package = self.root / "SED-MotS-fixture-osx-arm64"
        self.file(package.name + "/SED-MotS.app/Contents/MacOS/Sed.App", thin(0x0100000C), 0o755)
        self.file(package.name + "/data/room with spaces.tpl")
        self.mac.write_payload_manifest(package)
        return package

    def test_native_host_and_rid_must_match(self):
        self.mac.require_host("osx-arm64", system="Darwin", machine="arm64")
        self.mac.require_host("osx-x64", system="Darwin", machine="x86_64")
        for rid, system, machine in (("osx-x64", "Darwin", "arm64"), ("osx-arm64", "Linux", "aarch64"),
                                     ("win-x64", "Darwin", "x86_64"), ("osx-arm64", "Darwin", "x86_64")):
            with self.subTest(rid=rid, system=system, machine=machine), self.assertRaises(RuntimeError):
                self.mac.require_host(rid, system=system, machine=machine)

    def test_bundle_metadata_keeps_full_release_and_no_unimplemented_file_association(self):
        info = self.mac.bundle_info("0.4.0-mots.18-mac.1")
        self.assertEqual("Sed.App", info["CFBundleExecutable"])
        self.assertEqual("APPL", info["CFBundlePackageType"])
        self.assertEqual("15.0", info["LSMinimumSystemVersion"])
        self.assertEqual("0.4.0-mots.18-mac.1", info["SEDReleaseVersion"])
        self.assertEqual("0.4.0", info["CFBundleShortVersionString"])
        self.assertNotIn("CFBundleDocumentTypes", info)
        self.assertEqual(info, plistlib.loads(plistlib.dumps(info)))

    def test_versions_cannot_inject_paths_or_shell_syntax(self):
        for version in ("../escape", "0.4.0/evil", "0.4.0;touch bad", "0.4.0\nx", "", "01.2.3"):
            with self.subTest(version=version), self.assertRaises(RuntimeError):
                self.mac.bundle_info(version)

    def test_thin_and_fat_macho_are_checked_from_bytes(self):
        self.assertEqual({"osx-arm64"}, self.mac.macho_architectures(thin(0x0100000C)))
        self.assertEqual({"osx-x64"}, self.mac.macho_architectures(thin(0x01000007)))
        self.assertEqual({"osx-arm64", "osx-x64"}, self.mac.macho_architectures(universal()))
        self.assertEqual(set(), self.mac.macho_architectures(b"MZ original PE fixture"))

    def test_invalid_fat_slice_bounds_or_identity_are_refused(self):
        for data in (universal()[:50],
                     universal()[:8] + struct.pack(">IIIII", 0x01000007, 0, 999999, 32, 0) + universal()[28:],
                     universal()[:28] + struct.pack(">IIIII", 0x0100000C, 0, 48, 32, 0) + universal()[48:]):
            with self.subTest(length=len(data)), self.assertRaises(RuntimeError):
                self.mac.macho_architectures(data)

    def test_runtime_requires_included_frameworks_and_macho_apphost(self):
        directory = self.root / "runtime"
        for name in ("Sed.App", "libcoreclr.dylib", "libhostfxr.dylib", "libhostpolicy.dylib"):
            self.file("runtime/" + name, thin(0x0100000C), 0o755)
        for name in ("Sed.App.dll", "System.Private.CoreLib.dll"):
            self.file("runtime/" + name)
        config = self.file("runtime/Sed.App.runtimeconfig.json",
                           json.dumps({"runtimeOptions": {"includedFrameworks": [{"name": "Microsoft.NETCore.App", "version": "10.0.12"}]}}).encode())
        self.mac.require_runtime(directory, "Sed.App", "osx-arm64")
        with self.assertRaises(RuntimeError):
            self.mac.require_runtime(directory, "Sed.App", "osx-x64")
        config.write_text('{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App"}}}')
        with self.assertRaises(RuntimeError):
            self.mac.require_runtime(directory, "Sed.App", "osx-arm64")

    def test_manifest_detects_changed_added_and_missing_files(self):
        package = self.package()
        self.assertEqual(2, self.mac.verify_payload_manifest(package))
        target = package / "data/room with spaces.tpl"
        original = target.read_bytes()
        target.write_bytes(b"changed")
        with self.assertRaises(RuntimeError):
            self.mac.verify_payload_manifest(package)
        target.write_bytes(original)
        extra = package / "extra"
        extra.write_bytes(b"unmanifested")
        with self.assertRaises(RuntimeError):
            self.mac.verify_payload_manifest(package)
        extra.unlink()
        target.unlink()
        with self.assertRaises(RuntimeError):
            self.mac.verify_payload_manifest(package)

    @unittest.skipIf(os.name == "nt", "POSIX execute-bit enforcement is checked on native hosts")
    def test_manifest_detects_lost_executable_mode(self):
        package = self.package()
        (package / "SED-MotS.app/Contents/MacOS/Sed.App").chmod(0o644)
        with self.assertRaises(RuntimeError):
            self.mac.verify_payload_manifest(package)

    def test_archive_roundtrip_has_exact_members_bytes_and_modes(self):
        package = self.package()
        archive = self.root / "release.zip"
        self.mac.archive_package(package, archive)
        self.mac.verify_archive(archive, package)
        relocated = self.root / "relocated directory"
        extracted = self.mac.extract_package(archive, relocated)
        self.assertEqual((package / "data/room with spaces.tpl").read_bytes(),
                         (extracted / "data/room with spaces.tpl").read_bytes())
        self.mac.verify_payload_manifest(extracted)

    def test_archive_with_extra_or_duplicate_member_is_refused(self):
        package = self.package()
        for index, name in enumerate((package.name + "/extra", package.name + "/data/room with spaces.tpl")):
            archive = self.root / f"extra-{index}.zip"
            self.mac.archive_package(package, archive)
            with zipfile.ZipFile(archive, "a") as z:
                z.writestr(name, b"changed")
            with self.assertRaises(RuntimeError):
                self.mac.verify_archive(archive, package)

    def test_archive_extraction_rejects_traversal_links_and_mixed_roots(self):
        for index, names in enumerate((["../escape"], ["/absolute"], ["root/a", "other/b"], ["root/a/b"])):
            archive = self.root / f"unsafe-{index}.zip"
            with zipfile.ZipFile(archive, "w") as z:
                for name in names:
                    z.writestr(name, b"fixture")
            if index == 3:
                # zipfile normalizes the host separator when constructing an
                # entry on Windows. Patch equal-length raw names in both ZIP
                # headers so this is an actual malformed portable archive.
                archive.write_bytes(archive.read_bytes().replace(b"root/a/b", b"root/a\\b"))
            with self.assertRaises(RuntimeError):
                self.mac.extract_package(archive, self.root / f"out-{index}")
            self.assertFalse((self.root / f"out-{index}").exists())
        archive = self.root / "link.zip"
        with zipfile.ZipFile(archive, "w") as z:
            entry = zipfile.ZipInfo("root/link")
            entry.create_system = 3
            entry.external_attr = (stat.S_IFLNK | 0o777) << 16
            z.writestr(entry, "../escape")
        with self.assertRaises(RuntimeError):
            self.mac.extract_package(archive, self.root / "out-link")
        self.assertFalse((self.root / "escape").exists())

    def test_manifest_rejects_case_alias_members(self):
        package = self.package()
        manifest = package / "PAYLOAD.json"
        data = json.loads(manifest.read_text())
        data["files"].append(dict(data["files"][0], path=data["files"][0]["path"].upper()))
        manifest.write_text(json.dumps(data))
        with self.assertRaises(RuntimeError):
            self.mac.verify_payload_manifest(package)

    @unittest.skipIf(os.name == "nt", "Portable POSIX launchers execute on native Mac/Linux")
    def test_launchers_keep_spaces_arguments_and_callers_working_directory(self):
        package = self.root / "package with spaces"
        self.mac.write_launchers(package)
        executable = package / "agent/sed-agent"
        executable.parent.mkdir()
        executable.write_text('#!/bin/sh\nprintf "%s\\n" "$PWD" "$@"\n')
        executable.chmod(0o755)
        caller = self.root / "caller with spaces"
        caller.mkdir()
        result = subprocess.run([str(package / "bin/sed-agent"), "a b", "--", 'literal"$value'],
                                cwd=caller, capture_output=True, text=True, timeout=10)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual([str(caller), "a b", "--", 'literal"$value'], result.stdout.splitlines())
        self.assertNotIn("readlink -f", (package / "bin/sed-agent").read_text())


if __name__ == "__main__":
    unittest.main()
