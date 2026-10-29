#!/usr/bin/env python3
"""Build, package, verify and install the native Linux SED MotS distribution."""
from __future__ import annotations

import argparse
import base64
from datetime import datetime, timezone
import gzip
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import queue
import re
import shutil
import stat
import subprocess
import sys
import tarfile
import threading
import uuid


ROOT = Path(__file__).resolve().parent.parent
RID = "linux-x64"
LAUNCHERS = {"sed-mots": "Sed.App", "sed-agent": "agent/sed-agent", "sed-mcp": "mcp/sed-mcp"}
PROJECTS = {
    "editor": "dotnet/src/Sed.App/Sed.App.csproj",
    "agent": "dotnet/tools/Sed.Agent/Sed.Agent.csproj",
    "mcp": "dotnet/tools/Sed.Mcp/Sed.Mcp.csproj",
    "core-tests": "dotnet/tests/Sed.Core.Tests/Sed.Core.Tests.csproj",
    "mcp-tests": "dotnet/tests/Sed.Mcp.Tests/Sed.Mcp.Tests.csproj",
    "ui": "dotnet/tools/Sed.UiProbe/Sed.UiProbe.csproj",
    "appshot": "dotnet/tools/Sed.AppShot/Sed.AppShot.csproj",
    "vulkan": "dotnet/tools/Sed.VulkanSmoke/Sed.VulkanSmoke.csproj",
}


class ToolError(RuntimeError):
    pass


def utc():
    return datetime.now(timezone.utc).isoformat()


def sha(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def write_json(path, value):
    Path(path).write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def git(repo, *args):
    result = subprocess.run(["git", "-C", str(repo), *args], capture_output=True)
    if result.returncode:
        raise ToolError(result.stderr.decode(errors="replace").strip() or "Git command failed")
    return result.stdout


def source_state(repo, require_clean=False):
    commit = git(repo, "rev-parse", "HEAD").decode().strip()
    if not re.fullmatch(r"[0-9a-f]{40}|[0-9a-f]{64}", commit):
        raise ToolError("Cannot identify the source commit")
    clean = not git(repo, "status", "--porcelain", "--untracked-files=normal")
    if require_clean and not clean:
        raise ToolError("Commit the reviewed source before packaging; the source worktree must be clean")
    return {"commit": commit, "sourceClean": clean,
            "epoch": int(git(repo, "show", "-s", "--format=%ct", commit))}


def assert_source(repo, expected):
    actual = source_state(repo, require_clean=True)
    if actual["commit"] != expected["commit"]:
        raise ToolError("Source commit changed during packaging; this attempt cannot be distributed")


def new_output(path):
    path = Path(path).absolute()
    try:
        path.mkdir(parents=True, exist_ok=False)
    except FileExistsError as error:
        raise ToolError(f"Choose a new output directory; existing output is never replaced: {path}") from error
    return path


def safe_relative(value):
    path = PurePosixPath(value)
    if not value or path.is_absolute() or ".." in path.parts or any(c in value for c in "\r\n\\"):
        raise ToolError(f"Unsupported distribution path: {value!r}")
    return path


def snapshot_source(repo, state, output):
    # The compiler receives the same committed files as the corresponding source
    # archive, not ignored/untracked files admitted by project glob patterns.
    tracked = {}
    for entry in git(repo, "ls-tree", "-r", "-z", state["commit"]).split(b"\0"):
        if not entry:
            continue
        metadata, name = entry.split(b"\t", 1)
        relative = safe_relative(name.decode()).as_posix()
        mode, _, object_id = metadata.split()
        if mode not in (b"100644", b"100755"):
            raise ToolError(f"Source archive cannot contain symlinks or submodules: {name.decode()}")
        tracked[relative] = object_id.decode()
    # Git archive applies checkout EOL rules. Pin Linux settings without
    # modifying user configuration, then verify bytes against the committed
    # blobs so export-subst, filters or export-ignore cannot alter build inputs.
    raw = git(repo, "-c", "core.autocrlf=false", "-c", "core.eol=lf", "archive", "--format=tar", state["commit"])
    archive_path = output / "SED-MotS-source.tar.gz"
    with archive_path.open("xb") as destination, gzip.GzipFile(filename="", mode="wb", fileobj=destination,
                                                               mtime=state["epoch"]) as compressed:
        compressed.write(raw)
    source = new_output(output / ".source")
    seen = set()
    with tarfile.open(fileobj=io.BytesIO(raw), mode="r:") as archive:
        for member in archive:
            relative = safe_relative(member.name)
            target = source / relative
            if member.isdir():
                target.mkdir(parents=True, exist_ok=True)
            elif member.isfile():
                name = relative.as_posix()
                if name not in tracked or name in seen:
                    raise ToolError(f"Source archive has an unexpected or duplicate tracked file: {name}")
                with archive.extractfile(member) as incoming:
                    payload = incoming.read()
                object_hash = hashlib.new("sha1" if len(tracked[name]) == 40 else "sha256")
                object_hash.update(f"blob {len(payload)}\0".encode("ascii"))
                object_hash.update(payload)
                if object_hash.hexdigest() != tracked[name]:
                    raise ToolError(f"Source archive transformed a committed Git blob; check export attributes or filters: {name}")
                seen.add(name)
                target.parent.mkdir(parents=True, exist_ok=True)
                with target.open("xb") as destination:
                    destination.write(payload)
                target.chmod(member.mode & 0o777)
                os.utime(target, (state["epoch"], state["epoch"]))
            else:
                raise ToolError(f"Unsupported source archive entry: {member.name}")
    if seen != tracked.keys():
        raise ToolError("Source archive omitted committed Git inputs; remove export-ignore from package source")
    return source


def copy_support(source, package):
    for path in sorted(source.rglob("*")):
        relative = path.relative_to(source)
        if relative.parts[0] not in ("LICENSE", "README.md", "AGENTS.md", ".agents", "docs", "licenses", "data", "tools"):
            continue
        if path.is_symlink():
            raise ToolError(f"Support files cannot be symlinks: {relative}")
        if not path.is_file():
            continue
        destinations = [package / relative]
        if relative.parts[0] == "data":
            destinations += [package / component / relative for component in ("agent", "mcp")]
        for destination in destinations:
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(path, destination)
    shutil.copy2(source / "docs/LINUX_QUICKSTART.md", package / "START-HERE.md")


def write_launchers(package):
    (package / "bin").mkdir(exist_ok=True)
    for name, target in LAUNCHERS.items():
        path = package / "bin" / name
        with path.open("x", encoding="utf-8") as stream:
            stream.write('#!/bin/sh\nset -eu\n'
                         'script=$(readlink -f -- "$0")\n'
                         'root=$(CDPATH= cd -- "$(dirname -- "$script")/.." && pwd -P)\n'
                         f'exec "$root/{target}" "$@"\n')
        path.chmod(0o755)


def payload_files(root):
    files = []
    for path in sorted(root.rglob("*")):
        if path.is_symlink():
            raise ToolError(f"Package payload cannot contain symlinks: {path}")
        if path.is_file():
            safe_relative(path.relative_to(root).as_posix())
            files.append(path)
    return files


def write_manifest(root):
    lines = [f"{sha(path)}  {path.relative_to(root).as_posix()}\n" for path in payload_files(root)
             if path != root / "SHA256SUMS.txt"]
    (root / "SHA256SUMS.txt").write_text("".join(lines), encoding="utf-8")


def verify_manifest(root):
    expected = {}
    for line in (root / "SHA256SUMS.txt").read_text(encoding="utf-8").splitlines():
        if not re.fullmatch(r"[0-9a-f]{64}  .+", line):
            raise ToolError("Malformed SHA256SUMS.txt entry")
        digest, name = line.split("  ", 1)
        safe_relative(name)
        if name in expected or name == "SHA256SUMS.txt":
            raise ToolError(f"Duplicate or self-referencing checksum entry: {name}")
        expected[name] = digest
    actual = {path.relative_to(root).as_posix(): path for path in payload_files(root)
              if path != root / "SHA256SUMS.txt"}
    if expected.keys() != actual.keys():
        raise ToolError("Package file set does not match SHA256SUMS.txt")
    for name, path in actual.items():
        if sha(path) != expected[name]:
            raise ToolError(f"Package checksum failed: {name}")
    return len(actual)


def deduplicate(root):
    seen, saved = {}, 0
    for path in payload_files(root):
        info = path.stat()
        key = (info.st_size, stat.S_IMODE(info.st_mode), sha(path))
        if key not in seen:
            seen[key] = path
        elif info.st_ino != seen[key].stat().st_ino:
            temporary = path.with_name(path.name + ".hardlink-" + uuid.uuid4().hex)
            os.link(seen[key], temporary)
            os.replace(temporary, path)
            saved += info.st_size
    return saved


def archive_package(package, destination, epoch):
    def normalize(info):
        info.uid = info.gid = 0
        info.uname = info.gname = ""
        info.mtime = epoch
        info.pax_headers = {}
        return info
    with destination.open("xb") as file, gzip.GzipFile(filename="", mode="wb", fileobj=file, mtime=epoch) as compressed:
        with tarfile.open(fileobj=compressed, mode="w|", format=tarfile.PAX_FORMAT, dereference=False) as archive:
            for path in [package, *sorted(package.rglob("*"))]:
                archive.add(path, arcname=path.relative_to(package.parent).as_posix(), recursive=False, filter=normalize)


def desktop_exec(path):
    value = str(path)
    if any(c in value for c in "\r\n\0"):
        raise ToolError("Desktop executable paths cannot contain line breaks or NUL")
    # Desktop Entry string escaping is applied after Exec argument quoting;
    # literal percent signs must not become field codes.
    quoted = '"' + re.sub(r'([\\"`$])', r'\\\1', value.replace("%", "%%")) + '"'
    return quoted.replace("\\", "\\\\")


def install_desktop(package, applications, binaries):
    package = Path(package).resolve(strict=True)
    applications, binaries = Path(applications).absolute(), Path(binaries).absolute()
    if not (package / "Sed.App").is_file() or not all((package / "bin" / name).is_file() for name in LAUNCHERS):
        raise ToolError("--package must identify an extracted Linux package with its launchers")
    desktop = applications / "sed-mots.desktop"
    targets = [desktop, *(binaries / name for name in LAUNCHERS)]
    if any(path.exists() or path.is_symlink() for path in targets):
        raise ToolError("Desktop entry or user commands already exist; remove the previous integration explicitly first")
    content = ("[Desktop Entry]\nType=Application\nName=SED MotS\n"
               "Comment=Jedi Knight and Mysteries of the Sith level editor\n"
               f"Exec={desktop_exec(package / 'bin/sed-mots')} %f\n"
               "Icon=applications-graphics\nTerminal=false\nCategories=Graphics;Development;\n")
    applications.mkdir(parents=True, exist_ok=True)
    binaries.mkdir(parents=True, exist_ok=True)
    created = []
    try:
        with desktop.open("x", encoding="utf-8") as stream:
            stream.write(content)
        created.append(desktop)
        for name in LAUNCHERS:
            link = binaries / name
            link.symlink_to(package / "bin" / name)
            created.append(link)
    except OSError:
        for path in reversed(created):
            path.unlink()
        raise
    return desktop


def resolve_dotnet(value):
    candidate = shutil.which(str(value)) if "/" not in str(value) else str(Path(value).absolute())
    if not candidate or not Path(candidate).is_file() or not os.access(candidate, os.X_OK):
        raise ToolError("Install the SDK from global.json or pass --dotnet /absolute/path/to/dotnet")
    return Path(candidate).resolve()


def sdk_environment(dotnet):
    return os.environ | {"DOTNET_ROOT": str(dotnet.parent), "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
                         "DOTNET_GENERATE_ASPNET_CERTIFICATE": "false", "MSBUILDDISABLENODEREUSE": "1",
                         "PYTHONDONTWRITEBYTECODE": "1"}


def run_logged(arguments, cwd, log, env=None, timeout=None):
    print(f"Running {Path(str(arguments[0])).name} {arguments[1]} (log: {log})", file=sys.stderr, flush=True)
    with Path(log).open("wb") as stream:
        try:
            result = subprocess.run([str(value) for value in arguments], cwd=cwd, env=env,
                                    stdout=stream, stderr=subprocess.STDOUT, timeout=timeout)
        except subprocess.TimeoutExpired as error:
            raise ToolError(f"Command timed out; see {log}") from error
    if result.returncode:
        raise ToolError(f"Command exited {result.returncode}; see {log}")


def build(repo, dotnet, output, configuration="Release", no_restore=False, graphics=False, source=None):
    output = new_output(output)
    dotnet = resolve_dotnet(dotnet)
    env = sdk_environment(dotnet)
    receipt = {**(source or source_state(repo)), "status": "running", "startedUtc": utc(), "runtime": RID,
               "configuration": configuration, "dotnetSha256": sha(dotnet),
               "testProjects": ["Sed.Core.Tests", "Sed.Mcp.Tests"], "graphicsRequested": graphics,
               "guiLaunched": False, "gameLaunched": False}
    try:
        run_logged([dotnet, "--info"], repo, output / "sdk-info.log", env)
        for name, project in PROJECTS.items():
            arguments = [dotnet, "build", project, "-c", configuration, "-r", RID, "--self-contained", "false",
                         "--nologo", "--disable-build-servers", "-m:1", "-nodeReuse:false",
                         "-p:BuildInParallel=false", "-p:UseSharedCompilation=false"]
            if no_restore:
                arguments.append("--no-restore")
            run_logged(arguments, repo, output / f"build-{name}.log", env)
        for name in ("core-tests", "mcp-tests"):
            run_logged([dotnet, "test", PROJECTS[name], "-c", configuration, "-r", RID, "-p:SelfContained=false",
                        "--no-build", "--no-restore", "--nologo", "--logger", f"trx;LogFileName={name}.trx",
                        "--results-directory", output], repo, output / f"{name}.log", env)
        run_logged([sys.executable, "-B", "tools/test_linux.py"], repo, output / "linux-tools-tests.log", env)
        run_logged([sys.executable, "-B", "-m", "unittest", "discover", "-s", "tools/tests", "-v"],
                   repo, output / "python-tests.log", env)
        headless = env.copy()
        headless.pop("DISPLAY", None)
        headless.pop("WAYLAND_DISPLAY", None)
        if not graphics:
            headless.update(VK_ICD_FILENAMES=str(output / "no-driver.json"),
                            VK_DRIVER_FILES=str(output / "no-driver.json"))
        for name in (["ui", "vulkan", "appshot"] if graphics else ["ui"]):
            arguments = [dotnet, "run", "--project", PROJECTS[name], "-c", configuration, "-r", RID,
                         "--self-contained", "false", "--no-build", "--no-restore"]
            if name == "appshot":
                arguments += ["--", output / "appshot.png"]
            run_logged(arguments, repo, output / f"{name}.log", headless, timeout=180)
        receipt["status"] = "passed"
    except Exception as error:
        receipt.update(status="failed", error=str(error))
        raise
    finally:
        receipt["finishedUtc"] = utc()
        write_json(output / "build-receipt.json", receipt)
    return output / "build-receipt.json"


def validate_build_receipt(path, source, dotnet):
    receipt = json.loads(Path(path).read_text(encoding="utf-8"))
    expected = {"status": "passed", "commit": source["commit"], "sourceClean": True, "runtime": RID,
                "configuration": "Release", "dotnetSha256": sha(dotnet),
                "testProjects": ["Sed.Core.Tests", "Sed.Mcp.Tests"]}
    if any(receipt.get(key) != value for key, value in expected.items()):
        raise ToolError("Build receipt must confirm passing Linux Release tests for this clean commit and SDK")
    return receipt


def collect_licenses(source, package):
    inventory = {}
    for name in ("editor", "agent", "mcp"):
        assets = source / Path(PROJECTS[name]).parent / "obj/project.assets.json"
        if not assets.is_file():
            continue
        metadata = json.loads(assets.read_text(encoding="utf-8"))
        libraries = dict(metadata.get("libraries", {}))
        # SDK runtime packs are downloadDependencies, not NuGet libraries.
        for framework in metadata.get("project", {}).get("frameworks", {}).values():
            for dependency in framework.get("downloadDependencies", []):
                match = re.fullmatch(r"\[([^,]+),\s*\1\]", dependency["version"])
                if not match:
                    raise ToolError(f"Runtime pack version is not pinned: {dependency}")
                identity = dependency["name"] + "/" + match.group(1)
                libraries[identity] = {"type": "package", "path": identity.lower()}
        for identity, library in libraries.items():
            if library.get("type") != "package" or identity in inventory:
                continue
            relative = safe_relative(library.get("path", identity.lower()))
            folders = [Path(folder) / relative for folder in metadata.get("packageFolders", {})]
            directory = next((folder for folder in folders if folder.is_dir()), None)
            if directory is None:
                raise ToolError(f"Resolved dependency is absent from its package cache: {identity}")
            destination = package / "licenses/linux" / identity.replace("/", "-")
            destination.mkdir(parents=True, exist_ok=True)
            notices = []
            for file in sorted(directory.iterdir()):
                if file.is_file() and (file.suffix.lower() == ".nuspec" or
                        re.match(r"(?i)(licen[cs]e|notice|copying|third.?party)", file.name)):
                    shutil.copy2(file, destination / file.name)
                    notices.append({"path": (destination / file.name).relative_to(package).as_posix(), "sha256": sha(file)})
            if not notices:
                raise ToolError(f"No package metadata or license notice found for {identity}")
            inventory[identity] = {"package": identity, "files": notices}
    result = [inventory[key] for key in sorted(inventory)]
    (package / "licenses/linux").mkdir(parents=True, exist_ok=True)
    write_json(package / "licenses/linux/inventory.json", result)
    return result


def write_history(repo, state, output):
    assert_source(repo, state)
    path = output / "SED-MotS-history.bundle"
    if path.exists():
        raise ToolError(f"History output already exists: {path}")
    git(repo, "bundle", "create", str(path), "HEAD")
    if git(repo, "bundle", "list-heads", str(path)).decode().strip() != state["commit"] + " HEAD":
        raise ToolError("History bundle does not bind the package source commit")
    git(repo, "bundle", "verify", str(path))
    assert_source(repo, state)
    return path


class DefaultMcp:
    """Small acceptance client which deliberately never supplies --agent."""
    def __init__(self, server, workspace, env, stderr):
        self.events, self.sequence = queue.Queue(), 0
        self.errors = Path(stderr).open("wb")
        self.process = subprocess.Popen([str(server), "--workspace", str(workspace)], cwd=workspace, env=env,
                                        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self.errors)
        def read():
            try:
                while True:
                    line = self.process.stdout.readline(32 * 1024 * 1024 + 1)
                    if not line or len(line) > 32 * 1024 * 1024:
                        raise ToolError("MCP stdout closed or exceeded the response bound")
                    self.events.put(json.loads(line))
            except Exception as error:
                self.events.put(ToolError(f"MCP stdout must contain JSON-RPC only: {error}"))
        threading.Thread(target=read, daemon=True).start()
        try:
            self.initialized = self.rpc("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                "clientInfo": {"name": "sed-linux-package-check", "version": "1"}})
            if self.initialized.get("serverInfo", {}).get("name") != "sed-mcp":
                raise ToolError("Unexpected MCP server identity")
            self.send({"jsonrpc": "2.0", "method": "notifications/initialized"})
        except Exception:
            self.close()
            raise

    def send(self, message):
        self.process.stdin.write(json.dumps(message).encode() + b"\n")
        self.process.stdin.flush()

    def rpc(self, method, params):
        self.sequence += 1
        self.send({"jsonrpc": "2.0", "id": self.sequence, "method": method, "params": params})
        while True:
            try:
                response = self.events.get(timeout=210)
            except queue.Empty as error:
                raise ToolError(f"MCP request timed out: {method}") from error
            if isinstance(response, Exception):
                raise response
            if response.get("id") == self.sequence:
                if "error" in response:
                    raise ToolError(f"MCP error: {response['error']}")
                return response["result"]

    def tool(self, name, arguments):
        response = self.rpc("tools/call", {"name": name, "arguments": arguments})
        structured = response.get("structuredContent", {})
        if response.get("isError") or structured.get("ok") is not True:
            raise ToolError(f"MCP tool {name} failed: {structured}")
        self.last_response = response
        return structured.get("result")

    def close(self):
        if self.process.stdin and not self.process.stdin.closed:
            self.process.stdin.close()
        try:
            self.process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait(timeout=10)
        self.process.stdout.close()
        self.errors.close()

    def __enter__(self):
        return self

    def __exit__(self, *exception):
        self.close()


def require_runtime(package):
    for directory, name in ((package, "Sed.App"), (package / "agent", "sed-agent"), (package / "mcp", "sed-mcp")):
        for required in (name, name + ".dll", name + ".runtimeconfig.json", "libcoreclr.so", "libhostfxr.so",
                         "libhostpolicy.so", "System.Private.CoreLib.dll"):
            if not (directory / required).is_file():
                raise ToolError(f"Missing self-contained runtime component: {directory / required}")
        if not os.access(directory / name, os.X_OK):
            raise ToolError(f"Native apphost is not executable: {directory / name}")
        config = json.loads((directory / (name + ".runtimeconfig.json")).read_text())
        if not config.get("runtimeOptions", {}).get("includedFrameworks"):
            raise ToolError(f"Runtime configuration is not self-contained: {directory / name}")


def verify_package(package, output, graphics=False, full_workflows=False):
    package = Path(package).resolve(strict=True)
    verified_files = verify_manifest(package)
    require_runtime(package)
    output = new_output(output)
    receipt = {"status": "running", "startedUtc": utc(), "package": str(package), "manifestedFiles": verified_files,
               "graphicsRequested": graphics, "fullWorkflowsRequested": full_workflows,
               "guiLaunched": False, "gameLaunched": False, "defaultPreviewWorker": "not-exercised", "calls": []}
    # Hardlinks avoid a second runtime copy while still checking relocated paths.
    # The test never writes into either installation, and rechecks all hashes.
    def link_or_copy(source, target):
        try:
            os.link(source, target)
        except OSError:
            shutil.copy2(source, target)
    relocated = output / "relocated package with spaces"
    shutil.copytree(package, relocated, copy_function=link_or_copy)
    workspace = new_output(output / "caller project with spaces")
    agent, server = relocated / "bin/sed-agent", relocated / "bin/sed-mcp"
    env = os.environ.copy()
    for name in ("DISPLAY", "WAYLAND_DISPLAY"):
        env.pop(name, None)
    env.update(DOTNET_ROOT=str(output / "no-sdk"), DOTNET_ROOT_X64=str(output / "no-sdk"), PYTHONDONTWRITEBYTECODE="1")
    no_graphics = env | {"VK_ICD_FILENAMES": str(output / "no-driver.json"), "VK_DRIVER_FILES": str(output / "no-driver.json")}
    def invoke(label, arguments, request=None, expected=0):
        result = subprocess.run([str(agent), *arguments], cwd=workspace, env=no_graphics, capture_output=True,
                                input=json.dumps(request).encode() if request is not None else None, timeout=60)
        (output / f"{label}.stdout.json").write_bytes(result.stdout)
        (output / f"{label}.stderr.log").write_bytes(result.stderr)
        receipt["calls"].append({"name": label, "arguments": arguments, "exitCode": result.returncode, "expectedExit": expected})
        if result.returncode != expected:
            raise ToolError(f"Packaged {label} exited {result.returncode}, expected {expected}; see {output}")
        return json.loads(result.stdout)
    try:
        invoke("schema", ["schema"])
        version = invoke("version", ["version"])
        source, edited, refused = "room with spaces.jkl", "edited room.jkl", "refused room.jkl"
        invoke("new", ["new", source, "mots"])
        inspection = invoke("inspect", ["inspect", source])
        original_hash = inspection["inputSha256"]
        patch = {"schemaVersion": 1, "inputSha256": original_hash, "operations": [{"op": "setGravity", "value": 6}]}
        plan = invoke("plan", ["plan", source, "-"], patch)
        invoke("review-refusal", ["apply", source, "-", refused, "0" * 64], patch, expected=4)
        if (workspace / refused).exists():
            raise ToolError("An output was created after a failed reviewed-hash check")
        invoke("apply", ["apply", source, "-", edited, plan["outputSha256"]], patch)
        if sha(workspace / edited) != plan["outputSha256"]:
            raise ToolError("Applied bytes do not match the reviewed plan")
        validation = invoke("validate", ["validate", edited])
        readback = invoke("readback", ["query", edited, "-"], {"schemaVersion": 1, "collection": "summary"})
        if not validation["valid"] or readback["items"][0]["header"]["gravity"] != 6:
            raise ToolError("Packaged edit did not validate and reopen with the expected gravity")
        invoke("overwrite-refusal", ["apply", source, "-", edited, plan["outputSha256"]], patch, expected=4)
        if sha(workspace / source) != original_hash or sha(workspace / edited) != plan["outputSha256"]:
            raise ToolError("A packaged command changed the source or an existing output")
        with DefaultMcp(server, workspace, no_graphics, output / "mcp-default.stderr.log") as client:
            receipt["mcpInitialization"] = client.initialized
            client.tool("sed_discover", {})
            discovered = client.tool("sed_query", {"level": source, "request": {"schemaVersion": 1, "collection": "summary"}})
            if discovered["inputSha256"] != original_hash:
                raise ToolError("Default MCP server read a different source")
        if graphics:
            with DefaultMcp(server, workspace, env, output / "mcp-preview.stderr.log") as client:
                if client.initialized["serverInfo"]["version"] != version["version"]:
                    raise ToolError("Packaged agent and MCP informational versions differ")
                image_request = {"schemaVersion": 1, "inputSha256": original_hash, "position": [0, -1, 0.2],
                                 "target": [0, 0, 0], "fieldOfViewDegrees": 70, "width": 320, "height": 240}
                preview = client.tool("sed_snapshot", {"level": source, "request": image_request,
                                                        "output": str(workspace / "default-worker.png")})
                images = [item for item in client.last_response.get("content", []) if item.get("type") == "image"]
                png = (workspace / "default-worker.png").read_bytes()
                if len(images) != 1 or base64.b64decode(images[0]["data"], validate=True) != png or sha(workspace / "default-worker.png") != preview["sha256"]:
                    raise ToolError("Default sibling worker snapshot evidence does not match its output")
                receipt["defaultPreviewWorker"] = "passed"
                receipt["snapshot"] = preview
        if full_workflows:
            arguments = [sys.executable, "-B", relocated / "tools/test_mcp_workflow.py", "--server",
                         relocated / "mcp/sed-mcp", "--agent", relocated / "agent/sed-agent", "--output", output / "mcp-workflow"]
            if graphics:
                arguments.append("--snapshots")
            run_logged(arguments, workspace, output / "mcp-workflow.log", env if graphics else no_graphics)
            run_logged([sys.executable, "-B", relocated / "tools/test_tool_workbench.py", "--agent",
                        relocated / "agent/sed-agent", "--output-dir", output / "tool-workbench"],
                       workspace, output / "tool-workbench.log", no_graphics)
        verify_manifest(relocated)
        verify_manifest(package)
        receipt["status"] = "passed"
    except Exception as error:
        receipt.update(status="failed", error=str(error))
        raise
    finally:
        receipt["finishedUtc"] = utc()
        write_json(output / "verification.json", receipt)
    return output / "verification.json"


def package_release(repo, dotnet, version, output, build_receipt=None, graphics=False, full_workflows=False):
    if not re.fullmatch(r"\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?", version):
        raise ToolError("--version must be a SemVer-style version without spaces")
    state = source_state(repo, require_clean=True)
    dotnet = resolve_dotnet(dotnet)
    output = Path(output).absolute()
    if output.is_relative_to(repo):
        ignored = subprocess.run(["git", "-C", str(repo), "check-ignore", "-q", str(output)]).returncode == 0
        if not ignored:
            raise ToolError("Package output inside the source repository must be ignored; use build/packages or an external directory")
    if build_receipt:
        validate_build_receipt(build_receipt, state, dotnet)
    output = new_output(output)
    name = f"SED-MotS-{version}-{RID}"
    package = new_output(output / name)
    receipt = {**state, "status": "running", "startedUtc": utc(), "version": version, "target": RID,
               "selfContained": True, "dotnetSha256": sha(dotnet), "hardlinkBytesSaved": 0}
    try:
        source = snapshot_source(repo, state, output)
        source_hashes = {str(path.relative_to(source)): sha(path) for path in source.rglob("*") if path.is_file()}
        if build_receipt:
            shutil.copy2(build_receipt, output / "build-receipt.json")
        else:
            built = build(source, dotnet, output / "build-evidence", graphics=graphics, source=state)
            shutil.copy2(built, output / "build-receipt.json")
        env = sdk_environment(dotnet) | {"SOURCE_DATE_EPOCH": str(state["epoch"])}
        for component, destination in (("editor", package), ("agent", package / "agent"), ("mcp", package / "mcp")):
            arguments = [dotnet, "publish", PROJECTS[component], "-c", "Release", "-r", RID,
                         "--self-contained", "true", "--nologo", "--disable-build-servers", "-m:1", "-nodeReuse:false",
                         "-p:UseSharedCompilation=false", "-p:BuildInParallel=false", "-p:PublishTrimmed=false",
                         "-p:UseAppHost=true", "-p:Deterministic=true", "-p:ContinuousIntegrationBuild=true",
                         f"-p:Version={version}", f"-p:SourceRevisionId={state['commit']}",
                         f"-p:PathMap={source}=/_/", "-o", destination]
            if component == "mcp":
                arguments.append("-p:RestoreLockedMode=true")
            run_logged(arguments, source, output / f"publish-{component}.log", env)
            receipt["hardlinkBytesSaved"] += deduplicate(package)
        require_runtime(package)
        for relative, digest in source_hashes.items():
            if not (source / relative).is_file() or sha(source / relative) != digest:
                raise ToolError(f"A committed source input changed during build/publish: {relative}")
        copy_support(source, package)
        collect_licenses(source, package)
        write_launchers(package)
        # This snapshot belongs to this attempt. Preserve its archive, but free
        # compiler outputs before the package workflows and compression begin.
        shutil.rmtree(source)
        history = write_history(repo, state, output)
        provenance = {"product": "SED MotS native Linux", "version": version, "commit": state["commit"],
            "target": RID, "selfContained": True, "sourceEpoch": state["epoch"], "dotnetSha256": sha(dotnet),
            "fork": "https://github.com/CodaLilitari/SED-MotS",
            "basedOn": "https://github.com/davidagnome/SED/tree/908d53d70d6b95784a92e9f1746ab5b13afcfdf0",
            "sourceArchive": "SED-MotS-source.tar.gz", "sourceArchiveSha256": sha(output / "SED-MotS-source.tar.gz"),
            "historyBundle": history.name, "historyBundleSha256": sha(history),
            "automaticUpdater": "Windows-only; Linux installs use separate versioned directories",
            "gameEngineBundled": False, "gameAssetsBundled": False}
        write_json(package / "PROVENANCE.json", provenance)
        (package / "DEPENDENCIES.txt").write_text(
            "Linux x86-64 with glibc; .NET runtime is included.\n"
            "System libraries: ICU, OpenSSL, zlib, fontconfig, FreeType, X11/XCB and xkbcommon.\n"
            "Desktop and snapshots need Vulkan 1.2 and maxPushConstantsSize >= 160; CLI/MCP editing does not.\n"
            "Python 3.10+ is needed only for development, verification and optional desktop installation.\n"
            "See START-HERE.md for distribution packages and software rendering.\n"
            "No game engine, retail assets, community levels or API credentials are included.\n"
            "The automatic updater supports Windows only; keep Linux versions in separate directories.\n"
            "Inherited notices: licenses/net. Resolved Linux package notices and metadata: licenses/linux.\n",
            encoding="utf-8")
        receipt["hardlinkBytesSaved"] += deduplicate(package)
        write_manifest(package)
        verified = verify_package(package, output / "verification", graphics, full_workflows)
        receipt["verificationReceiptSha256"] = sha(verified)
        assert_source(repo, state)
        archive = output / (name + ".tar.gz")
        archive_package(package, archive, state["epoch"])
        artifacts = [archive, output / "SED-MotS-source.tar.gz", history]
        (output / "SHA256SUMS.txt").write_text("".join(f"{sha(path)}  {path.name}\n" for path in sorted(artifacts)), encoding="utf-8")
        assert_source(repo, state)
        receipt.update(status="passed", package=name, archive=archive.name, archiveSha256=sha(archive))
    except Exception as error:
        receipt.update(status="failed", error=str(error))
        raise
    finally:
        receipt["finishedUtc"] = utc()
        write_json(output / "package-receipt.json", receipt)
    return output / "package-receipt.json"


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    build_parser = commands.add_parser("build", help="Build Linux entrypoints, run Core/MCP/Python suites and the UI input probe")
    package_parser = commands.add_parser("package", help="Build a clean commit into a verified self-contained portable tarball")
    verify_parser = commands.add_parser("verify", help="Verify an extracted package through relocated CLI and MCP processes")
    install_parser = commands.add_parser("install-desktop", help="Create per-user desktop entry and command links; never overwrite")
    for command in (build_parser, package_parser):
        command.add_argument("--dotnet", default="dotnet", help="SDK executable path or command on PATH (default: dotnet)")
    for command in (build_parser, package_parser, verify_parser):
        command.add_argument("--output", type=Path, help="New evidence/output directory; an existing path is always refused")
        command.add_argument("--graphics", action="store_true", help="Require offscreen Vulkan graphics checks (choose a driver through the environment)")
    build_parser.add_argument("--configuration", choices=("Release", "Debug"), default="Release")
    build_parser.add_argument("--no-restore", action="store_true", help="Reuse existing linux-x64 restore outputs and the package cache")
    package_parser.add_argument("--version", required=True, help="Package/assembly version, e.g. 0.4.0-mots.17-linux.1")
    package_parser.add_argument("--build-receipt", type=Path, help="Reuse passing Release tests bound to this clean commit and SDK")
    for command in (package_parser, verify_parser):
        command.add_argument("--full-workflows", action="store_true", help="Also run the existing broad MCP and editing-workbench acceptance scripts")
    for command in (verify_parser, install_parser):
        command.add_argument("--package", type=Path, required=True, help="Extracted package root containing Sed.App and bin/")
    install_parser.add_argument("--applications-dir", type=Path,
        default=Path(os.environ.get("XDG_DATA_HOME", str(Path.home() / ".local/share"))) / "applications")
    install_parser.add_argument("--bin-dir", type=Path, default=Path.home() / ".local/bin")
    args = parser.parse_args(argv)
    if not sys.platform.startswith("linux"):
        parser.error("These entrypoints target native Linux; use the existing Windows tools on Windows")
    attempt = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-" + uuid.uuid4().hex[:8]
    try:
        if args.command == "build":
            result = build(ROOT, args.dotnet, args.output or ROOT / "build/linux" / attempt,
                           args.configuration, args.no_restore, args.graphics)
        elif args.command == "package":
            result = package_release(ROOT, args.dotnet, args.version, args.output or ROOT / "build/packages" / attempt,
                                     args.build_receipt, args.graphics, args.full_workflows)
        elif args.command == "verify":
            result = verify_package(args.package, args.output or Path.cwd() / ("sed-linux-verification-" + attempt),
                                    args.graphics, args.full_workflows)
        else:
            result = install_desktop(args.package, args.applications_dir, args.bin_dir)
        print(result)
        return 0
    except (ToolError, OSError, ValueError, subprocess.SubprocessError) as error:
        print(f"SED Linux tools: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
