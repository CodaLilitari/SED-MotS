"""Bounded GOB/GOO directory-only checks for an explicitly staged capture session.

This deliberately conservative alias check does not observe engine file opens or
model its complete lookup precedence. It never extracts archive payloads, scans
an installation, or adds files to the declared staging set.
"""
from __future__ import annotations

import hashlib
from pathlib import Path
import struct

MAX_ARCHIVE_ENTRIES = 100000
MAX_TOTAL_ARCHIVE_ENTRIES = 250000
MAX_CANDIDATES = 4096
RECORD_BYTES = 136


class ResourceClosureError(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise ResourceClosureError(message)


def normalize_entry(name):
    require(isinstance(name, str) and name and name.isascii(), "Archive entry name must be nonempty ASCII.")
    normalized = name.replace("\\", "/").lower()
    parts = normalized.split("/")
    require(not normalized.startswith("/") and all(part not in ("", ".", "..") for part in parts)
            and not any(ord(ch) < 32 or ch in ':"' for ch in normalized), "Unsupported archive entry path.")
    return normalized


def read_gob_index(path, max_entries=MAX_ARCHIVE_ENTRIES):
    """Read only a supported JK/MotS GOB header and fixed-width directory rows."""
    path = Path(path)
    size = path.stat().st_size
    with path.open("rb") as stream:
        header = stream.read(12)
        require(len(header) == 12, "Truncated GOB header.")
        magic, version, offset = struct.unpack("<4sII", header)
        require(magic == b"GOB " and version == 0x14, "Unsupported GOB magic or version.")
        require(12 <= offset <= size - 4, "GOB directory is outside the archive.")
        stream.seek(offset)
        raw_count = stream.read(4)
        require(len(raw_count) == 4, "Truncated GOB entry count.")
        count = struct.unpack("<I", raw_count)[0]
        require(count <= min(MAX_ARCHIVE_ENTRIES, max_entries) and count * RECORD_BYTES <= size - offset - 4,
                "GOB directory exceeds available bytes or100000-entry budget.")
        index_hash = hashlib.sha256(header + raw_count)
        entries = []
        for row in range(count):
            record = stream.read(RECORD_BYTES)
            require(len(record) == RECORD_BYTES, "Truncated GOB directory row.")
            index_hash.update(record)
            data_offset, data_length = struct.unpack_from("<II", record)
            require(data_offset + data_length <= size, "GOB entry payload range is outside the archive.")
            raw_name = record[8:]
            require(b"\0" in raw_name, "Unsupported unterminated GOB entry name.")
            try:
                name = raw_name.split(b"\0", 1)[0].decode("ascii")
            except UnicodeDecodeError as exc:
                raise ResourceClosureError("Unsupported non-ASCII GOB entry name.") from exc
            entries.append({"row": row, "name": name, "logicalPath": normalize_entry(name)})
    return {"bytes": size, "directoryOffset": offset, "entryCount": count,
            "indexSha256": index_hash.hexdigest()}, entries


def logical_loose_path(destination, episode):
    normalized = destination.replace("\\", "/").lower()
    prefix = f"episode/{episode.lower()}/"
    if normalized.startswith(prefix):
        return normalized[len(prefix):]
    if normalized.startswith("resource/"):
        return normalized[len("resource/"):]
    return normalized


def check_resource_closure(files, output, episode):
    """Return alias evidence for all explicitly provided scenario files.

    Any matching logical path OR basename outside a scenario file's own loose
    slot is refused. This includes the custom JKL/3DO/MAT/CMP and episode.jk.
    All declared resource .gob/.goo indexes are checked, conservatively including
    declared episode archives rather than guessing runtime visibility/order.
    """
    report = {"schemaVersion": 1, "valid": False, "runtimeResourceResolutionObserved": False,
              "scope": "Explicit staged scenario files versus all declared bootstrap archive indexes and loose aliases",
              "matching": "case-insensitive logical path OR basename; conservative alias refusal",
              "archivePayloadsRead": False, "archives": [], "unsupportedArchives": [],
              "customResourceCandidates": [], "duplicateCustomResources": []}
    by_logical, by_basename = {}, {}
    for item in files:
        if item["role"] != "scenario":
            continue
        logical = logical_loose_path(item["destination"], episode)
        custom = {"destination": item["destination"], "logicalPath": logical,
                  "aliases": [logical, logical.rsplit("/", 1)[-1]], "candidates": []}
        index = len(report["customResourceCandidates"])
        report["customResourceCandidates"].append(custom)
        by_logical.setdefault(logical, []).append(index)
        by_basename.setdefault(logical.rsplit("/", 1)[-1], []).append(index)
    require(report["customResourceCandidates"], "No explicitly staged scenario files.")
    candidate_count = 0

    def match(logical, candidate):
        nonlocal candidate_count
        indexes = set(by_logical.get(logical, [])) | set(by_basename.get(logical.rsplit("/", 1)[-1], []))
        for index in sorted(indexes):
            candidate_count += 1
            require(candidate_count <= MAX_CANDIDATES, "Custom resource alias matches exceeded4096-candidate budget.")
            report["customResourceCandidates"][index]["candidates"].append(dict(candidate, logicalPath=logical))

    total_entries = 0
    for item in files:
        destination = item["destination"]
        if destination.lower().endswith((".gob", ".goo")):
            # An archive outside the declared bootstrap resources is unsupported
            # rather than an opaque scenario file that silently escapes checking.
            if item["role"] != "resource":
                report["unsupportedArchives"].append({"destination": destination, "error": "Only explicit bootstrap resource archives are supported."})
                continue
            try:
                metadata, entries = read_gob_index(Path(output) / destination, MAX_TOTAL_ARCHIVE_ENTRIES - total_entries)
                total_entries += metadata["entryCount"]
                require(total_entries <= MAX_TOTAL_ARCHIVE_ENTRIES, "Declared archive directories exceeded250000 total entries.")
                report["archives"].append(dict(metadata, destination=destination, declaredSha256=item["sha256"]))
                for entry in entries:
                    match(entry["logicalPath"], {"kind": "archive-entry", "destination": destination,
                                                 "entry": entry["name"], "row": entry["row"]})
            except (OSError, ResourceClosureError) as exc:
                report["unsupportedArchives"].append({"destination": destination, "error": str(exc)})
        else:
            match(logical_loose_path(destination, episode), {"kind": "loose-file", "destination": destination,
                                                           "declaredSha256": item["sha256"]})
    for custom in report["customResourceCandidates"]:
        candidates = custom["candidates"]
        custom["exactlyOneIntendedCandidate"] = (len(candidates) == 1 and candidates[0]["kind"] == "loose-file"
                                                 and candidates[0]["destination"] == custom["destination"])
        if not custom["exactlyOneIntendedCandidate"]:
            report["duplicateCustomResources"].append(custom["destination"])
    report["valid"] = not report["duplicateCustomResources"] and not report["unsupportedArchives"]
    return report
