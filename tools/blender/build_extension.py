# Package jk_level_importer/ as a Blender extension zip (stdlib only; the zip is not committed).
# Part of SED-MotS (Red's claude branch). Finishes Wren's level-preview work: Wren (a Grok bot,
# one of Andrei's collaborators) wrote the original render.py; recovered 2026-10-31.
# Uses Coda's Blender-Sith fork (v1.0.1-coda.1), based on Blender-Sith by Crt Vavros (smlu).
# SPDX-License-Identifier: GPL-3.0-or-later
"""Write ``<out>/jk_level_importer-<version>.zip`` with the manifest at the zip root.

    python3 tools/blender/build_extension.py --out /tmp/ext

Install it in Blender 5 with Edit > Preferences > Get Extensions > Install from Disk, next to Coda's
Blender-Sith extension (which the importer needs), then use File > Import > JK level (.jkl / .gob).
"""
from __future__ import annotations

import argparse
import hashlib
import re
import sys
import zipfile
from pathlib import Path

PACKAGE = Path(__file__).resolve().parent / "jk_level_importer"
LICENSE = Path(__file__).resolve().parents[2] / "LICENSE"


def version() -> str:
    text = (PACKAGE / "blender_manifest.toml").read_text(encoding="utf-8")
    match = re.search(r'^version\s*=\s*"([^"]+)"', text, re.M)
    if not match:
        raise SystemExit("blender_manifest.toml has no version")
    return match.group(1)


def files():
    for path in sorted(PACKAGE.rglob("*")):
        if path.is_file() and "__pycache__" not in path.parts and path.suffix not in (".pyc", ".pyo"):
            yield path


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--out", required=True, help="output folder")
    parser.add_argument("--overwrite", action="store_true")
    args = parser.parse_args(argv)
    out = Path(args.out).resolve()
    target = out / f"jk_level_importer-{version()}.zip"
    if target.exists() and not args.overwrite:
        print(f"error: {target} exists (pass --overwrite)", file=sys.stderr)
        return 2
    out.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as archive:
        for path in files():
            archive.write(path, path.relative_to(PACKAGE).as_posix())
        if LICENSE.is_file():
            archive.write(LICENSE, "LICENSE")
    digest = hashlib.sha256(target.read_bytes()).hexdigest()
    print(f"{target} sha256 {digest}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
