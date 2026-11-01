# Locate, load and register Coda's Blender-Sith fork for the JK level importer.
# Part of SED-MotS (Red's claude branch). Finishes Wren's level-preview work: Wren (a Grok bot,
# one of Andrei's collaborators) wrote the original render.py; recovered 2026-10-31.
# Uses Coda's Blender-Sith fork (v1.0.1-coda.1), based on Blender-Sith by Crt Vavros (smlu).
# SPDX-License-Identifier: GPL-3.0-or-later
"""Blender-Sith (GPL-3.0-or-later, Crt Vavros / smlu; Coda's fork v1.0.1-coda.1) loads 3DO models and
MAT/CMP files. This importer never copies or edits it: it is found at ``--sith PATH`` (a folder that
contains ``sith/``), ``$BLENDER_SITH_PATH``, an already imported module, or an enabled extension.
"""
from __future__ import annotations

import importlib
import importlib.util
import os
import re
import sys
from dataclasses import dataclass
from pathlib import Path


class SithMissing(RuntimeError):
    pass


@dataclass
class SithBridge:
    module: object
    name: str
    path: str
    origin: str
    version: str
    maintainer: str
    fork_release: str
    import3do: object
    importMat: object
    ColorMap: object
    load3do: object

    def report(self) -> dict:
        return {"path": self.path, "module": self.name, "origin": self.origin, "version": self.version,
                "forkRelease": self.fork_release, "maintainer": self.maintainer}


_LOADED: SithBridge | None = None


def _package_dir(path: Path) -> Path:
    path = Path(path).expanduser().resolve()
    if (path / "sith" / "__init__.py").is_file():
        return path / "sith"
    if (path / "__init__.py").is_file() and path.name == "sith":
        return path
    raise SithMissing(f"No Blender-Sith package (sith/__init__.py) under {path}.")


def _usable(module) -> bool:
    return module is not None and hasattr(module, "register") and Path(getattr(module, "__file__", "") or "").name == "__init__.py" \
        and (Path(module.__file__).parent / "model" / "model3doImporter.py").is_file()


def _manifest(package: Path):
    version, maintainer, release = "unknown", "unknown", ""
    manifest = package / "blender_manifest.toml"
    if manifest.is_file():
        for line in manifest.read_text(encoding="utf-8", errors="replace").splitlines():
            key, _, value = line.partition("=")
            key = key.strip()
            if key == "version":
                version = value.strip().strip("\"'")
            elif key == "maintainer":
                maintainer = value.strip().strip("\"'")
    fork = package.parent / "FORK.md"
    if fork.is_file():
        match = re.search(r"Fork release identifier:\s*`([^`]+)`", fork.read_text(encoding="utf-8", errors="replace"))
        if match:
            release = match.group(1)
    return version, maintainer, release


def _from_path(package: Path):
    existing = sys.modules.get("sith")
    if _usable(existing) and Path(existing.__file__).parent.resolve() == package.resolve():
        return existing, "sith"
    spec = importlib.util.spec_from_file_location("sith", package / "__init__.py",
                                                  submodule_search_locations=[str(package)])
    module = importlib.util.module_from_spec(spec)
    previous = {name: sys.modules[name] for name in list(sys.modules) if name == "sith" or name.startswith("sith.")}
    for name in previous:
        del sys.modules[name]
    sys.modules["sith"] = module
    try:
        spec.loader.exec_module(module)
    except Exception:
        del sys.modules["sith"]
        sys.modules.update(previous)
        raise
    return module, "sith"


def _find_loaded():
    candidate = sys.modules.get("sith")
    if _usable(candidate):
        return candidate, "sith"
    for name, module in list(sys.modules.items()):
        if name.endswith(".sith") and _usable(module):
            return module, name
    try:
        import addon_utils  # Blender only
        for module in addon_utils.modules():
            name = module.__name__
            if (name == "sith" or name.endswith(".sith")) and addon_utils.check(name)[1]:
                loaded = importlib.import_module(name)
                if _usable(loaded):
                    return loaded, name
    except Exception:
        pass
    return None, None


def _register(module):
    """Call Blender-Sith's ``register()`` unless its classes and Object properties are already present."""
    try:
        import bpy
        if hasattr(bpy.types.Object, "sith_model3do_hnode_idx") and hasattr(bpy.types.WindowManager, "sith_mesh3do_face_layer"):
            return
    except ImportError:
        pass
    try:
        module.register()
    except (ValueError, RuntimeError) as exc:
        if "already" not in str(exc).lower():
            raise


def load(path=None) -> SithBridge:
    """Load and register Blender-Sith once; raise SithMissing with a clear message when it is absent."""
    global _LOADED
    requested = path or os.environ.get("BLENDER_SITH_PATH") or None
    keep = sys.dont_write_bytecode
    sys.dont_write_bytecode = True          # do not write __pycache__ into Blender-Sith's (Coda's) folder
    try:
        return _load(requested)
    finally:
        sys.dont_write_bytecode = keep


def _load(requested) -> SithBridge:
    global _LOADED
    if _LOADED is not None and (requested is None or Path(_LOADED.path) == _package_dir(Path(requested))):
        _register(_LOADED.module)        # a factory reset may have dropped the classes and properties
        return _LOADED
    if requested:
        package = _package_dir(Path(requested))
        module, name = _from_path(package)
        origin = "--sith/BLENDER_SITH_PATH"
    else:
        module, name = _find_loaded()
        if module is None:
            raise SithMissing("Blender-Sith was not found: pass --sith PATH (the folder containing sith/), set "
                              "BLENDER_SITH_PATH, or install and enable Coda's Blender-Sith extension.")
        package = Path(module.__file__).parent
        origin = "loaded module" if name == "sith" else f"extension {name}"
    _register(module)
    importer = importlib.import_module(name + ".model.model3doImporter")
    loader = importlib.import_module(name + ".model.model3doLoader")
    material = importlib.import_module(name + ".material")
    version, maintainer, release = _manifest(package)
    _LOADED = SithBridge(module, name, str(package), origin, version, maintainer, release,
                         importer.import3do, material.importMat, material.ColorMap, loader.load3do)
    return _LOADED
