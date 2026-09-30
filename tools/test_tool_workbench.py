#!/usr/bin/env python3
"""Actual TOOL-01 CLI-process acceptance using original, isolated JK/MotS fixtures.

No GUI, game, network service or third-party asset is started or loaded. Results
establish authoring/serialization contracts, not collision clearance or gameplay.
Run against the exact agent binary to be qualified and a new output directory.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
import math
from pathlib import Path
import re
import struct
import subprocess
import time


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def utc() -> str:
    return datetime.now(timezone.utc).isoformat()


def write_json(path: Path, value):
    path.write_text(json.dumps(value, indent=2, ensure_ascii=True) + "\n", encoding="utf-8")


class Workflow:
    def __init__(self, agent: Path, output: Path):
        self.agent = agent
        self.output = output
        self.receipt = {"status": "running", "startedUtc": utc(), "agent": str(agent),
            "scriptSha256": sha(Path(__file__).resolve()), "calls": [], "checks": [],
            "inputs": [], "outputs": [], "runtimeHashes": [], "gameLaunched": False,
            "guiLaunched": False, "scope": "Original CLI-process authoring and serialization; no gameplay, collision-volume or retail parity proof."}
        for name in (agent.name, "sed-agent.dll", "sed-agent.deps.json", "sed-agent.runtimeconfig.json",
                     "Sed.Core.dll", "Sed.Formats.dll", "Sed.Rendering.dll", "Sed.Rendering.Vulkan.dll"):
            path = agent.parent / name
            if path.is_file():
                self.receipt["runtimeHashes"].append({"path": str(path), "sha256": sha(path)})
        self.check("core runtime identities available", all(any(Path(row["path"]).name == name
            for row in self.receipt["runtimeHashes"]) for name in (agent.name, "sed-agent.dll", "Sed.Core.dll", "Sed.Formats.dll")))

    def check(self, name: str, passed: bool):
        self.receipt["checks"].append({"name": name, "passed": bool(passed)})
        if not passed:
            raise RuntimeError("Check failed: " + name)

    def track(self, path: Path, role: str):
        self.receipt[role].append({"path": str(path), "sha256": sha(path)})

    def call(self, name: str, arguments: list[str], request=None, expected_exit: int = 0):
        stem = f"{len(self.receipt['calls']) + 1:03d}-{name}"
        payload = b"" if request is None else json.dumps(request, separators=(",", ":"), allow_nan=False).encode("utf-8")
        (self.output / (stem + ".request.json")).write_bytes(payload)
        started = time.monotonic()
        row = {"name": name, "arguments": arguments, "requestFile": stem + ".request.json",
            "requestSha256": hashlib.sha256(payload).hexdigest(), "requestBytes": len(payload),
            "stdoutFile": stem + ".stdout.json", "stderrFile": stem + ".stderr.txt", "expectedExit": expected_exit}
        self.receipt["calls"].append(row)
        try:
            process = subprocess.run([str(self.agent), *arguments], input=payload, capture_output=True,
                cwd=self.output, timeout=60, check=False,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
            stdout, stderr = process.stdout, process.stderr
            row["exitCode"] = process.returncode
        except subprocess.TimeoutExpired as error:
            stdout, stderr = error.stdout or b"", error.stderr or b""
            row["timedOut"] = True
            raise
        finally:
            row["elapsedSeconds"] = round(time.monotonic() - started, 3)
            if "stdout" in locals():
                (self.output / row["stdoutFile"]).write_bytes(stdout)
                (self.output / row["stderrFile"]).write_bytes(stderr)
                row.update(stdoutSha256=hashlib.sha256(stdout).hexdigest(), stdoutBytes=len(stdout),
                    stderrSha256=hashlib.sha256(stderr).hexdigest(), stderrBytes=len(stderr))
        self.check(name + " exit", process.returncode == expected_exit)
        result = json.loads(stdout.decode("utf-8-sig"))
        self.check(name + " JSON object", isinstance(result, dict))
        return result

    def pages(self, name: str, path: Path, collection: str, **options):
        request = {"schemaVersion": 1, "collection": collection, "limit": 2, **options}
        items, expected_offset, identity, total = [], 0, None, None
        while True:
            page = self.call(f"{name}-{expected_offset}", ["query", str(path), "-"], request | {"offset": expected_offset})
            current = (page["inputSha256"], page["sourceRevision"], page["total"], page["sourceTotal"])
            if identity is None:
                identity, total = current, page["total"]
            self.check(name + " stable page source", current == identity and page["inputSha256"] == sha(path))
            self.check(name + " complete returned records", page["returnedCount"] == len(page["items"]) and page["offset"] == expected_offset)
            items.extend(page["items"])
            next_offset = page["nextOffset"]
            if next_offset is None:
                break
            self.check(name + " forward continuation", next_offset == expected_offset + len(page["items"]) and next_offset > expected_offset)
            expected_offset = next_offset
        self.check(name + " complete filtered enumeration", len(items) == total
            and (collection == "summary" or len({row["index"] for row in items}) == len(items)))
        return items

    def patch(self, source: Path, operations: list[dict], resources=None):
        query = self.call("source-revision", ["query", str(source), "-"], {"schemaVersion": 1, "collection": "summary"})
        patch = {"schemaVersion": 1, "inputSha256": sha(source), "sourceRevision": query["sourceRevision"], "operations": operations}
        if resources is not None:
            patch["resourceArchives"] = resources
        return patch

    def apply(self, name: str, source: Path, operations: list[dict], resources=None):
        patch = self.patch(source, operations, resources)
        output = self.output / (name + ".jkl")
        plan = self.call(name + "-plan", ["plan", str(source), "-"], patch)
        self.check(name + " planning preserves source", sha(source) == patch["inputSha256"] and not output.exists())
        if resources is not None:
            patch["resourceRevision"] = plan["resourceRevision"]
        applied = self.call(name + "-apply", ["apply", str(source), "-", str(output), plan["outputSha256"]], patch)
        self.check(name + " reviewed output hash", sha(output) == plan["outputSha256"] == applied["plan"]["outputSha256"])
        self.check(name + " apply preserves source", sha(source) == patch["inputSha256"])
        self.track(output, "outputs")
        return output, plan

    def refuse(self, name: str, source: Path, operations: list[dict], resources=None, message_part=None):
        output = self.output / ("refused-" + name + ".jkl")
        patch = self.patch(source, operations, resources)
        result = self.call(name, ["apply", str(source), "-", str(output)], patch, expected_exit=2)
        self.check(name + " refuses without output", result.get("error") == "invalid_request" and not output.exists())
        self.check(name + " preserves source", sha(source) == patch["inputSha256"])
        if message_part is not None:
            self.check(name + " intended rejection", message_part.lower() in result["message"].lower())


def section(text: str, name: str, body: str) -> str:
    replacement = f"SECTION: {name}\n{body}\nend"
    pattern = rf"^SECTION:\s*{re.escape(name)}\b.*?^end[ \t]*$"
    output, count = re.subn(pattern, lambda _: replacement, text, flags=re.IGNORECASE | re.MULTILINE | re.DOTALL)
    if count > 1:
        raise RuntimeError("Duplicate fixture section: " + name)
    return output if count else text + "\n" + replacement + "\n"


def fixture(text: str, capacity=256, existing=False, cog_reference=None) -> str:
    text = section(text, "TEMPLATES", "World templates 1\nwb_original none type=cog")
    things = f"World things {capacity}"
    if existing:
        things += "\n0: wb_original existing 0 0 0 0 0 0 0 -1"
    text = section(text, "THINGS", things)
    if cog_reference is not None:
        text = section(text, "COGS", f"World cogs 1\n0: workbench.cog {cog_reference} 1")
    return text


def positions_close(actual, expected):
    return len(actual) == len(expected) and all(math.isclose(a, b, rel_tol=1e-12, abs_tol=1e-12) for a, b in zip(actual, expected))


def fresh_thing_row(index, name, position, orientation):
    """Independently specified full readback for these original template instances."""
    return {"index": index, "num": index, "name": name, "template": "wb_original", "position": position,
        "orientation": orientation, "sector": 0, "layer": 0, "parameters": {}, "sourceParameters": [],
        "archLightingIndex": -1, "archLightingSourceModel": "", "archLightingSourceModelHash": "",
        "architecture": {"status": "unbound", "resourceStatus": "unverified-use-resource-validation",
            "provenance": None, "samplesSha256": None, "meshVertexCounts": None, "profileLimits": None}}


def expected_workbench_rows(game):
    # Analytical results, not values extracted from requests, receipts or output.
    # Four radial quadrants use mathematical zero; the comparison below permits
    # only the small trigonometric roundoff in position, never in stored fields.
    positions = [[-.5, -.5, 0], [0, -.5, 0], [.5, -.5, 0],
        [-.5, 0, 0], [0, 0, 0], [-.5, .5, 0], [0, .5, 0],
        [.5, 0, 0], [0, .5, 0], [-.5, 0, 0], [0, -.5, 0]]
    things, lights = [], []
    for index, position in enumerate(positions):
        group, start = ("linear", 0) if index < 3 else ("grid", 3) if index < 7 else ("radial", 7)
        things.append(fresh_thing_row(index, f"{group}_{index - start:03d}", position, [10, 20, 30]))
        white = 3 if index in (0, 3, 7) else 1
        stored = None if game == "jk" or index < 3 else 0 if index < 7 else .75
        lights.append({"index": index, "num": index, "flags": 1 if index < 3 else 3 if index < 7 else 4,
            "layer": 0, "position": position, "rgb": [.25, .5, .75] if game == "mots" else [1, 1, 1],
            "intensity": white, "range": 8, "rgbIntensity": stored,
            "effectiveRgbIntensity": white if stored is None else stored,
            "legacyRgbRange": 99 if game == "mots" else None, "rgbIntensityInherited": stored is None})
    return things, lights


def complete_row_matches(actual, expected):
    """Compare every field and nested value; position alone has a numeric tolerance."""
    return set(actual) == set(expected) and positions_close(actual["position"], expected["position"]) \
        and {key: value for key, value in actual.items() if key != "position"} \
        == {key: value for key, value in expected.items() if key != "position"}


def workbench(flow: Workflow, game: str):
    scaffold = flow.output / (game + "-scaffold.jkl")
    flow.call(game + "-new", ["new", str(scaffold), game])
    flow.track(scaffold, "inputs")
    source = flow.output / (game + "-source.jkl")
    source.write_text(fixture(scaffold.read_text(encoding="utf-8-sig")), encoding="utf-8", newline="\n")
    flow.track(source, "inputs")
    vertices = flow.pages(game + "-vertices", source, "vertices", sector=0)
    if game == "mots":
        ops = [{"op": "setVertexLight", "sector": 0, "vertex": row["index"], "channel": channel, "value": value}
            for row in vertices for channel, value in (("mono", .125), ("red", .25), ("green", .5), ("blue", .75))]
        source, _ = flow.apply(game + "-independent-corners", source, ops)
        flow.track(source, "inputs")
    surfaces = flow.pages(game + "-surfaces", source, "surfaces", sector=0)
    targets = [{"sector": 0, "surface": row["index"]} for row in surfaces if abs(row["normal"][2]) > .99]
    flow.check(game + " two parallel projection faces", len(targets) == 2)
    project = {"op": "projectSurfaceUv", "surfaces": targets, "direction": "+z", "origin": [0, 0, 0], "texelsPerUnit": 32}
    transform = {"op": "transformSurfaceUv", "surfaces": targets, "pivot": [0, 0], "scale": [2, 3], "rotationDegrees": 90, "offset": [5, -7]}
    layouts = [
        ("linear", {"kind": "linear", "origin": [-.5, -.5, 0], "step": [.5, 0, 0], "count": 3},
            [[-.5, -.5, 0], [0, -.5, 0], [.5, -.5, 0]]),
        ("grid", {"kind": "grid", "origin": [-.5, 0, 0], "stepU": [.5, 0, 0], "stepV": [0, .5, 0], "countU": 2, "countV": 2},
            [[-.5, 0, 0], [0, 0, 0], [-.5, .5, 0], [0, .5, 0]]),
        ("radial", {"kind": "radial", "origin": [0, 0, 0], "axis": [0, 0, 1], "radius": .5, "count": 4, "startDegrees": 0, "sweepDegrees": 360},
            [[.5, 0, 0], [0, .5, 0], [-.5, 0, 0], [0, -.5, 0]])]
    operations = [project, transform]
    for index, (name, layout, _) in enumerate(layouts):
        operations.append({"op": "addThingArray", "template": "wb_original", "namePrefix": name, "layout": layout, "orientation": [10, 20, 30]})
        light = {"op": "addLightArray", "layout": layout, "rgb": [.25, .5, .75] if game == "mots" else [1, 1, 1],
            "intensity": 1, "range": 8, "flags": [1, 3, 4][index], "rgbIntensity": [None, 0, .75][index] if game == "mots" else None}
        if game == "mots":
            light["legacyRgbRange"] = 99
        operations.append(light)
    operations.extend({"op": "setLight", "light": index, "intensity": 3} for index in (0, 3, 7))
    output, plan = flow.apply(game + "-workbench", source, operations)
    changed = flow.pages(game + "-uv-readback", output, "surfaces", sector=0)
    flow.check(game + " all ordered surfaces retained", [row["index"] for row in changed] == [row["index"] for row in surfaces])
    output_vertices = flow.pages(game + "-vertex-readback", output, "vertices", sector=0)
    flow.check(game + " complete vertex geometry retained", output_vertices == vertices)
    points = {row["index"]: row["position"] for row in vertices}
    target_indices = {row["surface"] for row in targets}
    for before, after in zip(surfaces, changed):
        before_without_uv = dict(before) | {"corners": [{k: v for k, v in c.items() if k != "uv"} for c in before["corners"]]}
        after_without_uv = dict(after) | {"corners": [{k: v for k, v in c.items() if k != "uv"} for c in after["corners"]]}
        flow.check(game + " preserved surface and lighting", before_without_uv == after_without_uv)
        if after["index"] in target_indices:
            for corner in after["corners"]:
                x, y, _ = points[corner["vertex"]]
                flow.check(game + " independent analytic UV", positions_close(corner["uv"], [5 - 96 * y, -7 + 64 * x]))
                if game == "mots":
                    flow.check("MotS independent corner channels", corner["mono"] == .125 and corner["rgb"] == [.25, .5, .75])
        else:
            flow.check(game + " unselected UV unchanged", before == after)
    flow.check(game + " serialized final supersedes earlier projection", plan["changes"][0]["serializedFinal"] == plan["changes"][1]["serializedFinal"]
        and plan["changes"][0]["afterOperation"] != plan["changes"][0]["serializedFinal"])
    things = flow.pages(game + "-things", output, "things", filter={"template": "WB_ORIGINAL"})
    lights = flow.pages(game + "-lights", output, "lights")
    expected_things, expected_lights = expected_workbench_rows(game)
    flow.receipt.setdefault("expectedWorkbenchRows", {})[game] = {"things": expected_things, "lights": expected_lights}
    flow.check(game + " all generated indices", [t["index"] for t in things] == list(range(11))
        and [light["index"] for light in lights] == list(range(11)))
    for actual, expected in zip(things, expected_things):
        flow.check(f"{game} complete authored thing row {expected['index']}", complete_row_matches(actual, expected))
    for actual, expected in zip(lights, expected_lights):
        flow.check(f"{game} complete authored source light row {expected['index']}", complete_row_matches(actual, expected))
    selected = flow.pages(game + "-filtered-flags", output, "lights", filter={"flags": {"any": 3, "all": 1, "none": 4}})
    flow.check(game + " flag predicates preserve original indices", [row["index"] for row in selected] == list(range(7)))
    exact = flow.pages(game + "-exact-name", output, "things", filter={"name": "RADIAL_002", "template": "WB_ORIGINAL"})
    flow.check(game + " exact name and template filter", [row["index"] for row in exact] == [9])
    untextured = flow.pages(game + "-filtered-surfaces", output, "surfaces", sector=0, filter={"materialIndex": -1, "layer": 0})
    flow.check(game + " untextured exact surface filter", [row["index"] for row in untextured] == [row["index"] for row in surfaces])
    summary = flow.pages(game + "-summary", output, "summary")[0]
    flow.check(game + " unchanged capacity and exact counts", summary["thingCapacity"] == 256 and summary["counts"]["things"] == 11 and summary["counts"]["lights"] == 11)
    validation = flow.call(game + "-validate", ["validate", str(output)])
    flow.check(game + " reopened structural validation", validation["valid"])

    flow.refuse(game + "-zero-axis", source, [{"op": "addLightArray", "rgb": [1, 1, 1], "intensity": 1, "range": 8,
        "layout": layouts[2][1] | {"axis": [0, 0, 0]}}], message_part="nonzero")
    flow.refuse(game + "-duplicate-points", source, [{"op": "addThingArray", "template": "wb_original", "namePrefix": "bad",
        "layout": layouts[0][1] | {"step": [0, 0, 0]}}], message_part="distinct")
    flow.refuse(game + "-duplicate-surfaces", source, [project | {"surfaces": [targets[0], targets[0]]}], message_part="duplicate")
    edge = next(row["index"] for row in surfaces if abs(row["normal"][2]) < .01)
    flow.refuse(game + "-edge-on", source, [project | {"surfaces": [targets[0], {"sector": 0, "surface": edge}]}], message_part="edge-on")
    constrained = flow.output / (game + "-capacity-one.jkl")
    constrained.write_text(fixture(source.read_text(encoding="utf-8"), capacity=1), encoding="utf-8", newline="\n")
    flow.track(constrained, "inputs")
    flow.refuse(game + "-capacity", constrained, [{"op": "addThingArray", "template": "wb_original", "namePrefix": "bad",
        "layout": layouts[0][1]}], message_part="capacity")
    return scaffold


def cog_controls(flow: Workflow, scaffold: Path):
    script = b"symbols\nthing target\nint scalar\nmessage startup\nend\ncode\nstartup:\nreturn;\nend\n"
    archive = flow.output / "workbench-original.goo"
    name = b"cog\\workbench.cog"
    archive.write_bytes(struct.pack("<4sII", b"GOB ", 0x14, 12 + len(script)) + script
        + struct.pack("<III128s", 1, 12, len(script), name))
    flow.track(archive, "inputs")
    resources = [{"path": str(archive), "sha256": sha(archive)}]
    operation = {"op": "addThingArray", "template": "wb_original", "namePrefix": "safe",
        "layout": {"kind": "linear", "origin": [.2, 0, 0], "step": [.2, 0, 0], "count": 2}}
    for raw in ("0", "-1", "1", "99"):
        source = flow.output / ("cog-reference-" + raw.replace("-", "minus") + ".jkl")
        source.write_text(fixture(scaffold.read_text(encoding="utf-8-sig"), existing=True, cog_reference=raw), encoding="utf-8", newline="\n")
        flow.track(source, "inputs")
        if raw in ("0", "-1"):
            before_things = flow.pages("cog-original-things-" + raw.replace("-", "minus"), source, "things")
            flow.check("COG fixture has exactly one original thing", len(before_things) == 1 and before_things[0]["index"] == 0)
            output, _ = flow.apply("cog-safe-" + raw.replace("-", "minus"), source, [operation], resources)
            rows = flow.pages("cog-readback-" + raw.replace("-", "minus"), output, "cogs")
            flow.check("typed thing and scalar values retained", len(rows) == 1 and rows[0]["values"] == [raw, "1"])
            after_things = flow.pages("cog-appended-things-" + raw.replace("-", "minus"), output, "things")
            flow.check("COG case appends exactly two things", [row["index"] for row in after_things] == [0, 1, 2])
            flow.check("COG case preserves complete old target", after_things[0] == before_things[0])
            for index, position in ((1, [.2, 0, 0]), (2, [.4, 0, 0])):
                expected = fresh_thing_row(index, f"safe_{index - 1:03d}", position, [0, 0, 0])
                flow.check(f"COG case complete appended thing {index}", complete_row_matches(after_things[index], expected))
            flow.refuse("cog-missing-schema-" + raw.replace("-", "minus"), source, [operation], message_part="schema")
        else:
            flow.refuse("cog-future-" + raw, source, [operation], resources, message_part="currently placed")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--agent", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    args = parser.parse_args()
    agent, output = args.agent.resolve(strict=True), args.output_dir.resolve()
    if not agent.is_file():
        parser.error("--agent must name an existing executable file")
    output.mkdir(parents=True, exist_ok=False)
    flow = None
    try:
        flow = Workflow(agent, output)
        version = flow.call("version", ["--version"])
        flow.receipt["version"] = version
        flow.check("new capabilities advertised", all(capability in version["capabilities"] for capability in
            ("exact-query-filters", "batch-surface-uv", "bounded-placement-arrays")))
        flow.call("help", ["--help"])
        for topic in ("query", "projectSurfaceUv", "transformSurfaceUv", "addThingArray", "addLightArray"):
            flow.call("schema-" + topic, ["schema", topic])
        workbench(flow, "jk")
        mots_scaffold = workbench(flow, "mots")
        cog_controls(flow, mots_scaffold)
        for row in flow.receipt["inputs"]:
            flow.check("retained input " + Path(row["path"]).name, sha(Path(row["path"])) == row["sha256"])
        for row in flow.receipt["outputs"]:
            flow.check("retained output " + Path(row["path"]).name, sha(Path(row["path"])) == row["sha256"])
        for call in flow.receipt["calls"]:
            for role in ("request", "stdout", "stderr"):
                artifact = output / call[role + "File"]
                flow.check("retained " + call["name"] + " " + role,
                    sha(artifact) == call[role + "Sha256"] and artifact.stat().st_size == call[role + "Bytes"])
        for row in flow.receipt["runtimeHashes"]:
            flow.check("unchanged runtime " + Path(row["path"]).name, sha(Path(row["path"])) == row["sha256"])
        flow.check("unchanged workflow source", sha(Path(__file__).resolve()) == flow.receipt["scriptSha256"])
        flow.receipt["status"] = "passed"
        return 0
    except Exception as error:
        if flow is None:
            write_json(output / "receipt.json", {"status": "failed", "error": repr(error), "finishedUtc": utc()})
        else:
            flow.receipt["status"] = "failed"
            flow.receipt["error"] = repr(error)
        return 1
    finally:
        if flow is not None:
            flow.receipt["finishedUtc"] = utc()
            flow.receipt["callCount"] = len(flow.receipt["calls"])
            flow.receipt["checkCount"] = len(flow.receipt["checks"])
            write_json(output / "receipt.json", flow.receipt)
            print(json.dumps({"status": flow.receipt["status"], "calls": flow.receipt["callCount"],
                "checks": flow.receipt["checkCount"], "receipt": str(output / "receipt.json"), "error": flow.receipt.get("error")}))


if __name__ == "__main__":
    raise SystemExit(main())
