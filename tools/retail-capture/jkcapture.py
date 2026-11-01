#!/usr/bin/env python3
# Capture frames from the retail JK.EXE under Wine: isolated prefix, scripted menus, frame grabs.
# Part of SED-MotS (Red's claude branch). Finishes Wren's level-preview work: Wren (a Grok bot,
# one of Andrei's collaborators) wrote the original jk.sh/run.sh; recovered 2026-10-31.
# SPDX-License-Identifier: GPL-3.0-or-later
"""Retail Jedi Knight capture under Wine (Wren's jk.sh/run.sh recipe, generalised).

Subcommands (run `jkcapture.py <command> -h` for options):
  build-stub  compile and verify the inert smackw32.dll stand-in from stub/
  build-loopsp compile and verify the inert DirectPlay loopback provider from loopsp/
  prepare     create a NEW isolated Wine prefix with a JK install layout
  run         launch JK.EXE on a private Xvfb display and run a JSON step script
  sp          single-player flow: player profile, New Game, level frame, optional walk
  mp-host     multiplayer flow: host a game on a chosen episode level
  walk        Wren's walk demo (scripts/walk.json) on a session kept running
  steps       run a step script on a session kept running (--keep-running)
  stop        tear down a session kept running
  sp-wrap     wrap one level of an episode GOB as a single-player episode GOB

Only the hash-checked retail JK.EXE and the two DLLs built here from source (the Smacker
stand-in and the DirectPlay loopback provider) run in the prefix; every run re-checks their
hashes and refuses any other module under C:\\JK. Inputs are GOB/GOO archives and JK_.CD;
nothing else is staged. The prefix keeps Wine's settings apart from other prefixes and drops the
Z: drive, but it is not a sandbox: JK.EXE runs as the calling Unix user.

Wine 9.0 findings this tool is built around (see README.md):
  * wined3d's GDI renderer overruns a buffer when JK updates its 8-bit palette at level load
    (crash or a black 3D view), so prepare defaults to --wined3d gl, which needs 32-bit Mesa;
  * Wine's TCP/IP DirectPlay provider cannot open a session (DPWSCB_Open is a stub), so prepare
    registers a local-only loopback provider and mp-host hosts through it.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import signal
import struct
import subprocess
import sys
import tempfile
import time
import zlib
from pathlib import Path

TOOL = "jkcapture"
TOOL_VERSION = "0.1.0"
SCHEMA_VERSION = 1
HERE = Path(__file__).resolve().parent
CREDITS = ("Retail captures finish work started by Wren, a Grok bot and one of Andrei's AI collaborators: "
           "Wren's jk.sh and run.sh drove the original JK.EXE under Wine with xdotool and grabbed frames "
           "with ffmpeg. jkcapture generalises that recipe with credit to Wren.")
# Executables this tool will run. Another JK.EXE needs --exe-sha256, which the user vouches for.
KNOWN_EXECUTABLES = {
    "4509cd690634ba20e132bd16e713f91e144b17e2beaaad5e678f1ddb189b51d9": "Star Wars Jedi Knight: Dark Forces II 1.01 (retail JK.EXE)",
}
STUB_ORDINALS = (14, 17, 18, 19, 21, 23, 26, 32, 38)
GUI_WIDTH, GUI_HEIGHT = 640, 480
DESKTOP_WINDOW = "Default - Wine desktop"
PREFIX_RECEIPT = "capture-prefix.json"
RUN_RECEIPT = "capture-run.json"
SESSION_FILE = "session.json"
LOCK_FILE = "jkcapture.lock"
RESOURCE_NAMES = ("res1hi.gob", "res2.gob", "jk_.cd")
ARCHIVE_SUFFIXES = (".gob", ".goo")
MAX_CD_MARKER_BYTES = 4096
DEFAULT_DISPLAYS = range(91, 100)
XVFB_SCREEN = "1280x1024x24"
JK_ARGS_ALLOWED = {"-windowgui": "-windowGUI", "-dispstats": "-dispStats", "-framerate": "-frameRate",
                   "-nohud": "-noHUD", "-devmode": "-devMode"}
WINE_OVERRIDES = "mscoree,mshtml=;winemenubuilder.exe=d"
NOT_BLACK_MEAN = 8.0
CHANGED_PIXEL_DELTA = 24
# Default WINEDEBUG: silent except the renderer choice and DirectPlay errors, which the receipt reports.
DEFAULT_WINE_DEBUG = "-all,err+winediag,err+dplay"
CRASH_MARKERS = ("Unhandled page fault", "Unhandled exception", "Unhandled illegal instruction",
                 "Unhandled stack overflow", "Unhandled privileged instruction")
# 32-bit OpenGL for wined3d's GL renderer (Debian/Ubuntu multiarch first, then lib32 layouts).
GL32_CANDIDATES = ("/usr/lib/i386-linux-gnu/libGL.so.1", "/usr/lib32/libGL.so.1", "/usr/lib/libGL.so.1")
GL32_HINT = ("install 32-bit Mesa (Ubuntu/Debian: sudo dpkg --add-architecture i386; sudo apt-get update; "
             "sudo apt-get install libgl1:i386 libgl1-mesa-dri:i386 libglx-mesa0:i386), or pass --wined3d gdi "
             "for menus only (Wine 9.0's GDI renderer crashes or shows a black 3D view at level load)")
DIRECTPLAY_SP_KEY = r"HKLM\Software\Microsoft\DirectPlay\Service Providers"
LOOPBACK_SP = {"name": "Virtual Loopback (jkcapture)", "guid": "{7158F146-36BB-4070-AC35-D6456B940FDB}",
               "file": "jkcap_loopsp.dll", "windowsPath": r"C:\JK\jkcap_loopsp.dll"}


class CaptureError(Exception):
    """An input or precondition was rejected; the CLI reports status "refused" (exit 2)."""


class ToolFailure(CaptureError):
    """An external tool (wineboot, the compiler, Xvfb, wineserver ...) failed; status "failed" (exit 1)."""


def require(condition, message):
    if not condition:
        raise CaptureError(message)


def check_ran(condition, message):
    if not condition:
        raise ToolFailure(message)


def sha256_bytes(data):
    return hashlib.sha256(data).hexdigest()


def sha256_file(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def write_json_new(path, data):
    with Path(path).open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(data, stream, indent=2, allow_nan=False)
        stream.write("\n")


def load_json(path, limit=1 << 20):
    path = Path(path)
    require(path.is_file(), f"Missing JSON file: {path}")
    raw = path.read_bytes()
    require(len(raw) <= limit, f"JSON file is larger than {limit} bytes: {path}")

    def unique(pairs):
        result = {}
        for key, value in pairs:
            require(key not in result, f"Duplicate JSON key {key!r} in {path}")
            result[key] = value
        return result
    try:
        return json.loads(raw.decode("utf-8-sig"), object_pairs_hook=unique)
    except ValueError as exc:
        raise CaptureError(f"Invalid JSON in {path}: {exc}") from exc


def is_within(path, root):
    try:
        Path(path).relative_to(root)
        return True
    except ValueError:
        return False


def tool_path(name):
    found = shutil.which(name)
    require(found is not None, f"{name} is not installed (needed by {TOOL}).")
    return found


def first_line(command):
    try:
        done = subprocess.run(command, capture_output=True, text=True, timeout=30)
        text = (done.stdout or done.stderr).strip().splitlines()
        return text[0] if text else ""
    except (OSError, subprocess.TimeoutExpired):
        return ""


# --------------------------------------------------------------------------------------------
# PE inspection (stdlib) for the stub and the executable allowlist

def _cstring(data, offset, limit=256):
    require(0 <= offset < len(data), "String offset outside the file.")
    end = data.find(b"\0", offset, offset + limit)
    require(end >= 0, "Unterminated string in PE file.")
    return data[offset:end].decode("ascii", "replace")


def parse_pe(data):
    """Parse the headers, sections, import directory and export table of a PE image."""
    require(len(data) >= 0x40 and data[:2] == b"MZ", "Not a PE image (no MZ header).")
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    require(pe + 24 <= len(data) and data[pe:pe + 4] == b"PE\0\0", "Not a PE image (no PE signature).")
    machine, section_count, _stamp, _symbols, _count, optional_size, characteristics = struct.unpack_from("<HHIIIHH", data, pe + 4)
    optional = pe + 24
    require(optional + optional_size <= len(data) and optional_size >= 96, "Truncated optional header.")
    magic = struct.unpack_from("<H", data, optional)[0]
    require(magic in (0x10B, 0x20B), "Unknown optional header magic.")
    entry_point = struct.unpack_from("<I", data, optional + 16)[0]
    count_offset, directory_offset = (optional + 92, optional + 96) if magic == 0x10B else (optional + 108, optional + 112)
    directory_count = min(struct.unpack_from("<I", data, count_offset)[0], 16)
    directories = []
    for index in range(directory_count):
        at = directory_offset + 8 * index
        if at + 8 > optional + optional_size:
            break
        directories.append(struct.unpack_from("<II", data, at))
    sections = []
    table = optional + optional_size
    require(section_count <= 96, "Implausible section count.")
    for index in range(section_count):
        at = table + 40 * index
        require(at + 40 <= len(data), "Truncated section table.")
        virtual_size, virtual_address, raw_size, raw_pointer = struct.unpack_from("<IIII", data, at + 8)
        sections.append({"name": data[at:at + 8].rstrip(b"\0").decode("ascii", "replace"),
                         "virtualAddress": virtual_address, "virtualSize": virtual_size,
                         "rawSize": raw_size, "rawPointer": raw_pointer,
                         "characteristics": struct.unpack_from("<I", data, at + 36)[0]})

    def offset_of(rva, size=1):
        for section in sections:
            if section["virtualAddress"] <= rva and rva + size <= section["virtualAddress"] + max(section["virtualSize"], section["rawSize"]):
                delta = rva - section["virtualAddress"]
                require(delta + size <= section["rawSize"] and section["rawPointer"] + delta + size <= len(data),
                        f"RVA {rva:#x} is not backed by file data.")
                return section["rawPointer"] + delta
        raise CaptureError(f"RVA {rva:#x} lies outside every section.")

    exports = None
    if directories and directories[0][0]:
        at = offset_of(directories[0][0], 40)
        (_flags, _stamp, _major, _minor, name_rva, base, function_count, name_count,
         functions_rva, names_rva, ordinals_rva) = struct.unpack_from("<IIHHIIIIIII", data, at)
        require(function_count <= 65536 and name_count <= function_count, "Implausible export table.")
        functions = {}
        if function_count:
            table_at = offset_of(functions_rva, 4 * function_count)
            for index in range(function_count):
                rva = struct.unpack_from("<I", data, table_at + 4 * index)[0]
                if rva:
                    functions[base + index] = rva
        names = []
        if name_count:
            names_at = offset_of(names_rva, 4 * name_count)
            ordinals_at = offset_of(ordinals_rva, 2 * name_count)
            for index in range(name_count):
                name_rva_i = struct.unpack_from("<I", data, names_at + 4 * index)[0]
                ordinal = base + struct.unpack_from("<H", data, ordinals_at + 2 * index)[0]
                names.append((_cstring(data, offset_of(name_rva_i)), ordinal))
        exports = {"dllName": _cstring(data, offset_of(name_rva)) if name_rva else "", "ordinalBase": base,
                   "functions": functions, "names": names}

    def directory(index):
        return directories[index] if index < len(directories) else (0, 0)
    return {"machine": machine, "characteristics": characteristics, "isDll": bool(characteristics & 0x2000),
            "magic": magic, "entryPoint": entry_point, "sections": sections, "exports": exports,
            "importDirectory": directory(1), "delayImportDirectory": directory(13),
            "tlsDirectory": directory(9)}


def _verify_inert_dll(data, label, dll_name):
    """Shared checks: PE32 i386 DLL, no entry point, no imports, delay imports or TLS, exports in .text."""
    info = parse_pe(data)
    require(info["machine"] == 0x14C and info["magic"] == 0x10B, f"{label} is not a PE32 i386 image.")
    require(info["isDll"], f"{label} is not a DLL.")
    require(info["entryPoint"] == 0, f"{label} must have no entry point (no DllMain runs).")
    require(info["importDirectory"] == (0, 0), f"{label} must have no import table.")
    require(info["delayImportDirectory"] == (0, 0) and info["tlsDirectory"] == (0, 0),
            f"{label} must have no delay imports or TLS callbacks.")
    exports = info["exports"]
    require(exports is not None, f"{label} has no export table.")
    require(exports["dllName"].lower() == dll_name, f"{label} export name is {exports['dllName']!r}, not {dll_name}.")
    text = [s for s in info["sections"] if s["name"] == ".text"]
    require(len(text) == 1, f"{label} needs exactly one .text section.")
    start, end = text[0]["virtualAddress"], text[0]["virtualAddress"] + text[0]["virtualSize"]
    for ordinal, rva in exports["functions"].items():
        require(start <= rva < end, f"{label} export #{ordinal} points outside .text.")
    return info, exports


def verify_stub(path):
    """Check the Smacker stand-in: inert DLL exporting exactly the nine ordinals JK imports, no names."""
    data = Path(path).read_bytes()
    info, exports = _verify_inert_dll(data, "Stub", "smackw32.dll")
    require(not exports["names"], "Stub must export by ordinal only (JK imports no names).")
    require(set(exports["functions"]) == set(STUB_ORDINALS),
            f"Stub ordinals {sorted(exports['functions'])} differ from {list(STUB_ORDINALS)}.")
    return {"sha256": sha256_bytes(data), "bytes": len(data), "ordinals": sorted(exports["functions"]),
            "exportRvas": {str(k): f"{v:#x}" for k, v in sorted(exports["functions"].items())},
            "sections": [s["name"] for s in info["sections"]], "imports": 0, "entryPoint": 0}


def verify_loopsp(path):
    """Check the DirectPlay loopback provider: inert DLL exporting only SPInit, with base relocations."""
    data = Path(path).read_bytes()
    info, exports = _verify_inert_dll(data, "Loopback provider", LOOPBACK_SP["file"])
    require([name for name, _ in exports["names"]] == ["SPInit"] and len(exports["functions"]) == 1,
            f"Loopback provider must export exactly SPInit; found {exports['names']}.")
    sections = [s["name"] for s in info["sections"]]
    require(".reloc" in sections, "Loopback provider needs base relocations (.reloc): it stores code addresses.")
    return {"sha256": sha256_bytes(data), "bytes": len(data), "exports": ["SPInit"],
            "exportRva": f"{exports['functions'][exports['names'][0][1]]:#x}", "sections": sections,
            "imports": 0, "entryPoint": 0}


def _build_dll(source, definition, target, verify, clang=None, lld=None):
    """Compile one freestanding C file and link it with a .def file into target (create-only)."""
    clang, lld = clang or tool_path("clang"), lld or tool_path("lld-link")
    target = Path(target)
    require(not target.exists(), f"{target} already exists (outputs are create-only).")
    with tempfile.TemporaryDirectory(prefix="jkcapture-dll-") as tmp:
        obj, dll = Path(tmp) / "module.obj", Path(tmp) / target.name
        commands = [[clang, "--target=i686-pc-windows-msvc", "-ffreestanding", "-fno-builtin", "-O1",
                     "-mno-incremental-linker-compatible", "-c", str(source), "-o", str(obj)],
                    [lld, "/dll", "/noentry", "/nodefaultlib", "/machine:x86", "/Brepro", f"/def:{definition}",
                     str(obj), f"/out:{dll}"]]
        for command in commands:
            done = subprocess.run(command, capture_output=True, text=True, timeout=120)
            check_ran(done.returncode == 0, f"Build step failed ({Path(command[0]).name}): {done.stderr.strip() or done.stdout.strip()}")
        verified = verify(dll)
        target.parent.mkdir(parents=True, exist_ok=True)
        with dll.open("rb") as src, target.open("xb") as dst:
            shutil.copyfileobj(src, dst)
    return {"path": str(target), **verified,
            "source": {"file": str(Path(source).relative_to(HERE)), "sha256": sha256_file(source)},
            "definition": {"file": str(Path(definition).relative_to(HERE)), "sha256": sha256_file(definition)},
            "compiler": first_line([clang, "--version"]), "linker": first_line([lld, "--version"])}


def build_stub(out_dir, clang=None, lld=None):
    """Compile stub/smackw32_stub.c and link it with stub/smackw32.def into out_dir/smackw32.dll."""
    return _build_dll(HERE / "stub" / "smackw32_stub.c", HERE / "stub" / "smackw32.def",
                      Path(out_dir) / "smackw32.dll", verify_stub, clang, lld)


def build_loopsp(out_dir, clang=None, lld=None):
    """Compile loopsp/jkcap_loopsp.c with loopsp/jkcap_loopsp.def into out_dir/jkcap_loopsp.dll."""
    return _build_dll(HERE / "loopsp" / "jkcap_loopsp.c", HERE / "loopsp" / "jkcap_loopsp.def",
                      Path(out_dir) / LOOPBACK_SP["file"], verify_loopsp, clang, lld)


def is_elf_i386(path):
    """True when path (after symlinks) is a 32-bit little-endian ELF for i386."""
    try:
        with Path(path).resolve().open("rb") as stream:
            header = stream.read(20)
    except OSError:
        return False
    return len(header) == 20 and header[:4] == b"\x7fELF" and header[4] == 1 and header[5] == 1 \
        and struct.unpack_from("<H", header, 18)[0] == 3


def find_gl32(candidates=GL32_CANDIDATES):
    """Path of a 32-bit i386 libGL.so.1 that Wine's 32-bit wined3d can load, or None."""
    for candidate in candidates:
        if Path(candidate).exists() and is_elf_i386(candidate):
            return str(candidate)
    return None


# --------------------------------------------------------------------------------------------
# GOB archives and episode.jk

def read_gob_directory(path):
    """Return {normalized name: (offset, length, raw name)} for a GOB/GOO archive."""
    path = Path(path)
    size = path.stat().st_size
    with path.open("rb") as stream:
        header = stream.read(12)
        require(len(header) == 12, f"{path.name}: too short for a GOB header.")
        magic, version, directory = struct.unpack("<4sII", header)
        require(magic == b"GOB " and version == 0x14, f"{path.name}: not a GOB archive (magic/version).")
        require(12 <= directory <= size - 4, f"{path.name}: GOB directory offset out of range.")
        stream.seek(directory)
        count = struct.unpack("<I", stream.read(4))[0]
        require(count <= 100000 and directory + 4 + 136 * count <= size, f"{path.name}: GOB directory out of range.")
        entries = {}
        rows = stream.read(136 * count)
    for index in range(count):
        offset, length, raw = struct.unpack_from("<II128s", rows, 136 * index)
        require(b"\0" in raw, f"{path.name}: unterminated GOB entry name.")
        name = raw.split(b"\0", 1)[0].decode("ascii", "replace")
        require(offset + length <= size, f"{path.name}: GOB entry {name} extends past the end.")
        entries[name.replace("\\", "/").lower()] = (offset, length, name)
    return entries


def read_gob_entry(path, name, limit=16 << 20):
    entries = read_gob_directory(path)
    key = name.replace("\\", "/").lower()
    require(key in entries, f"{Path(path).name} has no {name}.")
    offset, length, _ = entries[key]
    require(length <= limit, f"{name} in {Path(path).name} is larger than {limit} bytes.")
    with Path(path).open("rb") as stream:
        stream.seek(offset)
        return stream.read(length)


def parse_episode(text):
    """Parse episode.jk: title, TYPE (1 single player, 2 multiplayer), LEVEL files in order."""
    lines = []
    for raw in text.splitlines():
        line = raw.split("#", 1)[0].strip()
        if line:
            lines.append(line)
    require(lines, "episode.jk is empty.")
    match = re.match(r'"([^"]*)"', lines[0])
    title = match.group(1) if match else lines[0]
    kind = 1
    levels = []
    for line in lines[1:]:
        type_match = re.match(r"TYPE\s+(\d+)", line, re.IGNORECASE)
        if type_match:
            kind = int(type_match.group(1))
            continue
        tokens = line.split()
        if len(tokens) >= 5 and tokens[0].endswith(":") and tokens[3].upper() == "LEVEL":
            levels.append(tokens[4])
    return {"title": title, "type": kind, "singlePlayer": bool(kind & 1), "multiplayer": bool(kind & 2), "levels": levels}


def episode_info(path):
    info = parse_episode(read_gob_entry(path, "episode.jk", 1 << 20).decode("latin-1"))
    return {"file": Path(path).name, **info}


def write_gob(path, entries):
    """Write a GOB archive from [(name with backslashes, bytes)]; create-only."""
    path = Path(path)
    data_offset = 12
    blob, rows = bytearray(), []
    for name, payload in entries:
        encoded = name.encode("ascii")
        require(len(encoded) < 128, f"GOB entry name too long: {name}")
        rows.append(struct.pack("<II128s", data_offset + len(blob), len(payload), encoded))
        blob += payload
    directory = data_offset + len(blob)
    with path.open("xb") as stream:
        stream.write(struct.pack("<4sII", b"GOB ", 0x14, directory))
        stream.write(blob)
        stream.write(struct.pack("<I", len(rows)))
        for row in rows:
            stream.write(row)


def wrap_level(source, level, out, title=None):
    """Copy every entry of an episode GOB except episode.jk and add a one-level TYPE 1 episode.jk."""
    source, out = Path(source), Path(out)
    require(source.suffix.lower() in ARCHIVE_SUFFIXES, "sp-wrap reads a .gob/.goo archive.")
    require(out.suffix.lower() == ".gob" and not out.exists(), "sp-wrap writes a new .gob file.")
    entries = read_gob_directory(source)
    require(re.fullmatch(r"[A-Za-z0-9_.-]{1,60}\.jkl", level) is not None, "Level must be a plain NAME.jkl.")
    require(f"jkl/{level.lower()}" in entries, f"{source.name} has no jkl\\{level}.")
    info = episode_info(source)
    title = title or f"{info['title']}: {level} (single-player wrap)"
    require('"' not in title and len(title) <= 60 and title.isascii(), "Title must be ASCII, at most 60 characters, without quotes.")
    episode = (f'"{title}"\n# Written by {TOOL} sp-wrap from {source.name}; for captures only.\n'
               f"TYPE 1\nSEQ 1\n\n# line cd level type file light dark gotoA gotoB\n"
               f"10: 1 1 LEVEL {level} 0 0 -1 -1\nend\n").encode("ascii")
    payloads = [("episode.jk", episode)]
    with source.open("rb") as stream:
        for key, (offset, length, raw) in entries.items():
            if key == "episode.jk":
                continue
            stream.seek(offset)
            payloads.append((raw, stream.read(length)))
    write_gob(out, payloads)
    return {"source": str(source), "sourceSha256": sha256_file(source), "level": level, "title": title,
            "output": str(out), "outputSha256": sha256_file(out), "entries": len(payloads)}


# --------------------------------------------------------------------------------------------
# Prefix preparation

def check_new_prefix(prefix, environ=None, home=None):
    """Refuse an existing path, anything inside ~/.wine and the caller's $WINEPREFIX."""
    environ = os.environ if environ is None else environ
    home = Path(home if home is not None else environ.get("HOME", str(Path.home())))
    prefix = Path(os.path.abspath(prefix))
    require(not prefix.exists() and not prefix.is_symlink(), f"--prefix must be a new directory; {prefix} exists.")
    require(prefix.parent.is_dir(), f"The parent of --prefix must exist: {prefix.parent}")
    resolved = prefix.parent.resolve() / prefix.name
    check_not_user_prefix(resolved, environ, home)
    return resolved


def check_not_user_prefix(prefix, environ, home):
    wine_home = Path(os.path.abspath(Path(home) / ".wine"))
    for candidate in {wine_home, wine_home.resolve() if wine_home.exists() else wine_home}:
        require(not is_within(prefix, candidate), f"Refusing a prefix inside {candidate} (the user's own Wine prefix).")
    user_prefix = environ.get("WINEPREFIX")
    if user_prefix:
        user = Path(os.path.abspath(user_prefix))
        user = user.resolve() if user.exists() else user
        require(Path(prefix) != user and not is_within(prefix, user),
                f"Refusing the caller's $WINEPREFIX ({user}); jkcapture always uses its own new prefix.")


def check_executable(path, vouched_sha256=None):
    path = Path(path)
    require(path.is_file(), f"Executable not found: {path}")
    digest = sha256_file(path)
    if digest in KNOWN_EXECUTABLES:
        return {"sha256": digest, "identity": KNOWN_EXECUTABLES[digest], "vouched": False}
    require(vouched_sha256 is not None,
            f"{path.name} (sha256 {digest}) is not the official JK.EXE 1.01; pass --exe-sha256 {digest} only if you vouch for it.")
    require(vouched_sha256.lower() == digest, f"--exe-sha256 does not match {path.name} (sha256 {digest}).")
    info = parse_pe(path.read_bytes())
    require(info["machine"] == 0x14C and not info["isDll"], f"{path.name} is not a 32-bit Windows executable.")
    return {"sha256": digest, "identity": "vouched by the user with --exe-sha256", "vouched": True}


def classify_input(path):
    """Return 'archive' or 'cd-marker'; refuse every other file type."""
    path = Path(path)
    name = path.name.lower()
    require(path.is_file(), f"Input not found: {path}")
    if name.endswith(ARCHIVE_SUFFIXES):
        read_gob_directory(path)
        return "archive"
    require(name == "jk_.cd", f"Refusing {path.name}: only .gob/.goo archives and JK_.CD are staged (no DLL, EXE or video files).")
    require(path.stat().st_size <= MAX_CD_MARKER_BYTES, "JK_.CD should be a small marker file.")
    return "cd-marker"


def plan_staging(resource_dir=None, resources=(), episodes=()):
    """Return [(source Path, destination relative to drive_c/JK, kind)]."""
    plan, seen = [], set()

    def add(source, destination, kind):
        key = destination.lower()
        require(key not in seen, f"Two inputs would be staged as {destination}.")
        seen.add(key)
        plan.append((Path(source).resolve(), destination, kind))
    if resource_dir is not None:
        directory = Path(resource_dir)
        require(directory.is_dir(), f"--resource-dir is not a directory: {directory}")
        found = {entry.name.lower(): entry for entry in directory.iterdir() if entry.is_file()}
        for name in RESOURCE_NAMES:
            require(name in found, f"--resource-dir has no {name} (case-insensitive).")
            add(found[name], f"resource/{found[name].name}", classify_input(found[name]))
    for item in resources:
        add(item, f"resource/{Path(item).name}", classify_input(item))
    staged_resources = {destination.split("/", 1)[1].lower() for _, destination, _ in plan}
    for name in RESOURCE_NAMES:
        require(name in staged_resources, f"Missing resource {name}; JK needs Res1hi.gob, Res2.gob and JK_.CD.")
    for item in episodes:
        require(Path(item).name.lower().endswith(ARCHIVE_SUFFIXES), f"Episodes are .gob/.goo archives: {item}")
        classify_input(item)
        episode_info(item)
        add(item, f"episode/{Path(item).name}", "episode")
    return plan


def registry_commands(desktop="640x480", wined3d="gl", accel=False, loopback=True):
    """The `wine reg add` argument lists prepare applies (HKCU Wine settings, HKLM JK and DirectPlay)."""
    require(re.fullmatch(r"\d{3,4}x\d{3,4}", desktop) is not None, "--desktop must look like 640x480.")
    width, height = (int(v) for v in desktop.split("x"))
    require(width >= GUI_WIDTH and height >= GUI_HEIGHT and width <= 4096 and height <= 4096,
            "--desktop must be at least 640x480.")
    require(wined3d in ("gdi", "gl"), "--wined3d must be gdi or gl.")
    jk_key = r"HKLM\Software\LucasArts Entertainment Company\JediKnight\v1.0"
    commands = [["reg", "add", r"HKCU\Software\Wine\Explorer", "/v", "Desktop", "/t", "REG_SZ", "/d", "Default", "/f"],
                ["reg", "add", r"HKCU\Software\Wine\Explorer\Desktops", "/v", "Default", "/t", "REG_SZ", "/d", desktop, "/f"],
                ["reg", "add", r"HKCU\Software\Wine\Direct3D", "/v", "renderer", "/t", "REG_SZ", "/d", wined3d, "/f"],
                ["reg", "add", jk_key, "/v", "b3DAccel", "/t", "REG_DWORD", "/d", "1" if accel else "0", "/f"]]
    if loopback:
        key = DIRECTPLAY_SP_KEY + "\\" + LOOPBACK_SP["name"]
        for name, kind, value in (("Guid", "REG_SZ", LOOPBACK_SP["guid"]), ("Path", "REG_SZ", LOOPBACK_SP["windowsPath"]),
                                  ("dwReserved1", "REG_DWORD", "0"), ("dwReserved2", "REG_DWORD", "0")):
            commands.append(["reg", "add", key, "/v", name, "/t", kind, "/d", value, "/f"])
    return commands


def directplay_providers(system_reg_text):
    """DirectPlay service providers in the order dplayx enumerates them (registry key order).

    Wine keeps subkeys sorted case-insensitively, which is the order JK's provider list shows.
    """
    pattern = re.compile(r"^\[Software\\\\Microsoft\\\\DirectPlay\\\\Service Providers\\\\([^\]\\]+)\]", re.MULTILINE)
    names = {match.group(1).replace("\\\\", "\\") for match in pattern.finditer(system_reg_text)}
    return sorted(names, key=lambda name: name.lower())


def wine_environment(prefix, display, debug=DEFAULT_WINE_DEBUG, home=None):
    environment = {"PATH": os.environ.get("PATH", "/usr/bin:/bin"), "LANG": "C.UTF-8",
                   "HOME": str(home if home is not None else Path(prefix) / "home"),
                   "WINEPREFIX": str(prefix), "WINEARCH": "win32", "WINEDEBUG": debug,
                   "WINEDLLOVERRIDES": WINE_OVERRIDES, "DISPLAY": display}
    for name in ("XDG_RUNTIME_DIR",):
        if name in os.environ:
            environment[name] = os.environ[name]
    return environment


def remove_host_drives(prefix):
    """Unlink every DOS drive letter except C: from <prefix>/dosdevices (Z: maps the host root).

    This is what winetricks' `sandbox` verb does. Wine creates C: and Z: only when it first makes
    dosdevices, so they stay removed. It narrows what Windows programs see; it is not a sandbox.
    Staged symlinks under drive_c still resolve, because the kernel follows them.
    """
    removed = []
    devices = Path(prefix) / "dosdevices"
    if not devices.is_dir():
        return removed
    for entry in sorted(devices.iterdir(), key=lambda p: p.name):
        if re.fullmatch(r"[a-z]::?", entry.name.lower()) and entry.name.lower() not in ("c:", "c::"):
            target = os.readlink(entry) if entry.is_symlink() else None
            entry.unlink()
            removed.append({"drive": entry.name, "target": target})
    return removed


def run_checked(command, environment, timeout, what, log=None):
    done = subprocess.run(command, env=environment, stdin=subprocess.DEVNULL, capture_output=True, text=True,
                          timeout=timeout)
    if log is not None:
        log.write(f"$ {' '.join(command)}\n{done.stdout}{done.stderr}")
    check_ran(done.returncode == 0, f"{what} failed with exit code {done.returncode}: {(done.stderr or done.stdout).strip()[-400:]}")
    return done


def prepare(args):
    started = time.monotonic()
    prefix = check_new_prefix(args.prefix)
    executable = check_executable(args.exe, args.exe_sha256)
    plan = plan_staging(args.resource_dir, args.resource or (), args.episode or ())
    loopback = not args.no_loopback
    commands = registry_commands(args.desktop, args.wined3d, args.accel, loopback)
    gl32 = find_gl32()
    if args.wined3d == "gl":
        require(gl32 is not None, f"--wined3d gl needs a 32-bit libGL.so.1 (looked in {', '.join(GL32_CANDIDATES)}); {GL32_HINT}.")
    # Every tool prepare needs is checked before the (roughly 600 MB) prefix exists, so a missing
    # compiler never leaves a half-built prefix behind whose path prepare would then refuse.
    wine, wineboot, wineserver = tool_path("wine"), tool_path("wineboot"), tool_path("wineserver")
    for name in ("Xvfb", "clang", "lld-link"):
        tool_path(name)
    prefix.mkdir()
    (prefix / "home").mkdir()
    receipt = {"schemaVersion": SCHEMA_VERSION, "tool": TOOL, "toolVersion": TOOL_VERSION, "credits": CREDITS,
               "status": "failed", "prefix": str(prefix), "wineVersion": first_line([wine, "--version"]),
               "executable": {"source": str(Path(args.exe).resolve()), **executable},
               "settings": {"desktop": args.desktop, "wined3d": args.wined3d, "b3DAccel": 1 if args.accel else 0,
                            "staging": "copy" if args.copy else "symlink", "winearch": "win32",
                            "dllOverrides": WINE_OVERRIDES, "loopbackProvider": loopback,
                            "gl32": gl32},
               "registry": [" ".join(c[1:]) for c in commands]}
    log_path = prefix / "prepare.log"
    environment = None
    try:
        with log_path.open("x", encoding="utf-8") as log, PrivateDisplay(args.display, log_dir=prefix) as display:
            environment = wine_environment(prefix, display.name)
            run_checked([wineboot, "-i"], environment, 600, "wineboot -i", log)
            run_checked([wineserver, "-w"], environment, 300, "wineserver -w", log)
            for command in commands:
                run_checked([wine, *command], environment, 120, "wine reg add", log)
            run_checked([wineserver, "-w"], environment, 300, "wineserver -w", log)
        receipt["removedDrives"] = remove_host_drives(prefix)
        jk = prefix / "drive_c" / "JK"
        for sub in ("resource", "episode", "player"):
            (jk / sub).mkdir(parents=True, exist_ok=True)
        shutil.copyfile(args.exe, jk / "JK.EXE")
        (jk / "JK.EXE").chmod(0o444)
        stub_info = build_stub(jk)          # always built here from stub/; no prebuilt DLL is accepted
        (jk / "smackw32.dll").chmod(0o444)
        require(sha256_file(jk / "smackw32.dll") == stub_info["sha256"], "Staged stub differs from the verified stub.")
        loopsp_info = None
        if loopback:
            loopsp_info = build_loopsp(jk)
            (jk / LOOPBACK_SP["file"]).chmod(0o444)
            loopsp_info.update({"name": LOOPBACK_SP["name"], "guid": LOOPBACK_SP["guid"],
                                "windowsPath": LOOPBACK_SP["windowsPath"]})
        staged = []
        for source, destination, kind in plan:
            target = jk / destination
            if args.copy:
                shutil.copyfile(source, target)
                target.chmod(0o444)
            else:
                target.symlink_to(source)
            record = {"source": str(source), "destination": f"C:\\JK\\{destination.replace('/', chr(92))}",
                      "kind": kind, "bytes": source.stat().st_size, "sha256": sha256_file(source)}
            if kind == "episode":
                record["episode"] = episode_info(source)
            staged.append(record)
        receipt.update({"status": "prepared", "stub": stub_info, "loopbackProvider": loopsp_info, "staged": staged,
                        "directPlayProviders": directplay_providers((prefix / "system.reg").read_text("utf-8", "replace")),
                        "jkDirectory": str(jk), "elapsedSeconds": round(time.monotonic() - started, 1)})
    except BaseException as exc:
        receipt["error"] = f"{type(exc).__name__}: {exc}"
        # Stop whatever wineboot left running for this new prefix (never another prefix's processes).
        try:
            subprocess.run([wineserver, "-k"], env=environment or wine_environment(prefix, ":0"),
                           stdin=subprocess.DEVNULL, capture_output=True, timeout=60)
            receipt["cleanup"] = "wineserver -k"
        except (OSError, subprocess.SubprocessError) as cleanup:
            receipt["cleanup"] = f"wineserver -k failed: {cleanup}"
        raise
    finally:
        write_json_new(prefix / PREFIX_RECEIPT, receipt)
    return receipt


# --------------------------------------------------------------------------------------------
# Private display, window lookup and frame grabs

def choose_display(candidates, taken):
    """First display number whose socket and lock file are both absent."""
    for number in candidates:
        if not taken(number):
            return number
    raise CaptureError("No free private display number.")


def display_taken(number):
    return Path(f"/tmp/.X11-unix/X{number}").exists() or Path(f"/tmp/.X{number}-lock").exists()


class PrivateDisplay:
    """Xvfb on an unused display, TCP disabled; stopped by PID (never by pattern)."""

    def __init__(self, number=None, screen=XVFB_SCREEN, log_dir=None):
        self.requested, self.screen, self.log_dir = number, screen, log_dir
        self.process = self.number = None

    @property
    def name(self):
        return f":{self.number}"

    def __enter__(self):
        xvfb = tool_path("Xvfb")
        number = self.requested if self.requested is not None else choose_display(DEFAULT_DISPLAYS, display_taken)
        require(not display_taken(number), f"Display :{number} is in use.")
        log = open(Path(self.log_dir) / "xvfb.log", "ab") if self.log_dir else subprocess.DEVNULL
        try:
            self.process = subprocess.Popen([xvfb, f":{number}", "-screen", "0", self.screen, "-nolisten", "tcp"],
                                            stdin=subprocess.DEVNULL, stdout=log, stderr=log, start_new_session=True)
        finally:
            if log is not subprocess.DEVNULL:
                log.close()
        self.number = number
        socket = Path(f"/tmp/.X11-unix/X{number}")
        deadline = time.monotonic() + 15
        while not socket.exists():
            if self.process.poll() is not None:
                raise ToolFailure(f"Xvfb exited before display :{number} was ready.")
            if time.monotonic() > deadline:
                self.stop()
                raise ToolFailure(f"Xvfb display :{number} was not ready within 15 seconds.")
            time.sleep(0.05)
        return self

    def stop(self):
        if self.process is not None and self.process.poll() is None:
            self.process.terminate()
            try:
                self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=5)

    def __exit__(self, *exc):
        self.stop()
        return False


def kill_pid_if(pid, expect_in_cmdline):
    """Terminate one PID after checking its command line (a reused PID is left alone)."""
    try:
        cmdline = Path(f"/proc/{pid}/cmdline").read_bytes().replace(b"\0", b" ").decode("utf-8", "replace")
    except OSError:
        return False
    if expect_in_cmdline not in cmdline:
        return False
    try:
        os.kill(pid, signal.SIGTERM)
    except ProcessLookupError:
        return False
    for _ in range(100):
        if not Path(f"/proc/{pid}").exists():
            return True
        time.sleep(0.05)
    try:
        os.kill(pid, signal.SIGKILL)
    except ProcessLookupError:
        pass
    return True


def png_bytes(width, height, rgb):
    """Encode 8-bit RGB as PNG (filter 0, deterministic) with the standard library."""
    require(len(rgb) == width * height * 3, "Frame size does not match its dimensions.")
    stride = width * 3
    raw = b"".join(b"\0" + rgb[row * stride:(row + 1) * stride] for row in range(height))

    def chunk(tag, payload):
        return struct.pack(">I", len(payload)) + tag + payload + struct.pack(">I", zlib.crc32(tag + payload) & 0xFFFFFFFF)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def region_stats(width, height, rgb, region=None):
    """Mean RGB, mean luma (0..255) and the fraction of pixels brighter than 8 in a region."""
    x, y, w, h = region if region is not None else (0, 0, width, height)
    require(0 <= x and 0 <= y and w > 0 and h > 0 and x + w <= width and y + h <= height, "Region outside the frame.")
    totals, lit, stride = [0, 0, 0], 0, width * 3
    for row in range(y, y + h):
        line = rgb[row * stride + 3 * x: row * stride + 3 * (x + w)]
        reds, greens, blues = line[0::3], line[1::3], line[2::3]
        totals[0] += sum(reds)
        totals[1] += sum(greens)
        totals[2] += sum(blues)
        lit += sum(1 for r, g, b in zip(reds, greens, blues) if max(r, g, b) > 8)
    count = w * h
    mean = [round(v / count, 3) for v in totals]
    luma = round(0.299 * mean[0] + 0.587 * mean[1] + 0.114 * mean[2], 3)
    return {"meanRgb": mean, "meanLuma": luma, "nonBlackFraction": round(lit / count, 4)}


def parse_xwininfo(text):
    values = {}
    for key, label in (("x", "Absolute upper-left X"), ("y", "Absolute upper-left Y"), ("w", "Width"), ("h", "Height")):
        match = re.search(rf"{label}:\s+(-?\d+)", text)
        require(match is not None, f"xwininfo output lacks {label}.")
        values[key] = int(match.group(1))
    return values["x"], values["y"], values["w"], values["h"]


class Screen:
    """xdotool input and ffmpeg frame grabs against the Wine desktop window on one display."""

    def __init__(self, display, window=None, offset=(0, 0), scale=(1.0, 1.0)):
        self.display, self.window, self.offset, self.scale = display, window, offset, scale
        self.environment = {"PATH": os.environ.get("PATH", "/usr/bin:/bin"), "DISPLAY": display}

    def x(self, *args, timeout=30):
        done = subprocess.run(["xdotool", *[str(a) for a in args]], env=self.environment, capture_output=True,
                              text=True, timeout=timeout)
        require(done.returncode == 0, f"xdotool {' '.join(str(a) for a in args)} failed: {done.stderr.strip()}")
        return done.stdout

    def find_window(self, timeout=60):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            done = subprocess.run(["xdotool", "search", "--name", f"^{re.escape(DESKTOP_WINDOW)}$"],
                                  env=self.environment, capture_output=True, text=True, timeout=30)
            ids = [line for line in done.stdout.split() if line.isdigit()]
            if ids:
                self.window = int(ids[0])
                return self.window
            time.sleep(0.5)
        raise CaptureError(f"The X window '{DESKTOP_WINDOW}' did not appear within {timeout} s.")

    def geometry(self):
        require(self.window is not None, "No desktop window.")
        done = subprocess.run(["xwininfo", "-id", str(self.window)], env=self.environment, capture_output=True,
                              text=True, timeout=30)
        require(done.returncode == 0, "The Wine desktop window is gone (JK.EXE exited or crashed; see wine.log): "
                                      + (done.stderr.strip().splitlines() or ["xwininfo failed"])[-1])
        return parse_xwininfo(done.stdout)

    def to_screen(self, x, y):
        left, top, _, _ = self.geometry()
        return (left + self.offset[0] + round(x * self.scale[0]), top + self.offset[1] + round(y * self.scale[1]))

    def click(self, x, y):
        sx, sy = self.to_screen(x, y)
        self.x("mousemove", "--sync", sx, sy)
        time.sleep(0.3)
        self.x("mousedown", 1)
        time.sleep(0.08)
        self.x("mouseup", 1)

    def type(self, text, delay_ms):
        self.x("type", "--delay", delay_ms, "--", text, timeout=60 + len(text) * delay_ms / 1000)

    def key(self, keys, delay_ms=100):
        for key in keys:
            self.x("key", "--delay", delay_ms, "--", key)

    def hold(self, key, seconds):
        self.x("keydown", "--", key)
        try:
            time.sleep(seconds)
        finally:
            self.x("keyup", "--", key)

    def grab(self):
        """Grab the desktop window: (width, height, rgb bytes). The cursor is not drawn."""
        left, top, width, height = self.geometry()
        done = subprocess.run(["ffmpeg", "-loglevel", "error", "-nostdin", "-f", "x11grab", "-draw_mouse", "0",
                               "-video_size", f"{width}x{height}", "-i", f"{self.display}.0+{left},{top}",
                               "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"],
                              env=self.environment, capture_output=True, timeout=60)
        require(done.returncode == 0, f"ffmpeg frame grab failed: {done.stderr.decode('utf-8', 'replace').strip()[-300:]}")
        require(len(done.stdout) == width * height * 3, "ffmpeg returned a frame of the wrong size.")
        return width, height, done.stdout

    def gui_region(self, region, width, height):
        x, y, w, h = region
        fx, fy = self.offset[0] + round(x * self.scale[0]), self.offset[1] + round(y * self.scale[1])
        fw, fh = max(1, round(w * self.scale[0])), max(1, round(h * self.scale[1]))
        return max(0, fx), max(0, fy), min(fw, width - max(0, fx)), min(fh, height - max(0, fy))


# --------------------------------------------------------------------------------------------
# Menus and step scripts

def load_menus(path=None):
    menus = load_json(path or HERE / "menus.json")
    validate_menus(menus)
    return menus


def validate_menus(menus):
    require(menus.get("schemaVersion") == 1, "menus.json schemaVersion must be 1.")
    gui = menus.get("gui", {})
    require(gui.get("width") == GUI_WIDTH and gui.get("height") == GUI_HEIGHT, "menus.json GUI size must be 640x480.")
    for screen_name, screen in menus["screens"].items():
        require(re.fullmatch(r"[a-z][a-z0-9_]*", screen_name) is not None, f"Bad screen name {screen_name}.")
        for element_name, element in screen["elements"].items():
            label = f"{screen_name}.{element_name}"
            x, y, w, h = element["rect"]
            require(w > 0 and h > 0 and x >= 0 and y >= 0 and x + w <= GUI_WIDTH and y + h <= GUI_HEIGHT,
                    f"{label} rect lies outside 640x480.")
            cx, cy = element["click"]
            require(x <= cx < x + w and y <= cy < y + h, f"{label} click point lies outside its rect.")
            rows = element.get("rows")
            if rows:
                last = rows["firstY"] + rows["height"] * (rows["visible"] - 1)
                require(x <= rows["x"] < x + w and y <= rows["firstY"] and last < y + h,
                        f"{label} rows lie outside the list rect.")


def target_point(menus, target):
    screen, _, element = target.partition(".")
    require(screen in menus["screens"] and element in menus["screens"][screen]["elements"],
            f"Unknown menu target {target!r}.")
    return tuple(menus["screens"][screen]["elements"][element]["click"])


def row_point(menus, target, row):
    screen, _, element = target.partition(".")
    require(screen in menus["screens"] and element in menus["screens"][screen]["elements"], f"Unknown list {target!r}.")
    rows = menus["screens"][screen]["elements"][element].get("rows")
    require(rows is not None, f"{target} has no row layout in menus.json.")
    require(0 <= row < rows["visible"], f"Row {row} of {target} is not visible without scrolling (0..{rows['visible'] - 1}).")
    return rows["x"], rows["firstY"] + rows["height"] * row


STEP_FIELDS = {
    "wait": ({"s"}, set()),
    "click": (set(), {"x", "y", "target", "wait", "retry"}),
    "click_row": ({"list", "row"}, {"wait"}),
    "type": ({"text"}, {"delay_ms", "wait"}),
    "key": ({"keys"}, {"delay_ms", "wait"}),
    "hold": ({"key", "s"}, {"wait"}),
    "list_select": ({"list", "index"}, {"count", "wait"}),
    "shot": ({"name"}, {"wait"}),
    "expect": ({"name", "region"}, {"not_black", "min_mean"}),
    "expect_changed": ({"name", "from"}, {"min_fraction"}),
    "await": ({"region"}, {"not_black", "not_flat", "timeout"}),
}
# A click that changes less than this fraction of the frame did nothing (a lost click); a real
# screen change, even a small popup, changes far more, a blinking text cursor far less.
CLICK_EFFECT_MIN = 0.01
# A frame whose most common colour covers this fraction is a bare desktop or a blank screen, not JK's GUI.
FLAT_FRACTION = 0.9
KEY_NAME = re.compile(r"[A-Za-z0-9_]{1,24}(\+[A-Za-z0-9_]{1,24}){0,2}")


def _number(value, low, high, label):
    require(type(value) in (int, float) and low <= value <= high, f"{label} must be a number in {low}..{high}.")
    return value


def _region(value, label):
    require(isinstance(value, list) and len(value) == 4 and all(type(v) is int for v in value), f"{label} must be [x, y, w, h].")
    x, y, w, h = value
    require(x >= 0 and y >= 0 and w > 0 and h > 0 and x + w <= GUI_WIDTH and y + h <= GUI_HEIGHT,
            f"{label} must lie inside the 640x480 GUI.")
    return value


def validate_steps(steps, menus):
    """Check a step list strictly; return it unchanged. Coordinates are 640x480 GUI coordinates."""
    require(isinstance(steps, list) and 0 < len(steps) <= 500, "A step script is a list of 1..500 steps.")
    names = set()
    for index, step in enumerate(steps):
        label = f"step {index}"
        require(isinstance(step, dict) and isinstance(step.get("op"), str), f"{label} needs an op.")
        op = step["op"]
        require(op in STEP_FIELDS, f"{label}: unknown op {op!r}.")
        required, optional = STEP_FIELDS[op]
        keys = set(step) - {"op", "note"}
        require(required <= keys, f"{label} ({op}) lacks {sorted(required - keys)}.")
        require(keys <= required | optional, f"{label} ({op}) has unsupported fields {sorted(keys - required - optional)}.")
        if "note" in step:
            require(isinstance(step["note"], str) and len(step["note"]) <= 200, f"{label}: note must be a short string.")
        if "wait" in step:
            _number(step["wait"], 0, 120, f"{label} wait")
        if op == "wait":
            _number(step["s"], 0, 600, f"{label} s")
        elif op == "click":
            if "target" in step:
                require("x" not in step and "y" not in step, f"{label}: give target or x/y, not both.")
                target_point(menus, step["target"])
            else:
                require(type(step.get("x")) is int and type(step.get("y")) is int, f"{label}: click needs x and y or a target.")
                require(0 <= step["x"] < GUI_WIDTH and 0 <= step["y"] < GUI_HEIGHT, f"{label}: click outside 640x480.")
            require(type(step.get("retry", 0)) is int and 0 <= step.get("retry", 0) <= 5, f"{label}: retry must be 0..5.")
        elif op == "click_row":
            require(type(step["row"]) is int, f"{label}: row must be an integer.")
            row_point(menus, step["list"], step["row"])
        elif op == "type":
            text = step["text"]
            require(isinstance(text, str) and 0 < len(text) <= 64 and all(32 <= ord(c) < 127 for c in text),
                    f"{label}: text must be 1..64 printable ASCII characters.")
            _number(step.get("delay_ms", 120), 10, 1000, f"{label} delay_ms")
        elif op == "key":
            require(isinstance(step["keys"], list) and 0 < len(step["keys"]) <= 32
                    and all(isinstance(k, str) and KEY_NAME.fullmatch(k) for k in step["keys"]),
                    f"{label}: keys must be 1..32 X key names.")
            _number(step.get("delay_ms", 100), 10, 1000, f"{label} delay_ms")
        elif op == "hold":
            require(isinstance(step["key"], str) and KEY_NAME.fullmatch(step["key"]), f"{label}: bad key name.")
            _number(step["s"], 0.05, 30, f"{label} s")
        elif op == "list_select":
            target_point(menus, step["list"])
            require(type(step["index"]) is int and 0 <= step["index"] < 64, f"{label}: index must be 0..63.")
            require(type(step.get("count", 64)) is int and 1 <= step.get("count", 64) <= 64, f"{label}: count must be 1..64.")
        elif op == "shot":
            require(isinstance(step["name"], str) and re.fullmatch(r"[A-Za-z0-9_-]{1,64}", step["name"]),
                    f"{label}: shot names use letters, digits, _ and - only.")
            require(step["name"] not in names, f"{label}: duplicate shot name {step['name']}.")
            names.add(step["name"])
        elif op == "expect":
            require(step["name"] in names, f"{label}: expect refers to shot {step['name']!r}, which is not taken before it.")
            _region(step["region"], f"{label} region")
            require(step.get("not_black", True) is True, f"{label}: not_black can only be true.")
            _number(step.get("min_mean", NOT_BLACK_MEAN), 0, 255, f"{label} min_mean")
        elif op == "expect_changed":
            require(step["name"] in names and step["from"] in names and step["name"] != step["from"],
                    f"{label}: expect_changed compares two different shots taken before it.")
            _number(step.get("min_fraction", 0.5), 0.01, 1, f"{label} min_fraction")
        elif op == "await":
            _region(step["region"], f"{label} region")
            require(step.get("not_black", True) is True, f"{label}: not_black can only be true.")
            require(type(step.get("not_flat", False)) is bool, f"{label}: not_flat is true or false.")
            _number(step.get("timeout", 60), 1, 600, f"{label} timeout")
    return steps


def dominant_fraction(width, height, rgb, region=None, sample=5):
    """Share of (sampled) pixels in a region that have its most common colour."""
    x, y, w, h = region if region is not None else (0, 0, width, height)
    counts, total, stride = {}, 0, width * 3
    for row in range(y, y + h, sample):
        line = rgb[row * stride + 3 * x: row * stride + 3 * (x + w)]
        for offset in range(0, len(line), 3 * sample):
            key = line[offset:offset + 3]
            counts[key] = counts.get(key, 0) + 1
            total += 1
    return round(max(counts.values()) / total, 4) if total else 1.0


def changed_fraction(first, second, delta=CHANGED_PIXEL_DELTA):
    """Fraction of pixels whose largest channel difference exceeds delta; frames are (w, h, rgb)."""
    (w1, h1, a), (w2, h2, b) = first, second
    require((w1, h1) == (w2, h2), "Frames of different sizes cannot be compared.")
    changed = 0
    for offset in range(0, len(a), 3):
        if (abs(a[offset] - b[offset]) > delta or abs(a[offset + 1] - b[offset + 1]) > delta
                or abs(a[offset + 2] - b[offset + 2]) > delta):
            changed += 1
    return round(changed / (w1 * h1), 4)


class LogWatch:
    """Incremental reader of wine.log: crash markers, the wined3d renderer line, DirectPlay errors."""

    def __init__(self, path):
        self.path, self.offset, self.pending = Path(path), 0, b""
        self.crash = self.renderer = None
        self.dplay_errors = []

    def poll(self):
        try:
            with self.path.open("rb") as stream:
                stream.seek(self.offset)
                chunk = stream.read(1 << 22)
        except OSError:
            return self.crash
        self.offset += len(chunk)
        lines = (self.pending + chunk).split(b"\n")
        self.pending = lines.pop()[-4096:]
        for raw in lines:
            line = raw.decode("utf-8", "replace").strip()
            if self.crash is None and any(marker in line for marker in CRASH_MARKERS):
                self.crash = line
            elif "wined3d_dll_init" in line and self.renderer is None:
                self.renderer = ("OpenGL" if "OpenGL renderer" in line
                                 else "no 3D (GDI)" if "Disabling 3D support" in line else line[-120:])
            elif ":err:dplay:" in line and len(self.dplay_errors) < 20:
                self.dplay_errors.append(line[-200:])
        return self.crash

    def summary(self):
        self.poll()
        return {"wined3dRenderer": self.renderer, "crash": self.crash, "directPlayErrors": self.dplay_errors}


class StepRunner:
    """Runs validated steps; after each one, health() may report that JK.EXE crashed or exited."""

    def __init__(self, screen, out, menus, health=None):
        self.screen, self.out, self.menus = screen, Path(out), menus
        self.results, self.shots, self.frames = [], [], {}
        self.health = health or (lambda: None)

    def run(self, steps):
        for index, step in enumerate(steps):
            started = time.monotonic()
            record = {"index": index, "op": step["op"], **({"note": step["note"]} if "note" in step else {})}
            self.results.append(record)
            try:
                record.update(self.execute(step) or {})
                if "wait" in step and not step.get("retry"):
                    time.sleep(step["wait"])
                self.check_health(record)
                record["result"] = "ok"
            except CaptureError as exc:
                record["result"] = "failed"
                record["error"] = str(exc)
                self.check_health(record, raising=False)
                raise
            finally:
                record["seconds"] = round(time.monotonic() - started, 2)

    def check_health(self, record, raising=True):
        problem = self.health()
        if problem is None or record.get("health"):
            return
        record["health"] = problem
        if "zz_crash" not in self.frames:
            try:
                self.shot("zz_crash")
            except (CaptureError, OSError, subprocess.SubprocessError):
                pass
        if raising:
            raise CaptureError(problem)

    def execute(self, step):
        op = step["op"]
        if op == "wait":
            time.sleep(step["s"])
        elif op == "click":
            x, y = target_point(self.menus, step["target"]) if "target" in step else (step["x"], step["y"])
            result = {"gui": [x, y], **({"target": step["target"]} if "target" in step else {})}
            if not step.get("retry"):
                self.screen.click(x, y)
                return result
            return {**result, **self.click_until_changed(x, y, step)}
        elif op == "click_row":
            x, y = row_point(self.menus, step["list"], step["row"])
            self.screen.click(x, y)
            return {"gui": [x, y], "list": step["list"], "row": step["row"]}
        elif op == "type":
            self.screen.type(step["text"], step.get("delay_ms", 120))
            return {"characters": len(step["text"])}
        elif op == "key":
            self.screen.key(step["keys"], step.get("delay_ms", 100))
        elif op == "hold":
            self.screen.hold(step["key"], step["s"])
        elif op == "list_select":
            x, y = target_point(self.menus, step["list"])
            self.screen.click(x, y)
            self.screen.key(["Up"] * (step.get("count", 64) + 1), 40)
            if step["index"]:
                self.screen.key(["Down"] * step["index"], 80)
            return {"gui": [x, y], "index": step["index"]}
        elif op == "shot":
            return self.shot(step["name"])
        elif op == "expect":
            return self.expect(step)
        elif op == "expect_changed":
            return self.expect_changed(step)
        elif op == "await":
            return self.await_region(step)
        return None

    def click_until_changed(self, x, y, step):
        """Click, wait, compare; click again (up to `retry` times) while the screen has not changed.

        JK drops clicks that arrive while it switches screens or display modes; a lost click leaves
        the frame identical, which is what this detects.
        """
        before = self.screen.grab()
        attempts = step["retry"] + 1
        fractions = []
        for attempt in range(1, attempts + 1):
            self.screen.click(x, y)
            time.sleep(step.get("wait", 1.0))
            fractions.append(changed_fraction(before, self.screen.grab()))
            if fractions[-1] >= CLICK_EFFECT_MIN:
                return {"attempts": attempt, "changedFractions": fractions}
            problem = self.health()
            if problem is not None:
                raise CaptureError(problem)
        raise CaptureError(f"Clicking ({x}, {y}) changed nothing on screen in {attempts} attempts "
                           f"(changed fractions {fractions}).")

    def expect_changed(self, step):
        """Fail when a frame is still mostly the earlier one (an error dialog over a menu, not a level)."""
        fraction = changed_fraction(self.frames[step["from"]], self.frames[step["name"]])
        threshold = step.get("min_fraction", 0.5)
        result = {"shot": step["name"], "from": step["from"], "changedFraction": fraction, "minFraction": threshold}
        if fraction < threshold:
            raise CaptureError(f"Expectation failed: {step['name']} differs from {step['from']} in only "
                               f"{fraction:.1%} of its pixels (< {threshold:.0%}); JK is still on the earlier screen "
                               "(an error dialog?).")
        return result

    def shot(self, name):
        width, height, rgb = self.screen.grab()
        data = png_bytes(width, height, rgb)
        path = self.out / f"{name}.png"
        with path.open("xb") as stream:
            stream.write(data)
        self.frames[name] = (width, height, rgb)
        record = {"name": name, "file": path.name, "sha256": sha256_bytes(data), "width": width, "height": height,
                  **region_stats(width, height, rgb)}
        self.shots.append(record)
        return {"file": path.name, "sha256": record["sha256"], "meanLuma": record["meanLuma"]}

    def expect(self, step):
        width, height, rgb = self.frames[step["name"]]
        region = self.screen.gui_region(step["region"], width, height)
        stats = region_stats(width, height, rgb, region)
        threshold = step.get("min_mean", NOT_BLACK_MEAN)
        result = {"shot": step["name"], "region": list(region), **stats, "minMean": threshold}
        if stats["meanLuma"] < threshold:
            raise CaptureError(f"Expectation failed: region {step['region']} of {step['name']} is black "
                               f"(mean luma {stats['meanLuma']} < {threshold}).")
        return result

    def await_region(self, step):
        deadline = time.monotonic() + step.get("timeout", 60)
        polls = 0
        while True:
            width, height, rgb = self.screen.grab()
            polls += 1
            region = self.screen.gui_region(step["region"], width, height)
            stats = region_stats(width, height, rgb, region)
            if step.get("not_flat"):
                stats["dominantFraction"] = dominant_fraction(width, height, rgb, region)
            if stats["meanLuma"] >= NOT_BLACK_MEAN and stats.get("dominantFraction", 0) < FLAT_FRACTION:
                return {"polls": polls, **stats}
            if time.monotonic() > deadline:
                raise CaptureError(f"Region {step['region']} stayed black for {step.get('timeout', 60)} s.")
            time.sleep(1.0)


# --------------------------------------------------------------------------------------------
# Sessions: Xvfb + JK.EXE under Wine

# Module types Windows can load or run. Only the three files prepare staged may exist under C:\JK.
EXECUTABLE_SUFFIXES = (".exe", ".dll", ".drv", ".ocx", ".cpl", ".acm", ".ax", ".scr", ".sys", ".com")
RUN_TOOLS = ("wine", "wineserver", "Xvfb", "xdotool", "xwininfo", "ffmpeg")
ATTACH_TOOLS = ("xdotool", "xwininfo", "ffmpeg")


def check_prefix_receipt(prefix):
    """A prefix prepare completed (receipt with status prepared), never the user's own Wine prefix."""
    prefix = Path(prefix).resolve()
    receipt_path = prefix / PREFIX_RECEIPT
    require(receipt_path.is_file(), f"{prefix} was not prepared by {TOOL} (no {PREFIX_RECEIPT}); run prepare first.")
    check_not_user_prefix(prefix, os.environ, Path(os.environ.get("HOME", str(Path.home()))))
    receipt = load_json(receipt_path)
    require(isinstance(receipt, dict) and receipt.get("status") == "prepared",
            f"{prefix} is not a completed prepare (status {receipt.get('status') if isinstance(receipt, dict) else None!r}).")
    return prefix, receipt


def staged_executables(receipt):
    """C:\\JK files that may run, with the sha256 prepare recorded: JK.EXE, the stub and the provider."""
    try:
        expected = {"jk.exe": receipt["executable"]["sha256"], "smackw32.dll": receipt["stub"]["sha256"]}
        if receipt["settings"].get("loopbackProvider"):
            expected[LOOPBACK_SP["file"]] = receipt["loopbackProvider"]["sha256"]
    except (KeyError, TypeError, AttributeError) as exc:
        raise CaptureError(f"{PREFIX_RECEIPT} lacks the staged executable hashes ({exc}).") from exc
    return expected


def load_prefix(prefix):
    """Check a prepared prefix before anything in it runs.

    JK.EXE, smackw32.dll and (when registered) jkcap_loopsp.dll must still match the hashes prepare
    recorded, and C:\\JK must hold no other module Windows could load (a DLL dropped next to JK.EXE
    would be loaded before Wine's own).
    """
    prefix, receipt = check_prefix_receipt(prefix)
    jk = prefix / "drive_c" / "JK"
    expected = staged_executables(receipt)
    labels = {"jk.exe": "JK.EXE", "smackw32.dll": "smackw32.dll", LOOPBACK_SP["file"]: LOOPBACK_SP["file"]}
    for name, digest in expected.items():
        path = jk / labels[name]
        require(path.is_file() and not path.is_symlink(), f"{labels[name]} is missing from the prefix; run prepare again.")
        require(sha256_file(path) == digest, f"{labels[name]} in the prefix changed since prepare; refusing to run it.")
    extra = []
    for current, dirs, names in os.walk(jk, followlinks=False):
        dirs.sort()
        for name in sorted(names):
            relative = (Path(current) / name).relative_to(jk).as_posix()
            if name.lower().endswith(EXECUTABLE_SUFFIXES) and relative.lower() not in expected:
                extra.append(relative)
    require(not extra, "C:\\JK holds executable files prepare did not stage; refusing to run JK.EXE next to them: "
            + ", ".join(extra[:10]))
    return prefix, receipt


def process_matches(pid, marker):
    """True when PID runs and its command line contains marker; False for a gone or reused PID.

    A PID whose command line cannot be read, or reads empty (a process still inside execve, or a
    zombie), counts as a match: for a lock, an owner that cannot be ruled out is treated as alive.
    """
    if type(pid) is not int or pid <= 1:
        return False
    try:
        cmdline = Path(f"/proc/{pid}/cmdline").read_bytes()
    except FileNotFoundError:
        return False
    except OSError:
        return Path(f"/proc/{pid}").exists()
    if not cmdline:
        return Path(f"/proc/{pid}").exists()
    return marker in cmdline.replace(b"\0", b" ").decode("utf-8", "replace")


def lock_alive(owner):
    """Whether a prefix lock still belongs to a running session (its tool, Xvfb or JK.EXE)."""
    if not isinstance(owner, dict):
        return True
    return (process_matches(owner.get("pid"), TOOL) or process_matches(owner.get("xvfbPid"), "Xvfb")
            or process_matches(owner.get("winePid"), "JK.EXE"))


def check_lock_free(prefix):
    """Refuse early (before an output folder exists) when a live session holds the prefix lock."""
    path = Path(prefix) / LOCK_FILE
    if path.exists() or path.is_symlink():
        try:
            owner = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            owner = None
        require(not lock_alive(owner), f"{prefix} is in use by another jkcapture session ({path}); stop it first.")
    return path


def acquire_lock(prefix, note):
    """Take the prefix lock (create-only) before any process starts; a stale lock is replaced."""
    path = check_lock_free(prefix)
    if path.exists() or path.is_symlink():
        path.unlink()
    with path.open("x", encoding="utf-8") as stream:
        json.dump(note, stream)
    return path


def update_lock(path, **values):
    note = json.loads(Path(path).read_text(encoding="utf-8"))
    note.update(values)
    Path(path).write_text(json.dumps(note), encoding="utf-8")


class Session:
    """JK.EXE on a private Xvfb display inside the prefix's virtual desktop.

    The prefix lock is taken before Xvfb or Wine start. A session that does not hold the lock never
    runs `wineserver -k`, so a refused start cannot stop the session that owns the prefix.
    """

    def __init__(self, prefix, out, display=None, jk_args=(), wine_debug=DEFAULT_WINE_DEBUG, offset=(0, 0)):
        self.prefix, self.out = Path(prefix), Path(out)
        self.requested_display, self.jk_args, self.wine_debug, self.offset = display, list(jk_args), wine_debug, offset
        self.display = self.wine = self.screen = self.lock = None
        self.stopped = False
        self.log = LogWatch(self.out / "wine.log")
        self.started = time.monotonic()

    def health(self):
        """None while JK.EXE runs normally; otherwise why it does not (crash marker or exit)."""
        crash = self.log.poll()
        if crash is not None:
            return f"JK.EXE crashed: {crash}"
        if self.wine is not None and self.wine.poll() is not None:
            return f"JK.EXE exited with code {self.wine.returncode}."
        return None

    def start(self):
        jk = self.prefix / "drive_c" / "JK"
        wine = tool_path("wine")
        self.lock = acquire_lock(self.prefix, {"tool": TOOL, "pid": os.getpid(), "out": str(self.out)})
        try:
            self.display = PrivateDisplay(self.requested_display, log_dir=self.out).__enter__()
            update_lock(self.lock, xvfbPid=self.display.process.pid, display=self.display.name)
            environment = wine_environment(self.prefix, self.display.name, self.wine_debug)
            log = (self.out / "wine.log").open("xb")
            try:
                self.wine = subprocess.Popen([wine, "JK.EXE", *self.jk_args], cwd=jk, env=environment,
                                             stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT,
                                             start_new_session=True)
            finally:
                log.close()
            update_lock(self.lock, winePid=self.wine.pid)
            self.screen = Screen(self.display.name, offset=self.offset)
            self.screen.find_window(60)
        except BaseException:
            self.stop()
            raise
        return self

    def describe(self):
        geometry = None
        if self.screen is not None and self.screen.window is not None:
            try:
                geometry = list(self.screen.geometry())
            except CaptureError:
                geometry = None
        return {"display": self.display.name if self.display else None,
                "xvfbPid": self.display.process.pid if self.display and self.display.process else None,
                "winePid": self.wine.pid if self.wine else None,
                "desktopWindow": {"name": DESKTOP_WINDOW, "id": self.screen.window if self.screen else None,
                                  "geometry": geometry},
                "jkArgs": self.jk_args, "wineDebug": self.wine_debug, "guiOffset": list(self.offset)}

    def stop(self):
        """Tear down what this session started; idempotent. Only the lock owner runs `wineserver -k`."""
        if self.stopped:
            return
        self.stopped = True
        if self.lock is not None:
            environment = wine_environment(self.prefix, self.display.name if self.display else ":0")
            try:
                subprocess.run([tool_path("wineserver"), "-k"], env=environment, stdin=subprocess.DEVNULL,
                               capture_output=True, timeout=60)
            except (OSError, subprocess.SubprocessError, CaptureError):
                pass
        if self.wine is not None:
            try:
                self.wine.wait(timeout=15)
            except subprocess.TimeoutExpired:
                try:
                    os.killpg(self.wine.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
                try:
                    self.wine.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    pass
        if self.display is not None:
            self.display.stop()
        if self.lock is not None:
            try:
                self.lock.unlink()
            except FileNotFoundError:
                pass
            self.lock = None

def wine_log_tail(out, lines=40):
    path = Path(out) / "wine.log"
    if not path.exists():
        return []
    with path.open("rb") as stream:
        stream.seek(max(0, path.stat().st_size - 65536))
        text = stream.read().decode("utf-8", "replace")
    return text.splitlines()[-lines:]


def jk_arguments(values):
    result = []
    for value in values or ():
        key = value.lower()
        require(key in JK_ARGS_ALLOWED, f"Unsupported JK.EXE argument {value!r}; allowed: {sorted(JK_ARGS_ALLOWED.values())}.")
        result.append(JK_ARGS_ALLOWED[key])
    return result


def parse_offset(text):
    require(re.fullmatch(r"-?\d{1,4},-?\d{1,4}", text or "") is not None, "--gui-offset must look like X,Y.")
    x, y = text.split(",")
    return int(x), int(y)


def new_output(out):
    out = Path(os.path.abspath(out))
    require(not out.exists() and not out.is_symlink(), f"--out must be a new directory; {out} exists.")
    require(out.parent.is_dir(), f"The parent of --out must exist: {out.parent}")
    out.mkdir()
    return out


def run_session(args, steps, command, plan_note=None):
    prefix, prefix_receipt = load_prefix(args.prefix)
    menus = load_menus()
    validate_steps(steps, menus)
    jk_args = jk_arguments(getattr(args, "jk_arg", None))
    if getattr(args, "windowgui", False) and "-windowGUI" not in jk_args:
        jk_args.append("-windowGUI")
    offset = parse_offset(args.gui_offset) if getattr(args, "gui_offset", None) else (0, 0)
    debug = "err+all,fixme-all" if getattr(args, "wine_log", False) else (getattr(args, "wine_debug", None) or DEFAULT_WINE_DEBUG)
    require(re.fullmatch(r"[A-Za-z0-9_,+\-]{1,200}", debug) is not None, "Bad --wine-debug value.")
    for name in RUN_TOOLS:          # refuse before the output folder exists, not halfway through a run
        tool_path(name)
    check_lock_free(prefix)         # Session.start takes the lock itself; this only refuses early
    out = new_output(args.out)
    receipt = {"schemaVersion": SCHEMA_VERSION, "tool": TOOL, "toolVersion": TOOL_VERSION, "credits": CREDITS,
               "command": command, "status": "failed", "prefix": str(prefix),
               "prefixReceiptSha256": sha256_file(prefix / PREFIX_RECEIPT),
               "executable": prefix_receipt["executable"], "stubSha256": prefix_receipt["stub"]["sha256"],
               "wineVersion": prefix_receipt.get("wineVersion"), "settings": prefix_receipt.get("settings"),
               "plan": plan_note or {}, "steps": [], "shots": []}
    session = Session(prefix, out, getattr(args, "display", None), jk_args, debug, offset)
    keep = getattr(args, "keep_running", False)
    runner = None
    try:
        session.start()
        receipt["session"] = session.describe()
        runner = StepRunner(session.screen, out, menus, session.health)
        runner.run(steps)
        receipt["status"] = "completed"
    except (CaptureError, OSError, subprocess.SubprocessError) as exc:
        receipt["error"] = f"{type(exc).__name__}: {exc}"
    except KeyboardInterrupt:
        receipt["error"] = "Interrupted."
        keep = False
    finally:
        if runner is not None:
            receipt["steps"], receipt["shots"] = runner.results, runner.shots
        if session.wine is not None:
            receipt["wineExitCode"] = session.wine.poll()
        receipt["wine"] = session.log.summary()
        if keep and receipt["status"] == "completed":
            receipt["keptRunning"] = True
            write_json_new(out / SESSION_FILE, {"schemaVersion": SCHEMA_VERSION, "prefix": str(prefix), "out": str(out),
                                                **session.describe()})
        else:
            session.stop()
            receipt["keptRunning"] = False
        receipt["wineLogTail"] = wine_log_tail(out)
        receipt["elapsedSeconds"] = round(time.monotonic() - session.started, 1)
        write_json_new(out / RUN_RECEIPT, receipt)
    return receipt


def load_session(path):
    """A session.json written by --keep-running, every field checked (a damaged file is refused, not a traceback)."""
    data = load_json(path)
    require(isinstance(data, dict) and data.get("schemaVersion") == SCHEMA_VERSION, "Unsupported session file.")
    require(isinstance(data.get("prefix"), str) and data["prefix"], "The session file names no prefix.")
    require(isinstance(data.get("out"), str) and data["out"], "The session file names no output folder.")
    require(isinstance(data.get("display"), str) and re.fullmatch(r":\d{1,4}", data["display"]) is not None,
            "The session file has no valid display.")
    require(type(data.get("xvfbPid")) is int and data["xvfbPid"] > 1, "The session file has no valid xvfbPid.")
    wine_pid = data.get("winePid")
    require(wine_pid is None or (type(wine_pid) is int and wine_pid > 1), "The session file has an invalid winePid.")
    window = data.get("desktopWindow")
    require(isinstance(window, dict) and (window.get("id") is None or type(window["id"]) is int),
            "The session file has an invalid desktopWindow.")
    offset = data.get("guiOffset", [0, 0])
    require(isinstance(offset, list) and len(offset) == 2 and all(type(v) is int for v in offset),
            "The session file has an invalid guiOffset.")
    data["guiOffset"] = offset
    return data


def attach_session(path):
    data = load_session(path)
    prefix, _ = load_prefix(data["prefix"])
    require(data["desktopWindow"].get("id") is not None, "The session file has no desktop window id.")
    require(process_matches(data["xvfbPid"], "Xvfb") and display_taken(int(data["display"][1:])),
            "The session's Xvfb is no longer running.")
    screen = Screen(data["display"], data["desktopWindow"]["id"], tuple(data["guiOffset"]))
    screen.geometry()
    return prefix, data, screen

def run_attached(args, steps, command):
    for name in ATTACH_TOOLS:
        tool_path(name)
    prefix, data, screen = attach_session(args.session)
    menus = load_menus()
    validate_steps(steps, menus)
    out = new_output(args.out)
    receipt = {"schemaVersion": SCHEMA_VERSION, "tool": TOOL, "toolVersion": TOOL_VERSION, "credits": CREDITS,
               "command": command, "status": "failed", "prefix": str(prefix), "session": str(Path(args.session).resolve()),
               "steps": [], "shots": []}
    started = time.monotonic()
    log = LogWatch(Path(data["out"]) / "wine.log")
    wine_pid = data.get("winePid")

    def health():
        crash = log.poll()
        if crash is not None:
            return f"JK.EXE crashed: {crash}"
        if wine_pid and not Path(f"/proc/{wine_pid}").exists():
            return "JK.EXE is no longer running."
        return None
    runner = StepRunner(screen, out, menus, health)
    try:
        runner.run(steps)
        receipt["status"] = "completed"
    except (CaptureError, OSError, subprocess.SubprocessError) as exc:
        receipt["error"] = f"{type(exc).__name__}: {exc}"
    finally:
        receipt["steps"], receipt["shots"] = runner.results, runner.shots
        receipt["wine"] = log.summary()
        receipt["elapsedSeconds"] = round(time.monotonic() - started, 1)
        write_json_new(out / RUN_RECEIPT, receipt)
    return receipt


def stop_session(path):
    """Stop a --keep-running session: only in a prefix prepare made, and only that session's processes.

    `wineserver -k` runs only after the prefix is confirmed as a prepared jkcapture prefix that no
    other live session holds; the session's Xvfb and JK.EXE are stopped by PID after their command
    lines are checked (a reused PID is left alone).
    """
    data = load_session(path)
    prefix, _ = check_prefix_receipt(data["prefix"])
    lock = prefix / LOCK_FILE
    if lock.exists() or lock.is_symlink():
        try:
            owner = json.loads(lock.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            owner = None
        mine = isinstance(owner, dict) and owner.get("xvfbPid") == data["xvfbPid"]
        require(mine or not lock_alive(owner),
                f"{prefix} is held by a different live session ({lock}); this session file is stale.")
    try:
        subprocess.run([tool_path("wineserver"), "-k"], env=wine_environment(prefix, data["display"]),
                       stdin=subprocess.DEVNULL, capture_output=True, timeout=60)
        stopped = {"wine": kill_pid_if(data["winePid"], "JK.EXE") if data.get("winePid") else False,
                   "xvfb": kill_pid_if(data["xvfbPid"], f"Xvfb {data['display']}")}
        if lock.exists() or lock.is_symlink():
            lock.unlink()
    except (OSError, subprocess.SubprocessError, ValueError) as exc:
        raise ToolFailure(f"Stopping the session failed: {type(exc).__name__}: {exc}") from exc
    record = {"schemaVersion": SCHEMA_VERSION, "stopped": stopped, "at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}
    target = Path(path).parent / "session-stopped.json"
    if not target.exists():
        write_json_new(target, record)
    return record

# --------------------------------------------------------------------------------------------
# Flows

def staged_episodes(prefix):
    """Episodes as JK lists them: subdirectories, then GOB files, in the directory's own order."""
    directory = Path(prefix) / "drive_c" / "JK" / "episode"
    order = []
    with os.scandir(directory) as entries:
        listing = list(entries)
    for entry in listing:
        if not entry.name.startswith(".") and entry.is_dir():
            order.append({"name": entry.name, "kind": "directory"})
    for entry in listing:
        if not entry.name.startswith(".") and entry.name.lower().endswith(".gob") and not entry.is_dir():
            info = episode_info(entry.path)
            order.append({"name": entry.name[:-4], "kind": "gob", **info})
    return order


def player_profiles(prefix):
    directory = Path(prefix) / "drive_c" / "JK" / "player"
    if not directory.is_dir():
        return []
    return sorted(entry.name for entry in directory.iterdir() if entry.is_dir())


def mp_characters(prefix, player):
    directory = Path(prefix) / "drive_c" / "JK" / "player" / player
    if not directory.is_dir():
        return []
    return sorted(entry.name for entry in directory.iterdir() if entry.suffix.lower() == ".mpc")


def startup_steps(players, player, difficulty, startup_wait):
    steps = [{"op": "await", "region": [0, 0, 640, 480], "not_flat": True, "timeout": max(60, startup_wait * 8),
              "note": "JK's first GUI screen, not the flat Wine desktop or the blank screen of a mode switch"},
             {"op": "wait", "s": startup_wait}]
    if not players:
        steps += [{"op": "shot", "name": "00_new_player"},
                  {"op": "click", "target": "player_new.name", "wait": 0.5},
                  {"op": "type", "text": player, "wait": 0.5},
                  {"op": "click", "target": f"player_new.{difficulty}", "wait": 0.5},
                  {"op": "click", "target": "player_new.ok", "wait": 3, "retry": 2}]
    else:
        steps += [{"op": "shot", "name": "00_player_select"},
                  {"op": "click", "target": "player_select.ok", "wait": 3, "retry": 2}]
    steps += [{"op": "shot", "name": "01_main_menu"},
              {"op": "expect", "name": "01_main_menu", "region": [0, 0, 640, 480]}]
    return steps


def walk_steps():
    script = load_json(HERE / "scripts" / "walk.json")
    require(isinstance(script, dict) and isinstance(script.get("steps"), list), "scripts/walk.json needs a steps list.")
    return script["steps"]


def check_player_name(name):
    require(re.fullmatch(r"[A-Za-z][A-Za-z0-9]{0,15}", name) is not None, "Player and character names: a letter, then up to 15 letters or digits.")
    return name


def sp_plan(prefix, args):
    episodes = [e for e in staged_episodes(prefix) if e.get("singlePlayer")]
    require(episodes, "The prefix has no single-player episode (TYPE 1) staged.")
    index = 0
    if args.episode:
        names = [e["name"].lower() for e in episodes]
        wanted = Path(args.episode).stem.lower()
        require(wanted in names, f"No single-player episode named {args.episode}; staged: {[e['name'] for e in episodes]}.")
        index = names.index(wanted)
    players = player_profiles(prefix)
    player = check_player_name(args.player)
    steps = startup_steps(players, player, args.difficulty, args.startup_wait)
    steps += [{"op": "click", "target": "main.single_player", "wait": 3, "retry": 2},
              {"op": "shot", "name": "02_single_player"},
              {"op": "click", "target": "sp.new_game", "wait": 3, "retry": 1}]
    if len(episodes) > 1:
        steps += [{"op": "shot", "name": "02b_choose_episode"},
                  {"op": "list_select", "list": "sp_episode.list", "index": index, "count": len(episodes), "wait": 1},
                  {"op": "click", "target": "sp_episode.ok", "wait": 3, "retry": 1}]
    steps += [{"op": "wait", "s": args.level_wait}]
    steps += level_view_steps(args, "03_level", "02_single_player")
    if args.walk:
        steps += walk_steps()
    plan = {"episodes": episodes, "episodeIndex": index, "playerProfiles": players, "player": player,
            "difficulty": args.difficulty, "levelWait": args.level_wait, "fullView": args.full_view}
    return steps, plan


def level_view_steps(args, shot, menu_shot):
    """Optionally grow JK's 3D view, grab the level frame and check it is a level, not a menu or dialog."""
    steps = []
    if args.full_view:
        steps += [{"op": "key", "keys": ["equal"] * 10, "delay_ms": 120, "wait": 8,
                   "note": "INPUT_FUNC_INCREASE (=) grows the 3D view to full size; its 'Screen size' lines fade"}]
    steps.append({"op": "shot", "name": shot})
    if not args.allow_black:
        steps += [{"op": "expect", "name": shot, "region": [0, 60, 640, 360],
                   "note": "the 3D view (below the message lines, above the status bar)"},
                  {"op": "expect_changed", "name": shot, "from": menu_shot, "min_fraction": 0.5,
                   "note": "a level frame, not the last menu with an error dialog over it"}]
    return steps


def provider_row(prefix, requested):
    """Row of the network provider list: explicit, else the loopback provider when registered, else 0."""
    providers = directplay_providers((Path(prefix) / "system.reg").read_text("utf-8", "replace"))
    if requested is not None:
        require(0 <= requested < max(len(providers), 1), f"--provider-row {requested} is outside the {len(providers)} providers {providers}.")
        return requested, providers
    if LOOPBACK_SP["name"] in providers:
        return providers.index(LOOPBACK_SP["name"]), providers
    return 0, providers


def mp_plan(prefix, args):
    episodes = [e for e in staged_episodes(prefix) if e.get("multiplayer")]
    require(episodes, "The prefix has no multiplayer episode (TYPE 2) staged.")
    require(len(episodes) == 1 or args.episode, "Several multiplayer episodes are staged; choose one with --episode.")
    index = 0
    if args.episode:
        names = [e["name"].lower() for e in episodes]
        wanted = Path(args.episode).stem.lower()
        require(wanted in names, f"No multiplayer episode named {args.episode}.")
        index = names.index(wanted)
    episode = episodes[index]
    levels = [level.lower() for level in episode["levels"]]
    require(args.level.lower() in levels, f"{episode['name']} has no LEVEL {args.level}; levels: {episode['levels']}.")
    level_row = levels.index(args.level.lower())
    players = player_profiles(prefix)
    player = check_player_name(args.player)
    character = check_player_name(args.character or args.player)
    characters = mp_characters(prefix, player) if player in players else []
    row, providers = provider_row(prefix, args.provider_row)
    steps = startup_steps(players, player, args.difficulty, args.startup_wait)
    steps += [{"op": "click", "target": "main.multiplayer", "wait": 3, "retry": 2},
              {"op": "shot", "name": "02_multiplayer"},
              {"op": "click", "target": "mp.host", "wait": 3, "retry": 2},
              {"op": "shot", "name": "03_provider"},
              {"op": "click_row", "list": "mp_provider.list", "row": row, "wait": 0.5},
              {"op": "click", "target": "mp_provider.ok", "wait": 4, "retry": 2},
              {"op": "shot", "name": "04_host_setup"}]
    if len(episodes) > 1:
        steps.append({"op": "click_row", "list": "mp_host.episodes", "row": index, "wait": 1})
    steps += [{"op": "click_row", "list": "mp_host.levels", "row": level_row, "wait": 1},
              {"op": "shot", "name": "05_level_chosen"},
              {"op": "click", "target": "mp_host.ok", "wait": 4, "retry": 1}]
    if not characters:
        steps += [{"op": "shot", "name": "06_no_valid_characters"},
                  {"op": "click", "target": "popup.ok", "wait": 2, "retry": 2},
                  {"op": "click", "target": "mp_new_char.name", "wait": 0.5},
                  {"op": "type", "text": character, "wait": 0.5},
                  {"op": "click", "target": "mp_new_char.ok", "wait": 3, "retry": 2},
                  {"op": "shot", "name": "07_edit_character"},
                  {"op": "click", "target": "mp_edit_char.save", "wait": 3, "retry": 2}]
    steps += [{"op": "shot", "name": "08_load_character"},
              {"op": "click_row", "list": "mp_load_char.list", "row": 0, "wait": 0.5},
              {"op": "click", "target": "mp_load_char.ok", "wait": args.spawn_wait, "retry": 1}]
    steps += level_view_steps(args, "09_spawn", "08_load_character")
    if args.walk:
        steps += walk_steps()
    plan = {"episode": episode, "levelRow": level_row, "providerRow": row,
            "provider": providers[row] if row < len(providers) else None, "directPlayProviders": providers,
            "playerProfiles": players, "player": player, "character": character, "existingCharacters": characters,
            "spawnWait": args.spawn_wait, "fullView": args.full_view}
    return steps, plan


# --------------------------------------------------------------------------------------------
# Command line

def add_session_options(parser):
    parser.add_argument("--prefix", required=True, help="a prefix created by prepare")
    parser.add_argument("--out", required=True, help="new output directory (create-only)")
    parser.add_argument("--display", type=int, help="Xvfb display number (default: first free of :91..:99)")
    parser.add_argument("--windowgui", action="store_true", help="pass -windowGUI (Wren's flag); needs --gui-offset")
    parser.add_argument("--gui-offset", help="X,Y of JK's 640x480 GUI inside the desktop window (default 0,0)")
    parser.add_argument("--jk-arg", action="append", help="extra JK.EXE switch: -dispStats, -frameRate, -noHUD, -devMode, -windowGUI")
    parser.add_argument("--wine-log", action="store_true", help="WINEDEBUG=err+all,fixme-all into wine.log")
    parser.add_argument("--wine-debug", help="explicit WINEDEBUG value (diagnostics, e.g. +dplay)")
    parser.add_argument("--keep-running", action="store_true", help="leave JK running; use steps/walk/stop with session.json")


def add_player_options(parser):
    parser.add_argument("--player", default="Red", help="player profile name for a first start (default Red)")
    parser.add_argument("--difficulty", choices=("easy", "medium", "hard"), default="medium")
    parser.add_argument("--startup-wait", type=float, default=4.0, help="seconds after the first GUI frame")
    parser.add_argument("--allow-black", action="store_true",
                        help="skip the level-frame checks (3D view not black, frame differs from the last menu)")
    parser.add_argument("--full-view", action="store_true", help="press = ten times to grow JK's 3D view before the level frame")
    parser.add_argument("--walk", action="store_true", help="append Wren's walk demo (scripts/walk.json)")


def build_parser():
    parser = argparse.ArgumentParser(prog="jkcapture.py", description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)

    p = sub.add_parser("build-stub", help="compile and verify the inert smackw32.dll")
    p.add_argument("--out", required=True, help="directory for smackw32.dll (the file must not exist)")

    p = sub.add_parser("build-loopsp", help="compile and verify the inert DirectPlay loopback provider")
    p.add_argument("--out", required=True, help=f"directory for {LOOPBACK_SP['file']} (the file must not exist)")

    p = sub.add_parser("prepare", help="create a new isolated Wine prefix with a JK layout")
    p.add_argument("--prefix", required=True, help="new directory for the Wine prefix")
    p.add_argument("--exe", required=True, help="retail JK.EXE (hash-checked)")
    p.add_argument("--exe-sha256", help="vouch for another JK.EXE by its sha256")
    p.add_argument("--resource-dir", help="folder with Res1hi.gob, Res2.gob and JK_.CD")
    p.add_argument("--resource", action="append", help="one resource file (.gob/.goo or JK_.CD); repeatable")
    p.add_argument("--episode", action="append", help="episode GOB/GOO to stage in episode/; repeatable")
    p.add_argument("--copy", action="store_true",
                   help="stage read-only copies instead of symlinks to the original files")
    p.add_argument("--desktop", default="640x480", help="Wine virtual desktop size (default 640x480)")
    p.add_argument("--wined3d", choices=("gl", "gdi"), default="gl",
                   help="wined3d renderer that presents JK's frames (default gl, needs 32-bit Mesa; gdi crashes at level load on Wine 9.0)")
    p.add_argument("--accel", action="store_true", help="set b3DAccel=1 (JK's Direct3D renderer; untested)")
    p.add_argument("--no-loopback", action="store_true",
                   help="do not build and register the DirectPlay loopback provider (multiplayer hosting then fails on Wine 9.0)")
    p.add_argument("--display", type=int, help="Xvfb display for wineboot")

    p = sub.add_parser("run", help="launch JK.EXE and run a step script")
    add_session_options(p)
    p.add_argument("--steps", required=True, help="JSON step script (list, or object with a steps list)")

    p = sub.add_parser("sp", help="single-player flow up to a level frame")
    add_session_options(p)
    add_player_options(p)
    p.add_argument("--episode", help="single-player episode to choose when several are staged (GOB name)")
    p.add_argument("--level-wait", type=float, default=30.0, help="seconds after New Game before the level frame")

    p = sub.add_parser("mp-host", help="host a multiplayer game on one episode level")
    add_session_options(p)
    add_player_options(p)
    p.add_argument("--episode", help="multiplayer episode GOB name when several are staged")
    p.add_argument("--level", required=True, help="LEVEL file from episode.jk, e.g. m10.jkl")
    p.add_argument("--provider-row", type=int,
                   help="row in the network provider list (default: the loopback provider when registered, else 0)")
    p.add_argument("--character", help="multiplayer character name for a first host (default: the player name)")
    p.add_argument("--spawn-wait", type=float, default=40.0, help="seconds after Load Character Ok (Wren: 40)")

    p = sub.add_parser("steps", help="run a step script on a kept-running session")
    p.add_argument("--session", required=True, help="session.json written by --keep-running")
    p.add_argument("--steps", required=True)
    p.add_argument("--out", required=True)

    p = sub.add_parser("walk", help="Wren's walk demo on a kept-running session")
    p.add_argument("--session", required=True)
    p.add_argument("--out", required=True)

    p = sub.add_parser("stop", help="tear down a kept-running session")
    p.add_argument("--session", required=True)

    p = sub.add_parser("sp-wrap", help="wrap one level of an episode GOB as a single-player episode")
    p.add_argument("--from", dest="source", required=True, help="episode GOB/GOO, e.g. JK1MP.GOB")
    p.add_argument("--level", required=True, help="LEVEL file, e.g. m10.jkl")
    p.add_argument("--out", required=True, help="new .gob file")
    p.add_argument("--title", help="episode title shown by JK")
    return parser


def load_steps_file(path):
    data = load_json(path)
    if isinstance(data, dict):
        require(set(data) <= {"schemaVersion", "steps", "note", "credits"} and "steps" in data, "A step file object needs a steps list.")
        return data["steps"]
    return data


def main(argv=None):
    args = build_parser().parse_args(argv)
    try:
        if args.command == "build-stub":
            result = build_stub(args.out)
        elif args.command == "build-loopsp":
            result = build_loopsp(args.out)
        elif args.command == "prepare":
            result = prepare(args)
            result = {"status": result["status"], "prefix": result["prefix"], "receipt": str(Path(result["prefix"]) / PREFIX_RECEIPT)}
        elif args.command in ("run", "sp", "mp-host"):
            if args.command == "run":
                steps, plan = load_steps_file(args.steps), {"stepsFile": str(Path(args.steps).resolve())}
            else:
                prefix, _ = load_prefix(args.prefix)
                steps, plan = (sp_plan if args.command == "sp" else mp_plan)(prefix, args)
            receipt = run_session(args, steps, args.command, plan)
            result = {"status": receipt["status"], "receipt": str(Path(args.out).resolve() / RUN_RECEIPT),
                      "shots": [s["file"] for s in receipt["shots"]], **({"error": receipt["error"]} if "error" in receipt else {})}
        elif args.command in ("steps", "walk"):
            steps = load_steps_file(args.steps) if args.command == "steps" else walk_steps()
            receipt = run_attached(args, steps, args.command)
            result = {"status": receipt["status"], "receipt": str(Path(args.out).resolve() / RUN_RECEIPT),
                      "shots": [s["file"] for s in receipt["shots"]], **({"error": receipt["error"]} if "error" in receipt else {})}
        elif args.command == "stop":
            result = stop_session(args.session)
        elif args.command == "sp-wrap":
            result = wrap_level(args.source, args.level, args.out, args.title)
        else:
            raise CaptureError(f"Unknown command {args.command}.")
    except ToolFailure as exc:              # a tool failed: wineboot, the compiler, Xvfb, wineserver ...
        print(json.dumps({"status": "failed", "error": str(exc)}), file=sys.stderr)
        return 1
    except CaptureError as exc:             # an input or precondition was rejected
        print(json.dumps({"status": "refused", "error": str(exc)}), file=sys.stderr)
        return 2
    except (OSError, subprocess.SubprocessError) as exc:
        print(json.dumps({"status": "failed", "error": f"{type(exc).__name__}: {exc}"}), file=sys.stderr)
        return 1
    print(json.dumps(result, indent=2))
    return 0 if result.get("status", "ok") not in ("failed",) else 1


if __name__ == "__main__":
    sys.exit(main())
