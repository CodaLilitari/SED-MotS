#!/usr/bin/env python3
"""Real CLI and stdio MCP acceptance for reviewed resource packages.

Uses only original hand-authored fixtures and the Python standard library.
Every request and process stream is retained in a new work directory. Archive
readback uses an independent struct decoder, without importing the SED writer.
No display, renderer, game process or model service is requested.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import queue
import struct
import subprocess
import threading
import time
import traceback
import uuid
from datetime import datetime, timezone


TITLE = "Original reviewed package fixture"
LEVEL_NAME = "package_room.jkl"
MAX_WIRE_BYTES = 32 * 1024 * 1024
TIMEOUT = 60

# Original inward-facing cube, authored here rather than through sed-agent new.
# Four light components per corner identify MotS. Its stock palette and custom
# model template create real direct roots without any bundled game templates.
CUBE = b"""# Original process acceptance fixture; no gameplay qualification.
SECTION: HEADER
VERSION 1
WORLD GRAVITY 4
END

SECTION: MATERIALS
WORLD MATERIALS 0
END

SECTION: GEORESOURCE
WORLD COLORMAPS 1
0: stock_palette.cmp
WORLD VERTICES 8
0: -1 -1 0
1: 1 -1 0
2: 1 1 0
3: -1 1 0
4: -1 -1 2
5: 1 -1 2
6: 1 1 2
7: -1 1 2
WORLD TEXTURE VERTICES 4
0: 0 0
1: 64 0
2: 64 64
3: 0 64
WORLD ADJOINS 0
WORLD SURFACES 6
0: -1 0x4 0x0 4 3 3 -1 0 4 0,0 1,1 2,2 3,3 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1
1: -1 0x4 0x0 4 3 3 -1 0 4 7,0 6,1 5,2 4,3 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1
2: -1 0x4 0x0 4 3 3 -1 0 4 4,0 5,1 1,2 0,3 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1
3: -1 0x4 0x0 4 3 3 -1 0 4 3,0 2,1 6,2 7,3 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1
4: -1 0x4 0x0 4 3 3 -1 0 4 0,0 3,1 7,2 4,3 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1
5: -1 0x4 0x0 4 3 3 -1 0 4 5,0 6,1 2,2 1,3 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1
0: 0 0 1
1: 0 0 -1
2: 0 1 0
3: 0 -1 0
4: 1 0 0
5: -1 0 0
END

SECTION: SECTORS
WORLD SECTORS 1
SECTOR 0
FLAGS 0x0
AMBIENT LIGHT 0.5
EXTRA LIGHT 0
COLORMAP 0
TINT 0 0 0
CENTER 0 0 1
RADIUS 1.75
VERTICES 8
0: 0
1: 1
2: 2
3: 3
4: 4
5: 5
6: 6
7: 7
SURFACES 0 6
END

SECTION: TEMPLATES
WORLD TEMPLATES 1
original_package_model none model3d=package_triangle.3do
END

SECTION: THINGS
WORLD THINGS 16
END
"""

MODEL = b"""# Original small triangle; dependency is its declared MAT table.
SECTION: HEADER
3DO 2.1
END
SECTION: MODELRESOURCE
MATERIALS 1
0: package_paint.mat
END
SECTION: GEOMETRYDEF
RADIUS 0.5
INSERT OFFSET 0 0 0
GEOSETS 1
GEOSET 0
MESHES 1
MESH 0
NAME original_triangle
GEOMETRYMODE 4
LIGHTINGMODE 3
TEXTUREMODE 3
VERTICES 3
0: 0 0 0
1: 0.25 0 0
2: 0 0.25 0
TEXTURE VERTICES 3
0: 0 0
1: 1 0
2: 0 1
VERTEX NORMALS 3
0: 0 0 1
1: 0 0 1
2: 0 0 1
FACES 1
0: 0 0x0 3 0 0 0 3 0,0 1,1 2,2
FACE NORMALS 1
0: 0 0 1
END
SECTION: HIERARCHYDEF
HIERARCHY NODES 1
0: 0x0 0x00010 0 -1 -1 -1 0 0 0 0 0 0 0 0 0 0 root
END
"""


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def utc():
    return datetime.now(timezone.utc).isoformat()


def digest(data):
    return hashlib.sha256(data).hexdigest()


def sha(path):
    result = hashlib.sha256()
    with Path(path).open("rb") as source:
        for data in iter(lambda: source.read(64 * 1024), b""):
            result.update(data)
    return result.hexdigest()


def json_bytes(value, *, compact=False):
    options = {"ensure_ascii": False, "sort_keys": True, "allow_nan": False}
    if compact:
        options["separators"] = (",", ":")
    else:
        options["indent"] = 2
    return (json.dumps(value, **options) + "\n").encode("utf-8")


def write_new(path, data):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("xb") as output:
        output.write(data)
    return path


def file_record(path, root=None):
    path = Path(path)
    return {"path": str(path.relative_to(root) if root is not None else path),
            "bytes": path.stat().st_size, "sha256": sha(path)}


def original_mat():
    data = bytearray(100)
    struct.pack_into("<4sIII", data, 0, b"MAT ", 0x32, 0, 1)
    struct.pack_into("<I", data, 80, 7)
    return bytes(data)


def original_cmp():
    data = bytearray(64 + 256 * 3)
    data[:4] = b"CMP "
    for index in range(256):
        data[64 + index * 3:67 + index * 3] = bytes((index, index // 2, 255 - index))
    return bytes(data)


def write_gob(path, entries):
    """Independent tiny fixture encoder, with original caller-specified order."""
    directory = 12 + sum(len(data) for _, data in entries)
    result = bytearray(struct.pack("<4sII", b"GOB ", 0x14, directory))
    for _, data in entries:
        result.extend(data)
    result.extend(struct.pack("<I", len(entries)))
    offset = 12
    for name, data in entries:
        encoded = name.encode("ascii")
        require(0 < len(encoded) < 128 and b"\0" not in encoded, "Bad original fixture entry name")
        result.extend(struct.pack("<II128s", offset, len(data), encoded))
        offset += len(data)
    write_new(path, result)


def make_fixture(workspace):
    workspace.mkdir()
    source = write_new(workspace / "original room.jkl", CUBE)
    custom, stock, unused = (workspace / name for name in ("custom.goo", "stock.goo", "unused.goo"))
    # Deliberately reverse dependency/output sort order in the source directory.
    write_gob(custom, [("mat/package_paint.mat", original_mat()), ("3do/package_triangle.3do", MODEL)])
    write_gob(stock, [("misc/cmp/stock_palette.cmp", original_cmp())])
    write_gob(unused, [("misc/unreferenced.txt", b"Original unused fixture, never included.\n")])
    resources = [{"path": str(path), "sha256": sha(path)} for path in (custom, stock, unused)]
    request = {"schemaVersion": 1, "inputSha256": sha(source), "levelFileName": LEVEL_NAME,
               "episodeTitle": TITLE, "episodeType": 1,
               "resourceArchives": resources, "stockArchives": [str(stock)]}
    return {"source": source, "custom": custom, "stock": stock, "unused": unused, "request": request,
            "identities": [file_record(path) for path in (source, custom, stock, unused)]}


def assert_inputs_unchanged(fixture):
    for record in fixture["identities"]:
        require(file_record(record["path"]) == record, "An original input changed: " + record["path"])


def inspect_package(path, expected_hash):
    """Strict independent readback of the owned deterministic GOB layout."""
    data = Path(path).read_bytes()
    require(digest(data) == expected_hash, "Package bytes differ from reviewed output hash")
    require(16 <= len(data) <= 128 * 1024 * 1024, "Invalid package length")
    magic, version, directory = struct.unpack_from("<4sII", data)
    require(magic == b"GOB " and version == 0x14, "Wrong GOB signature/version")
    require(12 <= directory <= len(data) - 4, "Invalid GOB directory offset")
    count, = struct.unpack_from("<I", data, directory)
    require(count <= 4098 and directory + 4 + count * 136 == len(data), "Invalid/trailing GOB directory bytes")
    entries, order, cursor = {}, [], 12
    for index in range(count):
        offset, length, field = struct.unpack_from("<II128s", data, directory + 4 + index * 136)
        nul = field.find(b"\0")
        require(nul > 0 and not any(field[nul:]), "Missing terminator or nonzero name padding")
        raw_name = field[:nul].decode("ascii")
        name = raw_name.replace("\\", "/")
        require(name == name.lower() and name not in entries, "Noncanonical or duplicate output name")
        require(offset == cursor and offset + length <= directory, "Noncontiguous or out-of-range entry payload")
        entries[name] = data[offset:offset + length]
        order.append(name)
        cursor += length
    require(cursor == directory, "Unexpected unmanifested payload bytes")
    expected_order = ["3do/package_triangle.3do", "episode.jk", "jkl/" + LEVEL_NAME, "mat/package_paint.mat"]
    require(order == expected_order, "Unexpected package entry order or inclusion: " + repr(order))
    require(entries["jkl/" + LEVEL_NAME] == CUBE, "Original JKL bytes were rewritten")
    require(entries["3do/package_triangle.3do"] == MODEL, "Model payload changed")
    require(entries["mat/package_paint.mat"] == original_mat(), "Transitive material payload changed")
    episode = entries["episode.jk"].decode("ascii")
    require("\r" not in episode, "Episode must use deterministic LF endings")
    lines = [line.split("#", 1)[0].strip() for line in episode.splitlines()]
    lines = [line for line in lines if line]
    require(lines[:3] == ['"' + TITLE + '"', "TYPE 1", "SEQ 1"], "Incorrect episode identity/type/count")
    require(len(lines) == 5 and lines[-1].lower() == "end", "Unexpected episode records")
    require(lines[3].split() == ["0:", "1", "1", "LEVEL", LEVEL_NAME, "0", "0", "-1", "-1"],
            "Incorrect episode level sequence")
    return {"path": str(path), "bytes": len(data), "sha256": digest(data),
            "entries": [{"name": name, "bytes": len(payload), "sha256": digest(payload)}
                        for name, payload in entries.items()]}


class Cli:
    def __init__(self, agent, workspace, work, environment, report):
        self.agent, self.workspace, self.work = agent, workspace, work
        self.environment, self.report, self.sequence = environment, report, 0

    def call(self, label, arguments, request=None, expected=0):
        self.sequence += 1
        directory = self.work / "cli" / f"{self.sequence:03d}-{label}"
        stdin = json_bytes(request) if request is not None else b""
        request_path = write_new(directory / "stdin.json", stdin)
        argv = [str(self.agent), *map(str, arguments)]
        receipt = {"kind": "cli", "label": label, "argv": argv, "cwd": str(self.workspace),
                   "startedUtc": utc(), "expectedExit": expected,
                   "stdin": file_record(request_path, self.work)}
        self.report["calls"].append(receipt)
        stdout = stderr = b""
        launch_error = None
        try:
            result = subprocess.run(argv, cwd=self.workspace, env=self.environment, input=stdin,
                                    capture_output=True, timeout=TIMEOUT)
            stdout, stderr, receipt["exitCode"] = result.stdout, result.stderr, result.returncode
        except subprocess.TimeoutExpired as error:
            stdout, stderr = error.stdout or b"", error.stderr or b""
            receipt["exitCode"], receipt["timedOut"] = None, True
        except Exception as error:
            receipt["exitCode"], receipt["launchError"] = None, str(error)
            launch_error = error
        finally:
            receipt["finishedUtc"] = utc()
            receipt["stdout"] = file_record(write_new(directory / "stdout.json", stdout), self.work)
            receipt["stderr"] = file_record(write_new(directory / "stderr.log", stderr), self.work)
            write_new(directory / "call.json", json_bytes(receipt))
        if launch_error is not None:
            raise launch_error
        require(receipt["exitCode"] == expected, f"{label}: expected exit {expected}, got {receipt['exitCode']}")
        require(not stderr, f"{label}: unexpected stderr; inspect retained stream")
        return json.loads(stdout.decode("utf-8"))


class Mcp:
    def __init__(self, server, agent, workspace, work, name, environment, report):
        self.workspace, self.work, self.directory = workspace, work, work / "mcp" / name
        self.directory.mkdir(parents=True)
        self.report, self.events, self.sequence = report, queue.Queue(), 0
        self.input_path, self.output_path, self.error_path = (
            self.directory / item for item in ("stdin.ndjson", "stdout.ndjson", "stderr.log"))
        self.input_log = self.input_path.open("xb")
        self.output_log = self.output_path.open("xb")
        self.error_log = self.error_path.open("xb")
        self.receipt = {"kind": "mcp-session", "name": name, "startedUtc": utc(),
                        "argv": [str(server), "--workspace", str(workspace), "--agent", str(agent)],
                        "cwd": str(workspace), "calls": [], "notifications": []}
        report["sessions"].append(self.receipt)
        self.process = None
        try:
            self.process = subprocess.Popen(self.receipt["argv"], cwd=workspace, env=environment,
                                            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        except Exception:
            self.input_log.close()
            self.output_log.close()
            self.error_log.close()
            raise
        self.threads = [threading.Thread(target=self._stdout, daemon=True),
                        threading.Thread(target=self._stderr, daemon=True)]
        for thread in self.threads:
            thread.start()

    def _stdout(self):
        try:
            while True:
                line = self.process.stdout.readline(MAX_WIRE_BYTES + 1)
                if not line:
                    self.events.put(EOFError("MCP stdout closed")); return
                self.output_log.write(line)
                self.output_log.flush()
                require(len(line) <= MAX_WIRE_BYTES, "MCP response exceeded bounded wire limit")
                self.events.put((line, json.loads(line.decode("utf-8"))))
        except Exception as error:
            self.events.put(error)

    def _stderr(self):
        try:
            while data := self.process.stderr.read(4096):
                self.error_log.write(data)
                self.error_log.flush()
        except Exception as error:
            self.events.put(error)

    def send(self, body, label):
        data = json_bytes(body, compact=True)
        path = write_new(self.directory / (label + ".request.json"), data)
        self.input_log.write(data)
        self.input_log.flush()
        self.process.stdin.write(data)
        self.process.stdin.flush()
        return path

    def rpc(self, method, params, expected_error=None):
        self.sequence += 1
        label = f"{self.sequence:03d}-{method.replace('/', '-')}"
        request = {"jsonrpc": "2.0", "id": self.sequence, "method": method, "params": params}
        record = {"kind": "mcp", "method": method, "id": self.sequence, "startedUtc": utc(),
                  "expectedToolError": expected_error}
        self.receipt["calls"].append(record)
        self.report["calls"].append(record)
        try:
            record["request"] = file_record(self.send(request, label), self.work)
            deadline = time.monotonic() + TIMEOUT
            while True:
                try:
                    event = self.events.get(timeout=max(0.01, deadline - time.monotonic()))
                except queue.Empty as error:
                    raise TimeoutError("MCP response deadline exceeded") from error
                if isinstance(event, Exception):
                    raise event
                raw, response = event
                if "id" not in response:
                    self.receipt["notifications"].append(response)
                    require(time.monotonic() < deadline, "MCP response deadline exceeded")
                    continue
                record["response"] = file_record(
                    write_new(self.directory / (label + ".response.json"), raw), self.work)
                require(response.get("id") == self.sequence, "Unexpected MCP response id")
                require(response.get("jsonrpc") == "2.0" and "error" not in response,
                        "MCP protocol error: " + repr(response.get("error")))
                record["completed"] = True
                return response["result"]
        except Exception as error:
            record["error"] = str(error)
            raise
        finally:
            record["finishedUtc"] = utc()
            write_new(self.directory / (label + ".call.json"), json_bytes(record))

    def tool(self, name, arguments, expected_error=None):
        result = self.rpc("tools/call", {"name": name, "arguments": arguments}, expected_error)
        structured = result["structuredContent"]
        text = [part["text"] for part in result["content"] if part["type"] == "text"]
        require(len(text) == 1 and json.loads(text[0]) == structured, "MCP text/structured response differs")
        require(all(part["type"] == "text" for part in result["content"]), "Package tool returned payload/image content")
        require(structured["schemaVersion"] == 1, "Unsupported MCP result schema")
        if expected_error is None:
            require(not result.get("isError", False) and structured["ok"], "MCP tool failed: " + repr(structured))
            return structured["result"]
        require(result.get("isError") and not structured["ok"], "Expected MCP refusal")
        require(structured["error"]["code"] == expected_error, "Unexpected MCP refusal: " + repr(structured))
        return structured["error"]

    def __enter__(self):
        try:
            initialized = self.rpc("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                "clientInfo": {"name": "sed-package-process-acceptance", "version": "1"}})
            require(initialized["serverInfo"]["name"] == "sed-mcp", "Unexpected MCP host")
            self.receipt["initialization"] = initialized
            self.send({"jsonrpc": "2.0", "method": "notifications/initialized"}, "initialized")
            return self
        except Exception:
            try:
                self.close()
            except Exception:
                pass
            raise

    def close(self):
        problem = None
        try:
            if self.process.stdin and not self.process.stdin.closed:
                self.process.stdin.close()
            try:
                self.process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=5)
                problem = "MCP host did not exit after stdin closed"
            for thread in self.threads:
                thread.join(timeout=5)
            if any(thread.is_alive() for thread in self.threads):
                problem = "MCP stream reader did not finish"
            self.receipt["exitCode"] = self.process.returncode
            if self.process.returncode != 0:
                problem = "MCP host exit code " + str(self.process.returncode)
        finally:
            for stream in (self.input_log, self.output_log, self.error_log):
                stream.close()
            self.receipt["finishedUtc"] = utc()
            self.receipt["stdin"] = file_record(self.input_path, self.work)
            self.receipt["stdout"] = file_record(self.output_path, self.work)
            self.receipt["stderr"] = file_record(self.error_path, self.work)
            if problem:
                self.receipt["closeError"] = problem
            write_new(self.directory / "session.json", json_bytes(self.receipt))
        require(problem is None, problem)

    def __exit__(self, kind, value, trace):
        try:
            self.close()
        except Exception:
            if kind is None:
                raise
        return False


def check_plan(plan):
    require(plan["canBuild"], "Original package plan is blocked: " + repr(plan))
    require(plan["archiveExtension"] == ".goo", "Original MotS cube has wrong archive type")
    require(plan["runtimeClosureVerified"] is False, "Static package plan claimed runtime closure")
    require(len(plan["stockOmissions"]) == 1, "Expected one declared stock palette omission")
    for field in ("outputSha256", "manifestSha256"):
        value = plan[field]
        require(isinstance(value, str) and len(value) == 64 and all(c in "0123456789abcdef" for c in value),
                "Invalid reviewed identity: " + field)


def apply_args(fixture, output, plan, operation_id=None):
    result = {"level": str(fixture["source"]), "request": fixture["request"], "output": str(output),
              "expectedOutputSha256": plan["outputSha256"], "expectedManifestSha256": plan["manifestSha256"]}
    if operation_id is not None:
        result["operationId"] = operation_id
    return result


def cli_workflow(agent, fixture, work, environment, report):
    cli = Cli(agent, fixture["source"].parent, work, environment, report)
    version = cli.call("version", ["version"])
    require(version["name"] == "sed-agent" and "reviewed-resource-packaging" in version["capabilities"],
            "Selected CLI lacks package capability")
    schema = cli.call("schema", ["schema", "package"])
    require("levelFileName" in schema["properties"], "Focused package schema is absent")
    validation = cli.call("fixture-validation", ["validate", fixture["source"]])
    require(validation["valid"], "Hand-authored cube failed structural validation")
    request, source = fixture["request"], fixture["source"]
    plan = cli.call("plan", ["package-plan", source, "-"], request)
    check_plan(plan)
    again = cli.call("deterministic-plan", ["package-plan", source, "-"], request)
    require((again["outputSha256"], again["manifestSha256"]) == (plan["outputSha256"], plan["manifestSha256"]),
            "Repeated planning changed reviewed identities")
    for field in ("outputSha256", "manifestSha256"):
        target = source.parent / ("refused-" + field + ".goo")
        hashes = {name: ("0" * 64 if name == field else plan[name])
                  for name in ("outputSha256", "manifestSha256")}
        refused = cli.call("stale-" + field, ["package-apply", source, "-", target,
            hashes["outputSha256"], hashes["manifestSha256"]], request, expected=4)
        require(refused["error"] == "conflict" and not target.exists(), "Stale package review wrote output")
    incomplete = source.parent / "missing-review-hash.goo"
    cli.call("missing-review-hash", ["package-apply", source, "-", incomplete, plan["outputSha256"]],
             request, expected=2)
    require(not incomplete.exists(), "A package was created without both review hashes")
    missing = request | {"extraResources": [{"name": "missing.mat", "folder": "mat"}]}
    blocked = cli.call("missing-resource-plan", ["package-plan", source, "-"], missing)
    require(not blocked["canBuild"] and blocked["outputSha256"] is None and blocked["issues"],
            "Missing resource did not produce a reviewable blocked plan")
    target = source.parent / "missing-resource.goo"
    cli.call("missing-resource-apply", ["package-apply", source, "-", target,
        "0" * 64, blocked["manifestSha256"]], missing, expected=3)
    require(not target.exists(), "Blocked package was written")
    changed = request | {"stockArchives": [str(fixture["stock"]), str(fixture["unused"])]}
    changed_plan = cli.call("changed-stock-role-plan", ["package-plan", source, "-"], changed)
    require(changed_plan["canBuild"] and changed_plan["outputSha256"] == plan["outputSha256"],
            "Unused stock role unexpectedly changed package bytes")
    require(changed_plan["manifestSha256"] != plan["manifestSha256"], "Stock role was not bound to manifest review")
    target = source.parent / "changed-stock-role.goo"
    cli.call("changed-stock-role-refusal", ["package-apply", source, "-", target,
        plan["outputSha256"], plan["manifestSha256"]], changed, expected=4)
    require(not target.exists(), "Old manifest review accepted changed stock declarations")
    stale = request | {"resourceArchives": [request["resourceArchives"][0] | {"sha256": "0" * 64},
                                            *request["resourceArchives"][1:]]}
    cli.call("stale-resource-hash", ["package-plan", source, "-"], stale, expected=4)
    output = source.parent / "reviewed package.goo"
    cli.call("apply", ["package-apply", source, "-", output, plan["outputSha256"], plan["manifestSha256"]], request)
    archive = inspect_package(output, plan["outputSha256"])
    before = file_record(output)
    cli.call("overwrite-refusal", ["package-apply", source, "-", output,
        plan["outputSha256"], plan["manifestSha256"]], request, expected=4)
    require(file_record(output) == before, "Existing package changed during refused overwrite")
    assert_inputs_unchanged(fixture)
    report["cliPackage"] = {"planOutputSha256": plan["outputSha256"],
                            "planManifestSha256": plan["manifestSha256"], "readback": archive}
    return output


def mcp_workflow(server, agent, fixture, work, environment, report, cli_output):
    workspace = fixture["source"].parent
    receipts = workspace / ".sed-mcp" / "receipts"
    operation_id = str(uuid.uuid4())
    output = workspace / "reviewed package.goo"
    with Mcp(server, agent, workspace, work, "first", environment, report) as client:
        listed = client.rpc("tools/list", {})["tools"]
        require({"sed_package_plan", "sed_package_apply"} <= {tool["name"] for tool in listed},
                "Package tools are not registered over stdio")
        client.tool("sed_discover", {"topic": "package"})
        plan = client.tool("sed_package_plan", {"level": str(fixture["source"]), "request": fixture["request"]})
        check_plan(plan)
        arguments = apply_args(fixture, output, plan, operation_id)
        for field in ("expectedOutputSha256", "expectedManifestSha256"):
            missing_review = {name: value for name, value in arguments.items() if name != field}
            client.tool("sed_package_apply", missing_review, "invalid_request")
            client.tool("sed_package_apply", arguments | {field: "0" * 64}, "conflict")
            require(not output.exists() and not list(receipts.glob("*.json")), "Refusal created output/history")
        committed = client.tool("sed_package_apply", arguments)
        require(committed["outcome"]["observedState"] == "committed"
                and committed["outcome"]["matchesExpectedOutput"], "Package commit lacks durable matching outcome")
        readback = inspect_package(output, plan["outputSha256"])
        require(output.read_bytes() == cli_output.read_bytes(), "CLI and MCP produced different package bytes")
    assert_inputs_unchanged(fixture)
    receipt_files = list(receipts.glob("*.json"))
    require(len(receipt_files) == 1, "Unexpected package journal history")
    receipt_path = receipt_files[0]
    receipt_bytes, output_bytes = receipt_path.read_bytes(), output.read_bytes()
    output_mtime = output.stat().st_mtime_ns
    # Preserve exact inputs while making their original paths disappear.
    moved = []
    for key in ("source", "custom", "stock", "unused"):
        original = fixture[key]
        preserved = work / "preserved-mcp-inputs" / original.name
        preserved.parent.mkdir(exist_ok=True)
        original.rename(preserved)
        moved.append({"originalPath": str(original), "preserved": file_record(preserved, work)})
    with Mcp(server, agent, workspace, work, "reconnected", environment, report) as client:
        observed = client.tool("sed_outcome", {"operationId": operation_id})["outcome"]
        require(observed["observedState"] == "committed" and observed["matchesExpectedOutput"],
                "Reconnect lost committed package evidence")
        replay = client.tool("sed_package_apply", arguments)
        require(replay["replayed"] and replay["outcome"]["observedState"] == "committed",
                "Replay did not return history after inputs disappeared")
        client.tool("sed_package_apply", arguments | {"expectedManifestSha256": "0" * 64}, "conflict")
    require(receipt_path.read_bytes() == receipt_bytes, "Replay rewrote durable operation history")
    require(output.read_bytes() == output_bytes and output.stat().st_mtime_ns == output_mtime,
            "Replay performed a second package write")
    require(list(receipts.glob("*.json")) == receipt_files, "Replay created another operation")
    report["mcpPackage"] = {"operationId": operation_id, "manifestSha256": plan["manifestSha256"],
                            "readback": readback, "receipt": file_record(receipt_path, work),
                            "inputsPreservedAfterRemoval": moved, "replayChangedOutput": False}


def runtime_identity(path):
    candidates = [path]
    for name in (path.stem + ".dll", path.stem + ".deps.json", path.stem + ".runtimeconfig.json",
                 "Sed.Agent.dll", "Sed.Agent.deps.json", "Sed.Agent.runtimeconfig.json",
                 "Sed.Mcp.dll", "Sed.Mcp.deps.json", "Sed.Mcp.runtimeconfig.json", "Sed.Core.dll", "Sed.Formats.dll"):
        candidate = path.parent / name
        if candidate.is_file() and candidate not in candidates:
            candidates.append(candidate)
    return {"executable": str(path), "scope": "Explicit executable and present adjacent managed identity files",
            "files": [file_record(candidate) for candidate in candidates]}


def verify_captured_streams(report, work):
    for call in report["calls"]:
        for field in ("stdin", "stdout", "stderr", "request", "response"):
            if field in call:
                record = call[field]
                require(file_record(work / record["path"], work) == record,
                        "Captured process evidence changed: " + record["path"])
    for session in report["sessions"]:
        for field in ("stdin", "stdout", "stderr"):
            record = session[field]
            require(file_record(work / record["path"], work) == record,
                    "Captured MCP session evidence changed: " + record["path"])


def run(agent, server, work):
    work.mkdir(parents=True, exist_ok=False)
    environment = os.environ.copy()
    for name in ("DISPLAY", "WAYLAND_DISPLAY"):
        environment.pop(name, None)
    report = {"schemaVersion": 1, "status": "running", "startedUtc": utc(),
              "agent": runtime_identity(agent), "mcp": runtime_identity(server),
              "headless": {"displayEnvironmentRemoved": True, "snapshotToolsCalled": False},
              "calls": [], "sessions": [],
              "scope": "Original synthetic authoring/archive/transport acceptance; no engine, rendering or retail gameplay evidence."}
    write_new(work / "test_package_workflow.py", Path(__file__).read_bytes())
    try:
        cli_fixture = make_fixture(work / "cli-workspace")
        mcp_fixture = make_fixture(work / "mcp-workspace")
        report["initialInputs"] = cli_fixture["identities"] + mcp_fixture["identities"]
        cli_output = cli_workflow(agent, cli_fixture, work, environment, report)
        mcp_workflow(server, agent, mcp_fixture, work, environment, report, cli_output)
        require(not list(work.rglob(".sed-*.tmp")), "Temporary staging files remained after acceptance")
        verify_captured_streams(report, work)
        require(runtime_identity(agent) == report["agent"] and runtime_identity(server) == report["mcp"],
                "An executable or captured managed runtime file changed during acceptance")
        report["status"] = "passed"
    except Exception as error:
        report["status"] = "failed"
        report["error"] = {"type": type(error).__name__, "message": str(error)}
        write_new(work / "failure.txt", traceback.format_exc().encode("utf-8"))
    finally:
        report["finishedUtc"] = utc()
        report["callCount"] = len(report["calls"])
        report["evidence"] = [file_record(path, work) for path in sorted(work.rglob("*")) if path.is_file()]
        # verification.json is the receipt for this inventory, not self-hashed.
        write_new(work / "verification.json", json_bytes(report))
    print(json.dumps({"status": report["status"], "calls": report["callCount"],
                      "verification": str(work / "verification.json")}))
    return 0 if report["status"] == "passed" else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--agent", type=Path, required=True, help="Explicit native sed-agent executable")
    parser.add_argument("--mcp", type=Path, required=True, help="Explicit native sed-mcp executable")
    parser.add_argument("--work", type=Path, required=True, help="New evidence directory; must not already exist")
    args = parser.parse_args()
    agent, server = args.agent.resolve(strict=True), args.mcp.resolve(strict=True)
    require(agent.is_file() and server.is_file(), "Both executable paths must name files")
    return run(agent, server, args.work.absolute())


if __name__ == "__main__":
    raise SystemExit(main())
