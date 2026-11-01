# JK level importer and Wren-style previews for Blender

`tools/blender` imports a Jedi Knight or Mysteries of the Sith level into Blender 5 and renders
two auto-framed previews: an orthographic overhead plan and a three-quarter perspective "doll's
house" view. It works headless from the command line or inside Blender as **File > Import > JK
level (.jkl / .gob)**.

## Credits

> Level previews and retail captures finish work started by **Wren**, a Grok bot and one of
> Andrei's AI collaborators. Wren wrote the original Blender level-preview script (JKL geometry
> read from the level file, model placement, a backface-culled "doll's house" view, auto-framed
> orthographic overhead and three-quarter perspective cameras) and the retail-capture recipe (the
> original JK.EXE under Wine, driven by xdotool and captured with ffmpeg). Andrei shared the work;
> it was recovered on 31 October 2026. `tools/blender`, `tools/retail-capture` and `sed-agent
> overview` generalise it with credit to Wren. 3DO models and MAT/CMP files are loaded by Coda's
> Blender-Sith fork (v1.0.1-coda.1, GPL-3.0-or-later), based on Blender-Sith by Crt Vavros (smlu).

This folder is Wren's `render.py`, finished: the level-building core, the look and the camera
directions are Wren's. Blender-Sith is used as it is; nothing here copies or edits it.

| Wren's script | This tool |
|---|---|
| Workspace paths written into the script | `--jkl`, `--archive`, `--resource-dir`, `--sith`, `--out` |
| A hard-coded CMP | The level's master palette: World Colormap 0, else `dflt.cmp` (SED's rule and messages) |
| MATs loaded as found | SED's MAT rules: first cel, 16/32-bit true colour, retail mip fallback, refusals |
| Thing rotation approximated (`'ZXY'`) | Exact JK orientation (`rdMatrix_Build34`, Blender `'YXZ'`) |
| One shared-vertex bmesh (adjoin mirror faces dropped) | Per-corner vertices; both faces of an adjoin pair kept; winding from the stored normals |
| No light | `--light baked`: texel x the JKL's own light, composed as the engine does |
| No sky | Sky surfaces drawn with approximate static UVs (or `--sky hide`) |
| One 3DO import per thing | One prototype per 3DO, instanced (flat) or linked-duplicated (baked) |
| Bounds over every mesh, models included | Bounds over drawn world surfaces only (the same box `sed-agent overview` uses) |
| `<level>_persp.png` | `<level>_threequarter.png`, plus a JSON report with provenance |

## Requirements

- Blender 5.0 or later, either the `blender` binary or Blender's Python module (`bpy`).
- Coda's Blender-Sith fork, found in this order: `--sith PATH` (the folder that contains `sith/`),
  `$BLENDER_SITH_PATH`, an already imported `sith` module, or an installed and enabled Blender-Sith
  extension. Without it the tool stops with exit code 2 and says how to provide it.
- The level's resources: the episode GOB/GOO and the retail resource archives (JK: `Res2.gob`,
  `Res1hi.gob`; MotS: the retail GOOs), or loose folders laid out like a GOB.

The modules that do not need Blender (`gob`, `resources`, `jkl`, `matfile`, `threedo`, `orient`,
`framing`, `light`) use only the Python 3.10+ standard library.

## Command line

```sh
# Blender's Python module
<python with bpy> tools/blender/jkl_preview.py --jkl jkl/kellwar.jkl \
    --archive Episode/kelltest.gob --archive Resource/Res2.gob --archive Resource/Res1hi.gob \
    --out previews --sith /path/to/blender-sith

# or Blender itself
blender -b --factory-startup -P tools/blender/jkl_preview.py -- <the same arguments>
```

| Option | Meaning |
|---|---|
| `--jkl X` | An existing `.jkl` file, or an entry inside the sources: the exact path (`jkl/kellwar.jkl`, backslashes allowed), else `jkl/X`, else a unique basename. An ambiguous name stops with exit 2 and lists the candidates. |
| `--archive A` | GOB/GOO, repeatable, in precedence order: episode first, then `Res2.gob`, then `Res1hi.gob`. |
| `--resource-dir D` | Loose folder laid out like a GOB (`mat/`, `3do/`, `misc/cmp/`), repeatable. When the JKL itself sits in a folder named `jkl`, that folder's parent is searched first. |
| `--out DIR` | Output folder. Files are create-only unless `--overwrite`. |
| `--views overhead,threequarter` | Which cameras to render. |
| `--light flat\|baked` | `flat` (default) is Wren's Workbench look; `baked` multiplies each texel by the level's own light. |
| `--framing wren\|contain`, `--margin 0.025` | Wren's distance, or back off until the whole level fits. |
| `--width 1280 --height 960` | Image size (16 to 8192). |
| `--sky textured\|hide` | Draw sky surfaces with approximate UVs, or leave them out. |
| `--include-players`, `--include-hidden`, `--no-things` | Things to place (players and things the engine does not draw are skipped by default). |
| `--engine auto\|workbench\|eevee\|cycles` | `auto`: Workbench for flat; EEVEE for baked, falling back to Cycles on the CPU if EEVEE cannot render. |
| `--sith PATH`, `--cache DIR`, `--blend FILE`, `--report FILE`, `--strict`, `--overwrite` | Blender-Sith location; keep the extraction cache in `DIR`, which must be a new or empty folder outside every input (see below); also save a `.blend`; report path; fail on missing or refused resources. |

Outputs: `DIR/<level>_overhead.png`, `DIR/<level>_threequarter.png` and `DIR/<level>_preview.json`.
Exit codes: 0 done; 2 usage or input problem (missing JKL, unreadable archive, ambiguous entry,
nothing to frame, Blender-Sith not found); 3 render failure; 4 `--strict` found a missing MAT, 3DO
or CMP, a refused MAT or a failed 3DO import (the report is still written; nothing is rendered).

## In Blender

Package the add-on with `python3 tools/blender/build_extension.py --out <folder>` and install the
zip with **Edit > Preferences > Get Extensions > Install from Disk**, next to Coda's Blender-Sith
extension. **File > Import > JK level (.jkl / .gob)** then imports a `.jkl`, or a level inside a
`.gob`/`.goo` (the only `jkl/*.jkl`, or the first alphabetically, or the one named in *Level entry*).
*Extra archives* and *Resource folders* take `;`-separated lists searched after the level's own
source. The operator builds the scene and the cameras; it does not render. For baked light, render
with EEVEE or Cycles and the Raw view transform.

## How the level is built

- **Resources.** Lookup is path-major like the engine (`sithMaterial`'s `"mat;3do\mat"`): for each
  candidate path (`mat/x`, then `3do/mat/x`; `3do/x`; `misc/cmp/x`; `jkl/x`) every source is tried
  in order and the first hit wins. Only when no candidate path matches anywhere does a basename
  match apply, with a warning (and `ambiguous` when several sources have it). Source order: the
  level's own tree or GOB, then `--resource-dir` folders, then `--archive` files. Every resolved
  file is recorded with its source, entry, size and SHA-256; a different copy of the same file in
  another source is reported as `shadowed`. Files are extracted to a cache (`src/<source>/<path>`
  for 3DO and CMP, `mat/<name>` for normalised MATs); sources are never written. The default cache
  is a new temporary folder, removed afterwards. A kept `--cache DIR` must be a new or empty folder
  that neither is, contains nor lies inside a loose source, an archive's folder, a loose JKL's
  folder or Blender-Sith's folder; anything else stops with exit 2 before a file is written. An
  entry name with `.` or `..` segments that would place a file outside the cache is refused (the
  MAT, 3DO or CMP is then reported as refused), and no output path may name an input file.
- **Master palette.** World Colormap 0, else `dflt.cmp`; a listed but unavailable CMP falls back to
  `dflt.cmp` with SED's problem text. All 8-bit textures use it, as JK does.
- **MATs.** One Blender material per MAT name, case-insensitively (`01wgril1.mat` and
  `01WGRIL1.mat` are one material). The first cel's texture is shown; earlier textures must be
  complete. 16/32-bit MATs decode with their declared channel layout, OpenJKDF2's RGB565/ARGB1555
  when a 16-bit header declares none, and bit 15 as the mask of a masked 15-bit texture.
  **Retail mip fallback:** when mip 0 is complete but a later mip is short (DBZ Tournament's
  `zgrass1`), mip 0 is shown with a note; a short mip 0 is refused. Retail JK picks a mip per face
  by distance, so it shows mip 0 only on faces nearer than the level's MipMap Distances; farther
  faces sample the level the file never filled (DBZ's distances, 200 200 300 400, exceed its
  arena, so there retail shows mip 0 everywhere). Blender, like SED, never draws later mips. Each MAT is
  rewritten as a single-cel, single-mip MAT that Blender-Sith decodes to exactly those pixels
  (8-bit keeps its indices with colour key 0; true colour becomes 32-bit with SED's rounding).
  Missing or refused MATs get a neutral grey placeholder named `<mat> (missing)` or `(refused)`.
- **World.** Surfaces with geometry mode 0 (portals and invisible walls) or no material are
  skipped, as are surfaces with bad indices or no area; each is counted. Every corner gets its own
  vertex, so both faces of a visible adjoin pair survive; a face is reversed when its corner order
  disagrees with the JKL's stored normal, because the engine culls by that normal. UVs are
  `(u / w, -v / h)` with the MAT's mip 0 size. Face attributes `jk_surface`, `jk_sector`,
  `jk_surfflags` and `jk_faceflags` keep the level's numbers.
- **Sky.** Ceiling sky (`0x400`) uses the planar projection SED's shader uses (exact for straight-down
  views); horizon sky (`0x200`) a cylinder around the level's centre. Both stand in for the engine's
  view-dependent sky and are noted in the report.
- **Things.** The template chain gives `model3d`, `type` and `thingflags` (a thing's own parameters
  override its templates). Players and things with `thingflags & 0x80012` are skipped unless asked;
  each 3DO is imported once by Blender-Sith into a collection outside the scene and placed with
  JK's exact orientation, `R = Rz(yaw) Rx(pitch) Ry(roll)` (Blender `'YXZ'` with
  `(pitch, roll, yaw)`). The 3DO insert offset is not applied, as the engine draws placed things
  at their stored position. Meshes and faces with geometry mode 0 are not drawn. Mode values
  outside Blender-Sith's enums (geometry mode 5, lighting modes 4 and 5) are read as the modes the
  engine draws them as; non-UTF-8 3DOs are re-encoded for Blender-Sith. Both are noted.
- **Cameras.** Wren framing, shared with `sed-agent overview` (the golden values are in
  `tests/test_framing.py` and SED's `OverviewFramingTests`): bounds of the drawn world surfaces;
  overhead orthographic, north up, vertical extent `max(s.y, s.x / aspect) * (1 + 2 * margin)`;
  three-quarter from `(0.7, -0.7, 0.6)` with a 35 mm lens on a 36 mm sensor.

## Light

- **flat** (default): Wren's look. Workbench, studio light, texture colour, backface culling, world
  colour (0.18, 0.2, 0.24).
- **baked**: each corner's light `L` is composed as the engine's hardware path does (OpenJKDF2
  `sithRender.c`/`rdCache.c`, SED `SceneAssembler.StaticLight`): `1` for light mode 0 and sky,
  otherwise `max(clamp(clamp(I) + surface extra light), clamp(sector extra light))` with `I = 0`
  for light modes 1 and 2 (MotS rows use their RGB values). Things use their 3DO vertex and face
  light, floored at `clamp(sector ambient + extra)`. Materials become `emission = texel x L` with
  the Raw view transform, so a PNG byte is `byte(texel x L)` within one step; a level with no light
  (DBZ Tournament ships with zero light) renders dark, as the engine shows it. Dynamic lights are
  not added.

## Report

`<level>_preview.json` (`schemaVersion` 1) records the tool and credits, Blender and Blender-Sith
(path, fork release, how it was found), the level (source, entry, SHA-256), every source (SHA-256 of
archives), the master palette, every resolved resource with notes (refusals, mip fallbacks,
shadowing, re-encoding), counts, missing MATs/3DOs/templates, warnings, each view (camera numbers,
file, SHA-256, non-background fraction), the engine and any fallback, framing, margin, bounds and
the elapsed time. PNGs carry no metadata stamps (date, render time), so the same inputs give
byte-identical files and the hashes can be compared between runs.

## Limits

- Animated materials show their first cel; sector tint, fog and dynamic lights are not shown.
- Translucent faces (face flag `0x2`) draw opaque; 3DO faces marked double-sided are culled like
  single-sided faces.
- Colour (solid) MATs show their master-palette entry; SED shades solids through the sector CMP.
- Sky UVs are static approximations of the engine's view-dependent sky.
- Things the engine does not draw (`thingflags 0x2, 0x10, 0x80000`) are skipped here; SED's lit
  snapshot draws every thing that has a model.
- Sprites, particles and 3DO animation are not shown; models are in their rest pose.
- The `wren` three-quarter distance can crop tall or wide levels (Wren's framing); `--framing
  contain` fits the whole box.

## Tests

```sh
python3 -B -m unittest discover -s tools/blender/tests -v            # stdlib; Blender tests skip
BLENDER_SITH_PATH=/path/to/blender-sith \
  <python with bpy> -B -m unittest tools/blender/tests/test_bpy_import.py -v
```

The stdlib tests cover the GOB reader, the resolver, the JKL parser, every MAT rule (SED's test
vectors and the retail mip fallback), orientation against an independent `rdMatrix_Build34`
transcription, the framing golden values and the light composition. The Blender tests build a
synthetic two-room level in a GOB (8-bit and RGB565 MATs, a CMP, a two-node 3DO with an insert
offset, a player, hidden things) and check counts, both faces of an adjoin pair, winding, one
material per MAT, thing matrices, cameras, flat and baked renders (a fully lit texel
(200, 100, 40) renders within one step of itself and an `L = 0.5` face within one step of
(100, 50, 20) in EEVEE and in Cycles), strict mode, a `--cache` inside or over a loose level
being refused with its files left untouched, and installation as an extension. The
standard-library tests also check that cache paths never leave the cache and which folders a
`--cache` may not overlap. All test data is synthetic; no game data is committed.
