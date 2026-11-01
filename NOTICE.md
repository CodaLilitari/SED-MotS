# Notices and credits

This file records credits for work added on Red's `claude` branch. The fork's own
provenance (davidagnome/SED, smlu/SED, JED by Alexei Novikov and the Code Alliance)
is in [README.md](README.md#provenance), and dependency notices are in
[licenses/](licenses).

## Wren's level previews

> Level previews and retail captures finish work started by **Wren**, a Grok bot and one
> of Andrei's AI collaborators. Wren wrote the original Blender level-preview script
> (JKL geometry read from the level file, model placement, a backface-culled "doll's
> house" view, auto-framed orthographic overhead and three-quarter perspective cameras)
> and the retail-capture recipe (the original JK.EXE under Wine, driven by xdotool and
> captured with ffmpeg). Andrei shared the work; it was recovered on 31 October 2026.
> `tools/blender`, `tools/retail-capture` and `sed-agent overview` generalise it with
> credit to Wren. 3DO models and MAT/CMP files are loaded by Coda's Blender-Sith fork
> (v1.0.1-coda.1, GPL-3.0-or-later), based on Blender-Sith by Crt Vavros (smlu).

What came from Wren, and where it went:

| Wren's work | Generalised as |
|---|---|
| `render.py`: the level built from the JKL's own vertices and surfaces, invisible surfaces skipped, things placed with their models, backface culling, Workbench "doll's house" look | `tools/blender/` (JKL importer, `jkl_preview.py`, the *Import JK level (.jkl / .gob)* extension operator) |
| `render.py`: an orthographic overhead camera and a three-quarter perspective camera, both framed on the level's bounds | The shared "Wren framing" in `tools/blender/jk_level_importer/framing.py` and `dotnet/src/Sed.Rendering/OverviewFraming.cs`, used by `sed-agent overview` |
| `jk.sh` and `run.sh`: retail JK.EXE under Wine with `-windowgui`, menus driven by xdotool clicks, Wren's host sequence and walk demo, frames grabbed with ffmpeg | `tools/retail-capture/` (`jkcapture.py`, `menus.json`, `scripts/walk.json`) |

Wren's files were read as a reference and are not copied into this repository. Wren's
notes listed what remained to do (parameterised paths, the level's own colormap, exact
thing orientation, optional baked light); those are done here, and the READMEs list the
limits that remain.

## Blender-Sith

`tools/blender` loads 3DO models and MAT/CMP files through Blender-Sith at run time:
[Coda's fork](https://gitlab.com/codalilitari/blender-sith) (also on
[GitHub](https://github.com/CodaLilitari/blender-sith)), release v1.0.1-coda.1, based on
[Blender-Sith](https://github.com/smlu/blender-sith) by Crt Vavros (smlu), licensed GPL-3.0-or-later. Blender-Sith is not vendored, copied or
modified here; users install it separately or point `--sith` at it. The importer and its
extension are GPL-3.0-or-later, and `tools/blender/build_extension.py` packs this
repository's [LICENSE](LICENSE) into the extension zip.

## OpenJKDF2 as a reference

OpenJKDF2 was read as a reference for engine behaviour that these tools reproduce or
document: the thing orientation matrix (`rdMatrix_Build34`), static light composition
(`sithRender.c`, `rdCache.c`), MAT loading and per-face mip selection by the level's
MipMap Distances (`rdMaterial.c`, `rdCache.c`, the software rasterisers), and the retail
GUI element rectangles behind `tools/retail-capture/menus.json` (`src/Gui/jkGUI*.c`). No
OpenJKDF2 code is included.

## Original code in tools/retail-capture

The Smacker stand-in (`stub/smackw32_stub.c`) and the DirectPlay loopback service
provider (`loopsp/jkcap_loopsp.c`) are original code, GPL-3.0-or-later like the rest of
the folder. They are built from source by `jkcapture.py prepare`; no prebuilt DLL is
accepted. The provider's structure layouts were read from the debug information of
Wine 9.0's own `dpwsockx.dll`; no Wine code is included.

## Game data

No game data, community levels, Wren's images or generated previews are committed.
Tests use original synthetic fixtures. The retail JK.EXE, its resource archives and
the levels used for evidence remain external inputs that users supply from their own
copies.
