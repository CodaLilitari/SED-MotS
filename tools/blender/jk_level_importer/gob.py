# Read-only GOB/GOO archive reader for the JK level importer (stdlib only).
# Part of SED-MotS (Red's claude branch). Finishes Wren's level-preview work: Wren (a Grok bot,
# one of Andrei's collaborators) wrote the original render.py; recovered 2026-10-31.
# Uses Coda's Blender-Sith fork (v1.0.1-coda.1), based on Blender-Sith by Crt Vavros (smlu).
# SPDX-License-Identifier: GPL-3.0-or-later
"""JK/MotS GOB and GOO archives: a 12-byte header, a directory of 136-byte rows, payloads.

Layout (little-endian): ``"GOB "``, uint32 version ``0x14``, uint32 directory offset; at that
offset a uint32 entry count, then per entry ``uint32 offset, uint32 length, char[128] name``
(NUL-terminated ASCII, backslash separated). MotS ``.goo`` files use the same container.
The rules match ``tools/engine_resource_closure.py`` and Wren's ``gobx.py``: every row is
bounds-checked and names that could escape an extraction folder are refused.
"""
from __future__ import annotations

import hashlib
import os
import struct
from pathlib import Path

MAGIC = b"GOB "
VERSION = 0x14
ROW_BYTES = 136
MAX_ENTRIES = 100000


class GobError(ValueError):
    """The file is not a readable GOB/GOO archive."""


def normalize(name: str) -> str:
    """Archive path for matching: forward slashes, lower case (the engine is case-insensitive)."""
    return name.replace("\\", "/").lower()


def _safe(normalized: str) -> bool:
    parts = normalized.split("/")
    return (bool(normalized) and not normalized.startswith("/")
            and all(part not in ("", ".", "..") for part in parts)
            and not any(ord(ch) < 32 or ch in ':"' for ch in normalized))


class GobArchive:
    """Directory of one GOB/GOO file. Payloads are read on demand; the file is never written."""

    def __init__(self, path):
        self.path = Path(path).resolve()
        try:
            size = self.path.stat().st_size
        except OSError as exc:
            raise GobError(f"Cannot read archive {path}: {exc.strerror or exc}.") from exc
        self.size = size
        self.entries: dict[str, tuple[int, int, str]] = {}
        self.duplicates: list[str] = []
        self.order: list[str] = []
        self._sha256: str | None = None
        with self.path.open("rb") as stream:
            header = stream.read(12)
            if len(header) < 12:
                raise GobError("Truncated GOB header.")
            magic, version, offset = struct.unpack("<4sII", header)
            if magic != MAGIC:
                raise GobError("Not a GOB archive (bad magic).")
            if version != VERSION:
                raise GobError(f"Unsupported GOB version 0x{version:X}; expected 0x14.")
            if not 12 <= offset <= size - 4:
                raise GobError("GOB directory offset is outside the archive.")
            stream.seek(offset)
            count = struct.unpack("<I", stream.read(4))[0]
            if count > MAX_ENTRIES or count * ROW_BYTES > size - offset - 4:
                raise GobError("GOB directory exceeds the available bytes or the 100000-entry limit.")
            rows = stream.read(count * ROW_BYTES)
        for row in range(count):
            data_offset, length, raw = struct.unpack_from("<II128s", rows, row * ROW_BYTES)
            if data_offset + length > size:
                raise GobError(f"GOB entry {row} lies outside the archive.")
            if b"\0" not in raw:
                raise GobError(f"GOB entry {row} has an unterminated name.")
            try:
                name = raw.split(b"\0", 1)[0].decode("ascii")
            except UnicodeDecodeError as exc:
                raise GobError(f"GOB entry {row} has a non-ASCII name.") from exc
            key = normalize(name)
            if not _safe(key):
                raise GobError(f"GOB entry {row} has an unsupported path {name!r}.")
            if key in self.entries:
                # The first row wins, as in a linear directory search; report the rest.
                self.duplicates.append(name)
                continue
            self.entries[key] = (data_offset, length, name)
            self.order.append(key)

    def __contains__(self, normalized: str) -> bool:
        return normalized in self.entries

    def raw_name(self, normalized: str) -> str:
        return self.entries[normalized][2]

    def length(self, normalized: str) -> int:
        return self.entries[normalized][1]

    def read(self, normalized: str) -> bytes:
        data_offset, length, _ = self.entries[normalized]
        with self.path.open("rb") as stream:
            stream.seek(data_offset)
            data = stream.read(length)
        if len(data) != length:
            raise GobError(f"GOB entry {normalized!r} was truncated while reading.")
        return data

    @property
    def sha256(self) -> str:
        """SHA-256 of the whole archive file, for provenance (computed once, streamed)."""
        if self._sha256 is None:
            digest = hashlib.sha256()
            with self.path.open("rb") as stream:
                for block in iter(lambda: stream.read(1 << 20), b""):
                    digest.update(block)
            self._sha256 = digest.hexdigest()
        return self._sha256

    def names(self, suffix: str = "") -> list[str]:
        """Normalized entry names in directory order, optionally filtered by suffix."""
        suffix = suffix.lower()
        return [key for key in self.order if key.endswith(suffix)]


def is_archive_path(path) -> bool:
    return os.fspath(path).lower().endswith((".gob", ".goo"))
