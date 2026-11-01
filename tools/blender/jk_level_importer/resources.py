# Engine-style resource lookup over loose folders and GOB/GOO archives (stdlib only).
# Part of SED-MotS (Red's claude branch). Finishes Wren's level-preview work: Wren (a Grok bot,
# one of Andrei's collaborators) wrote the original render.py; recovered 2026-10-31.
# Uses Coda's Blender-Sith fork (v1.0.1-coda.1), based on Blender-Sith by Crt Vavros (smlu).
# SPDX-License-Identifier: GPL-3.0-or-later
"""Find level resources the way the engine does and record where each one came from.

Lookup is path-major, like OpenJKDF2's ``sithMaterial`` search path ``"mat;3do\\mat"``: for each
candidate path in order, every source is tried in precedence order and the first hit wins. Only
when no candidate path matches does a basename match across all sources apply, and that fallback
is reported. Wren's script searched fixed folders (episode first, then retail ``RES2``); this
generalises it to any number of archives and loose folders.
"""
from __future__ import annotations

import hashlib
import os
from dataclasses import dataclass, field
from pathlib import Path

from .gob import GobArchive, GobError, is_archive_path, normalize

KIND_PATHS = {
    "jkl": ("jkl/{}",),
    "mat": ("mat/{}", "3do/mat/{}"),
    "cmp": ("misc/cmp/{}",),
    "3do": ("3do/{}",),
}
LOOSE_INDEX_LIMIT = 200000


class CacheError(ValueError):
    """A cache path would leave the extraction cache, or ``--cache`` overlaps an input folder."""


class LooseSource:
    """A folder laid out like a GOB (``mat/``, ``3do/``, ``misc/cmp/`` ...); matching is case-insensitive."""

    kind = "dir"

    def __init__(self, root):
        self.path = Path(root).resolve()
        if not self.path.is_dir():
            raise GobError(f"Resource folder {root} does not exist.")
        self.files: dict[str, Path] = {}
        self.duplicates: list[str] = []
        self.truncated = False
        count = 0
        for current, dirs, names in os.walk(self.path, followlinks=False):
            dirs.sort()
            for name in sorted(names):
                full = Path(current) / name
                if not full.is_file():
                    continue
                count += 1
                if count > LOOSE_INDEX_LIMIT:
                    self.truncated = True
                    return
                key = normalize(full.relative_to(self.path).as_posix())
                if key in self.files:
                    self.duplicates.append(full.relative_to(self.path).as_posix())
                    continue
                self.files[key] = full
        self.order = list(self.files)

    def __contains__(self, key: str) -> bool:
        return key in self.files

    def raw_name(self, key: str) -> str:
        return self.files[key].relative_to(self.path).as_posix()

    def read(self, key: str) -> bytes:
        return self.files[key].read_bytes()

    def length(self, key: str) -> int:
        return self.files[key].stat().st_size

    def names(self, suffix: str = "") -> list[str]:
        return [key for key in self.order if key.endswith(suffix.lower())]

    @property
    def sha256(self):
        return None

    @property
    def entries(self):
        return self.files


class ArchiveSource:
    """A GOB/GOO archive used as a resource source."""

    kind = "gob"

    def __init__(self, path):
        self.archive = GobArchive(path)
        self.path = self.archive.path
        self.duplicates = self.archive.duplicates
        self.truncated = False

    def __contains__(self, key: str) -> bool:
        return key in self.archive

    def raw_name(self, key: str) -> str:
        return self.archive.raw_name(key)

    def read(self, key: str) -> bytes:
        return self.archive.read(key)

    def length(self, key: str) -> int:
        return self.archive.length(key)

    def names(self, suffix: str = "") -> list[str]:
        return self.archive.names(suffix)

    @property
    def sha256(self):
        return self.archive.sha256

    @property
    def entries(self):
        return self.archive.entries


def open_source(path):
    """A loose folder or an archive (``.gob``/``.goo``) as a source."""
    if is_archive_path(path) and not Path(path).is_dir():
        return ArchiveSource(path)
    return LooseSource(path)


@dataclass
class Resolved:
    requested: str
    kind: str
    source_index: int
    source: str
    entry: str
    key: str
    data: bytes = field(repr=False)
    sha256: str
    by_basename: bool = False
    notes: list = field(default_factory=list)
    shadowed: list = field(default_factory=list)
    report: dict | None = field(default=None, repr=False)   # the provenance row kept by the resolver

    def note(self, text: str):
        """Add a note to this resource's provenance row (for example a MAT refusal or mip fallback)."""
        self.notes.append(text)
        if self.report is not None and text not in self.report["notes"]:
            self.report["notes"].append(text)

    def record(self) -> dict:
        out = {"requested": self.requested, "kind": self.kind, "source": self.source,
               "entry": self.entry, "bytes": len(self.data), "sha256": self.sha256,
               "notes": list(self.notes)}
        if self.shadowed:
            out["shadowed"] = list(self.shadowed)
        return out


class Resolver:
    """Path-major resource lookup with provenance, warnings and an extraction cache."""

    def __init__(self, sources, cache_dir=None):
        self.sources = list(sources)
        self.cache_dir = Path(cache_dir) if cache_dir is not None else None
        self.records: list[dict] = []
        self.warnings: list[str] = []
        self.missing: dict[str, list[str]] = {}
        self._cache: dict[tuple[str, str], Resolved | None] = {}
        self._basenames: list[dict[str, list[str]]] | None = None
        for index, source in enumerate(self.sources):
            for name in source.duplicates[:20]:
                self.warnings.append(f"{source.path} lists '{name}' more than once; the first entry is used.")
            if source.truncated:
                self.warnings.append(f"{source.path} has more than {LOOSE_INDEX_LIMIT} files; only the first were indexed.")

    # -- lookup -------------------------------------------------------------------------------
    @staticmethod
    def candidates(kind: str, name: str) -> list[str]:
        key = normalize(name).strip().lstrip("/")
        while key.startswith("./"):
            key = key[2:]
        out = [pattern.format(key) for pattern in KIND_PATHS.get(kind, ())]
        if key not in out:
            out.append(key)
        return out

    def _basename_index(self):
        if self._basenames is None:
            self._basenames = []
            for source in self.sources:
                table: dict[str, list[str]] = {}
                for key in source.names():
                    table.setdefault(key.rsplit("/", 1)[-1], []).append(key)
                self._basenames.append(table)
        return self._basenames

    def _label(self, index: int) -> str:
        return str(self.sources[index].path)

    def resolve(self, kind: str, name: str) -> Resolved | None:
        """Find ``name`` of ``kind`` (``jkl``, ``mat``, ``cmp``, ``3do``); None when no source has it."""
        requested = name.strip()
        cache_key = (kind, normalize(requested))
        if cache_key in self._cache:
            return self._cache[cache_key]
        result = None
        if requested:
            candidates = self.candidates(kind, requested)
            hit = None
            for candidate in candidates:
                for index, source in enumerate(self.sources):
                    if candidate in source:
                        hit = (index, candidate)
                        break
                if hit:
                    break
            by_basename = False
            others: list[tuple[int, str]] = []
            if hit is None:
                base = candidates[-1].rsplit("/", 1)[-1]
                matches = [(index, key) for index, table in enumerate(self._basename_index())
                           for key in table.get(base, [])]
                if matches:
                    hit, by_basename, others = matches[0], True, matches[1:]
            else:
                others = [(index, candidate) for candidate in candidates for index, source in enumerate(self.sources)
                          if candidate in source and (index, candidate) != hit]
            if hit is not None:
                index, key = hit
                source = self.sources[index]
                data = source.read(key)
                digest = hashlib.sha256(data).hexdigest()
                result = Resolved(requested, kind, index, self._label(index), source.raw_name(key), key, data, digest,
                                  by_basename=by_basename)
                if by_basename:
                    text = f"{kind} '{requested}' resolved by basename to {self._label(index)}:{source.raw_name(key)}"
                    if others:
                        text += " (ambiguous: also " + ", ".join(
                            f"{self._label(i)}:{self.sources[i].raw_name(k)}" for i, k in others[:5]) + ")"
                        result.notes.append("ambiguous")
                    result.notes.append("resolved by basename")
                    self.warnings.append(text)
                for other_index, other_key in others:
                    other = self.sources[other_index]
                    other_data = other.read(other_key)
                    other_digest = hashlib.sha256(other_data).hexdigest()
                    if other_digest != digest:
                        result.shadowed.append({"source": self._label(other_index), "entry": other.raw_name(other_key),
                                                "sha256": other_digest, "bytes": len(other_data)})
                if result.shadowed:
                    result.notes.append(f"shadows {len(result.shadowed)} different cop{'y' if len(result.shadowed) == 1 else 'ies'}")
                result.report = result.record()
                self.records.append(result.report)
        if result is None:
            names = self.missing.setdefault(kind, [])
            if requested not in names:
                names.append(requested)
        self._cache[cache_key] = result
        return result

    # -- extraction cache ---------------------------------------------------------------------
    def cache_path(self, *parts: str) -> Path:
        """``<cache>/<parts...>``, refused when a part is ``.``/``..`` or the result leaves the cache.

        Entry names come from archives and level files, which are untrusted: a GOB row such as
        ``3do\\..\\..\\x.3do`` must never place a file outside the cache.
        """
        if self.cache_dir is None:
            raise RuntimeError("The resolver has no cache directory.")
        clean = [part for part in parts if part != ""]
        if not clean or any(part in (".", "..") for part in clean):
            raise CacheError(f"Refusing the cache path '{'/'.join(parts)}': it has '.' or '..' segments.")
        root = os.path.abspath(self.cache_dir)
        target = os.path.abspath(os.path.join(root, *clean))
        if target == root or os.path.commonpath([root, target]) != root:
            raise CacheError(f"Refusing to write {target}: it lies outside the extraction cache {root}.")
        return Path(target)

    def extract(self, resolved: Resolved, data: bytes | None = None) -> Path:
        """Write a resolved raw file (3DO, CMP) to ``<cache>/src/<sourceIndex>/<entry path>``."""
        target = self.cache_path("src", str(resolved.source_index), *resolved.key.split("/"))
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(resolved.data if data is None else data)
        return target

    def material_path(self, name: str) -> Path:
        """Where the normalised copy of MAT ``name`` lives: ``<cache>/mat/<lowercase basename>``."""
        base = normalize(name).rsplit("/", 1)[-1].strip()
        if base in ("", ".", ".."):
            raise CacheError(f"Refusing the MAT name '{name}': it names no file.")
        return self.cache_path("mat", base)

    def source_report(self) -> list[dict]:
        out = []
        for source in self.sources:
            item = {"path": str(source.path), "kind": source.kind, "entries": len(source.entries)}
            if source.kind == "gob":
                item["sha256"] = source.sha256
            out.append(item)
        return out


class LevelLookupError(LookupError):
    def __init__(self, message, candidates=()):
        super().__init__(message)
        self.candidates = list(candidates)


def _contains(outer: Path, inner: Path) -> bool:
    """True when ``inner`` is ``outer`` or lies inside it (both absolute)."""
    try:
        inner.relative_to(outer)
        return True
    except ValueError:
        return False


def protected_roots(level_input, sith_package=None) -> list:
    """Folders the extraction cache must stay out of: every loose source, every archive's folder, a
    loose JKL's folder and Blender-Sith's (Coda's) folder. Returns ``[(label, resolved Path)]``."""
    roots = []
    for source in level_input.sources:
        if source.kind == "dir":
            roots.append(("the resource folder", Path(source.path).resolve()))
        else:
            roots.append((f"the folder of {Path(source.path).name}", Path(source.path).resolve().parent))
    if level_input.origin.get("kind") == "loose":
        roots.append(("the level's folder", Path(level_input.origin["source"]).resolve().parent))
    if sith_package:
        roots.append(("Blender-Sith's folder", Path(sith_package).resolve().parent))
    return roots


def check_cache_dir(cache, roots) -> Path:
    """Resolve ``--cache`` and refuse it unless it is a new or empty folder outside every input.

    The cache receives extracted and rewritten (normalised) files. A cache that is, contains or lies
    inside a source would overwrite or add files there (SPEC: sources are never written), so it is
    refused, as is an existing cache that already holds files.
    """
    path = Path(cache).expanduser().resolve()
    for label, root in roots:
        if _contains(root, path) or _contains(path, root):
            raise CacheError(f"--cache {path} overlaps {label} ({root}); the cache must be a new folder "
                             "outside every input.")
    if path.exists() or path.is_symlink():
        if not path.is_dir():
            raise CacheError(f"--cache {path} exists and is not a folder.")
        if any(path.iterdir()):
            raise CacheError(f"--cache {path} is not empty; give a new or empty folder.")
    return path


def find_level_entry(sources, spec: str) -> tuple[int, str]:
    """Locate ``--jkl`` inside the sources: exact path, then ``jkl/<spec>``, then a unique basename."""
    key = normalize(spec).strip().lstrip("/")
    for candidate in (key, "jkl/" + key):
        for index, source in enumerate(sources):
            if candidate in source:
                return index, candidate
    base = key.rsplit("/", 1)[-1]
    matches = [(index, entry) for index, source in enumerate(sources)
               for entry in source.names(".jkl") if entry.rsplit("/", 1)[-1] == base]
    if len(matches) == 1:
        return matches[0]
    labels = [f"{sources[i].path}:{sources[i].raw_name(k)}" for i, k in matches]
    if matches:
        raise LevelLookupError(f"Level '{spec}' is ambiguous; candidates: " + "; ".join(labels), labels)
    available = [f"{source.path}:{source.raw_name(k)}" for source in sources for k in source.names(".jkl")]
    raise LevelLookupError(f"Level '{spec}' was not found in the given archives or folders."
                           + (" Levels available: " + "; ".join(available[:40]) if available else ""), available)


def level_entries(source) -> list[str]:
    """``jkl/*.jkl`` entries of one source, alphabetically (for the GOB import operator)."""
    return sorted(key for key in source.names(".jkl"))


@dataclass
class LevelInput:
    sources: list
    data: bytes = field(repr=False)
    origin: dict
    stem: str


def open_level(jkl, archives=(), resource_dirs=(), level_archive=None) -> LevelInput:
    """Sources in precedence order and the level's bytes.

    Order: the level's own source first (a loose JKL's tree when its folder is named ``jkl``, or
    ``level_archive``), then ``resource_dirs``, then ``archives``. A ``jkl`` that is not an existing
    file names an entry inside the sources (``find_level_entry``).
    """
    sources = []
    path = Path(jkl) if jkl else None
    loose = path is not None and path.is_file() and not is_archive_path(path)
    if loose and path.resolve().parent.name.lower() == "jkl":
        sources.append(LooseSource(path.resolve().parent.parent))
    if level_archive is not None:
        sources.append(open_source(level_archive))
    sources += [LooseSource(d) for d in resource_dirs]
    sources += [open_source(a) for a in archives]
    if loose:
        data = path.read_bytes()
        origin = {"kind": "loose", "source": str(path.resolve()), "entry": None,
                  "sha256": hashlib.sha256(data).hexdigest()}
        return LevelInput(sources, data, origin, path.stem)
    if not jkl:
        raise LevelLookupError("No level was named.")
    index, key = find_level_entry(sources, jkl)
    source = sources[index]
    data = source.read(key)
    origin = {"kind": "archive" if source.kind == "gob" else "folder", "source": str(source.path),
              "entry": source.raw_name(key), "sha256": hashlib.sha256(data).hexdigest()}
    return LevelInput(sources, data, origin, Path(key).stem)
