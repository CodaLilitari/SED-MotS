#!/usr/bin/env python3
"""Run the opt-in OpenMotS capture-v1 contract in a new private Windows cwd.

Usage: python tools/invoke_engine_capture.py REQUEST.json --output NEW_DIRECTORY

Request schemaVersion1 requires episode, map, camera {width,height,sector,
position:[x,y,z],pitchYawRoll:[p,y,r],horizontalFov}, and files [{source:absolute,
destination:relative/POSIX,sha256:hex,role:engine|runtime|resource|scenario|config}].
Optional frame=3, engineTimeoutMs=30000, runnerTimeoutMs=45000 and
logLimitBytes=1048576 specify finite budgets. The three fixed binary names,
resource/jk_.cd, selected episode.jk/map, registry.json and
openjkdf2_cvars.json must all be explicitly supplied. Configuration disables
audio and updates; staged configuration rewrites are recorded and allowed.

The request supplies exact source files, SHA256 identities and destinations; no
directory discovery, arbitrary command arguments, downloads, or engine patches
are performed. This is hidden rendered inspection, NOT deterministic simulation.
See validate_request for the deliberately narrow, versioned JSON input contract.
"""
from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
import math
import os
from pathlib import Path, PurePosixPath
import re
import stat
import subprocess
import sys
import threading
import time

from engine_resource_closure import check_resource_closure

SCHEMA_VERSION = 1
MAX_REQUEST_BYTES = 131072
MAX_FILES = 256
MAX_GENERATED_BYTES = 64 * 1024 * 1024
MAX_GENERATED_ENTRIES = 1024
POLL_SECONDS = 0.05
IMMUTABLE_ROLES = {"engine", "runtime", "resource", "scenario"}
REQUIRED_BINARIES = {"openjkdf2-64.exe": "engine", "OpenAL32.dll": "runtime",
                     "libGameNetworkingSockets.dll": "runtime"}
RESERVED = {"capture.ppm", "capture.ppm.json", "capture-request.txt", "request.json",
            "launcher-receipt.json", "manifest-before.json", "manifest-after.json", "manifest-prelaunch.json",
            "stdout.log", "stderr.log", ".openjkdf2-capture-session"}


class CaptureError(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise CaptureError(message)


def sha256(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, f"Duplicate JSON key: {key}")
        result[key] = value
    return result


def load_json_bytes(raw):
    return json.loads(raw.decode("utf-8-sig"), object_pairs_hook=unique_object,
                      parse_constant=lambda value: (_ for _ in ()).throw(CaptureError(f"Invalid JSON number: {value}")))


def write_json(path, data):
    with Path(path).open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(data, stream, indent=2, allow_nan=False)
        stream.write("\n")


def integer(value, low, high, name):
    require(type(value) is int and low <= value <= high, f"{name} must be an integer in {low}..{high}.")
    return value


def number(value, low, high, name):
    require(type(value) in (int, float) and math.isfinite(value) and low <= value <= high,
            f"{name} must be finite and in {low}..{high}.")
    return float(value)


def exact_keys(value, required, optional, name):
    require(isinstance(value, dict), f"{name} must be an object.")
    require(set(value) >= set(required), f"{name} is missing required fields: {sorted(set(required)-set(value))}")
    require(set(value) <= set(required) | set(optional), f"Unsupported {name} fields: {sorted(set(value)-set(required)-set(optional))}")


def safe_destination(value):
    require(isinstance(value, str) and 0 < len(value) <= 120, "Invalid staging destination length.")
    require("\\" not in value and not value.startswith("/"), "Destinations must be relative POSIX paths.")
    parts = value.split("/")
    devices = {"CON", "PRN", "AUX", "NUL", *(f"COM{i}" for i in range(1, 10)), *(f"LPT{i}" for i in range(1, 10))}
    for part in parts:
        require(re.fullmatch(r"[A-Za-z0-9_.-]+", part) is not None and part not in (".", "..")
                and not part.endswith(".") and part.split(".")[0].upper() not in devices,
                f"Unsafe staging destination: {value}")
    require(value.casefold() not in RESERVED, f"Reserved staging destination: {value}")
    return value


def is_link(path):
    info = Path(path).lstat()
    return stat.S_ISLNK(info.st_mode) or bool(getattr(info, "st_file_attributes", 0) & 0x400)


def validate_request(request):
    exact_keys(request, {"schemaVersion", "episode", "map", "camera", "files"},
               {"frame", "engineTimeoutMs", "runnerTimeoutMs", "logLimitBytes"}, "request")
    require(type(request["schemaVersion"]) is int and request["schemaVersion"] == 1, "Unsupported request schemaVersion.")
    episode, level = request["episode"], request["map"]
    require(isinstance(episode, str) and re.fullmatch(r"[A-Za-z0-9_-]{1,31}", episode), "Invalid episode name.")
    require(isinstance(level, str) and re.fullmatch(r"[A-Za-z0-9_-]{1,27}\.jkl", level), "Invalid map filename.")
    camera = request["camera"]
    exact_keys(camera, {"width", "height", "sector", "position", "pitchYawRoll", "horizontalFov"}, set(), "camera")
    normalized = {"width": integer(camera["width"], 64, 2048, "width"),
                  "height": integer(camera["height"], 64, 2048, "height"),
                  "sector": integer(camera["sector"], 0, 1000000, "sector"),
                  "horizontalFov": number(camera["horizontalFov"], 20, 140, "horizontalFov")}
    require(normalized["width"] * normalized["height"] <= 2097152, "Capture pixel budget exceeded.")
    for name in ("position", "pitchYawRoll"):
        require(isinstance(camera[name], list) and len(camera[name]) == 3, f"{name} must contain three numbers.")
        normalized[name] = [number(v, -1000000, 1000000, name) for v in camera[name]]
    frame = integer(request.get("frame", 3), 1, 120, "frame")
    engine_timeout = integer(request.get("engineTimeoutMs", 30000), 1000, 60000, "engineTimeoutMs")
    runner_timeout = integer(request.get("runnerTimeoutMs", 45000), 1000, 90000, "runnerTimeoutMs")
    require(runner_timeout >= engine_timeout, "runnerTimeoutMs must cover engineTimeoutMs.")
    log_limit = integer(request.get("logLimitBytes", 1048576), 1024, 8388608, "logLimitBytes")
    files = request["files"]
    require(isinstance(files, list) and 1 <= len(files) <= MAX_FILES, "files must be an explicit nonempty list of at most256 entries.")
    result_files, destinations = [], set()
    for item in files:
        exact_keys(item, {"source", "destination", "sha256", "role"}, set(), "file")
        dest = safe_destination(item["destination"])
        lower = dest.casefold()
        require(lower not in destinations and not any(lower.startswith(p + "/") or p.startswith(lower + "/") for p in destinations),
                f"Duplicate or conflicting staging destination: {dest}")
        destinations.add(lower)
        require(isinstance(item["sha256"], str) and re.fullmatch(r"[0-9a-fA-F]{64}", item["sha256"]), "Each file requires a SHA256 identity.")
        require(isinstance(item["source"], str) and Path(item["source"]).is_absolute(), "Each source must be an absolute path.")
        source = Path(item["source"])
        require(source.is_file() and not is_link(source), f"Source must be a regular, non-reparse file: {source}")
        source = source.resolve(strict=True)
        role = item["role"]
        require(isinstance(role, str) and role in IMMUTABLE_ROLES | {"config"}, "Unsupported file role.")
        if role in ("engine", "runtime"):
            require(REQUIRED_BINARIES.get(dest) == role, "Only the three explicit capture executable/DLL names are supported.")
        elif role == "config":
            require(dest in ("registry.json", "openjkdf2_cvars.json"), "Unsupported isolated configuration file.")
        elif role == "scenario":
            require(dest.startswith(f"episode/{episode}/"), "Scenario files must live under the selected episode directory.")
        else:
            require(dest.startswith("resource/") or (dest.startswith("episode/") and len(PurePosixPath(dest).parts) == 2
                    and dest.lower().endswith((".goo", ".gob"))), "Resource files must be explicit resource entries or episode archives.")
        result_files.append({"source": str(source), "destination": dest, "sha256": item["sha256"].lower(), "role": role})
    by_dest = {item["destination"]: item for item in result_files}
    required = {**REQUIRED_BINARIES, "registry.json": "config", "openjkdf2_cvars.json": "config",
                "resource/jk_.cd": "resource", f"episode/{episode}/episode.jk": "scenario",
                f"episode/{episode}/jkl/{level}": "scenario"}
    for dest, role in required.items():
        require(dest in by_dest and by_dest[dest]["role"] == role, f"Missing required {role} input: {dest}")
    return {"schemaVersion": 1, "episode": episode, "map": level, "camera": normalized, "frame": frame,
            "engineTimeoutMs": engine_timeout, "runnerTimeoutMs": runner_timeout, "logLimitBytes": log_limit,
            "files": result_files}


def validate_config(path, destination):
    require(path.stat().st_size <= 16384, "Configuration file exceeds16KiB.")
    value = load_json_bytes(path.read_bytes())
    if destination == "openjkdf2_cvars.json":
        exact_keys(value, {"net_disableUpdates"}, set(), "cvars")
        require(value["net_disableUpdates"] is True, "net_disableUpdates must be true.")
    else:
        exact_keys(value, {"sfxVolume", "musicVolume", "cutsceneVolume"},
                   {"Window_isFullscreen", "Window_isHiDpi", "InstallType"}, "registry")
        for key in ("sfxVolume", "musicVolume", "cutsceneVolume"):
            require(type(value[key]) in (int, float) and value[key] == 0, f"{key} must be zero.")
        for key in ("Window_isFullscreen", "Window_isHiDpi"):
            require(key not in value or value[key] is False, f"{key} must be false.")
        require("InstallType" not in value or type(value["InstallType"]) is int and value["InstallType"] == 9,
                "InstallType must be9 when supplied.")


def command_for(request, output):
    return [str(output / "openjkdf2-64.exe"), "-capture", "-autostart", "-sp", "-motsCompat", "-noHUD",
            "-episode", request["episode"], "-map", request["map"]]


def request_text(request, image):
    c = request["camera"]
    require(image.is_absolute() and image.suffix == ".ppm" and len(str(image)) <= 1023
            and str(image).isascii() and not any(ord(ch) < 32 or ch == '"' for ch in str(image)),
            "Capture output needs an ASCII absolute .ppm path of at most1023 characters.")
    pose = " ".join(format(v, ".17g") for v in [*c["position"], *c["pitchYawRoll"], c["horizontalFov"]])
    return f"sed-engine-capture-v1\n{c['width']} {c['height']} {c['sector']} {request['frame']} {request['engineTimeoutMs']}\n{pose}\n{image}\n"


def after_manifest(before, output):
    records = []
    for item in before:
        record = dict(item)
        for key, path in (("sourceAfter", Path(item["source"])), ("stagedAfter", output / item["destination"])):
            try:
                require(not is_link(path), "File became a reparse point.")
                record[key] = {"bytes": path.stat().st_size, "sha256": sha256(path)}
            except (OSError, CaptureError) as exc:
                record[key] = {"error": str(exc)}
        record["sourceUnchanged"] = record["sourceAfter"].get("sha256") == item["sourceBeforeSha256"]
        record["stagedUnchanged"] = (record["stagedAfter"].get("sha256") == item["stagedBeforeSha256"]
                                     if item["stagedBeforeSha256"] is not None else None)
        record["sourceAfterMatchesDeclared"] = record["sourceAfter"].get("sha256") == item["sha256"]
        record["stagedAfterMatchesDeclared"] = record["stagedAfter"].get("sha256") == item["sha256"]
        record["stagedMutationAllowed"] = item["role"] == "config"
        records.append(record)
    return records


class CappedDrain(threading.Thread):
    def __init__(self, source, destination, limit, stop):
        super().__init__(daemon=True)
        self.source, self.destination, self.limit, self.stop = source, destination, limit, stop
        self.observed = self.retained = 0
        self.error = None

    def run(self):
        try:
            with self.destination.open("xb") as target:
                while True:
                    block = self.source.read(65536)
                    if not block:
                        break
                    self.observed += len(block)
                    keep = block[:max(0, self.limit - self.retained)]
                    target.write(keep)
                    self.retained += len(keep)
                    if self.observed > self.limit:
                        self.stop.set()
        except Exception as exc:
            self.error = str(exc)
            self.stop.set()

    def receipt(self):
        return {"observedBytes": self.observed, "retainedBytes": self.retained,
                "limitBytes": self.limit, "truncated": self.observed > self.retained, "error": self.error}


class WindowsJob:
    """Contain assigned members; launch-to-assignment child creation is not atomic."""
    def __init__(self, process):
        from ctypes import wintypes as w
        class Basic(ctypes.Structure):
            _fields_ = [("perProcess", ctypes.c_int64), ("perJob", ctypes.c_int64), ("flags", w.DWORD),
                        ("minWork", ctypes.c_size_t), ("maxWork", ctypes.c_size_t), ("active", w.DWORD),
                        ("affinity", ctypes.c_size_t), ("priority", w.DWORD), ("scheduling", w.DWORD)]
        class Io(ctypes.Structure):
            _fields_ = [(n, ctypes.c_uint64) for n in ("readOps", "writeOps", "otherOps", "readBytes", "writeBytes", "otherBytes")]
        class Extended(ctypes.Structure):
            _fields_ = [("basic", Basic), ("io", Io), ("processMemory", ctypes.c_size_t),
                        ("jobMemory", ctypes.c_size_t), ("peakProcess", ctypes.c_size_t), ("peakJob", ctypes.c_size_t)]
        self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        self.kernel.CreateJobObjectW.argtypes = [ctypes.c_void_p, w.LPCWSTR]
        self.kernel.CreateJobObjectW.restype = w.HANDLE
        self.kernel.SetInformationJobObject.argtypes = [w.HANDLE, ctypes.c_int, ctypes.c_void_p, w.DWORD]
        self.kernel.AssignProcessToJobObject.argtypes = [w.HANDLE, w.HANDLE]
        self.kernel.QueryInformationJobObject.argtypes = [w.HANDLE, ctypes.c_int, ctypes.c_void_p, w.DWORD, ctypes.c_void_p]
        self.kernel.TerminateJobObject.argtypes = [w.HANDLE, w.UINT]
        self.kernel.CloseHandle.argtypes = [w.HANDLE]
        self.handle = self.kernel.CreateJobObjectW(None, None)
        require(self.handle, f"CreateJobObject failed: {ctypes.get_last_error()}")
        limits = Extended()
        limits.basic.flags = 0x2000  # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if not self.kernel.SetInformationJobObject(self.handle, 9, ctypes.byref(limits), ctypes.sizeof(limits)) or not self.kernel.AssignProcessToJobObject(self.handle, int(process._handle)):
            error = ctypes.get_last_error()
            self.close()
            raise CaptureError(f"Could not contain owned capture process: Win32 {error}")

    def kill(self):
        if self.handle:
            self.kernel.TerminateJobObject(self.handle, 6)

    def pids(self):
        class ProcessIds(ctypes.Structure):
            _fields_ = [("assigned", ctypes.c_uint32), ("count", ctypes.c_uint32),
                        ("processIds", ctypes.c_size_t * 256)]
        ids = ProcessIds()
        require(self.kernel.QueryInformationJobObject(self.handle, 3, ctypes.byref(ids), ctypes.sizeof(ids), None),
                f"Could not enumerate owned job processes: Win32 {ctypes.get_last_error()}")
        require(ids.count <= 256 and ids.assigned <= 256, "Owned process tree exceeded256 processes.")
        return list(ids.processIds[:ids.count])

    def close(self):
        if self.handle:
            self.kernel.CloseHandle(self.handle)
            self.handle = None


def owned_windows(pid):
    """Read only ownership/visibility flags; never titles, pixels or other apps."""
    from ctypes import wintypes as w
    user = ctypes.WinDLL("user32", use_last_error=True)
    callback_type = ctypes.WINFUNCTYPE(w.BOOL, w.HWND, w.LPARAM)
    user.EnumWindows.argtypes = [callback_type, w.LPARAM]
    user.GetWindowThreadProcessId.argtypes = [w.HWND, ctypes.POINTER(w.DWORD)]
    user.IsWindowVisible.argtypes = [w.HWND]
    user.GetForegroundWindow.restype = w.HWND
    foreground = user.GetForegroundWindow()
    count = [0, 0, 0]
    @callback_type
    def inspect(window, _):
        owner = w.DWORD()
        user.GetWindowThreadProcessId(window, ctypes.byref(owner))
        if owner.value == pid:
            count[0] += 1
            count[1] += bool(user.IsWindowVisible(window))
            count[2] += window == foreground
        return True
    require(user.EnumWindows(inspect, 0), f"Could not inspect owned windows: Win32 {ctypes.get_last_error()}")
    return count


def check_generated_budget(output, staged):
    total, count = 0, 0
    pending = [output]
    while pending:
        directory = pending.pop()
        with os.scandir(directory) as entries:
            for entry in entries:
                count += 1
                require(count <= MAX_GENERATED_ENTRIES + len(staged), "Session file/directory count exceeded budget.")
                path = Path(entry.path)
                require(not is_link(path), "Session contains an unexpected reparse point.")
                if entry.is_dir(follow_symlinks=False):
                    pending.append(path)
                else:
                    relative = path.relative_to(output).as_posix()
                    size = entry.stat(follow_symlinks=False).st_size
                    original = staged.get(relative)
                    if original is not None and original["role"] in IMMUTABLE_ROLES:
                        require(size == original["bytes"], f"Staged immutable file size changed: {relative}")
                    else:
                        total += size
                        require(total <= MAX_GENERATED_BYTES, "Generated session output exceeded64MiB.")
    return total


def supervise(command, output, environment, timeout_ms, log_limit, staged, *,
              observer=owned_windows, job_factory=WindowsJob, started_callback=None):
    """Internal supervisor; tests may supply a synthetic child, never via CLI."""
    started = time.monotonic()
    process = job = None
    drains = []
    stop = threading.Event()
    result = {"status": "failed", "engineStarted": False, "processExitCode": None, "timedOut": False,
              "processContainment": {"method": "Windows Job Object after Popen", "assignmentAtomicWithLaunch": False,
                                     "childCreationBeforeAssignmentExcluded": False},
              "windowObservation": {"samples": 0, "maximumOwnedWindows": 0, "maximumVisibleOwnedWindows": 0,
                                    "maximumForegroundOwnedWindows": 0, "pollMilliseconds": 50,
                                    "transientWindowsBetweenSamplesNotExcluded": True}}
    try:
        kwargs = {}
        if os.name == "nt":
            startup = subprocess.STARTUPINFO()
            startup.dwFlags = subprocess.STARTF_USESHOWWINDOW
            startup.wShowWindow = 0
            kwargs = {"startupinfo": startup, "creationflags": subprocess.CREATE_NO_WINDOW | subprocess.CREATE_NEW_PROCESS_GROUP}
        process = subprocess.Popen(command, cwd=output, env=environment, stdin=subprocess.DEVNULL,
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE, bufsize=0, **kwargs)
        result["engineStarted"] = True
        result["processId"] = process.pid
        if started_callback is not None:
            started_callback(process.pid)
        job = job_factory(process)
        for name, pipe in (("stdout", process.stdout), ("stderr", process.stderr)):
            drain = CappedDrain(pipe, output / f"{name}.log", log_limit, stop)
            drains.append((name, drain))
            drain.start()
        while True:
            pids = job.pids() if hasattr(job, "pids") else [process.pid]
            visible = [0, 0, 0]
            for pid in pids:
                visible = [a + b for a, b in zip(visible, observer(pid))]
            observation = result["windowObservation"]
            observation["samples"] += 1
            for key, value in zip(("maximumOwnedWindows", "maximumVisibleOwnedWindows", "maximumForegroundOwnedWindows"), visible):
                observation[key] = max(observation[key], value)
            require(visible[1] == 0 and visible[2] == 0, "Owned process exposed a visible or foreground window.")
            check_generated_budget(output, staged)
            require(not stop.is_set(), "A process log exceeded its byte limit or failed to drain.")
            if process.poll() is not None:
                break
            if (time.monotonic() - started) * 1000 >= timeout_ms:
                result["timedOut"] = True
                raise CaptureError("Capture exceeded runner deadline.")
            stop.wait(POLL_SECONDS)
        require(process.returncode == 0, f"Capture process exited with code {process.returncode}.")
        result["status"] = "completed"
    except (Exception, KeyboardInterrupt) as exc:
        result["error"] = f"{type(exc).__name__}: {exc}"
    finally:
        if job is not None:
            if process.poll() is None:
                job.kill()
            job.close()
        if process is not None:
            if process.poll() is None:
                process.kill()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                result["error"] = "Owned process did not exit after termination."
                result["status"] = "failed"
            result["processExitCode"] = process.returncode
        for name, drain in drains:
            drain.join(timeout=5)
            result[name] = drain.receipt()
            if drain.is_alive() or drain.error or drain.observed > log_limit:
                result["status"] = "failed"
                result["error"] = "A process log exceeded its byte limit or did not finish draining."
        if process is not None:
            for pipe in (process.stdout, process.stderr):
                if pipe is not None:
                    pipe.close()
        result["elapsedSeconds"] = time.monotonic() - started
    return result


def validate_capture(output, request):
    image = output / "capture.ppm"
    c = request["camera"]
    header = f"P6\n{c['width']} {c['height']}\n255\n".encode("ascii")
    require(image.is_file() and not is_link(image) and image.stat().st_size == len(header) + c["width"] * c["height"] * 3,
            "Engine image is missing or has invalid dimensions/length.")
    with image.open("rb") as stream:
        require(stream.read(len(header)) == header, "Engine image has an invalid PPM header.")
    path = output / "capture.ppm.json"
    require(path.is_file() and not is_link(path) and path.stat().st_size <= 16384, "Engine receipt is missing or exceeds16KiB.")
    receipt = load_json_bytes(path.read_bytes())
    expected = {"schemaVersion": 1, "mode": "hidden-rendered-inspection", "format": "ppm-rgb8",
                "width": c["width"], "height": c["height"], "renderedFrame": request["frame"],
                "sector": c["sector"], "position": c["position"], "pitchYawRoll": c["pitchYawRoll"],
                "horizontalFov": c["horizontalFov"], "deterministicSimulation": False, "resourceHashesVerifiedByEngine": False}
    exact_keys(receipt, set(expected), set(), "engine receipt")
    for key, value in expected.items():
        require(receipt[key] == value and (not isinstance(value, (bool, int)) or type(receipt[key]) is type(value)),
                f"Engine receipt does not match requested {key}.")
    return {"engineReportedReceipt": receipt, "imageSha256": sha256(image), "engineReceiptSha256": sha256(path), "validPpm": True}


def final_stage_verification(before, output):
    """Freshly rehash every declared source and staged file immediately prelaunch."""
    records = []
    for item in before:
        source, target = Path(item["source"]), output / item["destination"]
        record = {"destination": item["destination"], "declaredSha256": item["sha256"]}
        for name, path in (("sourceSha256", source), ("stagedSha256", target)):
            require(path.is_file() and not is_link(path), f"Final staging identity is not a regular file: {item['destination']}")
            record[name] = sha256(path)
        record["matchesDeclared"] = record["sourceSha256"] == record["stagedSha256"] == item["sha256"]
        records.append(record)
    return records


def run_capture(request_path, output, *, executor=supervise, _before_final_stage=None):
    """Create a fresh isolated session. Existing/unsafe destinations are untouched.

    Once a new output directory is accepted, all handled failures receive a
    launcher-receipt.json. Preflight refusals are returned on stderr by main.
    """
    output = Path(output).absolute()
    request_path = Path(request_path).absolute()
    require(not output.exists() and not output.is_symlink(), "Output must be a new directory.")
    require(output.parent.is_dir() and not is_link(output.parent), "Output parent must exist and not be a reparse point.")
    output = output.parent.resolve(strict=True) / output.name
    require(request_path.is_file() and not is_link(request_path), "Request must be an existing regular file.")
    require(request_path.stat().st_size <= MAX_REQUEST_BYTES, "Request exceeds128KiB.")
    with request_path.open("rb") as stream:
        raw = stream.read(MAX_REQUEST_BYTES + 1)
    require(len(raw) <= MAX_REQUEST_BYTES, "Request exceeds128KiB.")
    # Reject an output under any explicitly named input directory before creating
    # it. A malformed request still gets a receipt in a safe accepted destination.
    parsed = None
    parse_error = None
    try:
        parsed = load_json_bytes(raw)
    except Exception as exc:
        parse_error = exc
    if isinstance(parsed, dict) and isinstance(parsed.get("files"), list):
        for item in parsed["files"]:
            if isinstance(item, dict) and isinstance(item.get("source"), str) and Path(item["source"]).is_absolute():
                source_parent = Path(item["source"]).resolve().parent
                require(not output.is_relative_to(source_parent), "Output must not be within an input file directory.")
    output.mkdir()
    before = []
    receipt = {"schemaVersion": 1, "status": "failed", "engineStarted": False, "failureStage": "preflight", "requestSource": str(request_path),
               "requestSha256": hashlib.sha256(raw).hexdigest(), "outputDirectory": str(output),
               "mode": "hidden-rendered-inspection", "simulationDeterministic": False, "pixelParityClaimed": False,
               "runtimeResourceResolutionObserved": False, "effectiveCameraObserved": False,
               "lightingMode": {"requested": "native-default", "expectedFromSource": "MotS RGB", "observed": None}}
    try:
        if parse_error:
            raise parse_error
        request = validate_request(parsed)
        receipt["failureStage"] = "staging"
        receipt["requestedCamera"] = request["camera"]
        receipt["requestedFrame"] = request["frame"]
        receipt["limits"] = {"engineTimeoutMs": request["engineTimeoutMs"], "runnerTimeoutMs": request["runnerTimeoutMs"],
                             "logLimitBytesPerStream": request["logLimitBytes"], "generatedBytes": MAX_GENERATED_BYTES,
                             "generatedEntries": MAX_GENERATED_ENTRIES, "generatedBudgetIsPolledNotFilesystemQuota": True}
        (output / "request.json").write_bytes(raw)
        for item in request["files"]:
            source = Path(item["source"])
            observed_hash = sha256(source)
            record = dict(item, bytes=source.stat().st_size, sourceBeforeSha256=observed_hash,
                          sourceBeforeMatchesDeclared=observed_hash == item["sha256"], stagedBeforeSha256=None)
            before.append(record)
            require(observed_hash == item["sha256"], f"Source SHA256 mismatch: {item['destination']}")
            target = output / item["destination"]
            target.parent.mkdir(parents=True, exist_ok=True)
            with source.open("rb") as src, target.open("xb") as dst:
                for block in iter(lambda: src.read(1024 * 1024), b""):
                    dst.write(block)
            record["stagedBeforeSha256"] = sha256(target)
            require(record["stagedBeforeSha256"] == item["sha256"], f"Staged SHA256 mismatch: {item['destination']}")
            if item["role"] == "config":
                validate_config(target, item["destination"])
        write_json(output / "manifest-before.json", before)
        (output / ".openjkdf2-capture-session").write_text("isolated-original-sed-arch-inspection\n", encoding="ascii")
        capture_request = output / "capture-request.txt"
        capture_request.write_text(request_text(request, output / "capture.ppm"), encoding="ascii", newline="\n")
        receipt["captureRequestSha256"] = sha256(capture_request)
        command = command_for(request, output)
        receipt["command"] = command
        # Do not pass arbitrary caller secrets or inherited SDL/capture overrides.
        names = {"SYSTEMROOT", "WINDIR", "PATH", "TEMP", "TMP", "USERPROFILE", "LOCALAPPDATA", "APPDATA", "HOMEDRIVE", "HOMEPATH"}
        environment = {k: v for k, v in os.environ.items() if k.upper() in names}
        environment["OPENJKDF2_CAPTURE_REQUEST"] = str(capture_request)
        if executor is supervise:
            require(os.name == "nt", "Actual capture is supported only on Windows.")
        receipt["failureStage"] = "resource-closure"
        receipt["resourceClosure"] = check_resource_closure(before, output, request["episode"])
        require(receipt["resourceClosure"]["valid"], "Custom resource aliases collide or an explicit bootstrap archive index is unsupported.")
        # Internal unit-test seam only, never supplied by a CLI request.
        if _before_final_stage is not None:
            _before_final_stage(output)
        receipt["failureStage"] = "final-staging-identity"
        final_stage = final_stage_verification(before, output)
        receipt["finalStagingIdentity"] = final_stage
        write_json(output / "manifest-prelaunch.json", final_stage)
        require(all(item["matchesDeclared"] for item in final_stage), "Source or staged bytes changed before launch; capture refused.")
        receipt["failureStage"] = "execution"
        def started(pid):
            receipt["engineStarted"] = True
            receipt["processId"] = pid
        execution = executor(command, output, environment, request["runnerTimeoutMs"], request["logLimitBytes"],
                             {item["destination"]: item for item in before}, started_callback=started)
        receipt["execution"] = execution
        require(execution["status"] == "completed", execution.get("error", "Engine capture failed."))
        receipt["failureStage"] = "capture-validation"
        check_generated_budget(output, {item["destination"]: item for item in before})
        receipt.update(validate_capture(output, request))
        receipt["status"] = "completed"
        receipt["failureStage"] = None
    except (Exception, KeyboardInterrupt) as exc:
        receipt["error"] = f"{type(exc).__name__}: {exc}"
    finally:
        after = after_manifest(before, output)
        receipt["inputsUnchanged"] = all(item["sourceUnchanged"] and (item["stagedUnchanged"] is not False or item["stagedMutationAllowed"]) for item in after)
        if not receipt["inputsUnchanged"]:
            receipt["status"] = "failed"
            receipt["identityError"] = "Source or immutable staged input changed from its observed pre-run bytes; inspect manifest-after.json."
            if "error" not in receipt:
                receipt["error"] = receipt["identityError"]
                receipt["failureStage"] = "post-run-identity"
        if not (output / "manifest-before.json").exists():
            write_json(output / "manifest-before.json", before)
        write_json(output / "manifest-after.json", after)
        write_json(output / "launcher-receipt.json", receipt)
    return receipt


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("request", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args(argv)
    try:
        receipt = run_capture(args.request, args.output)
        print(json.dumps({"status": receipt["status"], "receipt": str(Path(receipt["outputDirectory"]) / "launcher-receipt.json")}, allow_nan=False))
        return 0 if receipt["status"] == "completed" else 1
    except (Exception, KeyboardInterrupt) as exc:
        print(json.dumps({"status": "refused", "error": f"{type(exc).__name__}: {exc}"}), file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
