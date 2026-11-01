# Tolerant JK/MotS JKL text parser for the level importer (stdlib only).
# Part of SED-MotS (Red's claude branch). Finishes Wren's level-preview work: Wren (a Grok bot,
# one of Andrei's collaborators) wrote the original render.py; recovered 2026-10-31.
# Uses Coda's Blender-Sith fork (v1.0.1-coda.1), based on Blender-Sith by Crt Vavros (smlu).
# SPDX-License-Identifier: GPL-3.0-or-later
"""Read the parts of a JKL that a preview needs: geometry, light, sectors, templates and things.

Wren's ``render.py`` read fixed-count lists with ``startswith``; this parser keeps its tolerance
(it never raises on a damaged line, it records a warning with the line number) but follows the
engine's list rules: ``materials``, ``colormaps``, ``things``, ``templates`` and similar headings
give a capacity, so their lists end at ``end`` or the next heading; ``vertices``,
``texture vertices``, ``adjoins`` and ``surfaces`` give exact counts, and the surface normals
follow the surfaces without a heading. Unparseable thing parameters (such as Caesar's Palace's
stray-parenthesis ``frame=``) are kept verbatim and ignored.
"""
from __future__ import annotations

import re
from dataclasses import dataclass, field
from pathlib import Path

SECTION_RE = re.compile(r"^section\s*:\s*(.*?)\s*$", re.I)
WORLD_RE = re.compile(r"^world\s+(.+?)\s+(-?\d+)\s*$", re.I)
INDEX_RE = re.compile(r"^(-?\d+)\s*:\s*(.*)$")
EXACT_LISTS = ("vertices", "texture vertices", "adjoins", "surfaces")

SURF_SKY_HORIZON = 0x200
SURF_SKY_CEILING = 0x400
SURF_SKY = SURF_SKY_HORIZON | SURF_SKY_CEILING
THING_NOT_DRAWN = 0x2 | 0x10 | 0x80000


@dataclass
class Header:
    version: int = 1
    gravity: float = 0.0
    ceiling_sky_z: float = 0.0
    horizon_distance: float = 0.0
    horizon_ppr: float = 768.0
    horizon_offset: tuple = (0.0, 0.0)
    ceiling_offset: tuple = (0.0, 0.0)


@dataclass
class Surface:
    index: int
    material: int = -1
    surfflags: int = 0
    faceflags: int = 0
    geo: int = 0
    light: int = 0
    tex: int = 0
    adjoin: int = -1
    extralight: float = 0.0
    verts: list = field(default_factory=list)
    texverts: list = field(default_factory=list)
    mono: list = field(default_factory=list)
    rgb: list = field(default_factory=list)
    has_rgb: bool = False
    normal: tuple | None = None
    sector: int = -1
    line: int = 0
    error: str | None = None

    @property
    def is_sky(self) -> bool:
        return bool(self.surfflags & SURF_SKY)


@dataclass
class Sector:
    index: int
    flags: int = 0
    ambient: float = 0.0
    extra: float = 0.0
    colormap: int = 0
    tint: tuple = (0.0, 0.0, 0.0)
    first_surface: int = 0
    surface_count: int = 0
    line: int = 0


@dataclass
class Template:
    name: str
    parent: str
    params: dict
    line: int = 0


@dataclass
class Thing:
    index: int
    template: str
    name: str
    position: tuple
    pyr: tuple
    sector: int
    params: dict
    raw: list = field(default_factory=list)
    line: int = 0


@dataclass
class Level:
    name: str = ""
    kind: str = "jk"
    header: Header = field(default_factory=Header)
    materials: list = field(default_factory=list)      # (name, xTile, yTile)
    colormaps: list = field(default_factory=list)
    vertices: list = field(default_factory=list)
    texverts: list = field(default_factory=list)
    surfaces: list = field(default_factory=list)
    normals: list = field(default_factory=list)
    sectors: list = field(default_factory=list)
    templates: dict = field(default_factory=dict)      # lower-case name -> Template
    things: list = field(default_factory=list)
    warnings: list = field(default_factory=list)


def parse_hex(token: str) -> int:
    """JKL flags are hexadecimal with or without ``0x`` (SED JklReader.ParseHex)."""
    text = token.strip()
    if text[:2].lower() == "0x":
        text = text[2:]
    return int(text, 16)


def _float(token: str) -> float:
    value = float(token)
    if value != value or value in (float("inf"), float("-inf")):
        raise ValueError(f"non-finite number {token!r}")
    return value


def _lines(text: str):
    out = []
    for number, raw in enumerate(text.splitlines(), start=1):
        line = raw.split("#", 1)[0].strip()
        if line:
            out.append((number, line))
    return out


def _heading(text: str):
    if SECTION_RE.match(text):
        return True
    return bool(WORLD_RE.match(text))


def _is_end(text: str) -> bool:
    return text.lower() == "end"


def _params(tokens, line, warnings, what):
    params, raw = {}, []
    for token in tokens:
        if "=" in token:
            key, value = token.split("=", 1)
            params[key.lower()] = value
        else:
            raw.append(token)
    if raw:
        warnings.append(f"line {line}: {what} has tokens without '=' kept verbatim: {' '.join(raw[:4])}")
    return params, raw


def parse_bytes(data: bytes, name: str = "") -> Level:
    """Parse JKL bytes (decoded as Latin-1, so stray high bytes never fail)."""
    return parse(data.decode("latin-1"), name)


def parse_file(path) -> Level:
    path = Path(path)
    return parse_bytes(path.read_bytes(), path.stem)


def parse(text: str, name: str = "") -> Level:
    level = Level(name=name)
    lines = _lines(text)
    warn = level.warnings.append
    section = ""
    i = 0
    total = len(lines)

    def capacity_list(start):
        rows = []
        j = start
        while j < total:
            number, line = lines[j]
            if _is_end(line):
                return rows, j + 1
            if _heading(line):
                return rows, j
            rows.append((number, line))
            j += 1
        return rows, j

    def exact_list(start, count, what):
        rows = []
        j = start
        while j < total and len(rows) < count:
            number, line = lines[j]
            match = INDEX_RE.match(line)
            if not match:
                break
            rows.append((number, match.group(2)))
            j += 1
        if len(rows) < count:
            where = lines[j][0] if j < total else (lines[-1][0] if lines else 0)
            warn(f"line {where}: {what} lists {count} entries but only {len(rows)} were read")
        return rows, j

    while i < total:
        number, line = lines[i]
        match = SECTION_RE.match(line)
        if match:
            section = match.group(1).upper()
            i += 1
            continue
        if section == "HEADER":
            _header_line(level, number, line)
            i += 1
            continue
        match = WORLD_RE.match(line)
        if not match:
            i += 1
            continue
        heading = " ".join(match.group(1).lower().split())
        count = int(match.group(2))
        i += 1
        if count < 0:
            warn(f"line {number}: negative count in '{line}'")
            count = 0
        if heading == "sectors":
            i = _sectors(level, lines, i)
            continue
        if heading in EXACT_LISTS:
            rows, i = exact_list(i, count, f"World {heading}")
            if heading == "vertices":
                for row_no, body in rows:
                    level.vertices.append(_vector(body, 3, row_no, warn, "vertex"))
            elif heading == "texture vertices":
                for row_no, body in rows:
                    level.texverts.append(_vector(body, 2, row_no, warn, "texture vertex"))
            elif heading == "surfaces":
                for row_no, body in rows:
                    level.surfaces.append(_surface(len(level.surfaces), body, row_no, warn))
                normals, i = exact_list(i, len(rows), "Surface normals")
                for row_no, body in normals:
                    level.normals.append(_vector(body, 3, row_no, warn, "surface normal"))
                for index, normal in enumerate(level.normals[:len(level.surfaces)]):
                    level.surfaces[index].normal = normal
            continue
        rows, i = capacity_list(i)
        if heading == "materials":
            for row_no, line_text in rows:
                body = INDEX_RE.match(line_text)
                tokens = (body.group(2) if body else line_text).split()
                if not tokens:
                    continue
                if body and int(body.group(1)) != len(level.materials):
                    warn(f"line {row_no}: material index {body.group(1)} read as {len(level.materials)}")
                try:
                    tiles = (_float(tokens[1]), _float(tokens[2])) if len(tokens) >= 3 else (1.0, 1.0)
                except ValueError:
                    tiles = (1.0, 1.0)
                level.materials.append((tokens[0], tiles[0], tiles[1]))
        elif heading == "colormaps":
            for row_no, line_text in rows:
                body = INDEX_RE.match(line_text)
                tokens = (body.group(2) if body else line_text).split()
                if tokens:
                    level.colormaps.append(tokens[0])
        elif heading == "templates":
            for row_no, line_text in rows:
                tokens = line_text.split()
                if len(tokens) < 2:
                    warn(f"line {row_no}: template row has no parent: {line_text[:60]}")
                    continue
                params, _ = _params(tokens[2:], row_no, warn, f"template '{tokens[0]}'")
                key = tokens[0].lower()
                if key in level.templates:
                    warn(f"line {row_no}: template '{tokens[0]}' is defined again; the first definition is kept")
                    continue
                level.templates[key] = Template(tokens[0], tokens[1], params, row_no)
        elif heading == "things":
            for row_no, line_text in rows:
                _thing(level, row_no, line_text, warn)
    for sector in level.sectors:
        for index in range(sector.first_surface, sector.first_surface + sector.surface_count):
            if 0 <= index < len(level.surfaces):
                level.surfaces[index].sector = sector.index
            else:
                warn(f"line {sector.line}: sector {sector.index} references missing surface {index}")
                break
    if any(surface.has_rgb for surface in level.surfaces):
        level.kind = "mots"
    return level


def _header_line(level, number, line):
    lower = " ".join(line.lower().split())
    tokens = line.split()
    header = level.header
    try:
        if lower.startswith("version "):
            header.version = int(tokens[1])
        elif lower.startswith("world gravity "):
            header.gravity = _float(tokens[2])
        elif lower.startswith("ceiling sky z "):
            header.ceiling_sky_z = _float(tokens[3])
        elif lower.startswith("horizon distance "):
            header.horizon_distance = _float(tokens[2])
        elif lower.startswith("horizon pixels per rev "):
            header.horizon_ppr = _float(tokens[4])
        elif lower.startswith("horizon sky offset "):
            header.horizon_offset = (_float(tokens[3]), _float(tokens[4]))
        elif lower.startswith("ceiling sky offset "):
            header.ceiling_offset = (_float(tokens[3]), _float(tokens[4]))
    except (IndexError, ValueError):
        level.warnings.append(f"line {number}: unreadable header line '{line[:60]}'")


def _vector(body, size, number, warn, what):
    tokens = body.split()
    try:
        values = tuple(_float(token) for token in tokens[:size])
        if len(values) < size:
            raise ValueError("too few values")
        return values
    except ValueError as exc:
        warn(f"line {number}: unreadable {what} ({exc}); using zeros")
        return (0.0,) * size


def _surface(index, body, number, warn):
    surface = Surface(index=index, line=number)
    tokens = body.replace(",", " ").split()
    try:
        surface.material = int(tokens[0])
        surface.surfflags = parse_hex(tokens[1])
        surface.faceflags = parse_hex(tokens[2])
        surface.geo = int(tokens[3])
        surface.light = int(tokens[4])
        surface.tex = int(tokens[5])
        surface.adjoin = int(tokens[6])
        surface.extralight = _float(tokens[7])
        count = int(tokens[8])
        if count < 0 or 9 + 2 * count > len(tokens):
            raise ValueError(f"vertex count {count} does not fit the row")
        for k in range(count):
            surface.verts.append(int(tokens[9 + 2 * k]))
            surface.texverts.append(int(tokens[10 + 2 * k]))
        values = [_float(token) for token in tokens[9 + 2 * count:]]
    except (IndexError, ValueError) as exc:
        warn(f"line {number}: surface {index} is unreadable ({exc}); it is skipped")
        surface.error = str(exc)
        surface.geo = 0
        surface.verts, surface.texverts = [], []
        return surface
    count = len(surface.verts)
    if len(values) == 4 * count and count:
        surface.has_rgb = True
        for k in range(count):
            mono, r, g, b = values[4 * k:4 * k + 4]
            surface.mono.append(mono)
            surface.rgb.append((r, g, b))
    else:
        if len(values) != count:
            warn(f"line {number}: surface {index} has {len(values)} light values for {count} corners")
        values = (values + [0.0] * count)[:count]
        surface.mono = list(values)
        surface.rgb = [(v, v, v) for v in values]
    return surface


def _sectors(level, lines, i):
    total = len(lines)
    sector = None
    while i < total:
        number, line = lines[i]
        if SECTION_RE.match(line):
            return i
        tokens = line.split()
        word = tokens[0].upper()
        try:
            if word == "SECTOR" and len(tokens) >= 2:
                sector = Sector(index=int(tokens[1]), line=number)
                if sector.index != len(level.sectors):
                    level.warnings.append(f"line {number}: sector {sector.index} read as {len(level.sectors)}")
                    sector.index = len(level.sectors)
                level.sectors.append(sector)
            elif sector is None:
                pass
            elif word == "FLAGS":
                sector.flags = parse_hex(tokens[1])
            elif word == "AMBIENT" and len(tokens) >= 3:
                sector.ambient = _float(tokens[2])
            elif word == "EXTRA" and len(tokens) >= 3:
                sector.extra = _float(tokens[2])
            elif word == "COLORMAP":
                sector.colormap = int(tokens[1])
            elif word == "TINT" and len(tokens) >= 4:
                sector.tint = (_float(tokens[1]), _float(tokens[2]), _float(tokens[3]))
            elif word == "SURFACES" and len(tokens) >= 3:
                sector.first_surface, sector.surface_count = int(tokens[1]), int(tokens[2])
        except (IndexError, ValueError):
            level.warnings.append(f"line {number}: unreadable sector line '{line[:60]}'")
        i += 1
    return i


def _thing(level, number, line, warn):
    match = INDEX_RE.match(line)
    if not match:
        warn(f"line {number}: thing row without an index: {line[:60]}")
        return
    tokens = match.group(2).split()
    if len(tokens) < 9:
        warn(f"line {number}: thing {match.group(1)} has {len(tokens)} fields; at least 9 expected")
        return
    try:
        position = tuple(_float(t) for t in tokens[2:5])
        pyr = tuple(_float(t) for t in tokens[5:8])
        sector = int(tokens[8])
    except ValueError as exc:
        warn(f"line {number}: thing {match.group(1)} is unreadable ({exc}); it is skipped")
        return
    params, raw = _params(tokens[9:], number, warn, f"thing {match.group(1)}")
    level.things.append(Thing(int(match.group(1)), tokens[0], tokens[1], position, pyr, sector, params, raw, number))


def resolve_template(level: Level, name: str):
    """Merged parameters of a template chain (child keys override parent keys).

    Returns ``(params, chain, problem)``; ``problem`` names a missing template or a cycle.
    """
    chain, seen, problem = [], set(), None
    current = name.lower()
    while current and current != "none":
        if current in seen:
            problem = f"template cycle at '{current}'"
            break
        seen.add(current)
        template = level.templates.get(current)
        if template is None:
            problem = f"missing template '{current}'"
            break
        chain.append(template)
        current = template.parent.lower()
    params = {}
    for template in reversed(chain):
        params.update(template.params)
    return params, [template.name for template in chain], problem


def thing_params(level: Level, thing: Thing):
    """Template chain parameters overridden by the thing's own parameters."""
    params, chain, problem = resolve_template(level, thing.template)
    merged = dict(params)
    merged.update(thing.params)
    return merged, chain, problem


def thing_flags(params: dict) -> int:
    value = params.get("thingflags")
    if not value:
        return 0
    try:
        return parse_hex(value)
    except ValueError:
        return 0
