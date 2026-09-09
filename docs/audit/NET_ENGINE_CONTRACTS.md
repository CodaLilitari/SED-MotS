# .NET editor engine and Windows integration checks

Engine source reference: local OpenJKDF2/MotS commit `e9e293a7a98c9a665509af96f9a87a9ca2470996`.
This is a bounded compatibility audit and implementation record, not an exhaustive engine/editor review.

## Launching and packaging

`Sed.Formats.Launching.GameLauncher.PrepareLaunch` returns a serializable plan and performs no writes or process launches. It accepts a `LaunchRequest` with game, game directory, project directory, level filename, optional executable, and Auto/OpenJKDF2/Retail profile. An optional `ILaunchFileSystem` supplies read-only host facts for tests. The plan contains the executable, working directory, argument list, resolved engine, resource-path token, and required loose files.

Automatic selection tries `openjkdf2-64.exe`, `openjkdf2.exe`, then `openjkdf2-32.exe`, before the matching retail executable. Explicit profiles never fall back to another engine. Renamed executables require an explicit profile. MotS/OpenJKDF2 adds `-motsCompat`; retail does not. Windows launches the executable directly with `ProcessStartInfo.ArgumentList`, without a batch file or shell expansion. Existing non-Windows shell templates remain a separate, less constrained path.

The engine command parser does not honor quoting for whitespace-bearing `-path` values. The planner chooses a valid relative, absolute, or available Windows short path; it reserves room for the longest required resource suffix within the engine's 127-byte content budget. Relative paths are based on the game working directory. For OpenJKDF2 the planner requires `resource/jk_.cd`, because the install helper may otherwise change the working directory. This check does not establish that every required game asset is installed.

Save and Test creates loose `episode.jk` and `jkl/<level>.jkl`, and a matching GOB (JK) or GOO (MotS) containing both entries. An archive merely placed inside the `-path` directory is insufficient. Existing episode text is preserved exactly when it references the selected level; a mismatched episode stops the operation. New episode filenames are restricted to 31 ASCII characters, including `.jkl`, matching `src/Main/jkEpisode.c:462-463`'s 32-byte filename field. Testing opens the engine menus; this implementation does not force a map or auto-start gameplay. Custom assets in the project directory remain loose resources; Save Archive is not an automatic dependency collector.

## Validation and editing

JK/MotS surfaces with more than 24 vertices are errors (`sithSurface.c` parser limit). IJIM retains its separate 64-vertex warning. The checker validates membership before dereferencing corners or adjoins, reports non-finite geometry and light data, rejects adjacent polygon edges at or below 1e-9 units, and treats zero-area JK/MotS surfaces as errors. Checking does not alter cached normals.

COG references are interpreted only when a script schema is available. Decimal 32-bit syntax is required; partial or hexadecimal tokens are rejected rather than being interpreted differently from the engine's `atoi`. All negative COG indices are null. Bit `0x8000` selects the static world; unplaced positive COG indices may refer to template-created runtime instances. Those last two cases receive unverified-bounds warnings, not false errors against the placed COG count (`src/Cog/sithCog.c:1832-1849`). Typed sector/surface/thing references are checked against available level counts. Missing schemas are reported when a resolver is supplied; schema-free validation does not guess types.

An explicitly selected retail profile warns above 640 placed things. This is inferred from the engine's non-QOL pointer range and is not a complete retail capacity model; runtime spawning also needs headroom.

Sector cleave snapshots preserve vertex owners, corner monochrome/RGB light values, and thing-sector membership across repeated undo/redo. Cuts require one closed cross-section. A cut that crosses an existing portal is refused before mutation; paired portal splitting remains unsupported. Compound Connect Sectors propagates that reason and rolls back earlier cuts instead of deleting a sector after a failed cut. Things in a successfully merged overlap transfer to its surviving sector through reversible commands. Successful cleaves disable inherited `COLLIDEBOX` shortcuts on both halves, and undo restores the original fields. This is necessary because `src/Engine/sithIntersect.c:58-67` treats the box as proof that a point is inside the sector, so an old box spanning the new split is unsafe.

## Human editor paths

File > New MotS Level and New JK Level create editable `JklDocument` rooms through the same `LevelFactory` used by the agent interface, loading the bundled game template library when present. They are untextured geometry starters; they need player/game content before gameplay testing. Untextured edits rebuild the viewport mesh, newly selected materials trigger a texture-cache refresh, and empty scenes clear old draws. Surface-inspector material choices are added to the document's material browser. Insert-key thing duplication retains its layer, flags, source parameters and architectural-light binding metadata.

`PreparedLevelOpen` parses candidate JKL/JED/archive input and opens its resources before the current document is replaced. Failed candidates dispose their own archive handles. Resource order is the selected addon, base episode, then configured game resources. Loose MotS levels therefore resolve `JKMRES.GOO` and other configured MotS archives instead of searching only for JK's `Res2.gob`. This archive resolution does not yet collect arbitrary loose custom materials/models alongside a JKL.

Edited documents receive uniquely named, flushed JKL recovery snapshots before switching or closing. A serialized-content fingerprint detects direct property edits as well as changes represented in undo history. File > Recover Previous Session opens the newest snapshot. If snapshot writing fails the current document is retained. An untouched import that cannot yet be serialized can still be left because its source file remains available; undo-history edits to that document require a successful snapshot. UI renderer failures may rebuild the prior view, so no claim is made that all transient selection/history state survives a GPU failure. Native desktop interactions were not exercised in this pass.

## Executed headless verification

The first Vulkan probe failed on Windows because the upstream loader resolver returned `vulkan`. The Windows system loader is `vulkan-1.dll`; selecting that file fixed instance creation. The corrected probe enumerated an NVIDIA GeForce RTX 5070 Ti (Vulkan 1.4.341) and Intel UHD Graphics 770 (Vulkan 1.4.323).

The existing `Sed.LevelRender` tool then parsed and rendered retail MotS `s1l1_rebelbase.jkl` from external, read-only game archives: 701 sectors, 38 submeshes, 38/38 requested materials resolved, a 1024×768 PNG produced. The image was inspected and contains textured exterior level geometry. This is a renderer smoke test, not a gameplay test or reference-image fidelity comparison. No desktop window or game process was launched.

`Sed.AppShot` also executed the composed MainWindow under `Avalonia.Headless`, with injected settings, autosave disabled and an isolated recovery directory. The actual New MotS menu created an editable one-sector/six-surface document. A vertex edit changed the rendered geometry; undo restored it; the document round-tripped through JKL. The Insert-key path cloned a synthetic thing while preserving metadata and could be undone. The offscreen window closed and the process exited zero. The headless session must be disposed on a worker because its dispatch can resume the caller on the UI thread; trying to synchronously join that same thread caused the initial harness shutdown hang and was corrected.

The two inspected 1100×720 synthetic UI images are `build/net-evidence/new-mots-app-headless.png` (SHA-256 `68C67D94F89347082D4DE8570124A69E1C0FD235F4945016074EE4844C700B45`) and `build/net-evidence/new-mots-app-headless-edited.png` (SHA-256 `7B6A78764BC37D188E0BD67CC3DD0004ADBD7B52BDFFF514D7B5E960827E3210`). These prove bounded offscreen integration, not a complete interactive usability test.

Local ignored output: `build/net-evidence/mots-rebelbase-headless.png`.
SHA-256: `6C2951876A9B3A0AAAD469229D73E5057CB2EF249352E637208104A6E6F6E3E0`.
The derived retail image and source game archives must not be included in the source/release package.

Executable synthetic coverage is in `LaunchContractTests`, `EngineConsistencyTests`, `CleaveSectorSafetyTests`, and `PreparedOpenTests`. The final focused pass, including existing consistency, sector-cleave and Connect tests, passed **102/102**, with zero failures or skips. Its TRX is `build/net-evidence/engine-contract-tests/engine-contract-final.trx`. The parent build runner owns the final repository-wide suite receipt.
