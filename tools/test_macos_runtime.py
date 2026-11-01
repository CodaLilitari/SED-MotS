#!/usr/bin/env python3
"""Native relocated acceptance; authoring, graphics and Cocoa evidence stay separate."""
from __future__ import annotations

import argparse
import base64
import json
import os
from pathlib import Path
import plistlib
import re
import shutil
import signal
import struct
import subprocess
import sys
import time

from linux import DefaultMcp, ToolError, new_output, run_logged, sha, utc, write_json
from macos import APP, ROOT, capture, require_host, require_runtime, verify_payload_manifest


def png_dimensions(path):
    data = Path(path).read_bytes()
    if len(data) < 33 or data[:8] != b"\x89PNG\r\n\x1a\n" or data[12:16] != b"IHDR":
        raise ToolError("Expected a real PNG image")
    width, height = struct.unpack_from(">II", data, 16)
    if not 0 < width <= 16384 or not 0 < height <= 16384:
        raise ToolError("PNG dimensions are invalid")
    return width, height


def validate_scene_evidence(log, dimensions):
    matches = re.findall(r"lit pixels (\d+)/(\d+)", log)
    if len(matches) != 1 or dimensions != (640, 480):
        raise ToolError("Scene probe did not provide its rendered image evidence")
    lit, total = map(int, matches[0])
    if total != 640 * 480 or not 0 < lit <= total:
        raise ToolError("Scene probe did not render positive pixels")
    return {"litPixels": lit, "totalPixels": total, "width": dimensions[0], "height": dimensions[1]}


def owned_window(windows, pid):
    return next((window for window in windows if window.get("pid") == pid and window.get("onScreen") is True
                 and window.get("layer") == 0 and window.get("width", 0) >= 400
                 and window.get("height", 0) >= 250 and window.get("windowID", 0) > 0), None)


def gui_qualified(evidence):
    return all(evidence.get(key) is True for key in ("processStarted", "windowObserved", "screenshotObserved"))


def native_cocoa(app, level, output, env):
    output = new_output(output)
    probe = output / "owned-window-probe"
    run_logged(["/usr/bin/xcrun", "swiftc", ROOT / "tools/macos_native_ui_probe.swift", "-o", probe],
               output, output / "compile-probe.log", env, timeout=120)
    evidence = {"processStarted": False, "windowObserved": False, "screenshotObserved": False,
                "scope": "Native Cocoa startup with synthetic document; no gameplay or exhaustive UI parity claim"}
    process = None
    try:
        with (output / "editor.log").open("wb") as log:
            process = subprocess.Popen([str(app / "Contents/MacOS/Sed.App"), "--", str(level)],
                                       cwd=output, env=env, stdout=log, stderr=subprocess.STDOUT,
                                       start_new_session=True)
            evidence.update(processStarted=True, pid=process.pid)
            deadline = time.monotonic() + 45
            while process.poll() is None and time.monotonic() < deadline:
                windows = json.loads(capture([probe, str(process.pid)], timeout=10))
                window = owned_window(windows, process.pid)
                if window:
                    evidence.update(windowObserved=True, window=window)
                    # Allow the first native frame to settle; capture only the
                    # verified child-owned window, never the whole desktop.
                    time.sleep(2)
                    image = output / "native-editor-window.png"
                    capture(["/usr/sbin/screencapture", "-x", "-l", str(window["windowID"]), image], timeout=15)
                    evidence.update(screenshotObserved=True, screenshotSha256=sha(image),
                                    dimensions=png_dimensions(image))
                    break
                time.sleep(0.25)
            if not gui_qualified(evidence):
                raise ToolError("Native Cocoa window and screenshot were not both observed")
            evidence["status"] = "passed"
    except Exception as error:
        evidence.update(status="failed", error=str(error))
        raise
    finally:
        if process is not None:
            if process.poll() is None:
                os.killpg(process.pid, signal.SIGTERM)
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    os.killpg(process.pid, signal.SIGKILL)
                    process.wait(timeout=10)
            evidence["exitCodeAfterOwnedCleanup"] = process.returncode
        write_json(output / "native-cocoa.json", evidence)
    return evidence


def verify_runtime(package, output, pwsh, graphics=False, full_workflows=False, scene_probe=None):
    package = Path(package).resolve(strict=True)
    provenance = json.loads((package / "PROVENANCE.json").read_text())
    require_host(provenance["rid"])
    verify_payload_manifest(package)
    output = new_output(output)
    receipt = {"status": "running", "startedUtc": utc(), "commit": provenance["commit"], "rid": provenance["rid"],
               "authoring": "pending", "graphics": "not-requested", "nativeCocoa": "pending",
               "fullWorkflowsRequested": full_workflows, "errors": []}
    relocated = output / "relocated package with spaces"
    shutil.copytree(package, relocated)
    alone = output / "copied app alone with spaces" / "SED-MotS.app"
    alone.parent.mkdir()
    shutil.copytree(relocated / "SED-MotS.app", alone)
    workspace = new_output(output / "caller workspace with spaces")
    temp = new_output(output / "native-temp")
    env = os.environ | {key: str(output / "no-sdk") for key in ("DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64")}
    env.update(DOTNET_MULTILEVEL_LOOKUP="0", PYTHONDONTWRITEBYTECODE="1", TMPDIR=str(temp), TMP=str(temp), TEMP=str(temp))
    for key in ("VK_DRIVER_FILES", "VK_ICD_FILENAMES"):
        env.pop(key, None)
    no_graphics = env | {"VK_DRIVER_FILES": str(output / "no-driver.json"), "VK_ICD_FILENAMES": str(output / "no-driver.json")}
    agent, server = relocated / "agent/sed-agent", relocated / "mcp/sed-mcp"
    for directory, name in ((relocated / APP, "Sed.App"), (relocated / "agent", "sed-agent"), (relocated / "mcp", "sed-mcp")):
        require_runtime(directory, name, provenance["rid"])
    level = workspace / "native Mac room.jkl"
    try:
        run_logged([relocated / "bin/sed-agent", "new", level.name, "mots"], workspace, output / "new.log", no_graphics, timeout=60)
        level_hash = sha(level)
        with DefaultMcp(relocated / "bin/sed-mcp", workspace, no_graphics, output / "default-mcp.log") as client:
            client.tool("sed_discover", {})
            result = client.tool("sed_query", {"level": level.name, "request": {"schemaVersion": 1, "collection": "summary"}})
            if result["inputSha256"] != level_hash:
                raise ToolError("Default packaged MCP server read a different document")
        for name in ("Workflow", "SourceLights", "Editing", "Design", "Spatial"):
            command = [pwsh, "-NoLogo", "-NoProfile", "-NonInteractive", "-File",
                       relocated / f"tools/Test-Agent{name}.ps1", "-AgentPath", agent,
                       "-OutputDirectory", output / ("agent-" + name.lower())]
            if name in ("Design", "Spatial"):
                command.append("-SkipSnapshot")
            run_logged(command, workspace, output / f"agent-{name}.log", no_graphics, timeout=600)
        run_logged([sys.executable, "-B", relocated / "tools/test_package_workflow.py",
                    "--agent", agent, "--mcp", server, "--work", output / "dependency-workflow"],
                   workspace, output / "dependency-workflow.log", no_graphics, timeout=600)
        if full_workflows:
            run_logged([sys.executable, "-B", relocated / "tools/test_mcp_workflow.py", "--server", server,
                        "--agent", agent, "--output", output / "mcp-workflow"],
                       workspace, output / "mcp-workflow.log", no_graphics, timeout=600)
            run_logged([sys.executable, "-B", relocated / "tools/test_tool_workbench.py", "--agent", agent,
                        "--output-dir", output / "workbench"], workspace, output / "workbench.log", no_graphics, timeout=600)
        if sha(level) != level_hash:
            raise ToolError("Packaged authoring changed the original synthetic document")
        receipt["authoring"] = "passed"
    except Exception as error:
        receipt["authoring"] = "failed"
        receipt["errors"].append("authoring: " + str(error))
    if graphics:
        try:
            if scene_probe is None:
                raise ToolError("--scene-probe is required for graphics qualification")
            scene = output / "scene.png"
            run_logged([scene_probe, scene], workspace, output / "scene.log", env, timeout=120)
            receipt["scene"] = validate_scene_evidence((output / "scene.log").read_text(), png_dimensions(scene))
            receipt["scene"]["sha256"] = sha(scene)
            run_logged([pwsh, "-NoLogo", "-NoProfile", "-NonInteractive", "-File",
                        relocated / "tools/Test-AgentLitSnapshot.ps1", "-AgentPath", agent,
                        "-OutputDirectory", output / "wren-snapshots"], workspace, output / "wren-snapshots.log", env, timeout=900)
            with DefaultMcp(relocated / "bin/sed-mcp", workspace, env, output / "default-worker.log") as client:
                image_request = {"schemaVersion": 1, "inputSha256": sha(level), "position": [0, -1, 0.2],
                                 "target": [0, 0, 0], "fieldOfViewDegrees": 70, "width": 320, "height": 240}
                image = workspace / "default-worker.png"
                result = client.tool("sed_snapshot", {"level": level.name, "request": image_request, "output": str(image)})
                images = [item for item in client.last_response.get("content", []) if item.get("type") == "image"]
                if len(images) != 1 or base64.b64decode(images[0]["data"], validate=True) != image.read_bytes() or sha(image) != result["sha256"]:
                    raise ToolError("Default sibling preview worker image did not match its response")
            receipt["graphics"] = "passed"
        except Exception as error:
            receipt["graphics"] = "failed"
            receipt["errors"].append("graphics: " + str(error))
    try:
        capture(["/usr/bin/codesign", "--verify", "--deep", "--strict", alone], timeout=60)
        receipt["cocoaEvidence"] = native_cocoa(alone, level, output / "native-cocoa", env)
        receipt["nativeCocoa"] = "passed"
    except Exception as error:
        receipt["nativeCocoa"] = "failed"
        receipt["errors"].append("native Cocoa: " + str(error))
    try:
        verify_payload_manifest(package)
        verify_payload_manifest(relocated)
        originals = {str(p.relative_to(package / "SED-MotS.app")): sha(p) for p in (package / "SED-MotS.app").rglob("*") if p.is_file()}
        moved = {str(p.relative_to(alone)): sha(p) for p in alone.rglob("*") if p.is_file()}
        if originals != moved:
            raise ToolError("Copied-alone app changed during native execution")
    except Exception as error:
        receipt["errors"].append("final payload integrity: " + str(error))
    receipt.update(status="failed" if receipt["errors"] else "passed", finishedUtc=utc())
    write_json(output / "runtime-verification.json", receipt)
    if receipt["errors"]:
        raise ToolError("; ".join(receipt["errors"]))
    return output / "runtime-verification.json"


def apfs_safety(build_output, dotnet, rid, output):
    """Use only new task-owned images; never format or force-detach a host disk."""
    require_host(rid)
    build_output, output = Path(build_output), new_output(output)
    cases = []
    for name, filesystem in (("insensitive", "APFS"), ("sensitive", "APFSX")):
        lane = new_output(output / name)
        image, mount = lane / "fixture.sparseimage", lane / "mounted"
        mount.mkdir()
        record = {"case": name, "status": "running"}
        attached = False
        try:
            capture(["/usr/bin/hdiutil", "create", "-size", "256m", "-fs", filesystem, "-type", "SPARSE",
                     "-volname", "SED-APFS-" + name, image], timeout=90)
            result = capture(["/usr/bin/hdiutil", "attach", "-nobrowse", "-mountpoint", mount, "-plist", image], timeout=45)
            attached = True
            devices = plistlib.loads(result.encode())
            matches = [row for row in devices.get("system-entities", []) if row.get("mount-point") == str(mount)]
            if len(matches) != 1:
                raise ToolError("Owned APFS mount was not identified")
            record["device"] = matches[0].get("dev-entry")
            temp = mount / "temp"
            temp.mkdir()
            marker = temp / "CaseProbe"
            marker.write_text("original fixture")
            insensitive = (temp / "caseprobe").exists()
            if insensitive != (name == "insensitive"):
                raise ToolError("Mounted filesystem case behavior does not match the requested lane")
            record["caseInsensitiveObserved"] = insensitive
            env = os.environ | {"TMPDIR": str(temp), "TMP": str(temp), "TEMP": str(temp)}
            for project, pattern in (("Sed.Core.Tests", "AtomicFilePromotionTests"),
                                     ("Sed.Mcp.Tests", "McpFilePolicyTests")):
                dll = build_output / f"artifacts/bin/{project}/release_{rid}/{project}.dll"
                if not dll.is_file():
                    raise ToolError(f"Native test assembly not found: {dll}")
                run_logged([dotnet, "vstest", dll, "--TestCaseFilter:FullyQualifiedName~" + pattern,
                            "--logger:trx;LogFileName=" + project + ".trx", "--ResultsDirectory:" + str(lane)],
                           lane, lane / (project + ".log"), env, timeout=180)
            record["status"] = "passed"
        except Exception as error:
            record.update(status="failed", error=str(error))
        finally:
            if attached:
                try:
                    capture(["/usr/bin/hdiutil", "detach", mount], timeout=45)
                    record["ownedMountDetached"] = True
                except Exception as error:
                    record.update(status="failed", cleanupError=str(error), ownedMountDetached=False)
            cases.append(record)
            write_json(output / "apfs-safety.json", {"cases": cases})
    if any(case["status"] != "passed" for case in cases):
        raise ToolError("One or more native APFS safety lanes failed")
    return output / "apfs-safety.json"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apfs", action="store_true")
    parser.add_argument("--build-output", type=Path)
    parser.add_argument("--dotnet", type=Path)
    parser.add_argument("--rid")
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    if not args.apfs or not all((args.build_output, args.dotnet, args.rid)):
        parser.error("Use --apfs --build-output --dotnet --rid --output")
    print(apfs_safety(args.build_output, args.dotnet, args.rid, args.output))


if __name__ == "__main__":
    main()
