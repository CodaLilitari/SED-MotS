"""Independent PPM/geometry oracle for the original ENGINE-01 static fixture.

This compares actual engine captures with each other, never with editor pixels.
The panel mask is projected from authored geometry before examining differences.
It does not certify simulation determinism or engine-observed resource identity.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path
import re


def read_ppm(path: Path):
    raw = path.read_bytes()
    match = re.match(rb"P6\n([0-9]+) ([0-9]+)\n255\n", raw)
    if not match:
        raise ValueError("Expected strict engine P6 RGB8 capture")
    width, height = map(int, match.groups())
    if not (64 <= width <= 2048 and 64 <= height <= 2048 and width * height <= 2097152):
        raise ValueError("Capture dimensions exceed contract")
    pixels = raw[match.end():]
    if len(pixels) != width * height * 3:
        raise ValueError("Capture payload is truncated or has trailing bytes")
    return width, height, pixels, hashlib.sha256(raw).hexdigest()


def _sub(a, b):
    return [x - y for x, y in zip(a, b)]


def _dot(a, b):
    return sum(x * y for x, y in zip(a, b))


def _cross(a, b):
    return [a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0]]


def _unit(a):
    length = math.sqrt(_dot(a, a))
    if not math.isfinite(length) or length < 1e-9:
        raise ValueError("Degenerate camera basis")
    return [v / length for v in a]


def project_panel(witness, width, height):
    position, target = witness["position"], witness["target"]
    up = witness.get("up", [0, 0, 1])
    points = witness["panelWorldVertices"]
    if not (3 <= len(points) <= 16):
        raise ValueError("Expected bounded convex panel polygon")
    for vector in [position, target, up, *points]:
        if len(vector) != 3 or any(not isinstance(v, (int, float)) or isinstance(v, bool) or not math.isfinite(v) for v in vector):
            raise ValueError("Witness requires finite 3D vectors")
    fov = witness["verticalFov"]
    if not isinstance(fov, (int, float)) or not 20 <= fov <= 140:
        raise ValueError("Invalid witness field of view")
    forward = _unit(_sub(target, position))
    right = _unit(_cross(forward, up))
    true_up = _cross(right, forward)
    focal = height / (2 * math.tan(math.radians(fov) / 2))
    polygon = []
    for point in points:
        delta = _sub(point, position)
        depth = _dot(delta, forward)
        if depth <= .01:
            raise ValueError("Panel witness crosses camera near plane")
        polygon.append([width/2 + focal*_dot(delta, right)/depth,
                        height/2 - focal*_dot(delta, true_up)/depth])
    area = sum(a[0]*b[1]-b[0]*a[1] for a, b in zip(polygon, polygon[1:]+polygon[:1]))
    if abs(area) < 1:
        raise ValueError("Degenerate projected witness")
    orientation = 1 if area > 0 else -1
    for index in range(len(polygon)):
        a, b, c = (polygon[(index+j) % len(polygon)] for j in range(3))
        if orientation*((b[0]-a[0])*(c[1]-b[1])-(b[1]-a[1])*(c[0]-b[0])) <= 0:
            raise ValueError("Panel witness must be strictly convex and ordered")
    return polygon, orientation


def masks(polygon, orientation, width, height, margin=4):
    interior, outside = [], []
    for y in range(height):
        for x in range(width):
            distances = []
            for a, b in zip(polygon, polygon[1:]+polygon[:1]):
                dx, dy = b[0]-a[0], b[1]-a[1]
                distances.append(orientation*(dx*(y+.5-a[1])-dy*(x+.5-a[0]))/math.hypot(dx, dy))
            if min(distances) >= margin:
                interior.append((y*width+x)*3)
            elif min(distances) <= -margin:
                outside.append((y*width+x)*3)
    return interior, outside


def difference(a, b, offsets, tolerance=2):
    if not offsets:
        raise ValueError("Empty pixel witness")
    sums, changed, maximum, channel_max = [0, 0, 0], 0, 0, [0, 0, 0]
    for i in offsets:
        delta = [b[i+c]-a[i+c] for c in range(3)]
        for c in range(3):
            sums[c] += delta[c]
            channel_max[c] = max(channel_max[c], abs(delta[c]))
        largest = max(map(abs, delta))
        maximum = max(maximum, largest)
        changed += largest > tolerance
    return {"pixels": len(offsets), "changedBeyondTolerance": changed,
            "maxAbsDelta": maximum, "maxAbsDeltaRgb": channel_max,
            "meanSignedDeltaRgb": [v/len(offsets) for v in sums]}


def compare(captures, witness):
    names = ("baked", "reset", "unbound", "rgb-swapped", "white-only-change", "extensions-omitted", "repeat")
    images = {name: read_ppm(Path(captures[name])) for name in names}
    width, height = images["baked"][:2]
    if any(image[:2] != (width, height) for image in images.values()):
        raise ValueError("Capture dimensions differ")
    polygon, orientation = project_panel(witness, width, height)
    interior, outside = masks(polygon, orientation, width, height)
    if len(interior) < 100 or len(outside) < 100:
        raise ValueError("Insufficient independent panel/world witness pixels")
    pixels = {name: image[2] for name, image in images.items()}
    bake_delta = difference(pixels["reset"], pixels["baked"], interior)
    covered_panel_pixels = sum(pixels["baked"][i] - pixels["reset"][i] > 2 for i in interior)
    fallback_delta = difference(pixels["reset"], pixels["unbound"], interior)
    fallback_channel_spread = max(max(pixels["unbound"][i:i+3])-min(pixels["unbound"][i:i+3]) for i in interior)
    swap_delta = difference(pixels["baked"], pixels["rgb-swapped"], interior)
    wrong_swap_direction = sum(pixels["rgb-swapped"][i] > pixels["baked"][i] + 2
                               or pixels["rgb-swapped"][i+2] < pixels["baked"][i+2] - 2
                               for i in interior)
    outside_changes = {name: difference(pixels["baked"], pixels[name], outside)
                       for name in names if name != "baked"}
    colored_world_pixels = sum(pixels["baked"][i] > 2 and pixels["baked"][i+2] > 2
                               and pixels["baked"][i+1] > pixels["baked"][i] + 2
                               and pixels["baked"][i+1] > pixels["baked"][i+2] + 2 for i in outside)
    # The original neutral panel uses source colors (1,.5,.25) versus (.25,.5,1).
    # A channel swap must decrease red, preserve green, and increase blue.
    checks = {
        "bakeVisibleOnPanel": bake_delta["changedBeyondTolerance"] >= 100 and all(v > 2 for v in bake_delta["meanSignedDeltaRgb"]),
        "bakeCoversProjectedPanel": covered_panel_pixels >= math.ceil(.98 * len(interior)),
        "unboundFallbackDistinctFromZeroReset": fallback_delta["changedBeyondTolerance"] >= 100 and all(v > 2 for v in fallback_delta["meanSignedDeltaRgb"]),
        "unboundNeutralFallback": fallback_channel_spread <= 2,
        "rgbSwapVisibleOnPanel": swap_delta["changedBeyondTolerance"] >= 100 and swap_delta["meanSignedDeltaRgb"][0] < -2 and swap_delta["maxAbsDeltaRgb"][1] <= 2 and swap_delta["meanSignedDeltaRgb"][2] > 2 and wrong_swap_direction == 0,
        "unchangedWorldOutsidePanel": all(value["changedBeyondTolerance"] == 0 for value in outside_changes.values()),
        "coloredWorldCoversOutsidePanel": colored_world_pixels >= math.ceil(.98 * len(outside)),
        "whiteOnlyChangePreservesRgbFrame": pixels["baked"] == pixels["white-only-change"],
        "extensionRemovalPreservesFrame": pixels["baked"] == pixels["extensions-omitted"],
        "repeatFrameMatches": pixels["baked"] == pixels["repeat"],
    }
    return {"schemaVersion": 1, "passed": all(checks.values()), "checks": checks,
            "width": width, "height": height, "projectedPanel": polygon,
            "interiorMarginPixels": 4, "byteTolerance": 2,
            "maskSource": "authored geometry and explicit camera, independent of capture differences",
            "panelPixels": len(interior), "worldPixels": len(outside),
            "bakeMinusReset": bake_delta, "swappedMinusBaked": swap_delta,
            "panelRedResponseCoverage": covered_panel_pixels / len(interior),
            "minimumPanelRedResponseCoverage": .98,
            "coloredWorldCoverage": colored_world_pixels / len(outside),
            "minimumColoredWorldCoverage": .98,
            "unboundMinusReset": fallback_delta, "unboundMaxChannelSpread": fallback_channel_spread,
            "swapWrongDirectionPixels": wrong_swap_direction,
            "worldDifferences": outside_changes,
            "captureSha256": {name: image[3] for name, image in images.items()},
            "deterministicSimulation": False, "pixelParityClaimed": False,
            "retailParityClaimed": False, "engineMonoModeTested": False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("request", type=Path, help="JSON with captures map and oracleWitness")
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    request = json.loads(args.request.read_text(encoding="utf-8-sig"))
    result = compare(request["captures"], request["oracleWitness"])
    with args.output.open("x", encoding="utf-8", newline="\n") as output:
        json.dump(result, output, indent=2)
        output.write("\n")
    print(json.dumps({"passed": result["passed"], "checks": result["checks"]}))
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
