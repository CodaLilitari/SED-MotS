# Retail capture: frames from the original JK.EXE under Wine

`jkcapture.py` starts the retail **Jedi Knight 1.01** executable in an isolated, purpose-built Wine prefix on a
private Xvfb display. It drives the menus with xdotool and grabs frames with ffmpeg. It can start a single-player
episode, host a multiplayer game on a chosen level, and replay Wren's walk demo. Use it when the retail look matters:
the original software renderer, HUD and menus, rather than SED's or OpenJKDF2's renderer.

## Credits

Level previews and retail captures finish work started by **Wren**, a Grok bot and one of Andrei's AI collaborators.
Wren wrote the original Blender level-preview script and the retail-capture recipe: the original JK.EXE under Wine,
driven by xdotool and captured with ffmpeg (`jk.sh` and `run.sh`, recovered on 31 October 2026). Wren's recipe
contributed the launch-and-click approach, the Escape-past-the-intro idea, the host sequence (player Ok, Multiplayer,
Host, provider, Ok, level, Ok, load character, Ok, wait) and the walk demo in `scripts/walk.json`. `jkcapture.py`
generalises that recipe with credit to Wren: parameterised paths, an isolated prefix, hash checks, menu coordinates
from a table, receipts and the Wine 9.0 fixes below.

GUI rectangles in `menus.json` come from the retail GUI tables as reconstructed by OpenJKDF2 (`src/Gui/jkGUI*.c`),
used as a reference only; no OpenJKDF2 code is included. The Smacker stand-in and the DirectPlay loopback provider
are original code (GPL-3.0-or-later, like the rest of this folder).

## What was proven in this cloud (31 October 2026)

Wine 9.0 (Ubuntu `9.0~repack-4build3`, 32-bit prefix), Xvfb, xdotool 3, ffmpeg 6, clang/lld-link 18, Mesa 25.2.8 i386
(llvmpipe). Executable: retail `JK.EXE` 1.01, sha256 `4509cd690634ba20e132bd16e713f91e144b17e2beaaad5e678f1ddb189b51d9`.
Resources: retail `Res1hi.gob`, `Res2.gob`, `JK_.CD`; episodes: Lily's `kelltest.gob` (Kell Riders, single player)
and retail `JK1MP.GOB` (multiplayer). Every frame below was looked at.

| Flow | Result | Frames |
|---|---|---|
| First start | New Player dialog, name typed, Medium, Ok → main menu | `00_new_player`, `01_main_menu` |
| Single player, Kell | New Game loads `kellwar.jkl`; full-size 3D view (Kell beast, canyon, sky); Wren's walk demo: turn, walk, turn, walk, F1 external camera | `02_single_player`, `03_level`, `04_turn` … `08_view` |
| Multiplayer host, JK1MP | Provider "Virtual Loopback (jkcapture)" (row 4), host setup lists the six JK1MP levels, Canyon Oasis (`m10.jkl`, row 3), No Valid Characters → New Character → Edit (3D preview) → Save → Load Character → spawn in Canyon Oasis; walk demo picks up power cells; F1 external camera | `03_provider` … `09_spawn`, `04_turn` … `08_view` |
| Control level | JK1MP's `m10.jkl` wrapped as a single-player episode (`sp-wrap`) renders the same way | — |

The receipts (`capture-prefix.json`, `capture-run.json`) record every input and output hash. The frame hashes of the
proof runs are listed in the work log.

After the review fixes (1 November 2026, UTC) both flows were proven again with the final code: Kell staged with
`--copy` and JK1MP staged as symlinks, each prefix without a `Z:` drive. A `--keep-running` Kell session held its
prefix while a second `sp` against it was refused (exit 2, no output folder); `walk` on the kept session then
completed, and `stop` left no JK.EXE or Xvfb process and no lock.

## Two Wine 9.0 problems and how jkcapture gets past them

### 1. Level load crashes, or the 3D view stays black, with wined3d's GDI renderer

Without 32-bit OpenGL, Wine falls back to its GDI ("no 3D") renderer. On New Game, JK updates its 8-bit palette, and
ddraw copies the 640x480 8-bit primary into the swapchain front buffer (`ddraw_palette_SetEntries` →
`ddraw_surface_update_frontbuffer` → wined3d `surface_cpu_blt`). That row copy runs past the end of the destination
block. Wine's log shows `wine: Unhandled page fault on write access to 02580000 at address 7AF3E6D3` in `ucrtbase`'s
`memmove`, with registers showing a 640-byte row copy. When the overrun does not fault, it silently corrupts memory
instead, and the level shows its HUD text over a black 3D view. A retail-only control level crashes the same way, so
this is not a property of any one level.

**Fix:** wined3d's OpenGL renderer. JK keeps its own software renderer (`b3DAccel=0`); wined3d only presents the
8-bit frames, through Mesa's llvmpipe. That needs 32-bit Mesa:

```sh
sudo dpkg --add-architecture i386 && sudo apt-get update
sudo apt-get install --no-install-recommends libgl1:i386 libgl1-mesa-dri:i386 libglx-mesa0:i386
```

`prepare` defaults to `--wined3d gl` and refuses when no 32-bit `libGL.so.1` exists. `--wined3d gdi` remains for
menu-only captures. Each run reports the renderer Wine actually used (`wine.wined3dRenderer` in the receipt).

### 2. Hosting fails: "Host Error: Cannot connect to that network service"

Wine 9.0's TCP/IP DirectPlay provider (`dpwsockx.dll`) implements its `Open` callback as a stub returning
`DPERR_UNSUPPORTED` (`fixme:dplay:DPWSCB_Open ... stub`, `err:dplay:DP_SecureOpen Unable to open session`). In the
planner's earlier probe, IPX and Serial reported "That connection is not available"; Modem was not tried. No registry
setting changes this.

**Fix:** `loopsp/jkcap_loopsp.c` is a tiny, original DirectPlay service provider. It accepts every request and sends
nothing: no sockets, no imports, no entry point. Its structure layouts were read from the DWARF debug information of
Wine 9.0's own `dpwsockx.dll`. Wine's dplayx keeps sessions, players and messages itself. `prepare` builds the
provider from source, verifies it (PE32 i386 DLL, only `SPInit` exported, no import table, base relocations present),
stages it as `C:\JK\jkcap_loopsp.dll` and registers it as `Virtual Loopback (jkcapture)` under
`HKLM\Software\Microsoft\DirectPlay\Service Providers`. JK lists providers in registry order: TCP/IP, IPX, Modem,
Serial, then the loopback at row 4. `mp-host` picks that row automatically. A game hosted this way has exactly one
machine, the host: enough to load and capture a multiplayer level as JK draws it, but nobody can join. Use
`prepare --no-loopback` to leave it out.

## Requirements

- Wine with 32-bit support (`wine`, `wineboot`, `wineserver`), Xvfb, xdotool, xwininfo (x11-utils), ffmpeg (x11grab);
  32-bit Mesa (see above); clang and lld-link to build the two DLLs. ImageMagick is optional, for contact sheets.
- A retail `JK.EXE` 1.01 (the hash above), `Res1hi.gob`, `Res2.gob` and `JK_.CD` from your own copy of the game.

## Rules the tool enforces

- **Isolation, and its limits.** Every run uses a prefix `prepare` created. `prepare` refuses an existing path,
  anything inside `~/.wine` and the caller's `$WINEPREFIX`. Wine runs with `HOME=<prefix>/home`. After `wineboot`,
  `prepare` removes every DOS drive letter except `C:` from `<prefix>/dosdevices`, including `Z:`, which Wine maps
  to the host's root (`/`); winetricks' `sandbox` verb does the same. This keeps the prefix's Wine settings apart
  from other prefixes and hides the host from Windows path lookups. **It is not a sandbox:** JK.EXE runs as your
  Unix user, and Windows code under Wine can still reach the host by other means. Run community levels this way only
  on a machine you would let them touch, such as a throwaway cloud machine; this tool was proven on one.
- **What runs.** Only the hash-checked retail `JK.EXE` runs (another build needs `--exe-sha256 <hash>`, which you
  vouch for), plus the two DLLs `prepare` builds here from source and verifies before staging. No prebuilt DLL is
  accepted. Before every run, `run`, `sp` and `mp-host` re-hash all three against `capture-prefix.json` and refuse
  if any changed or if `C:\JK` holds any other `.exe`, `.dll` or similar module.
  - `smackw32.dll`, an inert stand-in for RAD's Smacker library. It exports the nine ordinals JK imports, every
    function returns 0, and it needs no imports. All nine call sites sit in JK's cutscene player, which runs only when
    a `video\*.smk` file exists, and none is staged. The argument counts in `stub/smackw32_stub.c` are the pushes
    before each `call [IAT]`, counted twice independently with llvm-objdump.
  - `jkcap_loopsp.dll`, the DirectPlay loopback provider.
- **What is staged.** Only `.gob`/`.goo` archives and `JK_.CD` are staged. By default they are symlinks to your
  original files: JK reads them in place, and a write through `C:\JK\...` would reach the originals. `--copy`
  stages copies with read-only permission bits instead (permission bits do not bind root). DLL, EXE and video
  inputs are refused.
- **One session per prefix.** A run takes the prefix lock (`jkcapture.lock`) before it starts Xvfb or Wine. A second
  run against a prefix whose lock belongs to a live session (its jkcapture process, Xvfb or JK.EXE, matched by PID
  and command line) is refused with exit 2 before it creates its output folder, and touches nothing; a stale lock
  is replaced.
- **Clean-up.** Each run starts its own Xvfb on a free display (`:91`–`:99`, TCP off). Teardown runs
  `wineserver -k` for that prefix only when the run holds its lock, and stops Xvfb by its PID, never by name
  pattern. `stop` acts only on a prefix `prepare` made, refuses a session file whose prefix another live session
  holds, and checks each PID's command line before stopping it. A receipt is always written, and output directories
  are create-only. If `prepare` fails, it stops the new prefix's `wineserver` and records the error in
  `capture-prefix.json`.
- **Exit codes.** 0 done; 1 `failed` (a tool such as `wineboot`, the compiler, Xvfb or `wineserver` failed, or a run
  did not complete); 2 `refused` (an input or precondition was rejected; nothing ran). Errors are printed as JSON.

## Exact steps

Paths below are examples; `T=tools/retail-capture/jkcapture.py`.

```sh
# 1. A prefix for single player: retail resources + one TYPE 1 episode (New Game then loads it directly).
python3 -B $T prepare --prefix /work/jk-sp --exe /data/JK.EXE --resource-dir /data/jk/resource \
    --episode /data/episodes/kelltest.gob
# 2. Player profile (first start), Single Player, New Game, level frame, Wren's walk demo.
python3 -B $T sp --prefix /work/jk-sp --out /work/out-sp --level-wait 30 --full-view --walk

# 3. A prefix for multiplayer: the episode GOB (TYPE 2), e.g. retail JK1MP.GOB.
python3 -B $T prepare --prefix /work/jk-mp --exe /data/JK.EXE --resource-dir /data/jk/resource \
    --episode /data/jk/episode/JK1MP.GOB
# 4. Host on a level named in episode.jk (its LEVEL order is the row in the host screen's level list).
python3 -B $T mp-host --prefix /work/jk-mp --out /work/out-mp --level m10.jkl --spawn-wait 30 --full-view --walk
```

More commands:

- `sp-wrap --from JK1MP.GOB --level m10.jkl --out m10sp.gob` wraps one level of any episode GOB as a single-player
  episode (copies every entry, writes a one-level `TYPE 1` `episode.jk`). Use it to capture a multiplayer level
  without hosting, or as a control level.
- `--keep-running` on `sp`, `mp-host` or `run` leaves JK running and writes `session.json`. `walk --session ...`,
  `steps --session ... --steps my.json` and `stop --session ...` then act on that session.
- `run --prefix P --out O --steps my.json` runs any step script from the first frame on.
- `build-stub --out D` and `build-loopsp --out D` build and verify the two DLLs on their own.

Useful options: `--player NAME` and `--difficulty easy|medium|hard` for the first start, `--episode NAME` when
several episodes are staged, `--provider-row N` to force a provider, `--allow-black` to skip the level-frame checks,
and `--wine-debug '+ddraw'` (any WINEDEBUG string) or `--wine-log` for diagnostics. `--display N` selects the Xvfb
display. `--windowgui --gui-offset X,Y` passes Wren's `-windowGUI` switch; the GUI then has a frame inside the
desktop, so the offset must be measured from a frame grab first. This is untested here.

## What prepare builds

```
<prefix>/                         WINEPREFIX (new), WINEARCH=win32, wineboot -i with mscoree/mshtml disabled
<prefix>/dosdevices/              c: only (z: and any other drive letter removed after wineboot)
<prefix>/drive_c/JK/JK.EXE        hash-checked copy, read-only
<prefix>/drive_c/JK/smackw32.dll  Smacker stand-in (built from stub/)
<prefix>/drive_c/JK/jkcap_loopsp.dll  DirectPlay loopback provider (built from loopsp/)
<prefix>/drive_c/JK/resource/     Res1hi.gob, Res2.gob, JK_.CD
<prefix>/drive_c/JK/episode/      the --episode archives
<prefix>/drive_c/JK/player/       created by JK
<prefix>/capture-prefix.json      inputs + sha256, DLL builds + sha256, Wine version, registry, providers
```

Registry (`wine reg add`):
- `HKCU\Software\Wine\Explorer` `Desktop=Default` and `...\Desktops` `Default=640x480` (`--desktop`). This is a
  virtual desktop. Plain Xvfb offers DirectDraw a single 32-bit mode, and JK then refuses with "requires ... 256 Colors".
- `HKCU\Software\Wine\Direct3D` `renderer=gl` (`--wined3d`).
- `HKLM\Software\LucasArts Entertainment Company\JediKnight\v1.0` `b3DAccel=0`, JK's software renderer
  (`--accel` sets 1; untested).
- `HKLM\Software\Microsoft\DirectPlay\Service Providers\Virtual Loopback (jkcapture)`: `Guid`
  `{7158F146-36BB-4070-AC35-D6456B940FDB}`, `Path` `C:\JK\jkcap_loopsp.dll`, `dwReserved1/2 = 0`.

## Step scripts

A step script is a JSON list, or `{"steps": [...]}`. Coordinates are JK's 640x480 GUI coordinates, scaled to the
desktop window. Named targets come from `menus.json` (`"target": "mp_host.ok"`).

| op | fields | effect |
|---|---|---|
| `wait` | `s` | sleep |
| `click` | `x`,`y` or `target`; `wait`, `retry` | move, press, release; with `retry` *n*, click again (up to *n* times) while the frame stays unchanged (< 1 % of pixels), as when JK drops a click during a screen or mode switch |
| `click_row` | `list`, `row` | click row *n* of a list with a row layout in `menus.json` |
| `list_select` | `list`, `index`, `count` | click the list, `Up` ×(count+1), `Down` ×index |
| `type` | `text`, `delay_ms` | type printable ASCII |
| `key` | `keys`, `delay_ms` | X key names, e.g. `["F1"]`, `["equal"]` |
| `hold` | `key`, `s` | key down, sleep, key up (Wren's `hold`) |
| `shot` | `name` | grab the desktop window to `<name>.png` (hash and statistics go into the receipt) |
| `expect` | `name`, `region`, `min_mean` | fail when the region's mean luma is below `min_mean` (default 8) |
| `expect_changed` | `name`, `from`, `min_fraction` | fail when fewer than `min_fraction` of the pixels differ from an earlier shot |
| `await` | `region`, `not_flat`, `timeout` | poll until the region is not black and, with `not_flat`, not one flat colour (the bare Wine desktop) |

After every step the runner checks Wine's log for a crash marker and checks whether JK.EXE still runs. On a crash it
grabs `zz_crash.png` and stops with the crash line (`JK.EXE crashed: wine: Unhandled page fault ...`).

The built-in flows wait for JK's first textured GUI frame, because the first non-black frame can be the bare blue Wine
desktop, followed by a blank mode switch. Only screen-changing clicks (Ok, Host, Save, New Game …) retry, never
selections within a screen, so a repeated click cannot act on the next screen. The level frame must differ from the
last menu frame in at least half of its pixels, so an error dialog over a menu fails the run instead of passing as a
level.

## Limits

- Tested only with Wine 9.0, as packaged by Ubuntu 24.04. Newer Wine releases may implement DirectPlay's TCP/IP
  provider (Wren's original recipe ran on a Wine whose provider list differed); the loopback provider still works
  there.
- Multiplayer hosting is single-machine: no client joins, so no network play is captured.
- No sound: the container has no ALSA device, and JK runs silently.
- Timing-based waits (`--level-wait`, `--spawn-wait`) may need raising for large levels or slow machines.
- The provider names draw as boxes in JK's font under Wine. The rows still work.
- With several single-player episodes staged, the episode list is ordered by the directory listing. `list_select`
  follows that order, but it is unverified here: the proofs staged one episode per prefix.
- `--full-view` presses `=` ten times. JK prints "Screen size 10" lines that fade after a few seconds, and at full
  size JK hides the HUD.
- Frames are checked by brightness and change only (no OCR), so a level that is genuinely dark needs `--allow-black`.
- Jedi Knight: Mysteries of the Sith (`JKM.EXE`) is not covered.

## Tests

```sh
python3 -B -m unittest discover -s tools/retail-capture/tests -v
```

49 stdlib tests:

- the PE export parser (synthetic images, and the real stub and provider when clang and lld-link exist);
- `menus.json` integrity and step-script validation;
- coordinate scaling and xwininfo parsing;
- `episode.jk` parsing, GOB round trips and `sp-wrap`;
- prefix refusals, the executable allowlist and staging rules;
- registry commands and provider order;
- display selection, frame statistics, the log watcher and 32-bit libGL detection;
- the step runner with a fake screen (level checks, lost-click retries, waiting past the bare desktop, crash handling);
- runtime safety with recorded subprocess calls: a run refuses a changed `JK.EXE`, stub or provider and any extra
  module under `C:\JK`; lock owners are matched by PID and command line; a refused start never runs
  `wineserver -k` or starts Xvfb; the owner stops its prefix once; `stop` refuses unprepared prefixes, damaged
  session files and prefixes held by another live session; `kill_pid_if` leaves a process with another command
  line; `prepare` checks every tool before the prefix exists, stops its `wineserver` on failure and removes the
  host drive letters; the CLI prints `failed` or `refused` JSON; `--stub` no longer exists.

No test needs Wine, an X server or game data.
