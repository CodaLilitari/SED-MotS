#!/usr/bin/env python3
"""Prepare private OpenMotS capture inputs for SED's capture runner (Linux engine profile).

Red's helper, original code (MIT). It:

  bootstrap  writes the bootstrap manifest SED's test_engine_lighting.py --inputs
             expects: the Linux engine, the user's own MotS resource files found
             by exact name, and the two original configuration files;
  request    composes a capture-v1 request for one custom level, writing its
             episode.jk, for tools/invoke_engine_capture.py;
  check      re-hashes a bootstrap manifest's files before a long run;
  view       converts a capture.ppm (P6 RGB8) to PNG so it can be looked at.

It never launches the engine, never searches beyond the exact names below and
never copies game files: manifests point at them in place, by absolute path and
SHA-256. Manifests name local paths and identify owned game files, so they stay
private and out of shareable packets. Standard library only.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import struct
import sys
import zlib

SCHEMA = "red-workshop/openmots-capture/v1"
REQUIRED = ("resource/jkmres.goo", "resource/jkmsndlo.goo", "resource/jk_.cd")
EPISODES = ("episode/jkm.goo", "episode/jkm_kfy.goo", "episode/jkm_mp.goo", "episode/jkm_saber.goo")
# The identities Coda's Windows ENGINE-01 bootstrap used (build/engine01-20260920). A
# different retail copy can differ legitimately; matches are reported, not required.
ENGINE01_BASELINE = {
    "resource/jkmres.goo": "fa615eafa30114afd5e38369d790e721df56ce9a4ef62b66f34fc41b7c048a12",
    "resource/jkmsndlo.goo": "b80029082c93c7c47fd886ab659f41b5318fed1fa34b173cf5fd1bb7226d3462",
    "resource/jk_.cd": "efb79bac576656a08243b250ab333781d1c6f7e87a1572caff15b8db12de1679",
}
# Byte-identical to Coda's ENGINE-01 inputs, so their hashes compare across runs.
REGISTRY = b'{"sfxVolume":0,"musicVolume":0,"cutsceneVolume":0,"Window_isFullscreen":false,"Window_isHiDpi":false,"InstallType":9}'
CVARS = b'{"net_disableUpdates":true}'
EPISODE_NAME = re.compile(r"[A-Za-z0-9_-]{1,31}")
MAP_NAME = re.compile(r"[A-Za-z0-9_-]{1,27}\.jkl")
SCENARIO_DIRS = {"3do", "cog", "mat", "misc", "sound", "ui", "voice", "jkl", "key", "puppet", "soundclass"}


class Refusal(Exception):
    pass


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for block in iter(lambda: stream.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def regular_file(path: Path, what: str) -> Path:
    try:
        info = path.lstat()
    except FileNotFoundError:
        raise Refusal(f"{what} not found: {path}") from None
    if stat.S_ISLNK(info.st_mode) or not stat.S_ISREG(info.st_mode):
        raise Refusal(f"{what} must be a regular file, not a link or directory: {path}")
    return path.resolve()


def find_exact(root: Path, relative: str) -> Path | None:
    """Resolve one relative path component by component, ignoring case; refuse ambiguity and links."""
    current = root
    for part in PurePosixPath(relative).parts:
        if current.is_symlink() or not current.is_dir():
            return None
        matches = [entry for entry in current.iterdir() if entry.name.casefold() == part.casefold()]
        if len(matches) > 1:
            raise Refusal(f"ambiguous names in {current}: {sorted(m.name for m in matches)}")
        if not matches:
            return None
        current = matches[0]
    return current


def write_new(path: Path, data: bytes) -> None:
    with open(path, "xb") as stream:
        stream.write(data)


def new_directory(path: Path) -> Path:
    if path.exists() or path.is_symlink():
        raise Refusal(f"output exists: {path}")
    path.mkdir(parents=True)
    return path.resolve()


def entry(source: Path, destination: str, role: str) -> dict:
    return {"source": str(source), "destination": destination, "sha256": sha256(source), "role": role}


def bootstrap(args) -> dict:
    engine = regular_file(Path(args.engine).absolute(), "engine")
    with open(engine, "rb") as stream:
        if stream.read(4) != b"\x7fELF":
            raise Refusal("the Linux engine profile needs the Linux openjkdf2 build (an ELF executable)")
    game = Path(args.game_dir).resolve()
    if not game.is_dir():
        raise Refusal(f"game directory not found: {game}")
    wanted = list(REQUIRED) + (list(EPISODES) if args.episodes else [])
    found, missing = {}, []
    for relative in wanted:
        path = find_exact(game, relative)
        if path is None:
            missing.append(relative)
        else:
            found[relative] = regular_file(path, relative)
    if missing:
        raise Refusal("missing from the game directory: " + ", ".join(missing)
                      + " (stage the user's own MotS files; nothing is downloaded)")
    output = new_directory(Path(args.output).absolute())
    config = output / "config"
    config.mkdir()
    write_new(config / "registry.json", REGISTRY)
    write_new(config / "openjkdf2_cvars.json", CVARS)
    files = [entry(engine, "openjkdf2", "engine")]
    baseline = {}
    for relative, path in found.items():
        folder = PurePosixPath(relative).parts[0]
        item = entry(path, f"{folder}/{path.name}", "resource")
        files.append(item)
        if relative in ENGINE01_BASELINE:
            baseline[item["destination"]] = item["sha256"] == ENGINE01_BASELINE[relative]
    files += [entry(config / "registry.json", "registry.json", "config"),
              entry(config / "openjkdf2_cvars.json", "openjkdf2_cvars.json", "config")]
    manifest = {"schemaVersion": 1, "files": files, "preparedBy": SCHEMA,
                "matchesEngine01Baseline": baseline,
                "privacy": "Names local paths and identifies the user's own game files. Keep private; never share or package."}
    if args.engine_commit:
        manifest["engineCommit"] = args.engine_commit
    write_new(output / "bootstrap.json", (json.dumps(manifest, indent=2) + "\n").encode())
    return {"status": "prepared", "bootstrap": str(output / "bootstrap.json"),
            "files": len(files), "matchesEngine01Baseline": baseline}


def load_bootstrap(path: Path) -> dict:
    data = json.loads(regular_file(path.absolute(), "bootstrap manifest").read_bytes().decode("utf-8-sig"))
    if data.get("schemaVersion") != 1 or not isinstance(data.get("files"), list):
        raise Refusal("not a schemaVersion 1 bootstrap manifest")
    roles = {item.get("role") for item in data["files"]}
    if "engine" not in roles or "scenario" in roles:
        raise Refusal("a bootstrap manifest has one engine and no scenario files")
    return data


def check(args) -> dict:
    data = load_bootstrap(Path(args.bootstrap))
    results = []
    for item in data["files"]:
        source = Path(item["source"])
        try:
            current = sha256(regular_file(source, item["destination"]))
        except Refusal as error:
            results.append({"destination": item["destination"], "ok": False, "problem": str(error)})
            continue
        results.append({"destination": item["destination"], "ok": current == item["sha256"].lower()})
    return {"status": "ok" if all(r["ok"] for r in results) else "changed", "files": results}


def bounded(value, low, high, name, kind=float):
    if kind is int and int(value) != value:
        raise Refusal(f"{name} must be a whole number")
    value = kind(value)
    if not low <= value <= high:
        raise Refusal(f"{name} must be between {low} and {high}")
    return value


def scenario_destination(episode: str, relative: str) -> str:
    path = PurePosixPath(relative.replace("\\", "/"))
    if path.is_absolute() or not path.parts or any(p in ("", ".", "..") for p in path.parts):
        raise Refusal(f"scenario destination must be a plain relative path: {relative}")
    if path.parts[0].casefold() not in SCENARIO_DIRS or len(path.parts) < 2:
        raise Refusal(f"scenario files go in a resource folder ({', '.join(sorted(SCENARIO_DIRS))}): {relative}")
    return f"episode/{episode}/{path.as_posix()}"


def request(args) -> dict:
    data = load_bootstrap(Path(args.bootstrap))
    level = regular_file(Path(args.level).absolute(), "level")
    map_name = args.map or level.name
    if not MAP_NAME.fullmatch(map_name):
        raise Refusal(f"map name {map_name!r} must match [A-Za-z0-9_-]{{1,27}}.jkl; pass --map to stage it under another name")
    if not EPISODE_NAME.fullmatch(args.episode):
        raise Refusal("episode name must match [A-Za-z0-9_-]{1,31}")
    width, height = (bounded(v, 64, 2048, "size", int) for v in args.size)
    if width * height > 2097152:
        raise Refusal("capture pixel budget is 2,097,152")
    camera = {"width": width, "height": height, "sector": bounded(args.sector, 0, 1000000, "sector", int),
              "position": [bounded(v, -1e6, 1e6, "position") for v in args.position],
              "pitchYawRoll": [bounded(v, -1e6, 1e6, "pitchYawRoll") for v in args.pyr],
              "horizontalFov": bounded(args.fov, 20, 140, "fov")}
    engine_ms = bounded(args.engine_timeout_ms, 1000, 60000, "engine timeout", int)
    runner_ms = bounded(args.runner_timeout_ms or engine_ms + 15000, 1000, 90000, "runner timeout", int)
    if runner_ms < engine_ms:
        raise Refusal("the runner timeout must cover the engine timeout")
    limits = {"frame": bounded(args.frame, 1, 120, "frame", int), "engineTimeoutMs": engine_ms,
              "runnerTimeoutMs": runner_ms, "logLimitBytes": bounded(args.log_limit_bytes, 1024, 8388608, "log limit", int)}
    files = [{key: item[key] for key in ("source", "destination", "sha256", "role")} for item in data["files"]]
    scenario = [entry(level, f"episode/{args.episode}/jkl/{map_name}", "scenario")]
    seen = {item["destination"].casefold() for item in files + scenario}
    seen.add(f"episode/{args.episode}/episode.jk".casefold())
    for spec in args.file:
        source, sep, relative = spec.partition("=")
        if not sep:
            raise Refusal(f"--file takes SOURCE=DESTINATION: {spec}")
        item = entry(regular_file(Path(source).absolute(), "scenario file"), scenario_destination(args.episode, relative), "scenario")
        if item["destination"].casefold() in seen:
            raise Refusal(f"duplicate destination: {item['destination']}")
        seen.add(item["destination"].casefold())
        scenario.append(item)
    # Everything is validated; only now is anything written.
    output = new_directory(Path(args.output).absolute())
    title = args.title.replace('"', "'")
    episode_jk = output / "episode.jk"
    write_new(episode_jk, f'"{title}"\nTYPE 1\nSEQ 1\n0: 1 1 LEVEL {map_name} 0 0 -1 -1\nEND\n'.encode("ascii", "replace"))
    files += [entry(episode_jk, f"episode/{args.episode}/episode.jk", "scenario"), *scenario]
    body = {"schemaVersion": 1, "episode": args.episode, "map": map_name, "camera": camera, "files": files, **limits}
    write_new(output / "request.json", (json.dumps(body, indent=2) + "\n").encode())
    return {"status": "prepared", "request": str(output / "request.json"), "files": len(files),
            "next": f"python3 -B <SED>/tools/invoke_engine_capture.py {output / 'request.json'} --output <new capture dir>"}


def read_ppm(path: Path):
    data = regular_file(path.absolute(), "capture").read_bytes()
    tokens, index = [], 0
    while len(tokens) < 4:
        while index < len(data) and data[index:index + 1].isspace():
            index += 1
        if data[index:index + 1] == b"#":
            while index < len(data) and data[index] not in b"\r\n":
                index += 1
            continue
        start = index
        while index < len(data) and not data[index:index + 1].isspace():
            index += 1
        if start == index:
            raise Refusal("truncated PPM header")
        tokens.append(data[start:index])
    if tokens[0] != b"P6" or int(tokens[3]) != 255:
        raise Refusal("expected a binary P6 PPM with maxval 255")
    width, height = int(tokens[1]), int(tokens[2])
    pixels = data[index + 1:]
    if len(pixels) != width * height * 3:
        raise Refusal("PPM pixel data has the wrong length")
    return width, height, pixels


def png_bytes(width: int, height: int, rgb: bytes) -> bytes:
    def chunk(kind, body):
        return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body) & 0xFFFFFFFF)
    rows = b"".join(b"\x00" + rgb[y * width * 3:(y + 1) * width * 3] for y in range(height))
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(rows, 9)) + chunk(b"IEND", b""))


def view(args) -> dict:
    width, height, pixels = read_ppm(Path(args.capture))
    output = Path(args.output).absolute()
    write_new(output, png_bytes(width, height, pixels))
    return {"status": "written", "png": str(output), "width": width, "height": height}


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    b = commands.add_parser("bootstrap", help="write a bootstrap manifest for the Linux engine")
    b.add_argument("--engine", required=True, help="the Linux openjkdf2 build")
    b.add_argument("--game-dir", required=True, help="a staged MotS directory with resource/ (and episode/)")
    b.add_argument("--output", required=True, help="new directory for bootstrap.json and config/")
    b.add_argument("--engine-commit", help="documentary engine commit (keep the build receipt too)")
    b.add_argument("--episodes", action="store_true", help="also include the four episode GOOs")
    r = commands.add_parser("request", help="compose a capture-v1 request for one custom level")
    r.add_argument("--bootstrap", required=True)
    r.add_argument("--level", required=True, help="the JKL to capture (SED output, never a retail original)")
    r.add_argument("--map", help="stage the level under this name (default: its filename)")
    r.add_argument("--episode", default="REDTEST")
    r.add_argument("--title", default="Red capture")
    r.add_argument("--file", action="append", default=[], metavar="SOURCE=DEST",
                   help="extra scenario file, DEST relative to the episode (mat/x.mat, 3do/x.3do, misc/cmp/x.cmp, cog/x.cog)")
    r.add_argument("--sector", type=float, required=True)
    r.add_argument("--position", type=float, nargs=3, required=True, metavar=("X", "Y", "Z"))
    r.add_argument("--pyr", type=float, nargs=3, default=[0.0, 0.0, 0.0], metavar=("PITCH", "YAW", "ROLL"))
    r.add_argument("--fov", type=float, default=90.0)
    r.add_argument("--size", type=float, nargs=2, default=[640, 480], metavar=("WIDTH", "HEIGHT"))
    r.add_argument("--frame", type=float, default=3)
    r.add_argument("--engine-timeout-ms", type=float, default=30000)
    r.add_argument("--runner-timeout-ms", type=float)
    r.add_argument("--log-limit-bytes", type=float, default=1048576)
    r.add_argument("--output", required=True, help="new directory for request.json and episode.jk")
    c = commands.add_parser("check", help="re-hash a bootstrap manifest's files")
    c.add_argument("--bootstrap", required=True)
    v = commands.add_parser("view", help="convert capture.ppm to PNG")
    v.add_argument("capture")
    v.add_argument("--output", required=True)
    args = parser.parse_args(argv)
    try:
        result = {"bootstrap": bootstrap, "request": request, "check": check, "view": view}[args.command](args)
    except (Refusal, OSError, ValueError, json.JSONDecodeError) as error:
        print(json.dumps({"status": "refused", "error": str(error)}), file=sys.stderr)
        return 2
    print(json.dumps(result, indent=2))
    return 0 if result.get("status") != "changed" else 1


if __name__ == "__main__":
    sys.exit(main())
