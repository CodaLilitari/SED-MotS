#!/usr/bin/env python3
"""Acquire the reviewed MoltenVK release and notices, without executing them.

The CLI always uses the source-controlled dependency lock. Every downloaded byte
is verified before a success manifest is emitted. Only two allowlisted regular
archive members are materialized; framework links and tools are not extracted.
"""
from __future__ import annotations

import argparse
from contextlib import closing
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import sys
import tarfile
import time
import unicodedata
import urllib.parse
import urllib.request

from linux import ToolError, new_output

LOCK = Path(__file__).resolve().parent / "macos" / "dependency-lock.json"
MAX_FILE_BYTES = 128 * 1024 * 1024


def safe_relative(value):
    if (not isinstance(value, str) or not value or any(c in value for c in "\\:\r\n\0")
            or PurePosixPath(value).is_absolute()
            or any(part in ("", ".", "..") or part.endswith((".", " ")) for part in value.split("/"))):
        raise ToolError(f"Unsafe dependency path: {value!r}")
    return value


def canonical(value):
    return unicodedata.normalize("NFC", value).casefold()


def validate_lock(lock):
    if lock.get("schemaVersion") != 1 or not re.fullmatch(r"\d+\.\d+\.\d+", lock.get("version", "")):
        raise ToolError("Unsupported dependency lock version")
    if not re.fullmatch(r"[0-9a-f]{40}", lock.get("upstreamCommit", "")):
        raise ToolError("Dependency lock needs an exact upstream commit")
    if not lock.get("notices") or "member" not in lock["library"]:
        raise ToolError("Dependency lock is missing the library or notices")
    paths = {"moltenvk-manifest.json"}
    destinations = set()
    members = set()
    for row in [lock["artifact"], lock["library"], *lock["notices"]]:
        relative = canonical(safe_relative(row["path"]))
        if any(relative == other or relative.startswith(other + "/") or other.startswith(relative + "/") for other in paths):
            raise ToolError("Dependency output paths overlap or collide")
        paths.add(relative)
        if type(row.get("bytes")) is not int or not 0 < row["bytes"] <= MAX_FILE_BYTES:
            raise ToolError("Dependency size must be positive and bounded")
        if not re.fullmatch(r"[0-9a-f]{64}", row.get("sha256", "")):
            raise ToolError("Dependency is missing its SHA-256 pin")
        if "member" in row:
            member = safe_relative(row["member"])
            if member in members:
                raise ToolError("Duplicate selected archive member")
            members.add(member)
        if "url" in row:
            url = urllib.parse.urlsplit(row["url"])
            if url.scheme != "https" or not url.hostname or url.username or url.password or url.fragment:
                raise ToolError("Dependency sources must be credential-free HTTPS URLs")
        elif row is not lock["library"]:
            raise ToolError("Dependency source URL is missing")
    for row in lock["notices"]:
        destination = safe_relative(row["destination"])
        if not destination.startswith("licenses/moltenvk/") or canonical(destination) in destinations:
            raise ToolError("Notice destinations must be unique under licenses/moltenvk/")
        destinations.add(canonical(destination))


def check_deadline(deadline):
    if time.monotonic() >= deadline:
        raise ToolError("Dependency acquisition exceeded its 300-second deadline")


def verified_copy(stream, target, expected, deadline):
    digest = hashlib.sha256()
    count = 0
    target.parent.mkdir(parents=True, exist_ok=True)
    with target.open("xb") as output:
        while True:
            check_deadline(deadline)
            block = stream.read(min(1024 * 1024, expected["bytes"] - count + 1))
            if not block:
                break
            count += len(block)
            if count > expected["bytes"]:
                raise ToolError(f"Dependency exceeds its pinned size: {expected['path']}")
            output.write(block)
            digest.update(block)
        output.flush()
        os.fsync(output.fileno())
    if count != expected["bytes"] or digest.hexdigest() != expected["sha256"]:
        raise ToolError(f"Dependency hash/size mismatch: {expected['path']}")


def open_https(url, timeout):
    return urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": "SED-MotS-pinned-release"}), timeout=timeout)


def download(row, output, open_url, deadline):
    check_deadline(deadline)
    with closing(open_url(row["url"], timeout=min(30, max(1, deadline - time.monotonic())))) as response:
        if hasattr(response, "geturl") and urllib.parse.urlsplit(response.geturl()).scheme != "https":
            raise ToolError("Dependency download redirected away from HTTPS")
        verified_copy(response, output / row["path"], row, deadline)


def extract_selected(archive_path, output, selected, deadline):
    # Validate every header before writing any selected member. Legitimate
    # unselected framework symlinks are ignored, never followed or extracted.
    wanted = {row["member"]: row for row in selected}
    payloads = {}
    seen = set()
    try:
        with tarfile.open(archive_path, mode="r:") as archive:
            for index, member in enumerate(archive):
                check_deadline(deadline)
                if index >= 1000:
                    raise ToolError("Dependency archive has too many members")
                name = safe_relative(member.name.rstrip("/") if member.isdir() else member.name)
                if name in seen:
                    raise ToolError(f"Duplicate archive member: {name}")
                seen.add(name)
                if name not in wanted:
                    continue
                row = wanted[name]
                if not member.isfile() or member.size != row["bytes"]:
                    raise ToolError(f"Selected member is not the expected regular file: {name}")
                with archive.extractfile(member) as source:
                    data = source.read(row["bytes"] + 1)
                if len(data) != row["bytes"] or hashlib.sha256(data).hexdigest() != row["sha256"]:
                    raise ToolError(f"Selected archive member hash mismatch: {name}")
                payloads[name] = data
    except (tarfile.TarError, OSError) as error:
        raise ToolError(f"Cannot validate dependency archive: {error}") from error
    if set(payloads) != set(wanted):
        raise ToolError("Dependency archive is missing a selected member")
    for name, data in payloads.items():
        target = output / wanted[name]["path"]
        target.parent.mkdir(parents=True, exist_ok=True)
        with target.open("xb") as destination:
            destination.write(data)
        target.chmod(0o644)


def acquire(output, lock, *, open_url=open_https, archive_path=None):
    """Build a new portable manifest from a validated release lock.

    The CLI supplies only LOCK; callers can use independent locks/streams for
    offline fixture validation. There is no command-line pin override.
    """
    validate_lock(lock)
    output = new_output(output)
    deadline = time.monotonic() + 300
    if archive_path is None:
        download(lock["artifact"], output, open_url, deadline)
    else:
        archive_path = Path(archive_path).absolute()
        for ancestor in (archive_path, *archive_path.parents):
            info = ancestor.lstat()
            if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
                raise ToolError("Cached dependency archive cannot pass through links or reparse points")
        if not archive_path.is_file():
            raise ToolError("Cached dependency archive must be a regular file")
        with archive_path.open("rb") as source:
            verified_copy(source, output / lock["artifact"]["path"], lock["artifact"], deadline)
    selected = [lock["library"], *(row for row in lock["notices"] if "member" in row)]
    extract_selected(output / lock["artifact"]["path"], output, selected, deadline)
    for row in lock["notices"]:
        if "member" not in row:
            download(row, output, open_url, deadline)
    manifest = {key: lock[key] for key in ("schemaVersion", "version", "upstreamCommit")}
    manifest["artifact"] = dict(lock["artifact"])
    manifest["library"] = {key: value for key, value in lock["library"].items() if key != "member"}
    manifest["notices"] = [{key: value for key, value in row.items() if key != "member"} for row in lock["notices"]]
    # Success is emitted only after the final on-disk bytes are checked again.
    for row in [manifest["artifact"], manifest["library"], *manifest["notices"]]:
        path = output / row["path"]
        with path.open("rb") as source:
            digest = hashlib.file_digest(source, "sha256").hexdigest() if hasattr(hashlib, "file_digest") else hashlib.sha256(source.read()).hexdigest()
        if path.stat().st_size != row["bytes"] or digest != row["sha256"]:
            raise ToolError("Dependency changed before manifest publication")
    result = output / "moltenvk-manifest.json"
    with result.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(manifest, stream, indent=2)
        stream.write("\n")
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path, help="New directory outside every Git worktree")
    parser.add_argument("--archive", type=Path, help="Optional existing release archive; the same pins are still required")
    args = parser.parse_args()
    try:
        print(acquire(args.output, json.loads(LOCK.read_text(encoding="utf-8")), archive_path=args.archive))
    except (ToolError, OSError, ValueError) as error:
        print(f"Mac dependency acquisition failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
