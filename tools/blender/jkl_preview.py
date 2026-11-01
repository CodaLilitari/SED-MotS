# Headless JK/MotS level preview: overhead and three-quarter PNGs plus a JSON report, from a JKL or GOB.
# Part of SED-MotS (Red's claude branch). Finishes Wren's level-preview work: Wren (a Grok bot,
# one of Andrei's collaborators) wrote the original render.py; recovered 2026-10-31.
# Uses Coda's Blender-Sith fork (v1.0.1-coda.1), based on Blender-Sith by Crt Vavros (smlu).
# SPDX-License-Identifier: GPL-3.0-or-later
"""Wren's level previews, finished and parameterised.

Run with Blender's Python module or with Blender itself:

    <python with bpy> tools/blender/jkl_preview.py --jkl jkl/kellwar.jkl --archive episode.gob \\
        --archive Res2.gob --archive Res1hi.gob --out previews --sith /path/to/blender-sith
    blender -b --factory-startup -P tools/blender/jkl_preview.py -- <the same arguments>

Exit codes: 0 done; 2 usage or input problem; 3 render failure; 4 --strict found a missing or refused
resource (the report is still written).
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import sys
import tempfile
import time
import traceback
from pathlib import Path

sys.dont_write_bytecode = True      # never leave __pycache__ in this repo or in Blender-Sith's folder
HERE = Path(__file__).resolve().parent
if str(HERE) not in sys.path:
    sys.path.insert(0, str(HERE))

TOOL_VERSION = "0.1.0"
CREDITS = ("Finishes Wren's level-preview work: Wren, a Grok bot and one of Andrei's AI collaborators, wrote the "
           "original Blender render.py (JKL geometry, model placement, backface-culled views, auto-framed overhead "
           "and three-quarter cameras). 3DO, MAT and CMP files are loaded by Coda's Blender-Sith fork "
           "(v1.0.1-coda.1, GPL-3.0-or-later), based on Blender-Sith by Crt Vavros (smlu).")
EXIT_USAGE, EXIT_RENDER, EXIT_STRICT = 2, 3, 4
WARNING_CAP = 300


class Usage(Exception):
    pass


def parse_args(argv):
    parser = argparse.ArgumentParser(
        prog="jkl_preview.py", description="Render Wren-style previews of a JK/MotS level (JKL file or GOB entry).")
    parser.add_argument("--jkl", required=True, help="a .jkl file, or an entry inside the archives (jkl/x.jkl or x.jkl)")
    parser.add_argument("--archive", action="append", default=[], help="GOB/GOO in precedence order (repeatable)")
    parser.add_argument("--resource-dir", action="append", default=[], help="loose folder laid out like a GOB (repeatable)")
    parser.add_argument("--out", required=True, help="output folder (created if needed; files are create-only)")
    parser.add_argument("--views", default="overhead,threequarter", help="comma-separated: overhead, threequarter")
    parser.add_argument("--light", choices=("flat", "baked"), default="flat")
    parser.add_argument("--framing", choices=("wren", "contain"), default="wren")
    parser.add_argument("--margin", type=float, default=0.025)
    parser.add_argument("--width", type=int, default=1280)
    parser.add_argument("--height", type=int, default=960)
    parser.add_argument("--sky", choices=("textured", "hide"), default="textured")
    parser.add_argument("--include-players", action="store_true")
    parser.add_argument("--include-hidden", action="store_true")
    parser.add_argument("--no-things", action="store_true")
    parser.add_argument("--engine", choices=("auto", "workbench", "eevee", "cycles"), default="auto")
    parser.add_argument("--sith", help="folder containing Blender-Sith's sith/ (default $BLENDER_SITH_PATH, then an "
                                       "installed extension)")
    parser.add_argument("--cache", help="keep extracted and normalised files here: a new or empty folder outside "
                                        "every input (default: a temporary folder, removed afterwards)")
    parser.add_argument("--blend", help="also save the scene as this .blend file")
    parser.add_argument("--report", help="report path (default OUT/<stem>_preview.json)")
    parser.add_argument("--strict", action="store_true", help="exit 4 when a MAT, 3DO or CMP is missing or refused")
    parser.add_argument("--overwrite", action="store_true", help="replace existing output files")
    args = parser.parse_args(argv)
    views = tuple(v.strip() for v in args.views.split(",") if v.strip())
    if not views or any(v not in ("overhead", "threequarter") for v in views) or len(set(views)) != len(views):
        parser.error("--views must list overhead and/or threequarter once each")
    args.views = views
    if not 0.0 <= args.margin <= 0.25:
        parser.error("--margin must be from 0 through 0.25")
    if not (16 <= args.width <= 8192 and 16 <= args.height <= 8192):
        parser.error("--width and --height must be from 16 through 8192")
    if args.light == "flat" and args.engine in ("eevee", "cycles"):
        parser.error("--light flat is Wren's Workbench look; use --light baked for eevee or cycles")
    if args.light == "baked" and args.engine == "workbench":
        parser.error("--light baked needs eevee or cycles (Workbench cannot multiply a texture by the light)")
    return args


def script_argv():
    return sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:]


def log(text):
    print(text, flush=True)


def main(argv=None) -> int:
    started = time.monotonic()
    args = parse_args(script_argv() if argv is None else argv)
    try:
        import bpy
    except ImportError:
        log("error: Blender's Python module (bpy) is required; run with Blender's Python or 'blender -b -P'.")
        return EXIT_USAGE
    from jk_level_importer import build, framing, jkl, render, resources, sith_bridge
    from jk_level_importer.gob import GobError

    out = Path(args.out).resolve()
    blender_mode = "blender -b -P" if "--" in sys.argv else "bpy module"
    log(f"Blender {bpy.app.version_string} ({blender_mode}, background={bpy.app.background})")

    try:
        level_input = resources.open_level(args.jkl, args.archive, args.resource_dir)
    except (GobError, resources.LevelLookupError, OSError) as exc:
        log(f"error: {exc}")
        return EXIT_USAGE
    stem = level_input.stem
    outputs = {view: out / f"{stem}_{view}.png" for view in args.views}
    report_path = Path(args.report).resolve() if args.report else out / f"{stem}_preview.json"
    planned = list(outputs.values()) + [report_path] + ([Path(args.blend).resolve()] if args.blend else [])
    existing = [str(p) for p in planned if p.exists()]
    if existing and not args.overwrite:
        log("error: refusing to overwrite existing outputs (pass --overwrite): " + ", ".join(existing))
        return EXIT_USAGE
    inputs = {Path(source.path).resolve() for source in level_input.sources if source.kind == "gob"}
    if level_input.origin.get("kind") == "loose":
        inputs.add(Path(level_input.origin["source"]).resolve())
    clashing = [str(p) for p in planned if p.resolve() in inputs]
    if clashing:
        log("error: an output path names an input file (sources are never written): " + ", ".join(clashing))
        return EXIT_USAGE

    level = jkl.parse_bytes(level_input.data, stem)
    log(f"Level {stem}: {len(level.surfaces)} surfaces, {len(level.sectors)} sectors, {len(level.things)} things, "
        f"{len(level.materials)} materials ({level.kind}); {len(level.warnings)} parser warnings")

    bpy.ops.wm.read_factory_settings(use_empty=True)
    sith_source = "--sith" if args.sith else ("BLENDER_SITH_PATH" if os.environ.get("BLENDER_SITH_PATH") else "installed")
    try:
        sith = sith_bridge.load(args.sith)
    except sith_bridge.SithMissing as exc:
        log(f"error: {exc}")
        return EXIT_USAGE
    log(f"Blender-Sith {sith.fork_release or sith.version} from {sith.path} ({sith_source}; {sith.origin})")

    temporary = None
    if args.cache:
        # The cache receives rewritten MATs and extracted files: never inside, over or around an input.
        try:
            cache = resources.check_cache_dir(args.cache, resources.protected_roots(level_input, sith.path))
        except resources.CacheError as exc:
            log(f"error: {exc}")
            return EXIT_USAGE
        cache.mkdir(parents=True, exist_ok=True)
    else:
        temporary = tempfile.mkdtemp(prefix="jkl_preview_")
        cache = Path(temporary)
    out.mkdir(parents=True, exist_ok=True)
    resolver = resources.Resolver(level_input.sources, cache)
    options = build.BuildOptions(light=args.light, sky=args.sky, include_things=not args.no_things,
                                 include_players=args.include_players, include_hidden=args.include_hidden,
                                 framing=args.framing, margin=args.margin, views=args.views,
                                 width=args.width, height=args.height)
    report = {"schemaVersion": 1, "tool": "tools/blender/jkl_preview.py", "version": TOOL_VERSION, "credits": CREDITS,
              "blender": {"version": bpy.app.version_string, "mode": blender_mode,
                          "binary": getattr(bpy.app, "binary_path", "") or None},
              "blenderSith": dict(sith.report(), selectedBy=sith_source),
              "level": dict(level_input.origin, name=stem, kind=level.kind),
              "sources": resolver.source_report(), "light": args.light, "framing": args.framing,
              "margin": args.margin, "options": {"sky": args.sky, "includePlayers": args.include_players,
                                                 "includeHidden": args.include_hidden, "things": not args.no_things,
                                                 "width": args.width, "height": args.height, "strict": args.strict}}
    status = 0
    try:
        builder = build.LevelBuilder(level, resolver, sith, options, log=log)
        try:
            builder.build()
            if builder.box is None:
                raise framing.FramingError("Level has no drawn world surfaces to frame.")
        except framing.FramingError as exc:
            log(f"error: {exc}")
            report["error"] = str(exc)
            status = EXIT_USAGE
        counts = builder.counts
        log("Counts: " + ", ".join(f"{k}={v}" for k, v in counts.items() if v))
        warnings = resolver.warnings + builder.warnings
        parser_warnings = level.warnings
        report.update({
            "masterColormap": builder.master, "resources": resolver.records, "counts": counts,
            "missing": {"materials": builder.missing["materials"], "models": builder.missing["models"],
                        "templates": builder.missing["templates"], "cmp": resolver.missing.get("cmp", [])},
            "warnings": warnings[:WARNING_CAP], "warningCount": len(warnings),
            "parserWarnings": parser_warnings[:WARNING_CAP], "parserWarningCount": len(parser_warnings),
            "notes": builder.notes,
            "bounds": {"min": list(builder.box.min), "max": list(builder.box.max)} if builder.box else None,
            "views": [], "engine": None, "engineFallbacks": []})
        strict_problems = []
        if args.strict:
            strict_problems += [f"missing MAT {n}" for n in builder.missing["materials"]]
            strict_problems += [f"missing 3DO {n}" for n in builder.missing["models"]]
            strict_problems += [f"3DO import failed {n}" for n in builder.failed_models]
            strict_problems += [f"refused MAT {k}" for k, info in builder.mat_infos.items() if info is not None and not info.ok]
            strict_problems += [f"master colormap: {p}" for p in builder.master["problems"]]
            if strict_problems:
                report["strictFailures"] = strict_problems
                log("strict: " + "; ".join(strict_problems[:20]))
                status = EXIT_STRICT
        if status == 0:
            status = render_views(bpy, render, builder, args, outputs, report)
        if status == 0 and args.blend:
            bpy.ops.wm.save_as_mainfile(filepath=str(Path(args.blend).resolve()), copy=True, compress=True)
            report["blend"] = str(Path(args.blend).resolve())
    except Exception as exc:  # report what failed instead of a bare traceback
        traceback.print_exc()
        report["error"] = f"{type(exc).__name__}: {exc}"
        status = EXIT_RENDER
    finally:
        if temporary:
            shutil.rmtree(temporary, ignore_errors=True)
        else:
            report["cache"] = str(cache)
        report["exitCode"] = status
        report["elapsedSeconds"] = round(time.monotonic() - started, 2)
        report_path.parent.mkdir(parents=True, exist_ok=True)
        report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        log(f"Report: {report_path}")
    return status


def render_views(bpy, render, builder, args, outputs, report) -> int:
    scene = bpy.context.scene
    engine = "workbench" if args.light == "flat" else ("cycles" if args.engine == "cycles" else "eevee")
    background = render.background_bytes(args.light)
    fallbacks = report["engineFallbacks"]

    def configure(choice):
        if args.light == "flat":
            return render.setup_flat(scene, args.width, args.height)
        return render.setup_baked(scene, args.width, args.height, choice)

    used = configure(engine)
    report["engine"] = used
    for view in args.views:
        cam, obj = builder.cameras[view]
        path = outputs[view]
        log(f"Rendering {view} with {used} -> {path}")
        started = time.monotonic()
        try:
            render.render_still(scene, obj, str(path))
        except Exception as exc:
            if used != "BLENDER_EEVEE" or args.engine == "eevee":
                log(f"error: render failed: {exc}")
                report["error"] = f"render failed: {exc}"
                return EXIT_RENDER
            fallbacks.append(f"EEVEE failed ({exc}); using Cycles on the CPU with {render.CYCLES_SAMPLES} samples")
            log("Warning: " + fallbacks[-1])
            used = configure("cycles")
            report["engine"] = used
            try:
                render.render_still(scene, obj, str(path))
            except Exception as exc2:
                report["error"] = f"render failed: {exc2}"
                return EXIT_RENDER
        item = cam.report()
        item.update(file=str(path), renderSeconds=round(time.monotonic() - started, 2))
        item.update(render.png_summary(str(path), background))
        report["views"].append(item)
        log(f"  {path.name}: sha256 {item['sha256'][:16]}..., non-background {item['nonBackgroundFraction']:.3f}")
    return 0


if __name__ == "__main__":
    code = main()
    sys.stdout.flush()
    if "--" in sys.argv:
        # Inside 'blender -b -P' the script cannot end the process with its status any other way.
        os._exit(code)
    sys.exit(code)
