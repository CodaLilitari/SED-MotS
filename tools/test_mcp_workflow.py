"""Actual stdio MCP acceptance against an explicitly supplied local SED package.

Only original fixtures are created, in a fresh output directory. No GUI/game or
model service is started. This standard-library client tests wire behavior,
replay/restart evidence and optional isolated PNG output, not engine parity.
"""
from __future__ import annotations
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import queue
import re
import struct
import subprocess
import threading
import time
import uuid


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write_gob(path: Path, entries: dict[str, bytes]):
    """Original fixture entries in the documented little-endian JK GOB table."""
    directory_offset = 12 + sum(len(value) for value in entries.values())
    with path.open("xb") as output:
        output.write(struct.pack("<4sII", b"GOB ", 0x14, directory_offset))
        for value in entries.values():
            output.write(value)
        output.write(struct.pack("<I", len(entries)))
        offset = 12
        for name, value in entries.items():
            encoded = name.encode("ascii")
            assert len(encoded) < 128
            output.write(struct.pack("<II128s", offset, len(value), encoded))
            offset += len(value)


def fixture_scaffold(source: Path, templates: str = "World templates 0") -> str:
    """Retain generated room geometry but remove unrelated bundled templates."""
    text = source.read_text(encoding="utf-8")
    text, count = re.subn(r"^SECTION:\s*TEMPLATES\b.*?^end[ \t]*$",
        f"SECTION: TEMPLATES\n{templates}\nend", text,
        count=1, flags=re.IGNORECASE | re.MULTILINE | re.DOTALL)
    assert count == 1, "Scaffold template layout changed; review fixture construction"
    return text


def fixture_single_thing(text: str, row: str) -> str:
    """Populate exactly one empty generated THINGS section, retaining its capacity."""
    sections = list(re.finditer(r"^SECTION:[ \t]*THINGS[ \t]*\r?\n(?P<body>.*?)^end[ \t]*$",
        text, flags=re.IGNORECASE | re.MULTILINE | re.DOTALL))
    assert len(sections) == 1, "Expected exactly one generated THINGS section"
    match = sections[0]
    records = [line.split("#", 1)[0].strip() for line in match.group("body").splitlines()]
    records = [line for line in records if line]
    assert len(records) == 1, "Generated scaffold must not already contain placed things or unrecognized THINGS records"
    header = re.fullmatch(r"World\s+things\s+([0-9]+)", records[0], flags=re.IGNORECASE)
    assert header is not None and int(header.group(1)) > 0, "Generated scaffold must retain a positive declared thing capacity"
    replacement = f"SECTION: THINGS\nWorld things {int(header.group(1))}\n{row}\nend"
    return text[:match.start()] + replacement + text[match.end():]


def typed_cog_workflow(client, workspace: Path, scaffold: Path):
    source = workspace / "typed-cog-source.jkl"
    text = fixture_scaffold(scaffold)
    assert not re.search(r"^SECTION:\s*COGS\b", text, flags=re.MULTILINE | re.IGNORECASE)
    source.write_text(text + "\nSECTION: COGS\nWorld cogs 1\n0: mcp_original.cog 0 -1 1.25\nend\n",
        encoding="utf-8", newline="\n")
    script = ("symbols\nsector trigger=0 linkid=3\nthing receiver=-1 nolink\nflex speed=1\n"
        "message startup\nend\ncode\nstartup:\nreturn;\nend\n").encode("ascii")
    archive = workspace / "typed-cog-original.goo"
    write_gob(archive, {"cog\\mcp_original.cog": script})
    source_hash, archive_hash = sha(source), sha(archive)
    resources = [{"path": str(archive), "sha256": archive_hash}]
    client.call("sed_discover", {"topic": "rebindCogReference"})
    query, _ = client.call("sed_query", {"level": str(source),
        "request": {"schemaVersion": 1, "collection": "cogs"}})
    assert query["total"] == 1 and query["items"][0]["values"] == ["0", "-1", "1.25"]
    binding_request = {"schemaVersion": 1, "inputSha256": source_hash,
        "resourceArchives": resources, "cog": 0, "limit": 2}
    bindings, _ = client.call("sed_diagnose", {"level": str(source), "kind": "bindings", "request": binding_request})
    assert bindings["schemaComplete"] and bindings["parameterCountMatches"] and bindings["nextOffset"] == 2
    assert bindings["rows"][0]["eventLinkCandidate"] and not bindings["rows"][1]["eventLinkCandidate"]
    assert bindings["scriptSha256"] == hashlib.sha256(script).hexdigest()
    last, _ = client.call("sed_diagnose", {"level": str(source), "kind": "bindings",
        "request": binding_request | {"offset": 2}})
    assert last["rows"][0]["parameterIndex"] == 2 and last["nextOffset"] is None
    operation = {"op": "rebindCogReference", "cog": 0, "parameter": 0, "symbol": "trigger",
        "kind": "sector", "targetIndex": -1, "scriptSha256": bindings["scriptSha256"]}
    patch = {"schemaVersion": 1, "inputSha256": source_hash, "sourceRevision": query["sourceRevision"],
        "resourceArchives": resources, "operations": [operation]}
    plan, _ = client.call("sed_plan", {"level": str(source), "request": patch})
    assert len(plan["resourceRevision"]) == 64 and sha(source) == source_hash
    reviewed = patch | {"resourceRevision": plan["resourceRevision"]}
    output = workspace / "typed-cog-rebound.jkl"
    applied, _ = client.call("sed_apply", {"level": str(source), "request": reviewed, "output": str(output),
        "operationId": str(uuid.uuid4()), "expectedOutputSha256": plan["outputSha256"]})
    assert applied["outcome"]["matchesExpectedOutput"] and sha(output) == plan["outputSha256"]
    readback, _ = client.call("sed_query", {"level": str(output),
        "request": {"schemaVersion": 1, "collection": "cogs"}})
    assert readback["items"][0]["values"] == ["-1", "-1", "1.25"]
    refusals = [
        ("script", reviewed | {"operations": [operation | {"scriptSha256": "0" * 64}]}, "conflict"),
        ("archive", reviewed | {"resourceArchives": [{"path": str(archive), "sha256": "0" * 64}]}, "conflict"),
        ("kind", reviewed | {"operations": [operation | {"kind": "thing"}]}, "invalid_request"),
    ]
    for name, request, expected_error in refusals:
        refused = workspace / f"typed-cog-refused-{name}.jkl"
        client.call("sed_apply", {"level": str(source), "request": request, "output": str(refused),
            "operationId": str(uuid.uuid4()), "expectedOutputSha256": plan["outputSha256"]}, expected_error)
        assert not refused.exists()
    assert sha(source) == source_hash and sha(archive) == archive_hash
    return {"source": str(source), "sourceSha256": source_hash, "archive": str(archive),
        "archiveSha256": archive_hash, "scriptSha256": bindings["scriptSha256"],
        "output": str(output), "outputSha256": sha(output), "resourceRevision": plan["resourceRevision"],
        "typedBindingChanged": "sector trigger: 0 -> -1; receiver and speed preserved",
        "refusals": [name for name, _, _ in refusals], "runtimeCogExecutionTested": False}


def architecture_bake_workflow(client, workspace: Path, scaffold: Path):
    # Original quad and closed room. The first sample (-.6,-.6,.2) is four units
    # from the source; range eight gives a squared falloff of exactly one quarter.
    # The room ceiling z=1 blocks every ray unless source NoBlock=1 is selected.
    model = """SECTION: HEADER
3DO 2.1
END
SECTION: MODELRESOURCE
MATERIALS 1
0: mcp_original.mat
END
SECTION: GEOMETRYDEF
RADIUS 1
INSERT OFFSET 0 0 0
GEOSETS 1
GEOSET 0
MESHES 1
MESH 0
NAME mcp_original_quad
GEOMETRYMODE 4
LIGHTINGMODE 3
TEXTUREMODE 3
VERTICES 4
0: -0.6 -0.6 0 0.05
1: 0.6 -0.6 0 0.05
2: 0.6 0.6 0 0.05
3: -0.6 0.6 0 0.05
TEXTURE VERTICES 4
0: 0 0
1: 2 0
2: 2 2
3: 0 2
VERTEX NORMALS 4
0: 0 0 1
1: 0 0 1
2: 0 0 1
3: 0 0 1
FACES 1
0: 0 0x1 4 3 3 0 4 0,0 1,1 2,2 3,3
FACE NORMALS 1
0: 0 0 1
END
SECTION: HIERARCHYDEF
HIERARCHY NODES 1
0: 0x0 0x10 0 -1 -1 -1 0 0 0 0 0 0 0 0 0 0 root
END
""".encode("ascii")
    archive = workspace / "architecture-original.gob"
    # Original indexed MAT and complete CMP, as in the CLI lighting acceptance.
    material = b"MAT " + struct.pack("<6i", 0x32, 2, 1, 1, 0, 8) + bytes(48 + 40)
    material += struct.pack("<6i", 2, 2, 0, 0, 0, 1) + bytes([1, 1, 1, 1])
    colormap = bytearray(64 + 768 + 16384)
    colormap[:4] = b"CMP "
    colormap[67:70] = bytes([200, 160, 100])
    for row in range(64):
        colormap[832 + row * 256:832 + (row + 1) * 256] = bytes(range(256))
    write_gob(archive, {"3do\\mcp_original.3do": model, "mat\\mcp_original.mat": material,
        "misc\\cmp\\dflt.cmp": bytes(colormap)})
    archive_hash = sha(archive)
    inspection, _ = client.call("sed_archive", {"archive": str(archive)})
    assert inspection["inputSha256"] == archive_hash and inspection["levels"] == []
    resources = [{"path": str(archive), "sha256": archive_hash}]
    source = workspace / "architecture-source.jkl"
    text = fixture_scaffold(scaffold,
        "World templates 1\nmcp_panel none type=cog thingflags=0x8 model3d=mcp_original.3do")
    assert not re.search(r"^SECTION:\s*MODELS\b", text, flags=re.MULTILINE | re.IGNORECASE)
    text = fixture_single_thing(text, "0: mcp_panel MCPPanel 0 0 0.2 0 0 0 0 -1")
    text += "\nSECTION: MODELS\nWorld models 1\n0: mcp_original.3do\nend\n"
    source.write_text(text, encoding="utf-8", newline="\n")
    source_hash = sha(source)
    schema, _ = client.call("sed_discover", {"topic": "bakeThingArchitecture"})
    assert "source-points-world-occluded-v1" in json.dumps(schema)
    query, _ = client.call("sed_query", {"level": str(source),
        "request": {"schemaVersion": 1, "collection": "things"}})
    assert query["total"] == 1 and query["items"][0]["archLightingIndex"] == -1
    base_patch = {"schemaVersion": 1, "inputSha256": source_hash, "sourceRevision": query["sourceRevision"],
        "resourceArchives": resources}
    no_resources = {"schemaVersion": 1, "inputSha256": source_hash,
        "operations": [{"op": "bakeThingArchitecture", "thing": 0, "profile": "source-points-v1"}]}
    client.call("sed_plan", {"level": str(source), "request": no_resources}, "invalid_request")
    cases = [("unoccluded", "source-points-v1", 0, [0.2, 0.1, 0.05, 0.025]),
        ("blocked", "source-points-world-occluded-v1", 0, [0, 0, 0, 0]),
        ("noblock", "source-points-world-occluded-v1", 1, [0.2, 0.1, 0.05, 0.025])]
    records = []
    for name, profile, flags, expected in cases:
        patch = base_patch | {"operations": [
            {"op": "addLight", "position": [-0.6, -0.6, 4.2], "rgb": [1, 0.5, 0.25],
                "intensity": 0.8, "rgbIntensity": 0.4, "range": 8, "flags": flags},
            {"op": "bakeThingArchitecture", "thing": 0, "profile": profile}]}
        plan, _ = client.call("sed_plan", {"level": str(source), "request": patch})
        effect = plan["changes"][1]["serializedFinal"]["state"]
        assert effect["archLightingIndex"] == 0 and plan["after"]["architecturalObjects"] == 1
        provenance = effect["architecture"]["provenance"]
        assert effect["architecture"]["status"] == "current-inputs" and provenance["origin"] == profile
        assert provenance["modelSha256"] == hashlib.sha256(model).hexdigest()
        reviewed = patch | {"resourceRevision": plan["resourceRevision"]}
        if name == "unoccluded":
            for refusal, invalid in [("resource-revision", reviewed | {"resourceRevision": "0" * 64}),
                    ("archive", reviewed | {"resourceArchives": [{"path": str(archive), "sha256": "0" * 64}]})]:
                refused = workspace / f"architecture-refused-{refusal}.jkl"
                client.call("sed_apply", {"level": str(source), "request": invalid, "output": str(refused),
                    "operationId": str(uuid.uuid4()), "expectedOutputSha256": plan["outputSha256"]}, "conflict")
                assert not refused.exists()
        output = workspace / f"architecture-{name}.jkl"
        applied, _ = client.call("sed_apply", {"level": str(source), "request": reviewed, "output": str(output),
            "operationId": str(uuid.uuid4()), "expectedOutputSha256": plan["outputSha256"]})
        assert sha(output) == plan["outputSha256"] and applied["outcome"]["matchesExpectedOutput"]
        saved, _ = client.call("sed_query", {"level": str(output),
            "request": {"schemaVersion": 1, "collection": "things"}})
        architecture = saved["items"][0]["architecture"]
        assert architecture["status"] == "current-inputs"
        assert architecture["resourceStatus"] == "unverified-use-resource-validation"
        assert architecture["provenance"] == provenance
        samples, _ = client.call("sed_query", {"level": str(output), "request": {
            "schemaVersion": 1, "collection": "architecturalVertices", "lightingIndex": 0, "mesh": 0, "limit": 4}})
        assert samples["total"] == 4 and samples["returnedCount"] == 4 and samples["nextOffset"] is None
        first = samples["items"][0]
        actual = [first["mono"], *first["rgb"]]
        assert all(abs(a - b) < 1e-12 for a, b in zip(actual, expected)), (name, actual, expected)
        if name == "blocked":
            assert all(sample["mono"] == 0 and sample["rgb"] == [0, 0, 0] for sample in samples["items"])
        else:
            assert all(sample["mono"] > 0 and all(channel > 0 for channel in sample["rgb"]) for sample in samples["items"])
        records.append({"name": name, "profile": profile, "flags": flags, "firstSample": first,
            "output": str(output), "outputSha256": sha(output), "provenance": provenance,
            "resourceRevision": plan["resourceRevision"]})
        assert sha(source) == source_hash and sha(archive) == archive_hash
    assert records[1]["provenance"]["inputSha256"] != records[2]["provenance"]["inputSha256"]
    assert records[1]["provenance"]["samplesSha256"] != records[2]["provenance"]["samplesSha256"]
    return {"source": str(source), "sourceSha256": source_hash, "archive": str(archive),
        "archiveSha256": archive_hash, "modelSha256": hashlib.sha256(model).hexdigest(), "cases": records,
        "limits": "Editor point-source/world-polygon recipes only; no material-alpha, model/self shadows, gameplay or retail parity."}


class Client:
    def __init__(self, server: Path, worker: Path, workspace: Path):
        self.events: queue.Queue = queue.Queue()
        self.stderr = bytearray()
        self.calls = []
        self.unmatched_responses = []
        self.seq = 0
        self.process = subprocess.Popen(
            [str(server), "--workspace", str(workspace), "--agent", str(worker)],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
        )
        def stdout():
            try:
                while True:
                    line = self.process.stdout.readline(32 * 1024 * 1024 + 1)
                    if not line:
                        self.events.put(RuntimeError("MCP stdout closed")); return
                    if len(line) > 32 * 1024 * 1024:
                        raise RuntimeError("MCP response exceeded 32 MiB")
                    self.events.put(json.loads(line))
            except Exception as error:
                self.events.put(error)
        def stderr():
            while data := self.process.stderr.read(4096):
                if len(self.stderr) + len(data) <= 1024 * 1024:
                    self.stderr.extend(data)
        threading.Thread(target=stdout, daemon=True).start()
        threading.Thread(target=stderr, daemon=True).start()
        initialized = self.rpc("initialize", {"protocolVersion": "2025-11-25",
            "capabilities": {}, "clientInfo": {"name": "sed-mcp-acceptance", "version": "1"}})
        assert initialized["serverInfo"]["name"] == "sed-mcp"
        assert initialized["protocolVersion"] in ("2024-11-05", "2025-03-26", "2025-06-18", "2025-11-25")
        self.send({"jsonrpc": "2.0", "method": "notifications/initialized"})
        self.initialized = initialized

    def send(self, body):
        self.process.stdin.write(json.dumps(body, separators=(",", ":")).encode() + b"\n")
        self.process.stdin.flush()

    def rpc(self, method, params):
        self.seq += 1
        current = self.seq
        self.send({"jsonrpc": "2.0", "id": current, "method": method, "params": params})
        while True:
            result = self.events.get(timeout=210)
            if isinstance(result, Exception):
                raise result
            if result.get("id") == current:
                if "error" in result:
                    raise AssertionError(result["error"])
                return result["result"]
            if "id" in result:
                self.unmatched_responses.append(result)

    def call(self, name, arguments, error=None):
        result = self.rpc("tools/call", {"name": name, "arguments": arguments})
        structured = result["structuredContent"]
        text = next(c["text"] for c in result["content"] if c["type"] == "text")
        assert json.loads(text) == structured, "Text/structured result diverged"
        assert structured["schemaVersion"] == 1
        if error is None:
            assert not result.get("isError", False) and structured["ok"], structured
        else:
            assert result["isError"] and not structured["ok"], structured
            assert structured["error"]["code"] == error, structured
        self.calls.append({"tool": name, "arguments": arguments,
            "result": structured, "imageCount": sum(c["type"] == "image" for c in result["content"])})
        return structured.get("result"), result

    def close(self):
        if self.process.stdin and not self.process.stdin.closed:
            self.process.stdin.close()
        try:
            self.process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait(timeout=5)
            raise AssertionError("MCP host failed to exit after stdin closed")
        assert self.process.returncode == 0, self.stderr.decode(errors="replace")


def interrupted_write_workflow(server: Path, worker: Path, directory: Path):
    """Real protocol cancellation and an uninspected reply, without child renderers."""
    directory.mkdir()
    records, sessions = [], []
    for cancel in (False, True):
        client = Client(server, worker, directory)
        operation_id = str(uuid.uuid4())
        target = directory / ("cancel-requested.jkl" if cancel else "reply-uninspected.jkl")
        try:
            plan, _ = client.call("sed_new", {"game": "mots"})
            arguments = {"game": "mots", "output": str(target), "operationId": operation_id,
                "expectedOutputSha256": plan["outputSha256"]}
            client.seq += 1
            request_id = client.seq
            client.send({"jsonrpc": "2.0", "id": request_id, "method": "tools/call",
                "params": {"name": "sed_new", "arguments": arguments}})
            if cancel:
                client.send({"jsonrpc": "2.0", "method": "notifications/cancelled",
                    "params": {"requestId": request_id, "reason": "Acceptance cancellation probe"}})
                # Verify transport still responds, then fully stop it before reconciliation.
                # Cancellation may legitimately arrive after promotion; never assume rollback.
                client.rpc("tools/list", {})
                client.close()
            else:
                # Observe through the supported API: polling the receipt file can
                # deny its atomic replacement on Windows and create the failure
                # the probe is trying to observe. The original write reply is
                # still never inspected to decide its outcome.
                deadline = time.monotonic() + 10
                while True:
                    observed, _ = client.call("sed_outcome", {"operationId": operation_id})
                    outcome = observed["outcome"]
                    if outcome is not None and outcome["observedState"] == "committed":
                        break
                    assert outcome is None or outcome["observedState"] not in ("failed", "unknown"), outcome
                    assert client.process.poll() is None, "Host exited before commit receipt"
                    assert time.monotonic() < deadline, "No durable commit within bounded probe"
                    time.sleep(0.01)
                client.process.kill()
                client.process.wait(timeout=5)
                client.process.stdin.close()
            sessions.append(client.calls)
        finally:
            if client.process.poll() is None:
                client.close()
        reconnected = Client(server, worker, directory)
        try:
            status, _ = reconnected.call("sed_outcome", {"operationId": operation_id})
            outcome = status["outcome"]
            if outcome is None:
                assert cancel and not target.exists()
            elif outcome["observedState"] == "committed":
                assert target.exists() and outcome["matchesExpectedOutput"]
                before = sha(target)
                replay, _ = reconnected.call("sed_new", arguments)
                assert replay["replayed"] and sha(target) == before
            else:
                assert cancel and outcome["observedState"] in ("failed", "unknown")
                if target.exists():
                    assert sha(target) == plan["outputSha256"]
                replay, _ = reconnected.call("sed_new", arguments)
                assert replay["replayed"], "An existing interrupted intent must never execute again"
            records.append({"operationId": operation_id, "cancellationRequested": cancel,
                "originalReplyUsedToDecideOutcome": False, "outcome": outcome,
                "outputExists": target.exists(), "unmatchedResponses": client.unmatched_responses,
                "limit": "A late cancellation may commit. The first probe kills only after sed_outcome observes the durable commit; it does not simulate every crash boundary."})
            sessions.append(reconnected.calls)
        finally:
            reconnected.close()
    return records, sessions


def run(server: Path, worker: Path, output: Path, snapshots: bool):
    if not __debug__:
        raise RuntimeError("Acceptance assertions require normal Python mode; remove -O/PYTHONOPTIMIZE.")
    output.mkdir(parents=True, exist_ok=False)
    workspace = output / "project"
    workspace.mkdir()
    report = {"schemaVersion": 1, "status": "running", "server": str(server),
        "serverSha256": sha(server), "managedSha256": sha(server.with_suffix(".dll")),
        "worker": str(worker), "snapshotsRequested": snapshots, "sessions": [],
        "guiLaunched": False, "gameLaunched": False}
    client = None
    try:
        client = Client(server, worker, workspace)
        report["negotiation"] = client.initialized
        tools = client.rpc("tools/list", {})["tools"]
        assert len(tools) == 11 and len({t["name"] for t in tools}) == 11
        client.call("sed_discover", {})
        client.call("sed_discover", {"topic": "setGravity"})
        client.call("sed_discover", {"typo": True}, "invalid_request")
        plan, _ = client.call("sed_new", {"game": "mots"})
        source = workspace / "room.jkl"
        original_id = str(uuid.uuid4())
        create = {"game": "mots", "output": str(source), "operationId": original_id,
            "expectedOutputSha256": plan["outputSha256"]}
        client.call("sed_new", create | {"expectedOutputSha256": "0" * 64}, "conflict")
        assert not source.exists()
        created, _ = client.call("sed_new", create)
        assert created["outcome"]["receipt"]["state"] == "committed"
        assert sha(source) == plan["outputSha256"]
        replay, _ = client.call("sed_new", create)
        assert replay["replayed"]
        client.call("sed_new", create | {"game": "jk"}, "conflict")
        query, _ = client.call("sed_query", {"level": str(source), "request": {"schemaVersion": 1, "collection": "summary"}})
        converted_plan, _ = client.call("sed_convert", {"level": str(source)})
        converted_output = workspace / "converted.jkl"
        client.call("sed_convert", {"level": str(source), "output": str(converted_output),
            "operationId": str(uuid.uuid4()), "expectedOutputSha256": converted_plan["outputSha256"],
            "expectedInputSha256": converted_plan["inputSha256"],
            "expectedSourceRevision": converted_plan["sourceRevision"]})
        assert sha(converted_output) == converted_plan["outputSha256"] and sha(source) == query["inputSha256"]
        patch = {"schemaVersion": 1, "inputSha256": query["inputSha256"],
            "operations": [{"op": "setGravity", "value": 6}]}
        planned, _ = client.call("sed_plan", {"level": str(source), "request": patch})
        edit_id = str(uuid.uuid4())
        edited = workspace / "edited.jkl"
        apply = {"level": str(source), "request": patch, "output": str(edited),
            "operationId": edit_id, "expectedOutputSha256": planned["outputSha256"]}
        client.call("sed_apply", apply | {"expectedOutputSha256": "0" * 64}, "conflict")
        client.call("sed_apply", apply | {"request": patch | {"inputSha256": "0" * 64}}, "conflict")
        assert not edited.exists()
        applied, _ = client.call("sed_apply", apply)
        assert applied["outcome"]["matchesExpectedOutput"]
        assert sha(source) == query["inputSha256"]
        assert sha(edited) == planned["outputSha256"]
        client.call("sed_diagnose", {"level": str(edited), "kind": "validate"})
        client.call("sed_apply", apply | {"request": patch | {"operations": [{"op": "setGravity", "value": 7}]}}, "conflict")
        client.call("sed_query", {"level": str(output / "outside.jkl"), "request": {"schemaVersion": 1, "collection": "summary"}}, "invalid_request")
        assert not (output / "outside.jkl").exists()
        # Geometry-selected design, reciprocal portal readback and independent MotS source channels.
        client.call("sed_discover", {"topic": "extrudeSurface"})
        selected, _ = client.call("sed_select", {"level": str(edited), "request": {
            "schemaVersion": 1, "kind": "surface", "sectorPoint": [0, 0, 0], "outward": [1, 0, 0]}})
        assert selected["status"] == "unique"
        design_patch = {"schemaVersion": 1, "inputSha256": sha(edited), "sourceRevision": selected["sourceRevision"],
            "operations": [{"op": "extrudeSurface", "target": selected["selection"], "distance": 2},
                {"op": "addLight", "position": [0, 0, 0], "rgb": [1, 0.5, 0.25],
                 "intensity": 0.2, "rgbIntensity": 0.7, "range": 3}]}
        design_plan, _ = client.call("sed_plan", {"level": str(edited), "request": design_patch})
        connected = workspace / "connected.jkl"
        client.call("sed_apply", {"level": str(edited), "request": design_patch, "output": str(connected),
            "operationId": str(uuid.uuid4()), "expectedOutputSha256": design_plan["outputSha256"]})
        sector_page, _ = client.call("sed_query", {"level": str(connected), "request": {"schemaVersion": 1, "collection": "sectors"}})
        assert sector_page["total"] == 2
        surfaces = []
        for sector in sector_page["items"]:
            page, _ = client.call("sed_query", {"level": str(connected), "request": {
                "schemaVersion": 1, "collection": "surfaces", "sector": sector["index"]}})
            surfaces.append(page["items"])
        portals = [(s, face) for s, faces in enumerate(surfaces) for face in faces if face["adjoinSector"] >= 0]
        assert len(portals) == 2
        for sector, face in portals:
            reciprocal = surfaces[face["adjoinSector"]][face["adjoinSurface"]]
            assert reciprocal["adjoinSector"] == sector and reciprocal["adjoinSurface"] == face["index"]
        light_page, _ = client.call("sed_query", {"level": str(connected), "request": {"schemaVersion": 1, "collection": "lights"}})
        assert light_page["total"] == 1
        assert light_page["items"][0]["intensity"] == 0.2 and light_page["items"][0]["rgbIntensity"] == 0.7
        client.call("sed_diagnose", {"level": str(connected), "kind": "validate"})
        assert sha(edited) == planned["outputSha256"]
        report["typedCog"] = typed_cog_workflow(client, workspace, source)
        report["architectureBake"] = architecture_bake_workflow(client, workspace, source)
        assert sha(source) == query["inputSha256"]
        if snapshots:
            image_request = {"schemaVersion": 1, "inputSha256": sha(connected),
                "position": [0, -1, 0.2], "target": [0, 0, 0], "fieldOfViewDegrees": 70,
                "width": 320, "height": 240}
            preview, result = client.call("sed_snapshot", {"level": str(connected),
                "request": image_request, "output": str(workspace / "preview.png")})
            images = [c for c in result["content"] if c["type"] == "image"]
            assert len(images) == 1 and images[0]["mimeType"] == "image/png"
            decoded = base64.b64decode(images[0]["data"], validate=True)
            assert decoded == (workspace / "preview.png").read_bytes()
            assert hashlib.sha256(decoded).hexdigest() == preview["sha256"]
            assert decoded.startswith(b"\x89PNG\r\n\x1a\n")
            baked = report["architectureBake"]
            lit_source = Path(baked["cases"][2]["output"])
            lit_request = {"schemaVersion": 1, "inputSha256": sha(lit_source),
                "position": [0, -0.3, 0.8], "target": [0, 0, 0.2], "fieldOfViewDegrees": 70,
                "width": 320, "height": 240, "mode": "lit", "lightingMode": "rgb", "renderProfile": "openjk",
                "resourceArchives": [{"path": baked["archive"], "sha256": baked["archiveSha256"]}]}
            lit, lit_result = client.call("sed_snapshot", {"level": str(lit_source),
                "request": lit_request, "output": str(workspace / "lit-preview.png")})
            assert lit["receipt"]["mode"] == "lit" and lit["receipt"]["lightingMode"] == "rgb"
            assert lit["receipt"]["nonBackgroundPixels"] > 0
            assert len(lit["receipt"]["usedResources"]) >= 3
            lit_images = [c for c in lit_result["content"] if c["type"] == "image"]
            assert len(lit_images) == 1
            assert base64.b64decode(lit_images[0]["data"], validate=True) == (workspace / "lit-preview.png").read_bytes()
        report["sessions"].append(client.calls)
        client.close(); client = None
        # New host process: durable committed identity survives without automatic replay.
        client = Client(server, worker, workspace)
        status, _ = client.call("sed_outcome", {"operationId": edit_id})
        assert status["outcome"]["observedState"] == "committed"
        assert status["outcome"]["matchesExpectedOutput"]
        replay, _ = client.call("sed_apply", apply)
        assert replay["replayed"] and sha(edited) == planned["outputSha256"]
        # Historical commit remains committed even if somebody subsequently changes bytes.
        edited.write_bytes(edited.read_bytes() + b"\n# external modification\n")
        changed, _ = client.call("sed_outcome", {"operationId": edit_id})
        assert changed["outcome"]["observedState"] == "committed"
        assert changed["outcome"]["matchesExpectedOutput"] is False
        report["sessions"].append(client.calls)
        client.close(); client = None
        interruptions, interrupt_sessions = interrupted_write_workflow(server, worker, output / "interruption-project")
        report["interruptedWriteProbes"] = interruptions
        report["uninspectedWriteRequests"] = len(interruptions)
        report["sessions"].extend(interrupt_sessions)
        report["status"] = "passed"
    except Exception as error:
        report["status"] = "failed"
        report["error"] = str(error)
        if client:
            report["sessions"].append(client.calls)
        raise
    finally:
        if client:
            try:
                client.close()
            except Exception as error:
                report["shutdownError"] = str(error)
        report["toolCalls"] = sum(len(s) for s in report["sessions"])
        (output / "verification.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({"status": report["status"], "toolCalls": report["toolCalls"], "receipt": str(output / "verification.json")}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", type=Path, required=True)
    parser.add_argument("--agent", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--snapshots", action="store_true")
    options = parser.parse_args()
    run(options.server.resolve(strict=True), options.agent.resolve(strict=True), options.output.resolve(), options.snapshots)
