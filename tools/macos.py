#!/usr/bin/env python3
"""Build native SED-MotS Mac bundles and verify their exact distribution bytes."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import plistlib
import re
import shutil
import stat
import struct
import subprocess
import sys
import unicodedata
import zipfile

from acquire_macos_dependencies import safe_relative
from linux import (ToolError, assert_source, external_output, git, new_output,
                   run_logged, sha, snapshot_source, source_state, utc,
                   write_history, write_json)

ROOT = Path(__file__).resolve().parent.parent
TARGETS = {"osx-arm64": "arm64", "osx-x64": "x86_64"}
CPUS = {0x01000007: "osx-x64", 0x0100000C: "osx-arm64"}
PROJECTS = {
    "editor": ("dotnet/src/Sed.App/Sed.App.csproj", "Sed.App"),
    "agent": ("dotnet/tools/Sed.Agent/Sed.Agent.csproj", "sed-agent"),
    "mcp": ("dotnet/tools/Sed.Mcp/Sed.Mcp.csproj", "sed-mcp"),
    "scene": ("dotnet/tools/Sed.SceneProbe/Sed.SceneProbe.csproj", "Sed.SceneProbe"),
    "ui": ("dotnet/tools/Sed.UiProbe/Sed.UiProbe.csproj", "Sed.UiProbe"),
}
APP = Path("SED-MotS.app/Contents/MacOS")
LAUNCHERS = {"sed-mots": str(APP / "Sed.App"), "sed-agent": "agent/sed-agent", "sed-mcp": "mcp/sed-mcp"}


def require_host(rid, *, system=None, machine=None):
    if rid not in TARGETS or (system or platform.system()) != "Darwin" or (machine or platform.machine()) != TARGETS[rid]:
        raise ToolError(f"{rid} requires the matching native macOS process architecture; cross-publishing is not qualification")


def bundle_info(version):
    match = re.fullmatch(r"(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z.-]+)?", version)
    if not match:
        raise ToolError("Version must be a SemVer-style release without paths or whitespace")
    core = ".".join(match.groups())
    return {"CFBundleExecutable": "Sed.App", "CFBundleIdentifier": "org.codalilitari.sed-mots",
            "CFBundleName": "SED-MotS", "CFBundleDisplayName": "SED-MotS", "CFBundlePackageType": "APPL",
            "CFBundleShortVersionString": core, "CFBundleVersion": core, "SEDReleaseVersion": version,
            "LSMinimumSystemVersion": "15.0", "NSHighResolutionCapable": True}


def macho_architectures(data):
    if len(data) < 4:
        return set()
    magic = data[:4]
    if magic in (b"\xcf\xfa\xed\xfe", b"\xfe\xed\xfa\xcf"):
        if len(data) < 32:
            raise ToolError("Truncated 64-bit Mach-O header")
        cpu = struct.unpack_from("<I" if magic[0] == 0xCF else ">I", data, 4)[0]
        if cpu not in CPUS:
            raise ToolError(f"Unsupported Mach-O CPU: {cpu:x}")
        return {CPUS[cpu]}
    if magic in (b"\xca\xfe\xba\xbe", b"\xca\xfe\xba\xbf"):
        if len(data) < 8:
            raise ToolError("Truncated FAT Mach-O header")
        count = struct.unpack_from(">I", data, 4)[0]
        stride = 32 if magic[-1] == 0xBF else 20
        header_end = 8 + stride * count
        if not 1 <= count <= 8 or header_end > len(data):
            raise ToolError("Invalid FAT Mach-O slice table")
        result, ranges = set(), []
        for index in range(count):
            offset = 8 + index * stride
            cpu = struct.unpack_from(">I", data, offset)[0]
            start, size = struct.unpack_from(">QQ" if stride == 32 else ">II", data, offset + 8)
            if start < header_end or size < 32 or start + size > len(data) or any(start < b and a < start + size for a, b in ranges):
                raise ToolError("FAT Mach-O slices are out of bounds or overlap")
            architectures = macho_architectures(data[start:start + size])
            if cpu not in CPUS or architectures != {CPUS[cpu]} or CPUS[cpu] in result:
                raise ToolError("FAT Mach-O slice identity mismatch")
            ranges.append((start, start + size))
            result.update(architectures)
        return result
    return set()


def require_runtime(directory, name, rid):
    directory = Path(directory)
    for required in (name, name + ".dll", name + ".runtimeconfig.json", "libcoreclr.dylib",
                     "libhostfxr.dylib", "libhostpolicy.dylib", "System.Private.CoreLib.dll"):
        path = directory / required
        if not path.is_file() or path.is_symlink():
            raise ToolError(f"Missing regular self-contained runtime file: {path}")
        if required == name or required.endswith(".dylib"):
            if rid not in macho_architectures(path.read_bytes()):
                raise ToolError(f"Runtime binary does not contain {rid}: {path}")
    config = json.loads((directory / (name + ".runtimeconfig.json")).read_text())
    options = config.get("runtimeOptions", {})
    if not options.get("includedFrameworks") or options.get("framework") or options.get("frameworks"):
        raise ToolError("Runtime configuration must be self-contained")
    if os.name != "nt" and not os.access(directory / name, os.X_OK):
        raise ToolError("Packaged apphost is not executable")


def payload_files(root):
    result, canonical_names = {}, set()
    for path in sorted(Path(root).rglob("*")):
        info = path.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise ToolError(f"Payload cannot contain links or reparse points: {path}")
        if path.is_dir():
            continue
        if not stat.S_ISREG(info.st_mode):
            raise ToolError(f"Unsupported payload file type: {path}")
        name = safe_relative(path.relative_to(root).as_posix())
        canonical = unicodedata.normalize("NFC", name).casefold()
        if canonical in canonical_names:
            raise ToolError("Payload has case or Unicode aliases")
        canonical_names.add(canonical)
        result[name] = path
    return result


def write_payload_manifest(root):
    files = payload_files(root)
    if "PAYLOAD.json" in files:
        raise ToolError("A payload manifest already exists")
    write_json(Path(root) / "PAYLOAD.json", {"schemaVersion": 1, "files": [
        {"path": name, "bytes": path.stat().st_size, "sha256": sha(path), "mode": stat.S_IMODE(path.stat().st_mode)}
        for name, path in files.items()]})


def verify_payload_manifest(root):
    root = Path(root)
    data = json.loads((root / "PAYLOAD.json").read_text(encoding="utf-8"))
    if data.get("schemaVersion") != 1:
        raise ToolError("Unknown payload manifest")
    expected, canonical_names = {}, set()
    for row in data.get("files", []):
        name = safe_relative(row["path"])
        canonical = unicodedata.normalize("NFC", name).casefold()
        if canonical in canonical_names or name == "PAYLOAD.json":
            raise ToolError("Duplicate, aliased or self-referencing payload member")
        expected[name] = row
        canonical_names.add(canonical)
    actual = payload_files(root)
    actual.pop("PAYLOAD.json", None)
    if not expected or expected.keys() != actual.keys():
        raise ToolError("Payload membership differs from the manifest")
    for name, path in actual.items():
        row = expected[name]
        if path.stat().st_size != row["bytes"] or sha(path) != row["sha256"]:
            raise ToolError(f"Payload content mismatch: {name}")
        if os.name != "nt" and stat.S_IMODE(path.stat().st_mode) != row["mode"]:
            raise ToolError(f"Payload permission mismatch: {name}")
    return len(actual)


def write_launchers(package):
    directory = Path(package) / "bin"
    directory.mkdir(parents=True, exist_ok=True)
    for name, target in LAUNCHERS.items():
        script = ('#!/bin/sh\nset -eu\nscript=$0\ncount=0\n'
                  'while [ -L "$script" ]; do\n'
                  '  count=$((count + 1)); [ "$count" -le 40 ] || exit 70\n'
                  '  parent=$(CDPATH= cd -- "$(dirname -- "$script")" && pwd -P)\n'
                  '  link=$(readlink "$script")\n'
                  '  case "$link" in /*) script=$link ;; *) script=$parent/$link ;; esac\n'
                  'done\nroot=$(CDPATH= cd -- "$(dirname -- "$script")/.." && pwd -P)\n'
                  f'exec "$root/{target}" "$@"\n')
        path = directory / name
        with path.open("x", encoding="utf-8", newline="\n") as stream:
            stream.write(script)
        path.chmod(0o755)


def archive_package(package, destination):
    package, destination = Path(package), Path(destination)
    verify_payload_manifest(package)
    with zipfile.ZipFile(destination, "x", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for name, path in payload_files(package).items():
            entry = zipfile.ZipInfo(package.name + "/" + name, date_time=(2026, 1, 1, 0, 0, 0))
            entry.create_system = 3
            entry.external_attr = (stat.S_IFREG | stat.S_IMODE(path.stat().st_mode)) << 16
            entry.compress_type = zipfile.ZIP_DEFLATED
            with path.open("rb") as source, archive.open(entry, "w") as target:
                shutil.copyfileobj(source, target, length=1024 * 1024)


def archive_index(archive):
    entries, roots, aliases, total = {}, set(), set(), 0
    for entry in archive.infolist():
        name = safe_relative(entry.orig_filename)
        if name != entry.filename:
            raise ToolError("Archive names must not rely on host normalization")
        parts = name.split("/")
        mode = entry.external_attr >> 16
        alias = unicodedata.normalize("NFC", name).casefold()
        if len(parts) < 2 or alias in aliases or entry.is_dir() or stat.S_IFMT(mode) not in (0, stat.S_IFREG):
            raise ToolError(f"Unsafe or duplicate archive member: {name}")
        roots.add(parts[0]); aliases.add(alias)
        total += entry.file_size
        if len(entries) >= 20000 or total > 2 * 1024**3 or entry.file_size > 256 * 1024**2:
            raise ToolError("Archive exceeds distribution bounds")
        entries[name] = entry
    if len(roots) != 1:
        raise ToolError("Archive must have exactly one package root")
    if next(iter(roots)) + "/PAYLOAD.json" not in entries:
        raise ToolError("Archive has no payload manifest")
    for name in entries:
        if any("/".join(name.split("/")[:i]) in entries for i in range(1, len(name.split("/")))):
            raise ToolError("Archive file shadows a parent directory")
    return next(iter(roots)), entries


def verify_archive(archive_path, package):
    package = Path(package)
    expected = {package.name + "/" + name: path for name, path in payload_files(package).items()}
    with zipfile.ZipFile(archive_path) as archive:
        _, entries = archive_index(archive)
        if entries.keys() != expected.keys():
            raise ToolError("Archive membership differs from the package")
        for name, entry in entries.items():
            digest = hashlib.sha256()
            with archive.open(entry) as source:
                for block in iter(lambda: source.read(1024 * 1024), b""):
                    digest.update(block)
            path = expected[name]
            if entry.file_size != path.stat().st_size or digest.hexdigest() != sha(path):
                raise ToolError(f"Archive content mismatch: {name}")
            if stat.S_IMODE(entry.external_attr >> 16) != stat.S_IMODE(path.stat().st_mode):
                raise ToolError(f"Archive permission mismatch: {name}")
    return len(expected)


def extract_package(archive_path, output):
    with zipfile.ZipFile(archive_path) as archive:
        root_name, entries = archive_index(archive)
        output = new_output(output)
        for name, entry in entries.items():
            target = output / name
            target.parent.mkdir(parents=True, exist_ok=True)
            with target.open("xb") as destination, archive.open(entry) as source:
                shutil.copyfileobj(source, destination, length=1024 * 1024)
            target.chmod(stat.S_IMODE(entry.external_attr >> 16))
    package = output / root_name
    verify_payload_manifest(package)
    return package


def pinned_dependencies(manifest_path, source):
    manifest_path = external_output(manifest_path)
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    lock = json.loads((source / "tools/macos/dependency-lock.json").read_text())
    expected = {key: lock[key] for key in ("schemaVersion", "version", "upstreamCommit", "artifact")}
    expected["library"] = {key: value for key, value in lock["library"].items() if key != "member"}
    expected["notices"] = [{key: value for key, value in row.items() if key != "member"} for row in lock["notices"]]
    if manifest != expected:
        raise ToolError("MoltenVK manifest does not match this source commit's dependency lock")
    for row in [manifest["artifact"], manifest["library"], *manifest["notices"]]:
        path = manifest_path.parent / safe_relative(row["path"])
        external_output(path)  # Refuse symlink/reparse parents as well as the leaf.
        if not path.is_file() or path.stat().st_size != row["bytes"] or sha(path) != row["sha256"]:
            raise ToolError(f"Dependency file changed: {row['path']}")
    return manifest, manifest_path.parent


def capture(arguments, *, timeout=30):
    result = subprocess.run([str(a) for a in arguments], capture_output=True, text=True, timeout=timeout)
    if result.returncode:
        raise ToolError(f"Native tool failed: {arguments[0]}: {result.stderr[-2000:]}")
    return result.stdout


def inspect_native_closure(root, rid):
    root = Path(root)
    records = []
    for name, path in payload_files(root).items():
        with path.open("rb") as stream:
            magic = stream.read(4)
        if magic not in (b"\xcf\xfa\xed\xfe", b"\xfe\xed\xfa\xcf", b"\xca\xfe\xba\xbe", b"\xca\xfe\xba\xbf"):
            continue
        architectures = macho_architectures(path.read_bytes())
        if rid not in architectures:
            raise ToolError(f"Native payload lacks requested architecture: {name}")
        libraries = capture(["/usr/bin/otool", "-arch", TARGETS[rid], "-L", path]).splitlines()[1:]
        loads = capture(["/usr/bin/otool", "-arch", TARGETS[rid], "-l", path])
        rpaths = re.findall(r"cmd LC_RPATH\s+cmdsize \d+\s+path (.*?) \(offset", loads)
        if any(value.startswith(("/opt/", "/usr/local/", "/Users/", "/private/")) for value in rpaths):
            raise ToolError(f"Build-machine runtime search path leaked into {name}")
        dependencies = []
        for line in libraries:
            dependency = line.strip().split(" (compatibility version", 1)[0]
            if not dependency:
                continue
            dependencies.append(dependency)
            if dependency.startswith(("/System/Library/", "/usr/lib/")):
                continue
            # otool lists a dylib's own install ID first.
            if dependency.startswith("@rpath/") and dependency[7:] == path.name:
                continue
            candidates = []
            if dependency.startswith("@loader_path/"):
                candidates.append(path.parent / dependency[len("@loader_path/"):])
            elif dependency.startswith("@executable_path/"):
                candidates.append(path.parent / dependency[len("@executable_path/"):])
            elif dependency.startswith("@rpath/"):
                for search in rpaths:
                    search = search.replace("@loader_path", str(path.parent)).replace("@executable_path", str(path.parent))
                    candidates.append(Path(search) / dependency[len("@rpath/"):])
                # .NET apphosts carry their runtime next to the executable.
                candidates.append(path.parent / dependency[len("@rpath/"):])
            if not any(candidate.resolve().is_relative_to(root.resolve()) and candidate.is_file() for candidate in candidates):
                raise ToolError(f"Unclosed native dependency in {name}: {dependency}")
        records.append({"path": name, "architectures": sorted(architectures), "dependencies": dependencies, "rpaths": rpaths})
    if not records:
        raise ToolError("No native Mach-O payload was found")
    return records


def sign_payload(package, records, source, *, app_bundle=True):
    entitlements = source / "tools/macos/entitlements.plist"
    for record in sorted(records, key=lambda row: (not row["path"].endswith(".dylib"), row["path"])):
        path = package / record["path"]
        arguments = ["/usr/bin/codesign", "--force", "--sign", "-"]
        if not path.name.endswith(".dylib"):
            arguments += ["--entitlements", entitlements]
        capture([*arguments, path], timeout=60)
    if app_bundle:
        capture(["/usr/bin/codesign", "--force", "--sign", "-", "--entitlements", entitlements, package / "SED-MotS.app"], timeout=60)
    for record in records:
        capture(["/usr/bin/codesign", "--verify", "--strict", package / record["path"]], timeout=30)
    if app_bundle:
        capture(["/usr/bin/codesign", "--verify", "--deep", "--strict", package / "SED-MotS.app"], timeout=60)
    return {"mode": "ad-hoc", "verifiedNativeFiles": len(records), "bundleVerified": app_bundle, "notarized": False}


def copy_support(source, package):
    included = {"LICENSE", "NOTICE.md", "README.md", "AGENTS.md", ".agents", "docs", "licenses", "data", "tools"}
    for path in source.rglob("*"):
        relative = path.relative_to(source)
        if relative.parts[0] not in included or not path.is_file():
            continue
        destinations = [package / relative]
        if relative.parts[0] == "data":
            destinations += [package / component / relative for component in (APP, Path("agent"), Path("mcp"))]
        # The GUI's tools/help/templates remain available when the app is moved alone.
        if relative.parts[0] in {"docs", "tools", ".agents"}:
            destinations.append(package / APP / relative)
        for target in destinations:
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(path, target)
    shutil.copy2(source / "docs/MACOS_QUICKSTART.md", package / "START-HERE.md")


def package_release(repo, rid, dotnet, pwsh, manifest, version, output):
    require_host(rid)
    info = bundle_info(version)
    dotnet, pwsh = Path(dotnet).resolve(strict=True), Path(pwsh).resolve(strict=True)
    if capture([dotnet, "--version"]).strip() != "10.0.401":
        raise ToolError("The reviewed Mac build requires .NET SDK 10.0.401")
    state = source_state(repo, require_clean=True)
    output = new_output(output)
    package = new_output(output / f"SED-MotS-{version}-{rid}")
    receipt = {**state, "status": "running", "startedUtc": utc(), "rid": rid, "version": version,
               "host": {"system": platform.system(), "machine": platform.machine(), "macOS": platform.mac_ver()[0]},
               "dotnetSha256": sha(dotnet), "nativeRuntimeQualification": "pending"}
    try:
        source = snapshot_source(repo, state, output)
        original = {name: sha(path) for name, path in payload_files(source).items()}
        dependencies, dependency_root = pinned_dependencies(manifest, source)
        env = os.environ.copy()
        for key, directory in {"DOTNET_CLI_HOME": "dotnet-home", "NUGET_PACKAGES": "nuget",
                               "NUGET_HTTP_CACHE_PATH": "nuget-http", "NUGET_SCRATCH": "nuget-scratch",
                               "TMPDIR": "temp", "TMP": "temp", "TEMP": "temp"}.items():
            target = output / directory
            target.mkdir(exist_ok=True)
            env[key] = str(target)
        env.update(DOTNET_ROOT=str(dotnet.parent), DOTNET_CLI_TELEMETRY_OPTOUT="1",
                   DOTNET_ADD_GLOBAL_TOOLS_TO_PATH="false", DOTNET_GENERATE_ASPNET_CERTIFICATE="false",
                   MSBUILDDISABLENODEREUSE="1", PYTHONDONTWRITEBYTECODE="1",
                   SOURCE_DATE_EPOCH=str(state["epoch"]))
        env["PATH"] = str(pwsh.parent) + os.pathsep + env.get("PATH", "")
        artifacts = output / "artifacts"
        properties = ["-m:1", "-nr:false", "-p:BuildInParallel=false", "-p:UseSharedCompilation=false",
                      f"-p:ArtifactsPath={artifacts}"]
        for project in ("Sed.Core.Tests", "Sed.Mcp.Tests"):
            path = source / f"dotnet/tests/{project}/{project}.csproj"
            run_logged([dotnet, "test", path, "-c", "Release", "-r", rid, "-p:SelfContained=false",
                        "--logger", f"trx;LogFileName={project}.trx", "--results-directory", output,
                        *properties], source, output / f"{project}.log", env, timeout=1800)
        run_logged([sys.executable, "-B", "-m", "unittest", "discover", "-s", "tools/tests", "-v"],
                   source, output / "python-tests.log", env, timeout=600)
        for component, (project, apphost) in PROJECTS.items():
            destination = (package / APP if component == "editor" else package / component
                           if component in ("agent", "mcp") else output / "qualification-tools" / component)
            run_logged([dotnet, "publish", project, "-c", "Release", "-r", rid, "--self-contained", "true",
                        "-p:PublishTrimmed=false", "-p:UseAppHost=true", "-p:Deterministic=true",
                        "-p:ContinuousIntegrationBuild=true", f"-p:Version={version}",
                        f"-p:SourceRevisionId={state['commit']}", f"-p:PathMap={source}=/_/",
                        "-o", destination, *properties, *(["-p:RestoreLockedMode=true"] if component == "mcp" else [])],
                       source, output / f"publish-{component}.log", env, timeout=1800)
            require_runtime(destination, apphost, rid)
        copy_support(source, package)
        run_logged([pwsh, "-NoLogo", "-NoProfile", "-NonInteractive", "-File", source / "tools/Collect-NetNotices.ps1",
                    "-DotNet", dotnet, "-ArtifactsPath", artifacts, "-OutputDirectory", package / "licenses/resolved-net"],
                   source, output / "notices.log", env, timeout=600)
        library = dependency_root / dependencies["library"]["path"]
        for destination in (package / APP, package / "agent", output / "qualification-tools/scene"):
            shutil.copy2(library, destination / "libMoltenVK.dylib")
        for row in dependencies["notices"]:
            target = package / row["destination"]
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(dependency_root / row["path"], target)
        resources = package / "SED-MotS.app/Contents/Resources"
        resources.mkdir(parents=True)
        shutil.copytree(package / "licenses", resources / "licenses")
        for name in ("LICENSE", "NOTICE.md", "START-HERE.md"):
            shutil.copy2(package / name, resources / name)
        (package / "SED-MotS.app/Contents/Info.plist").write_bytes(plistlib.dumps(info))
        write_launchers(package)
        receipt["nativeClosure"] = inspect_native_closure(package, rid)
        receipt["signing"] = sign_payload(package, receipt["nativeClosure"], source)
        qualification = output / "qualification-tools"
        receipt["qualificationSigning"] = sign_payload(qualification, inspect_native_closure(qualification, rid), source,
                                                       app_bundle=False)
        run_logged([qualification / "ui/Sed.UiProbe"], source, output / "headless-ui.log", env, timeout=180)
        history = write_history(repo, state, output)
        provenance = {"schemaVersion": 1, "product": "SED-MotS", "commit": state["commit"], "version": version,
                      "rid": rid, "minimumMacOS": "15.0", "selfContained": True, "signing": receipt["signing"],
                      "sourceArchive": "SED-MotS-source.tar.gz", "sourceArchiveSha256": sha(output / "SED-MotS-source.tar.gz"),
                      "historyBundle": history.name, "historyBundleSha256": sha(history), "moltenVK": dependencies,
                      "automaticUpdater": "Windows-only", "nativeCppWorker": "not-included",
                      "gameAssetsBundled": False, "nativeRuntimeQualification": "separate receipt required"}
        write_json(package / "PROVENANCE.json", provenance)
        if {name: sha(path) for name, path in payload_files(source).items()} != original:
            raise ToolError("Build or notice collection modified the committed source snapshot")
        assert_source(repo, state)
        write_payload_manifest(package)
        archive = output / (package.name + ".zip")
        archive_package(package, archive)
        receipt["archiveMembers"] = verify_archive(archive, package)
        artifacts_to_hash = (archive, output / "SED-MotS-source.tar.gz", history)
        (output / "SHA256SUMS.txt").write_text("".join(f"{sha(path)}  {path.name}\n" for path in artifacts_to_hash), encoding="utf-8")
        receipt.update(status="passed", package=package.name, archive=archive.name, archiveSha256=sha(archive))
    except Exception as error:
        receipt.update(status="failed", error=str(error))
        raise
    finally:
        receipt["finishedUtc"] = utc()
        write_json(output / "package-receipt.json", receipt)
    return output / "package-receipt.json"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    package = commands.add_parser("package")
    package.add_argument("--rid", required=True, choices=TARGETS)
    for name in ("dotnet", "pwsh", "moltenvk-manifest", "output"):
        package.add_argument("--" + name, required=True, type=Path)
    package.add_argument("--version", required=True)
    verify = commands.add_parser("verify")
    verify.add_argument("--package", required=True, type=Path)
    verify.add_argument("--output", required=True, type=Path)
    verify.add_argument("--graphics", action="store_true")
    verify.add_argument("--full-workflows", action="store_true")
    verify.add_argument("--pwsh", required=True, type=Path)
    verify.add_argument("--scene-probe", type=Path)
    args = parser.parse_args()
    try:
        if args.command == "package":
            print(package_release(ROOT, args.rid, args.dotnet, args.pwsh, args.moltenvk_manifest, args.version, args.output))
        else:
            from test_macos_runtime import verify_runtime
            print(verify_runtime(args.package, args.output, args.pwsh, args.graphics, args.full_workflows, args.scene_probe))
    except (ToolError, OSError, ValueError, subprocess.SubprocessError) as error:
        print(f"Mac distribution operation failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
