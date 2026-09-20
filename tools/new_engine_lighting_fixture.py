#!/usr/bin/env python3
"""Generate original ENGINE-01 scenarios using real sed-agent plan/apply.

This prepares files and starts only the explicitly supplied sed-agent executable.
It does not launch an engine, render, access game installs, or make parity claims.
Use: python tools/new_engine_lighting_fixture.py --agent <exe> --output <new-dir>
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import re
import struct
import subprocess
import sys
import time


PANEL_VERTICES = ((-0.8, -0.6, 0), (0.9, -0.6, 0), (0.65, 0.7, 0), (-0.6, 0.45, 0))
PANEL_POSITION = (0, 0, 0.2)
SPAWN_POSITION = (1.5, -1.5, 1.5)
SPAWN_VERTICES = ((-0.02, -0.02, 0), (0.02, -0.02, 0), (0.02, 0.02, 0), (-0.02, 0.02, 0))
LIGHT_POSITION = (-0.8, -0.6, 1.2)
LIGHT_RGB = (1, 0.5, 0.25)
WHITE_INTENSITY, RGB_INTENSITY, LIGHT_RANGE = 0.8, 0.4, 3.0
EXTENSIONS = ("SED_SOURCE_LIGHTS", "SED_ARCH_AUTHORING")
BASELINE_SAMPLES = ((0.7, 0.6, 0.2, 0.1), (0.6, 0.5, 0.15, 0.08),
                    (0.5, 0.4, 0.1, 0.06), (0.4, 0.3, 0.05, 0.04))
# Engine WORLD table declarations reserve storage; rows stop at END. Startup
# inventory can load resources after map parsing. Finite headroom is deliberately
# separate from populated original entries, not a measured minimum or asset list.
BOOTSTRAP_CAPACITIES = {"MATERIALS": "materials", "SOUNDS": "sounds", "MODELS": "models",
                        "SPRITES": "sprites", "KEYFRAMES": "keyframes", "ANIMCLASS": "puppets",
                        "SOUNDCLASS": "soundclasses", "AICLASS": "aiclasses", "COGSCRIPTS": "scripts",
                        "COGS": "cogs", "TEMPLATES": "templates", "THINGS": "things"}
BOOTSTRAP_CAPACITY = 512


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def json_bytes(value) -> bytes:
    return (json.dumps(value, indent=2, allow_nan=False) + "\n").encode("utf-8")


def write_new(path: Path, data: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("xb") as stream:
        stream.write(data)


def material(pixels: bytes) -> bytes:
    if len(pixels) != 4:
        raise ValueError("The original test material is exactly 2 by 2 pixels.")
    # rdMaterialHeader: 76 bytes; 24-byte textured texinfo + 16-byte extension;
    # rdTextureHeader: width,height,alpha,unknown,transparentIndex,mipCount.
    return (b"MAT " + struct.pack("<6I", 0x32, 2, 1, 1, 0, 8) + bytes(48)
            + struct.pack("<10I", 8, 0, 0, 0, 0, 0, 0, 0, 0, 0)
            + struct.pack("<6I", 2, 2, 0, 0, 0, 1) + pixels)


def colormap() -> bytes:
    palette = bytearray(value for index in range(256) for value in (index, index, index))
    palette[750:753] = bytes((40, 100, 70))  # Stable contrasting room, index 250.
    ramps = bytearray()
    for row in range(64):
        for index in range(256):
            target = tuple(channel * row / 63 for channel in palette[index * 3:index * 3 + 3])
            closest = min(range(256), key=lambda candidate: sum(
                (palette[candidate * 3 + component] - target[component]) ** 2 for component in range(3)))
            ramps.append(closest)
    return b"CMP " + bytes(60) + palette + ramps


def _original_model(vertices, name: str, material_name: str, radius: float) -> bytes:
    # Unlike JKL sections, rdModel3's fixed-order parser does not consume END
    # between 3DO sections. Its mesh RADIUS field is mandatory.
    lines = ["SECTION: HEADER", "3DO 2.1", "SECTION: MODELRESOURCE", "MATERIALS 1",
             f"0: {material_name}", "SECTION: GEOMETRYDEF", f"RADIUS {radius}", "INSERT OFFSET 0 0 0",
             "GEOSETS 1", "GEOSET 0", "MESHES 1", "MESH 0", f"NAME {name}",
             f"RADIUS {radius}", "GEOMETRYMODE 4", "LIGHTINGMODE 3", "TEXTUREMODE 3", "VERTICES 4"]
    lines += [f"{index}: {x} {y} {z} 0.02" for index, (x, y, z) in enumerate(vertices)]
    lines += ["TEXTURE VERTICES 4", "0: 0 0", "1: 2 0", "2: 2 2", "3: 0 2", "VERTEX NORMALS 4"]
    lines += [f"{index}: 0 0 1" for index in range(4)]
    lines += ["FACES 1", "0: 0 0x1 4 3 3 0 4 0,0 1,1 2,2 3,3", "FACE NORMALS 1", "0: 0 0 1",
              "SECTION: HIERARCHYDEF", "HIERARCHY NODES 1",
              "0: 0x0 0x10 0 -1 -1 -1 0 0 0 0 0 0 0 0 0 0 root"]
    return ("\n".join(lines) + "\n").encode("ascii")


def model() -> bytes:
    return _original_model(PANEL_VERTICES, "original_asymmetric_panel", "archpanel.mat", 2)


def spawn_model() -> bytes:
    # Player initialization serializes a model filename even with a fixed capture
    # camera. This separate original mesh stays outside that camera's frustum.
    return _original_model(SPAWN_VERTICES, "original_off_camera_spawn", "archroom.mat", 0.03)


def seed_level() -> bytes:
    vertices = ((-2, -2, 0), (2, -2, 0), (2, 2, 0), (-2, 2, 0),
                (-2, -2, 3), (2, -2, 3), (2, 2, 3), (-2, 2, 3))
    faces = ((0, 1, 2, 3), (7, 6, 5, 4), (4, 5, 1, 0), (5, 6, 2, 1), (6, 7, 3, 2), (7, 4, 0, 3))
    normals = ("0 0 1", "0 0 -1", "0 1 0", "-1 0 0", "0 -1 0", "1 0 0")
    # sithWorld_ReadCopyrightText consumes exactly34 noncomment32-byte rows.
    # The QOL_IMPROVEMENTS build skips its proprietary-banner comparison but
    # still requires the section. All rows here are newly authored fixture text.
    lines = ["# Original ENGINE-01 fixture. Runtime acceptance remains a separate receipt.", "SECTION: COPYRIGHT"]
    lines += [f"SED ORIGINAL FIXTURE ROW {index:02d}".ljust(32, "=") for index in range(1, 35)]
    lines += ["END", "SECTION: HEADER", "VERSION 1", "WORLD GRAVITY 0", "CEILING SKY Z 3", "HORIZON DISTANCE 100",
             "HORIZON PIXELS PER REV 768", "HORIZON SKY OFFSET 0 0", "CEILING SKY OFFSET 0 0",
             "MIPMAP DISTANCES 100 200 300 400", "LOD DISTANCES 100 200 300 400", "PERSPECTIVE DISTANCE 100",
             "GOURAUD DISTANCE 100", "END", "SECTION: MATERIALS", f"World materials {BOOTSTRAP_CAPACITY}",
             "0: archroom.mat 1 1", "1: archpanel.mat 1 1", "END", "SECTION: GEORESOURCE", "World colormaps 1",
             "0: archcheck.cmp", "World vertices 8"]
    lines += [f"{index}: {x} {y} {z}" for index, (x, y, z) in enumerate(vertices)]
    lines += ["World texture vertices 4", "0: 0 0", "1: 2 0", "2: 2 2", "3: 0 2", "World adjoins 0", "World surfaces 6"]
    for index, corners in enumerate(faces):
        pairs = " ".join(f"{vertex},{uv}" for uv, vertex in enumerate(corners))
        # World channels fixed and equal; only authored model samples vary.
        lines.append(f"{index}: 0 0x1000000 0x1 4 3 3 -1 0 4 {pairs} " + " ".join(["0.5"] * 16))
    lines += [f"{index}: {normal}" for index, normal in enumerate(normals)]
    lines += ["END", "SECTION: SECTORS", "World sectors 1", "SECTOR 0", "FLAGS 0", "AMBIENT LIGHT 0",
              "EXTRA LIGHT 0", "COLORMAP 0", "TINT 0 0 0", "BOUNDBOX -2 -2 0 2 2 3",
              "CENTER 0 0 1.5", "RADIUS 4", "VERTICES 8"]
    lines += [f"{index}: {index}" for index in range(8)]
    lines += ["SURFACES 0 6", "END"]
    for section in ("SOUNDS", "KEYFRAMES", "ANIMCLASS", "SOUNDCLASS", "AICLASS", "COGSCRIPTS", "COGS", "SPRITES"):
        lines += ["SECTION: " + section, f"World {BOOTSTRAP_CAPACITIES[section]} {BOOTSTRAP_CAPACITY}"]
        if section == "SOUNDCLASS":
            lines.append("0: ky.snd")  # Explicit external bootstrap reference, no copied soundclass bytes.
        lines.append("END")
    lines += ["SECTION: MODELS", f"World models {BOOTSTRAP_CAPACITY}", "0: archpanel.3do", "1: archspawn.3do", "END",
              "SECTION: TEMPLATES", f"World templates {BOOTSTRAP_CAPACITY}",
              "sedarchpanel none type=cog thingflags=0x8 move=none collide=0 model3d=archpanel.3do",
              "sedarchspawn none type=player move=none collide=0 size=0.05 health=100 maxhealth=100 model3d=archspawn.3do soundclass=ky.snd",
              "END", "SECTION: THINGS", f"World things {BOOTSTRAP_CAPACITY}",
              "0: sedarchpanel OriginalPanel 0 0 0.2 0 0 0 0 0",
              "1: sedarchspawn OriginalSpawn " + " ".join(map(str, SPAWN_POSITION)) + " 0 0 0 0 -1", "END",
              "SECTION: ARCHLIGHTING", "Num ArchObjects 1", "archobject 0", "nummeshes: 1", "mesh 0", "numvertices: 4"]
    lines += [f"{index}: " + " ".join(str(channel) for channel in sample) for index, sample in enumerate(BASELINE_SAMPLES)]
    return ("\n".join(lines + ["END", ""]) + "\n").encode("ascii")


def assert_bootstrap_capacities(level: bytes) -> dict[str, int]:
    actual = {}
    for section, noun in BOOTSTRAP_CAPACITIES.items():
        pattern = rb"(?im)^SECTION:[ \t]*" + section.encode() + rb"[ \t]*\r?\n(?:[ \t]*\r?\n)*World[ \t]+" + noun.encode() + rb"[ \t]+([0-9]+)[ \t]*\r?$"
        matches = re.findall(pattern, level)
        if matches != [str(BOOTSTRAP_CAPACITY).encode()]:
            raise ValueError(f"Required {section} bootstrap capacity was removed, duplicated or changed: {matches!r}.")
        actual[section] = int(matches[0])
    return actual


def episode() -> bytes:
    # Same single-level sequence grammar as Formats/Launching/TestEpisode.Build.
    return b'"Original SED ARCH verification"\nTYPE 1\nSEQ 1\n0: 1 1 LEVEL archcheck.jkl 0 0 -1 -1\nEND\n'


def gob(entries: dict[str, bytes]) -> bytes:
    cursor = 16 + 136 * len(entries)
    index = bytearray(b"GOB " + struct.pack("<III", 0x14, 12, len(entries)))
    for name, data in entries.items():
        encoded = name.replace("/", "\\").encode("ascii")
        if len(encoded) > 127:
            raise ValueError("GOB entry name is too long.")
        index.extend(struct.pack("<II128s", cursor, len(data), encoded))
        cursor += len(data)
    return bytes(index) + b"".join(entries.values())


def expected_bake(white: float, rgb: float, color=LIGHT_RGB) -> list[dict]:
    samples = []
    for point in PANEL_VERTICES:
        world = tuple(a + b for a, b in zip(point, PANEL_POSITION))
        distance = math.dist(world, LIGHT_POSITION)
        falloff = max(0, 1 - distance / LIGHT_RANGE) ** 2
        samples.append({"mono": white * falloff, "rgb": [rgb * channel * falloff for channel in color]})
    return samples


def camera() -> dict:
    position, target = [0, -1.4, 2], [0, 0, 0.2]
    dx, dy, dz = (b - a for a, b in zip(position, target))
    # Engine BuildRotate34 yaw positive turns +Y toward -X. This camera uses yaw0.
    return {"position": position, "target": target, "up": [0, 0, 1], "fieldOfViewDegrees": 65,
            "width": 640, "height": 480, "sector": 0, "nearPlane": 0.01, "farPlane": 100,
            "pitchYawRoll": [math.degrees(math.atan2(dz, math.hypot(dx, dy))), -math.degrees(math.atan2(dx, dy)), 0],
            "horizontalFov": math.degrees(2 * math.atan(640 / 480 * math.tan(math.radians(65) / 2)))}


def omit_extensions(source: bytes) -> tuple[bytes, list[dict]]:
    """Remove only complete, unique named extensions; preserve every other byte."""
    pattern = re.compile(rb"(?im)^SECTION:[ \t]*(SED_SOURCE_LIGHTS|SED_ARCH_AUTHORING)[ \t]*\r?$.*?^END[ \t]*(?:\r?\n|$)", re.S)
    found = list(pattern.finditer(source))
    names = [match.group(1).decode("ascii").upper() for match in found]
    if sorted(names) != sorted(EXTENSIONS) or any(re.search(rb"(?im)^SECTION:", match.group()[1:]) for match in found):
        raise ValueError("Expected exactly one complete occurrence of each authored extension.")
    removed = [{"name": name, "offset": match.start(), "bytes": match.end() - match.start(),
                "sha256": sha(match.group()), "dataHex": match.group().hex()} for name, match in zip(names, found)]
    return pattern.sub(b"", source), removed


def known_section_hashes(source: bytes) -> dict[str, str]:
    pattern = re.compile(rb"(?im)^SECTION:[ \t]*([A-Z_]+)[ \t]*\r?$.*?^END[ \t]*(?:\r?\n|$)", re.S)
    result = {}
    for match in pattern.finditer(source):
        name = match.group(1).decode("ascii").upper()
        if name in EXTENSIONS:
            continue
        if name in result:
            raise ValueError("Duplicate original section in fixture.")
        result[name] = sha(match.group())
    return result


def interleave_extensions(source: bytes) -> tuple[bytes, list[dict]]:
    without, removed = omit_extensions(source)
    anchors = list(re.finditer(rb"(?im)^SECTION:[ \t]*THINGS[ \t]*\r?$", without))
    if len(anchors) != 1:
        raise ValueError("Expected exactly one THINGS section for the interleaving control.")
    index = anchors[0].start()
    blocks = bytearray()
    for section in removed:
        block = bytes.fromhex(section["dataHex"])
        blocks.extend(block)
        # JklWriter can end its final extension at EOF without a newline.
        # Moving those exact bytes before THINGS needs a separate line boundary;
        # otherwise ENDSECTION merges and swallows the known section in parsing.
        separator = b"" if block.endswith(b"\n") else b"\n"
        section["insertedSeparatorAfterHex"] = separator.hex()
        blocks.extend(separator)
    derived = without[:index] + bytes(blocks) + without[index:]
    if known_section_hashes(derived) != known_section_hashes(source):
        raise ValueError("Interleaving changed an engine-facing section.")
    return derived, removed


class Agent:
    def __init__(self, executable: Path, output: Path, receipt: dict, timeout: int):
        self.executable, self.output, self.receipt, self.timeout = executable, output, receipt, timeout

    def invoke(self, name: str, arguments: list[str], request=None, expected_exit: int = 0) -> dict:
        data = b"" if request is None else json_bytes(request)
        stem = f"{len(self.receipt['steps']) + 1:02d}-{name}"
        step = {"name": name, "arguments": arguments, "requestSha256": sha(data), "expectedExit": expected_exit,
                "requestPath": str(self.output / (stem + ".request.json")), "status": "started"}
        self.receipt["steps"].append(step)
        write_new(Path(step["requestPath"]), data)
        started = time.monotonic()
        try:
            result = subprocess.run([str(self.executable), *arguments], input=data, capture_output=True,
                                    cwd=self.output, timeout=self.timeout, check=False,
                                    creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
        except subprocess.TimeoutExpired:
            step["status"] = "timed-out"
            raise RuntimeError(f"sed-agent timed out in {name}.") from None
        step.update(exitCode=result.returncode, elapsedSeconds=time.monotonic() - started)
        for stream, content in (("stdout", result.stdout), ("stderr", result.stderr)):
            path = self.output / (stem + "." + stream + ".txt")
            write_new(path, content)
            step[stream + "Path"] = str(path)
            step[stream + "Sha256"] = sha(content)
        if result.returncode != expected_exit or result.stderr:
            raise RuntimeError(f"sed-agent {name} returned {result.returncode}; inspect its captured logs.")
        value = json.loads(result.stdout)
        if not isinstance(value, dict):
            raise ValueError("Agent reply must be an object.")
        step["status"] = "passed"
        return value


def check_samples(actual: list[dict], expected: list[dict]) -> None:
    if len(actual) != len(expected):
        raise ValueError("Architectural sample count differs from the original model.")
    for index, (got, wanted) in enumerate(zip(actual, expected)):
        if len(got["rgb"]) != 3 or any(not math.isclose(a, b, rel_tol=1e-12, abs_tol=1e-12)
                for a, b in zip([got["mono"], *got["rgb"]], [wanted["mono"], *wanted["rgb"]])):
            raise ValueError(f"Architectural sample {index} differs from the independent source-point calculation.")


def assert_unbound_readback(thing: dict) -> None:
    architecture = thing["architecture"]
    if (thing["archLightingIndex"] != -1 or architecture["status"] != "unbound"
            or any(architecture[key] is not None for key in ("provenance", "samplesSha256", "meshVertexCounts"))):
        raise ValueError("Unbound control must have no live ARCH binding, provenance or attached samples; zero ARCH is not unbound.")


def generate(agent_path: Path, output: Path, timeout: int = 30) -> dict:
    agent_path, output = agent_path.resolve(strict=True), output.resolve()
    if not agent_path.is_file():
        raise ValueError("--agent must name an existing executable file.")
    if timeout < 1 or timeout > 120:
        raise ValueError("Timeout must be between 1 and 120 seconds.")
    output.mkdir(exist_ok=False)  # Do not reuse any earlier evidence directory.
    receipt = {"schemaVersion": 1, "status": "preparing", "assetProvenance": "Original data generated by this script; no retail/community assets.",
               "generatorSha256": sha(Path(__file__).read_bytes()), "agentPath": str(agent_path),
               "agentSha256": sha(agent_path.read_bytes()), "engineLaunched": False, "guiLaunched": False,
               "episode": "SEDARCH", "map": "archcheck.jkl", "camera": camera(), "steps": [], "cases": [],
               "oracleWitness": {"panelWorldVertices": [[a + b for a, b in zip(point, PANEL_POSITION)] for point in PANEL_VERTICES],
                                 "cameraTarget": camera()["target"], "cameraUp": [0, 0, 1],
                                 "minimumPanelPixels": 100, "toleranceBytes": 2,
                                 "modelVertexBaseIntensity": 0.02, "sectorAmbient": 0, "surfaceExtraLight": 0,
                                 "worldCornerMonoAndRgb": 0.5,
                                 "panelPalette": "Original neutral grayscale; asymmetric texture and polygon."},
               "runtimeAcceptance": "not-run", "engineLightingMode": "engine-native-default",
               "primaryRuntimeTarget": "OpenMotS with QOL_IMPROVEMENTS; original copyright-section rows are not a retail banner.",
               "bootstrapReservations": {"capacityPerTable": BOOTSTRAP_CAPACITY, "tables": BOOTSTRAP_CAPACITIES,
                                         "populatedOriginalEntries": {"MATERIALS": 2, "MODELS": 2, "TEMPLATES": 2, "THINGS": 2},
                                         "externalReferenceEntries": {"SOUNDCLASS": ["ky.snd"]},
                                         "otherTablesPopulated": 0,
                                         "purpose": "Finite spare slots for current-world startup resource loads; no retail assets or COG instances embedded; not a measured minimum."},
               "limits": ["No engine mono-mode switch is assumed.", "Rest-pose point bake is unoccluded and nondirectional.",
                          "Retail or builds enforcing the proprietary copyright-banner comparison are outside this fixture's acceptance target.",
                          "A prepared fixture is not proof of a playable mission or retail parity."]}
    try:
        agent = Agent(agent_path, output, receipt, timeout)
        receipt["agentVersion"] = agent.invoke("version", ["--version"])
        agent.invoke("help", ["--help"])
        for operation in ("bindThingArchitecture", "clearThingArchitecture", "rebindThingArchitecture", "bakeThingArchitecture", "setLight", "addLight"):
            agent.invoke("schema-" + operation, ["schema", operation])
        entries = {"mat/archpanel.mat": material(bytes((255, 192, 128, 224))),
                   "mat/archroom.mat": material(bytes([250] * 4)), "misc/cmp/archcheck.cmp": colormap(),
                   "3do/archpanel.3do": model(), "3do/archspawn.3do": spawn_model()}
        archive = output / "original-resources.gob"
        write_new(archive, gob(entries))
        resources = [{"path": str(archive), "sha256": sha(archive.read_bytes())}]
        receipt["resourceArchives"] = resources
        receipt["originalEntries"] = [{"name": name, "sha256": sha(data), "bytes": len(data)} for name, data in entries.items()]
        seed = output / "original-seed.jkl"
        write_new(seed, seed_level())
        assert_bootstrap_capacities(seed.read_bytes())
        original_seed_hash = sha(seed.read_bytes())
        receipt["seed"] = {"path": str(seed), "sha256": original_seed_hash}

        def make_case(name, source, operations, expected, status):
            inspection = agent.invoke(name + "-inspect-source", ["inspect", str(source)])
            patch = {"schemaVersion": 1, "inputSha256": inspection["inputSha256"], "sourceRevision": inspection["sourceRevision"],
                     "resourceArchives": resources, "operations": operations}
            planned = agent.invoke(name + "-plan", ["plan", str(source), "-"], patch)
            patch["resourceRevision"] = planned["resourceRevision"]
            destination = output / (name + ".jkl")
            agent.invoke(name + "-apply", ["apply", str(source), "-", str(destination), planned["outputSha256"]], patch)
            if sha(destination.read_bytes()) != planned["outputSha256"] or sha(source.read_bytes()) != inspection["inputSha256"]:
                raise ValueError("Reviewed output identity or immutable input check failed.")
            retained_capacities = assert_bootstrap_capacities(destination.read_bytes())
            things = agent.invoke(name + "-things", ["query", str(destination), "-"], {"schemaVersion": 1, "collection": "things"})
            if len(things["items"]) != 2 or things["items"][0]["architecture"]["status"] != status:
                raise ValueError("Prepared thing count or architectural status differs from expected.")
            lighting_index = things["items"][0]["archLightingIndex"]
            samples, sample_query_receipt = [], None
            if status == "unbound":
                assert_unbound_readback(things["items"][0])
                if expected is not None:
                    raise ValueError("Unbound expectation must describe native fallback, not ARCH samples.")
            else:
                queried = agent.invoke(name + "-samples", ["query", str(destination), "-"],
                                       {"schemaVersion": 1, "collection": "architecturalVertices", "lightingIndex": lighting_index, "mesh": 0})
                samples = queried["items"]
                sample_query_receipt = receipt["steps"][-1]["stdoutPath"]
                check_samples(samples, expected)
            validated = agent.invoke(name + "-validate", ["validate", str(destination), "-"],
                                     {"schemaVersion": 1, "inputSha256": planned["outputSha256"], "resourceArchives": resources})
            validation_status = "not-applicable" if status == "unbound" else "compatible-current-resources"
            if (not validated["valid"] or validated["architectureValidation"]["status"] != validation_status
                    or status == "unbound" and validated["architectureValidation"]["boundThings"] != 0):
                raise ValueError("Prepared fixture failed current-resource validation.")
            required_extensions = ("SED_SOURCE_LIGHTS",) if status == "unbound" else EXTENSIONS
            if any(b"SECTION: " + extension.encode() not in destination.read_bytes() for extension in required_extensions):
                raise ValueError("Real agent output is missing authoring extensions.")
            case = {"name": name, "sourcePath": str(source), "sourceSha256": inspection["inputSha256"],
                    "authoredPath": str(destination), "sourceRevision": validated["sourceRevision"],
                    "resourceRevision": validated["resourceRevision"], "resourceArchives": resources,
                    "architecturalIndex": lighting_index, "samples": samples, "expectedSamples": expected,
                    "sampleQueryReceiptPath": sample_query_receipt,
                    "retainedBootstrapCapacities": retained_capacities,
                    "architecture": things["items"][0]["architecture"], "sourceKind": "agent-authored"}
            if status == "unbound":
                case["expectedFallback"] = {"source": "native-model-vertex-base", "mono": 0.02, "rgb": [0.02, 0.02, 0.02],
                                            "note": "No attached ARCH samples; fallback is a separate runtime expectation."}
            stage_case(case, destination.read_bytes(), entries, output)
            receipt["cases"].append(case)
            return destination

        baseline = make_case("baseline", seed, [
            {"op": "addLight", "position": LIGHT_POSITION, "rgb": LIGHT_RGB, "intensity": WHITE_INTENSITY,
             "rgbIntensity": RGB_INTENSITY, "range": LIGHT_RANGE},
            {"op": "clearThingArchitecture", "thing": 0}, {"op": "bindThingArchitecture", "thing": 0, "lightingIndex": 0}],
            [{"mono": s[0], "rgb": list(s[1:])} for s in BASELINE_SAMPLES], "bound")
        make_case("reset", baseline, [{"op": "rebindThingArchitecture", "thing": 0, "policy": "reset"}],
                  [{"mono": 0, "rgb": [0, 0, 0]} for _ in PANEL_VERTICES], "reset")
        make_case("unbound", baseline, [{"op": "clearThingArchitecture", "thing": 0}], None, "unbound")
        bake = {"op": "bakeThingArchitecture", "thing": 0, "profile": "source-points-v1"}
        baked = make_case("baked", baseline, [bake], expected_bake(WHITE_INTENSITY, RGB_INTENSITY), "current-inputs")
        make_case("rgb-swapped", baseline, [{"op": "setLight", "light": 0, "rgb": [0.25, 0.5, 1]}, bake],
                  expected_bake(WHITE_INTENSITY, RGB_INTENSITY, (0.25, 0.5, 1)), "current-inputs")
        make_case("white-only-change", baseline, [{"op": "setLight", "light": 0, "intensity": 0}, bake],
                  expected_bake(0, RGB_INTENSITY), "current-inputs")
        derived, removed = omit_extensions(baked.read_bytes())
        case = {"name": "extensions-omitted", "sourceKind": "derived-metadata-loss-control",
                "sourcePath": str(baked), "sourceSha256": sha(baked.read_bytes()), "omittedSections": removed,
                "knownSectionHashes": known_section_hashes(derived),
                "resourceArchives": resources, "expectedSamples": expected_bake(WHITE_INTENSITY, RGB_INTENSITY),
                "loss": "Only authoring metadata removed; engine-facing sections remain byte-identical. Historical provenance is lost."}
        if case["knownSectionHashes"] != known_section_hashes(baked.read_bytes()):
            raise ValueError("Extension removal changed an engine-facing section.")
        stage_case(case, derived, entries, output)
        receipt["cases"].append(case)
        interleaved, moved = interleave_extensions(baked.read_bytes())
        case = {"name": "metadata-interleaved", "sourceKind": "derived-section-order-control",
                "sourcePath": str(baked), "sourceSha256": sha(baked.read_bytes()), "movedSections": moved,
                "knownSectionHashes": known_section_hashes(interleaved), "resourceArchives": resources,
                "expectedSamples": expected_bake(WHITE_INTENSITY, RGB_INTENSITY),
                "derivation": "Exact metadata blocks moved before THINGS; THINGS and ARCHLIGHTING must still be consumed."}
        stage_case(case, interleaved, entries, output)
        receipt["cases"].append(case)
        receipt["caseNames"] = [item["name"] for item in receipt["cases"]]
        if sha(seed.read_bytes()) != original_seed_hash or sha(archive.read_bytes()) != resources[0]["sha256"]:
            raise ValueError("Original source or archive changed during preparation.")
        if sha(agent_path.read_bytes()) != receipt["agentSha256"]:
            raise ValueError("Agent executable changed during preparation.")
        receipt["status"] = "prepared"
        return receipt
    except Exception as error:
        receipt["status"] = "failed"
        receipt["error"] = str(error)
        raise
    finally:
        write_new(output / "fixture-receipt.json", json_bytes(receipt))


def stage_case(case: dict, level: bytes, entries: dict[str, bytes], output: Path) -> None:
    root = output / "cases" / case["name"]
    prefix = "episode/SEDARCH/"
    files = {prefix + "episode.jk": episode(), prefix + "jkl/archcheck.jkl": level}
    files.update((prefix + name, data) for name, data in entries.items())
    manifest = []
    for relative, data in files.items():
        destination = root / relative
        write_new(destination, data)
        manifest.append({"path": str(destination), "source": str(destination), "stageRelativePath": relative,
                         "destination": relative, "sha256": sha(data), "bytes": len(data), "role": "scenario"})
    case.update(stageRoot=str(root), episodeDirectory=str(root / "episode/SEDARCH"),
                levelPath=str(root / "episode/SEDARCH/jkl/archcheck.jkl"), levelSha256=sha(level), files=manifest)


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--agent", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=30)
    args = parser.parse_args(argv)
    try:
        result = generate(args.agent, args.output, args.timeout)
        print(json.dumps({"status": result["status"], "receipt": str(args.output.resolve() / "fixture-receipt.json"),
                          "cases": [case["name"] for case in result["cases"]]}))
        return 0
    except Exception as error:
        print(json.dumps({"status": "failed", "error": str(error)}), file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
