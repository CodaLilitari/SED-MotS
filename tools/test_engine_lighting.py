#!/usr/bin/env python3
"""Run original ENGINE-01 fixture cases through isolated hidden OpenMotS capture.

Requires an explicit local bootstrap manifest (no assets are discovered/downloaded).
Use --case for a diagnostic attempt; omit it for all differential acceptance gates.
"""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path
import stat
import sys

from invoke_engine_capture import run_capture, sha256, write_json, CaptureError, is_link
from compare_engine_lighting import compare, read_ppm


def prepare_request(fixture, case, bootstrap):
    keys = ("width", "height", "sector", "position", "pitchYawRoll", "horizontalFov")
    files = [{key: item[key] for key in ("source", "destination", "sha256", "role")}
             for item in [*bootstrap["files"], *case["files"]]]
    return {"schemaVersion": 1, "episode": fixture["episode"], "map": fixture["map"],
            "camera": {key: fixture["camera"][key] for key in keys}, "files": files,
            "frame": 3, "engineTimeoutMs": 15000, "runnerTimeoutMs": 20000, "logLimitBytes": 1048576}


def read_manifest(path):
    raw = path.read_bytes()
    return json.loads(raw.decode("utf-8-sig")), hashlib.sha256(raw).hexdigest()


def snapshot_owned_output(directory):
    """Hash every regular file and retain empty directories; never follow links."""
    entries, pending = [], [Path(directory)]
    while pending:
        current = pending.pop()
        if is_link(current):
            raise CaptureError("Existing output contains an unexpected reparse point.")
        info = current.lstat()
        relative = current.relative_to(directory).as_posix()
        if stat.S_ISDIR(info.st_mode):
            entries.append({"path": relative, "kind": "directory"})
            pending.extend(current.iterdir())
        elif stat.S_ISREG(info.st_mode):
            entries.append({"path": relative, "kind": "file", "bytes": info.st_size, "sha256": sha256(current)})
        else:
            raise CaptureError("Existing output contains an unexpected file type.")
    return sorted(entries, key=lambda item: item["path"])


def check_existing_output_refusal(request_path, destination):
    before = snapshot_owned_output(destination)
    refused = False
    try:
        run_capture(request_path, destination)
    except (CaptureError, FileExistsError):
        refused = True
    after = snapshot_owned_output(destination)
    return {"passed": refused and before == after, "refused": refused,
            "before": before, "after": after}


def check_model_mismatch_refusal(receipt, destination, model):
    image = destination / "capture.ppm"
    manifest_path = destination / "manifest-before.json"
    if (receipt.get("status") != "failed" or receipt.get("engineStarted") is not False
            or receipt.get("failureStage") != "staging" or image.exists() or image.is_symlink()
            or not manifest_path.is_file() or is_link(manifest_path)):
        return False
    try:
        manifest = json.loads(manifest_path.read_bytes().decode("utf-8-sig"))
        matches = [item for item in manifest if item.get("destination") == model["destination"]]
        if len(matches) != 1:
            return False
        item = matches[0]
        expected = model["sha256"].lower()
        observed = sha256(Path(model["source"]))
        return (item.get("source") == model["source"] and item.get("role") == model["role"]
                and item.get("sha256") == expected and item.get("sourceBeforeMatchesDeclared") is False
                and item.get("sourceBeforeSha256") == observed and observed != expected
                and item.get("stagedBeforeSha256") is None)
    except (OSError, ValueError, TypeError, AttributeError):
        return False


def run(fixture_path, bootstrap_path, output, only_case=None, repeats=3):
    fixture_path, bootstrap_path = fixture_path.resolve(strict=True), bootstrap_path.resolve(strict=True)
    fixture, fixture_hash = read_manifest(fixture_path)
    bootstrap, bootstrap_hash = read_manifest(bootstrap_path)
    if fixture["status"] != "prepared" or fixture["engineLaunched"]:
        raise ValueError("Need a completed, original fixture preparation receipt")
    if not 2 <= repeats <= 5:
        raise ValueError("Use between two and five clean-process repeats")
    cases = {case["name"]: case for case in fixture["cases"]}
    required = ("baked", "reset", "unbound", "rgb-swapped", "white-only-change", "extensions-omitted", "metadata-interleaved")
    if not all(name in cases for name in required) or only_case is not None and only_case not in cases:
        raise ValueError("Required fixture cases are absent")
    output = output.resolve()
    output.mkdir(exist_ok=False)
    record = {"schemaVersion": 1, "status": "running", "scope": "diagnostic-one-case" if only_case else "ENGINE-01-RGB",
              "fixtureReceiptSha256": fixture_hash, "bootstrapManifestSha256": bootstrap_hash,
              "editorVersion": fixture["agentVersion"], "engineCommitSupplied": bootstrap.get("engineCommit"),
              "toolHashes": {name: sha256(Path(__file__).with_name(name)) for name in
                             ("test_engine_lighting.py", "invoke_engine_capture.py", "engine_resource_closure.py", "compare_engine_lighting.py")},
              "captures": [], "comparisons": [], "controls": {}, "controlEvidence": {}, "deterministicSimulation": False,
              "engineMonoModeTested": False, "runtimeResourceResolutionObserved": False,
              "effectiveCameraObserved": False, "retailParityClaimed": False, "gameplayClaimed": False}
    try:
        frames = []
        for repeat in range(1 if only_case else repeats):
            current = {}
            for name in (only_case,) if only_case else required:
                request = prepare_request(fixture, cases[name], bootstrap)
                request_path = output / f"{name}-{repeat+1}.request.json"
                write_json(request_path, request)
                destination = output / f"{name}-{repeat+1}"
                print(json.dumps({"capturing": name, "repeat": repeat+1}), flush=True)
                receipt = run_capture(request_path, destination)
                record["captures"].append({"case": name, "repeat": repeat+1, "status": receipt["status"],
                                           "receipt": str(destination / "launcher-receipt.json"),
                                           "receiptSha256": sha256(destination / "launcher-receipt.json")})
                if receipt["status"] != "completed":
                    raise RuntimeError(f"{name} capture failed: {receipt.get('error')}")
                current[name] = str(destination / "capture.ppm")
            frames.append(current)
        if only_case:
            record["status"] = "captured-diagnostic"
            return record
        witness = dict(fixture["oracleWitness"], position=fixture["camera"]["position"],
                       target=fixture["camera"]["target"], up=fixture["camera"]["up"],
                       verticalFov=fixture["camera"]["fieldOfViewDegrees"])
        for index, current in enumerate(frames):
            request = {"captures": dict(current, repeat=frames[(index+1) % len(frames)]["baked"]), "oracleWitness": witness}
            result = compare(request["captures"], witness)
            result["checks"]["interleavedMetadataPreservesFrame"] = read_ppm(Path(current["baked"]))[2] == read_ppm(Path(current["metadata-interleaved"]))[2]
            result["passed"] = all(result["checks"].values())
            write_json(output / f"comparison-{index+1}.request.json", request)
            write_json(output / f"comparison-{index+1}.json", result)
            record["comparisons"].append({"repeat": index+1, "passed": result["passed"], "checks": result["checks"],
                                           "receipt": str(output / f"comparison-{index+1}.json")})
        # Substitution keeps the logical name and expected digest but changes the supplied bytes.
        negative = prepare_request(fixture, cases["baked"], bootstrap)
        model = next(item for item in negative["files"] if item["destination"].endswith("/archpanel.3do"))
        mutation_dir = output / "control-input"
        mutation_dir.mkdir()
        mutation = mutation_dir / "archpanel.3do"
        with mutation.open("xb") as stream:
            stream.write(Path(model["source"]).read_bytes()+b"\n# deliberate manifest mismatch\n")
        model["source"] = str(mutation)
        negative_path = output / "manifest-mismatch.request.json"
        write_json(negative_path, negative)
        refusal_directory = output / "manifest-mismatch"
        refused = run_capture(negative_path, refusal_directory)
        record["controls"]["modelSubstitutionRefusedBeforeLaunch"] = check_model_mismatch_refusal(refused, refusal_directory, model)
        record["controlEvidence"]["modelSubstitution"] = {
            "receipt": str(refusal_directory / "launcher-receipt.json"),
            "receiptSha256": sha256(refusal_directory / "launcher-receipt.json"),
            "manifest": str(refusal_directory / "manifest-before.json"),
            "manifestSha256": sha256(refusal_directory / "manifest-before.json"),
            "failureStage": refused.get("failureStage"), "targetDestination": model["destination"]}
        exclusive = check_existing_output_refusal(output / "baked-1.request.json", output / "baked-1")
        record["controlEvidence"]["existingOutput"] = exclusive
        record["controls"]["existingOutputRefusedUnchanged"] = exclusive["passed"]
        record["status"] = "passed" if all(item["passed"] for item in record["comparisons"]) and all(record["controls"].values()) else "failed"
        return record
    except Exception as exc:
        record["status"] = "failed"
        record["error"] = str(exc)
        return record
    finally:
        try:
            record["preparationAndBootstrapReceiptsUnchanged"] = (sha256(fixture_path) == record["fixtureReceiptSha256"]
                                                                and sha256(bootstrap_path) == record["bootstrapManifestSha256"])
        except OSError as exc:
            record["preparationAndBootstrapReceiptsUnchanged"] = False
            record["finalIdentityError"] = str(exc)
        if not record["preparationAndBootstrapReceiptsUnchanged"]:
            record["status"] = "failed"
        write_json(output / "acceptance.json", record)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixture", type=Path, required=True)
    parser.add_argument("--inputs", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--case")
    parser.add_argument("--repeats", type=int, default=3)
    args = parser.parse_args()
    try:
        result = run(args.fixture, args.inputs, args.output, args.case, args.repeats)
        print(json.dumps({"status": result["status"], "error": result.get("error"), "receipt": str(args.output / "acceptance.json")}), flush=True)
        return 0 if result["status"] in ("passed", "captured-diagnostic") else 1
    except Exception as exc:
        print(json.dumps({"status": "refused", "error": str(exc)}), file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
