#!/usr/bin/env python3
"""Behavior checks for the Linux distribution tools; no SDK, GPU or network."""
from __future__ import annotations

import importlib.util
import json
import os
from pathlib import Path
import shlex
import subprocess
import sys
import tarfile
import tempfile
import unittest


DRIVER = Path(__file__).with_name("linux.py")


class LinuxToolsTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue(DRIVER.is_file(), "The Linux distribution driver has not been implemented")
        spec = importlib.util.spec_from_file_location("sed_linux_tools", DRIVER)
        self.tools = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.tools)
        self.temp = tempfile.TemporaryDirectory(prefix="sed linux tooling ")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def executable(self, path, body):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("#!/usr/bin/env python3\n" + body, encoding="utf-8")
        path.chmod(0o755)
        return path

    def git(self, repo, *arguments):
        return subprocess.check_output(["git", "-C", str(repo), *arguments], stderr=subprocess.STDOUT).decode().strip()

    def repository(self):
        repo = self.root / "source repo"
        repo.mkdir()
        self.git(repo, "init", "-q")
        self.git(repo, "config", "user.name", "Linux tooling test")
        self.git(repo, "config", "user.email", "linux-test@example.invalid")
        for name, content in {
            ".gitignore": "private/\nbuild/\n",
            "LICENSE": "Original fixture license\n",
            "README.md": "Original fixture\n",
            "docs/LINUX_QUICKSTART.md": "Fixture quickstart\n",
            "data/mots.tpl": "Original fixture templates\n",
            ".agents/skills/fixture/SKILL.md": "Original fixture skill\n",
            "licenses/fixture.txt": "Original fixture attribution\n",
        }.items():
            path = repo / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(content, encoding="utf-8")
        self.git(repo, "add", ".")
        self.git(repo, "commit", "-qm", "Original packaging fixture")
        return repo

    def test_launchers_preserve_relative_arguments_cwd_exit_and_relocation(self):
        package = self.root / "initial package"
        for relative in ("Sed.App", "agent/sed-agent", "mcp/sed-mcp"):
            self.executable(package / relative,
                "import json, os, sys\nprint(json.dumps({'cwd': os.getcwd(), 'args': sys.argv[1:]}))\n"
                "sys.stderr.write('target stderr\\n')\nsys.exit(37)\n")
        self.tools.write_launchers(package)
        relocated = self.root / "relocated 'package' $ with spaces"
        package.rename(relocated)
        caller = self.root / "caller directory"
        caller.mkdir()
        arguments = ["relative input.jkl", "", "quotes'\"$;()", "--flag", "кириллица"]
        for name in ("sed-mots", "sed-agent", "sed-mcp"):
            with self.subTest(name=name):
                completed = subprocess.run([str(relocated / "bin" / name), *arguments], cwd=caller,
                    capture_output=True, text=True)
                self.assertEqual(completed.returncode, 37)
                self.assertEqual(json.loads(completed.stdout), {"cwd": str(caller), "args": arguments})
                self.assertEqual(completed.stderr, "target stderr\n")

    def test_mcp_launcher_keeps_stdio_bytes_and_symlink_invocation(self):
        package = self.root / "package"
        self.executable(package / "mcp/sed-mcp", "import sys\nsys.stdout.buffer.write(sys.stdin.buffer.read())\n")
        self.tools.write_launchers(package)
        alias = self.root / "user bin" / "sed-mcp"
        alias.parent.mkdir()
        alias.symlink_to(package / "bin/sed-mcp")
        payload = b'{"jsonrpc":"2.0","id":1}\n\x00\xff'
        result = subprocess.run([str(alias)], input=payload, capture_output=True, cwd=self.root)
        self.assertEqual(result.returncode, 0)
        self.assertEqual(result.stdout, payload)
        self.assertEqual(result.stderr, b"")

    def test_source_snapshot_excludes_ignored_and_untracked_private_content(self):
        repo = self.repository()
        private = repo / "private" / "do-not-ship.txt"
        private.parent.mkdir()
        private.write_text("PRIVATE FIXTURE", encoding="utf-8")
        state = self.tools.source_state(repo, require_clean=True)
        output = self.root / "handoff"
        output.mkdir()
        source = self.tools.snapshot_source(repo, state, output)
        self.assertFalse((source / "private").exists())
        self.assertEqual((source / "LICENSE").read_text(), "Original fixture license\n")
        with tarfile.open(output / "SED-MotS-source.tar.gz") as archive:
            self.assertNotIn("private/do-not-ship.txt", archive.getnames())
        package = self.root / "package"
        package.mkdir()
        self.tools.copy_support(source, package)
        for relative in ("LICENSE", "docs/LINUX_QUICKSTART.md", ".agents/skills/fixture/SKILL.md",
                         "licenses/fixture.txt", "data/mots.tpl", "agent/data/mots.tpl", "mcp/data/mots.tpl"):
            self.assertTrue((package / relative).is_file(), relative)
        self.assertFalse((package / "private").exists())

    def test_source_snapshot_preserves_git_blob_bytes_with_autocrlf_enabled(self):
        repo = self.repository()
        (repo / "tools").mkdir()
        (repo / "tools/fixture.sh").write_bytes(b"#!/bin/sh\nexit 0\n")
        (repo / "tools/fixture.sh").chmod(0o755)
        (repo / "docs/source.txt").write_bytes(b"original first line\noriginal second line\n")
        (repo / ".gitattributes").write_bytes(b"*.sh text\n*.txt text\n")
        self.git(repo, "add", ".")
        self.git(repo, "commit", "-qm", "Original LF shell and source fixture")
        self.git(repo, "config", "core.autocrlf", "true")
        self.git(repo, "config", "core.eol", "crlf")
        state = self.tools.source_state(repo, require_clean=True)
        output = self.root / "handoff"
        output.mkdir()
        source = self.tools.snapshot_source(repo, state, output)
        names = self.git(repo, "ls-files").splitlines()
        with tarfile.open(output / "SED-MotS-source.tar.gz") as archive:
            for name in names:
                with self.subTest(name=name):
                    blob = subprocess.check_output(["git", "-C", str(repo), "show", f"{state['commit']}:{name}"])
                    self.assertEqual((source / name).read_bytes(), blob)
                    self.assertEqual(archive.extractfile(name).read(), blob)
        completed = subprocess.run([str(source / "tools/fixture.sh")], cwd=self.root, capture_output=True)
        self.assertEqual(completed.returncode, 0, completed.stderr)

    def test_source_snapshot_refuses_export_subst_that_changes_tracked_bytes(self):
        repo = self.repository()
        (repo / ".gitattributes").write_bytes(b"docs/revision.txt export-subst\n")
        (repo / "docs/revision.txt").write_bytes(b"Original source token: $Format:%H$\n")
        self.git(repo, "add", ".")
        self.git(repo, "commit", "-qm", "Source export substitution fixture")
        output = self.root / "handoff"
        output.mkdir()
        with self.assertRaises(self.tools.ToolError):
            self.tools.snapshot_source(repo, self.tools.source_state(repo, require_clean=True), output)

    def test_source_snapshot_refuses_export_ignore_that_omits_tracked_input(self):
        repo = self.repository()
        (repo / ".gitattributes").write_bytes(b"docs/omitted.txt export-ignore\n")
        (repo / "docs/omitted.txt").write_bytes(b"Original tracked source input\n")
        self.git(repo, "add", ".")
        self.git(repo, "commit", "-qm", "Source export omission fixture")
        output = self.root / "handoff"
        output.mkdir()
        with self.assertRaises(self.tools.ToolError):
            self.tools.snapshot_source(repo, self.tools.source_state(repo, require_clean=True), output)

    def test_dirty_and_changed_source_commit_are_refused(self):
        repo = self.repository()
        state = self.tools.source_state(repo, require_clean=True)
        (repo / "README.md").write_text("Changed fixture\n", encoding="utf-8")
        with self.assertRaises(self.tools.ToolError):
            self.tools.assert_source(repo, state)
        self.git(repo, "add", "README.md")
        self.git(repo, "commit", "-qm", "New source identity")
        with self.assertRaises(self.tools.ToolError):
            self.tools.assert_source(repo, state)
        (repo / "untracked.txt").write_text("Unreviewed fixture\n", encoding="utf-8")
        with self.assertRaises(self.tools.ToolError):
            self.tools.source_state(repo, require_clean=True)

    def test_tracked_symlink_cannot_pull_external_files_into_source(self):
        repo = self.repository()
        outside = self.root / "outside.txt"
        outside.write_text("External fixture\n", encoding="utf-8")
        (repo / "docs/external.txt").symlink_to(outside)
        self.git(repo, "add", "docs/external.txt")
        self.git(repo, "commit", "-qm", "Unsupported symlink fixture")
        output = self.root / "handoff"
        output.mkdir()
        with self.assertRaises(self.tools.ToolError):
            self.tools.snapshot_source(repo, self.tools.source_state(repo, require_clean=True), output)

    def test_manifest_rejects_modified_extra_missing_and_traversal_entries(self):
        package = self.root / "package"
        package.mkdir()
        payload = package / "file with spaces.txt"
        payload.write_bytes(b"original")
        self.tools.write_manifest(package)
        self.tools.verify_manifest(package)
        original_manifest = (package / "SHA256SUMS.txt").read_bytes()
        payload.write_bytes(b"modified")
        with self.assertRaises(self.tools.ToolError):
            self.tools.verify_manifest(package)
        payload.write_bytes(b"original")
        extra = package / "extra.txt"
        extra.write_bytes(b"unexpected")
        with self.assertRaises(self.tools.ToolError):
            self.tools.verify_manifest(package)
        extra.unlink()
        payload.unlink()
        with self.assertRaises(self.tools.ToolError):
            self.tools.verify_manifest(package)
        (package / "SHA256SUMS.txt").write_bytes(original_manifest.replace(b"file with spaces.txt", b"../outside.txt"))
        with self.assertRaises(self.tools.ToolError):
            self.tools.verify_manifest(package)

    def test_runtime_deduplication_preserves_modes_and_tar_hardlinks(self):
        package = self.root / "SED-MotS-test-linux-x64"
        package.mkdir()
        for relative, mode in (("libcoreclr.so", 0o755), ("agent/libcoreclr.so", 0o755),
                               ("mcp/libcoreclr.so", 0o755), ("different-mode.txt", 0o644)):
            path = package / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"same original runtime fixture")
            path.chmod(mode)
        saved = self.tools.deduplicate(package)
        self.assertEqual(saved, 2 * len(b"same original runtime fixture"))
        self.assertEqual((package / "libcoreclr.so").stat().st_ino, (package / "mcp/libcoreclr.so").stat().st_ino)
        self.assertNotEqual((package / "libcoreclr.so").stat().st_ino, (package / "different-mode.txt").stat().st_ino)
        first, second = self.root / "first.tar.gz", self.root / "second.tar.gz"
        self.tools.archive_package(package, first, 1700000000)
        os.utime(package / "different-mode.txt", (1800000000, 1800000000))
        self.tools.archive_package(package, second, 1700000000)
        self.assertEqual(first.read_bytes(), second.read_bytes())
        with tarfile.open(first) as archive:
            self.assertEqual(sum(member.islnk() for member in archive), 2)

    def test_new_output_never_replaces_existing_directory(self):
        output = self.root / "existing handoff"
        output.mkdir()
        marker = output / "receipt.txt"
        marker.write_bytes(b"keep")
        with self.assertRaises(self.tools.ToolError):
            self.tools.new_output(output)
        self.assertEqual(marker.read_bytes(), b"keep")

    def test_desktop_installation_handles_spaces_and_refuses_replacement(self):
        package = self.root / "portable package"
        self.executable(package / "Sed.App", "pass\n")
        self.tools.write_launchers(package)
        applications, binaries = self.root / "local share/applications", self.root / "local bin"
        desktop = self.tools.install_desktop(package, applications, binaries)
        values = dict(line.split("=", 1) for line in desktop.read_text().splitlines() if "=" in line)
        self.assertEqual(shlex.split(values["Exec"]), [str(package / "bin/sed-mots"), "%f"])
        self.assertNotIn("Path", values, "Desktop launch must retain the desktop caller's working directory")
        self.assertEqual((binaries / "sed-agent").resolve(), package / "bin/sed-agent")
        original = desktop.read_bytes()
        with self.assertRaises(self.tools.ToolError):
            self.tools.install_desktop(package, applications, binaries)
        self.assertEqual(desktop.read_bytes(), original)

    def test_desktop_conflict_does_not_partially_install_other_files(self):
        package = self.root / "package"
        self.executable(package / "Sed.App", "pass\n")
        self.tools.write_launchers(package)
        applications, binaries = self.root / "apps", self.root / "bin"
        binaries.mkdir()
        (binaries / "sed-agent").write_text("existing user command", encoding="utf-8")
        with self.assertRaises(self.tools.ToolError):
            self.tools.install_desktop(package, applications, binaries)
        self.assertFalse((applications / "sed-mots.desktop").exists())
        self.assertFalse((binaries / "sed-mots").exists())

    def fake_sdk(self, fail=False):
        records = self.root / "sdk calls.jsonl"
        sdk = self.executable(self.root / "SDK with spaces/dotnet",
            "import json, os, sys\n"
            f"with open({str(records)!r}, 'a') as log: log.write(json.dumps({{'args': sys.argv[1:], 'cwd': os.getcwd(), 'root': os.environ.get('DOTNET_ROOT'), 'icd': os.environ.get('VK_ICD_FILENAMES')}}) + '\\n')\n"
            "print('10.0.401' if '--version' in sys.argv else 'Fixture SDK output')\n"
            + ("sys.exit(17 if 'build' in sys.argv else 0)\n" if fail else ""))
        return sdk, records

    def test_build_uses_explicit_sdk_linux_runtime_and_records_tests(self):
        repo = self.repository()
        (repo / "tools/tests").mkdir(parents=True)
        (repo / "tools/test_linux.py").write_text("print('fixture tooling checks')\n", encoding="utf-8")
        (repo / "tools/tests/test_fixture.py").write_text(
            "import unittest\nfrom pathlib import Path\nclass Fixture(unittest.TestCase):\n"
            "    def test_original_source(self):\n"
            "        self.assertEqual(Path('LICENSE').read_text(), 'Original fixture license\\n')\n", encoding="utf-8")
        sdk, records = self.fake_sdk()
        output = self.root / "build evidence"
        self.tools.build(repo, sdk, output, no_restore=True)
        receipt = json.loads((output / "build-receipt.json").read_text())
        self.assertEqual(receipt["status"], "passed")
        self.assertEqual(receipt["runtime"], "linux-x64")
        calls = [json.loads(line) for line in records.read_text().splitlines()]
        builds = [row for row in calls if row["args"][0] == "build"]
        self.assertEqual(len(builds), 8)
        for row in builds:
            self.assertEqual(row["cwd"], str(repo))
            self.assertEqual(row["root"], str(sdk.parent))
            self.assertIn("linux-x64", row["args"])
            self.assertIn("--no-restore", row["args"])
            self.assertEqual(row["args"][row["args"].index("--self-contained") + 1], "false")
            self.assertNotIn("dotnet/Sed.slnx", row["args"])
        tests = [row for row in calls if row["args"][0] == "test"]
        self.assertEqual(len(tests), 2)
        self.assertTrue(all("--no-build" in row["args"] and "linux-x64" in row["args"] for row in tests))
        probes = [row for row in calls if row["args"][0] == "run"]
        self.assertEqual(len(probes), 1)
        self.assertEqual(probes[0]["icd"], str(output / "no-driver.json"))

    def test_build_failure_stops_and_keeps_failed_receipt(self):
        repo = self.repository()
        sdk, records = self.fake_sdk(fail=True)
        output = self.root / "failed build evidence"
        with self.assertRaises(self.tools.ToolError):
            self.tools.build(repo, sdk, output)
        receipt = json.loads((output / "build-receipt.json").read_text())
        self.assertEqual(receipt["status"], "failed")
        calls = [json.loads(line) for line in records.read_text().splitlines()]
        self.assertFalse(any(row["args"][0] == "test" for row in calls))
        self.assertTrue((output / "build-editor.log").is_file())

    def test_license_inventory_copies_notices_for_the_resolved_linux_runtime(self):
        source, package = self.root / "source", self.root / "package"
        package.mkdir()
        cache = self.root / "nuget packages"
        dependency = cache / "microsoft.netcore.app.runtime.linux-x64/10.0.12"
        dependency.mkdir(parents=True)
        (dependency / "LICENSE.TXT").write_text("Original runtime fixture license", encoding="utf-8")
        (dependency / "THIRD-PARTY-NOTICES.TXT").write_text("Original runtime fixture notices", encoding="utf-8")
        (dependency / "microsoft.netcore.app.runtime.linux-x64.nuspec").write_text("<package />", encoding="utf-8")
        assets = source / "dotnet/src/Sed.App/obj/project.assets.json"
        assets.parent.mkdir(parents=True)
        assets.write_text(json.dumps({"packageFolders": {str(cache): {}}, "libraries": {}, "project": {
            "frameworks": {"net10.0": {"downloadDependencies": [{"name": "Microsoft.NETCore.App.Runtime.linux-x64",
                "version": "[10.0.12, 10.0.12]"}]}}}}), encoding="utf-8")
        inventory = self.tools.collect_licenses(source, package)
        self.assertEqual(inventory[0]["package"], "Microsoft.NETCore.App.Runtime.linux-x64/10.0.12")
        copied = package / "licenses/linux/Microsoft.NETCore.App.Runtime.linux-x64-10.0.12"
        self.assertEqual((copied / "LICENSE.TXT").read_text(), "Original runtime fixture license")
        self.assertTrue((copied / "THIRD-PARTY-NOTICES.TXT").is_file())

    def test_build_receipt_must_bind_same_source_sdk_runtime_and_success(self):
        repo = self.repository()
        state = self.tools.source_state(repo, require_clean=True)
        sdk, _ = self.fake_sdk()
        valid = {**state, "status": "passed", "runtime": "linux-x64", "configuration": "Release",
                 "dotnetSha256": self.tools.sha(sdk), "testProjects": ["Sed.Core.Tests", "Sed.Mcp.Tests"]}
        path = self.root / "build-receipt.json"
        path.write_text(json.dumps(valid), encoding="utf-8")
        self.tools.validate_build_receipt(path, state, sdk)
        for change in ({"commit": "0" * 40}, {"status": "failed"}, {"runtime": "win-x64"},
                       {"sourceClean": False}, {"dotnetSha256": "0" * 64}, {"testProjects": []}):
            with self.subTest(change=change):
                path.write_text(json.dumps(valid | change), encoding="utf-8")
                with self.assertRaises(self.tools.ToolError):
                    self.tools.validate_build_receipt(path, state, sdk)

    def test_default_mcp_client_uses_sibling_worker_and_captures_clean_stdio(self):
        server = self.executable(self.root / "mcp server",
            "import json, sys\n"
            "if len(sys.argv) != 3 or sys.argv[1] != '--workspace': sys.exit(19)\n"
            "sys.stderr.write('server diagnostics\\n')\n"
            "for line in sys.stdin:\n"
            "    request = json.loads(line)\n"
            "    if 'id' not in request: continue\n"
            "    result = {'serverInfo': {'name': 'sed-mcp', 'version': 'fixture'}} if request['method'] == 'initialize' else {'structuredContent': {'ok': True, 'result': {'received': request['params']}}, 'content': []}\n"
            "    print(json.dumps({'jsonrpc': '2.0', 'id': request['id'], 'result': result}), flush=True)\n")
        with self.tools.DefaultMcp(server, self.root, os.environ.copy(), self.root / "stderr.log") as client:
            result = client.tool("sed_discover", {"topic": "setGravity"})
            self.assertEqual(result["received"], {"name": "sed_discover", "arguments": {"topic": "setGravity"}})
        self.assertEqual((self.root / "stderr.log").read_text(), "server diagnostics\n")

    def test_default_mcp_client_rejects_stdout_diagnostics(self):
        server = self.executable(self.root / "broken mcp server", "print('unexpected stdout log', flush=True)\n")
        with self.assertRaises(self.tools.ToolError):
            with self.tools.DefaultMcp(server, self.root, os.environ.copy(), self.root / "stderr.log"):
                pass

    def test_history_bundle_has_exact_head_and_standalone_ancestry(self):
        repo = self.repository()
        state = self.tools.source_state(repo, require_clean=True)
        output = self.root / "history handoff"
        output.mkdir()
        bundle = self.tools.write_history(repo, state, output)
        self.assertEqual(self.git(repo, "bundle", "list-heads", str(bundle)), state["commit"] + " HEAD")
        clone = self.root / "history clone"
        subprocess.run(["git", "-c", "advice.detachedHead=false", "clone", "-q", str(bundle), str(clone)], check=True)
        self.assertEqual(self.git(clone, "rev-parse", "HEAD"), state["commit"])


if __name__ == "__main__":
    unittest.main()
