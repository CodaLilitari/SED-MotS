# Public SED fork delta audit

Checked 2026-09-08. This supplement covers all 11 published branches of four public forks: `davidagnome/SED`, `jscaltreto/SED`, `LadyEebs/SED`, and `thekovic/SED`. Comparison base is upstream `smlu/SED` commit **c3b6c0e59f192e96dc6df2daf698fb179e63603a** (2026-08-08, `Fix plugin SDK`). Source acquisition and inspection were read-only. No downloaded build scripts, executables, .NET restore/build, tests, or renderer were executed.

**Decision:** retain upstream Delphi SED as the implementation baseline for the current MotS work. The davidagnome fork contains a real, substantial C#/Avalonia/Vulkan editor, worth preserving as a possible later parallel effort, but its source has specific data-loss and geometry-undo defects that prevent treating it as a compatible MotS replacement today. The other forks provide no new correctness fixes beyond upstream; thekovic's only unique branch is a personal input remap.

## Pinned branch comparisons

The full SHA values, comparison URLs, commit lists, changed-file lists, acquisition SHA256, inventory metrics, and coverage classifications are in `SED_FORK_DELTA_MANIFEST.json`. Comparisons use GitHub's merge-base-to-head semantics. A behind-only branch has no unique changes; it is not necessarily tree-identical to current upstream.

| Repository / branch | Pinned head (abbreviated) | Ahead / behind | Unique file delta | Review |
|---|---|---:|---:|---|
| davidagnome/SED main | 908d53d70d6b | 23 / 1 | 219 | Pinned source archive, full inventory/static patterns, selected components reviewed |
| jscaltreto/SED main | 28d9e46a5187 | 0 / 33 | 0 | Ancestry only; no redundant checkout |
| LadyEebs/SED main | 784b7351a19d | 0 / 17 | 0 | Ancestry only; no redundant checkout |
| thekovic/SED fix-hotkey-in-3d | af9ce67b55d3 | 0 / 76 | 0 | Already upstream ancestry |
| thekovic/SED main | 67aa632c4bca | 0 / 1 | 0 | Already upstream ancestry |
| thekovic/SED noclip-persist | cdbc7c6c4d57 | 0 / 83 | 0 | Already upstream ancestry |
| thekovic/SED per | 5a41dde5e8b3 | 1 / 1 | 1 | Entire source patch reviewed |
| thekovic/SED raft-repair-kit | 5b43077a0449 | 0 / 71 | 0 | Already upstream ancestry |
| thekovic/SED reorg-dirs | 2f64ec21d14b | 0 / 87 | 0 | Already upstream ancestry |
| thekovic/SED stop-tracking-res | d624d9e76a50 | 0 / 84 | 0 | Already upstream ancestry |
| thekovic/SED ui-tweaks | bc2c2b1aa0df | 0 / 31 | 0 | Already upstream ancestry |

Thekovic's [`per` commit](https://github.com/thekovic/SED/commit/5a41dde5e8b342dc0d62c7861bdb9e1e930c8bce) changes only `src/JED_MAIN.PAS` (13 insertions, 23 deletions). The complete patch changes middle-button camera operations to right-button operations, modifier behavior, and keyboard rotation; it removes the `R` rotation shortcut and page-key rotation actions. It has no level parser/writer, MotS, geometry, or engine integration changes. This is a preference option to consider separately, not a source of missing bug fixes. The commit message also mentions a cleave shortcut, but the inspected one-file patch is the evidence for actual behavior.

## C# fork provenance and real architecture

Source: [davidagnome/SED at 908d53d](https://github.com/davidagnome/SED/tree/908d53d70d6b95784a92e9f1746ab5b13afcfdf0).

Pinned archive: `https://codeload.github.com/davidagnome/SED/zip/908d53d70d6b95784a92e9f1746ab5b13afcfdf0`.

SHA256: `980c21252083b4afc608ee95f3e412688692db8512b0290490425d02f92a191f`.

Local inspection root: `sources/davidagnome-SED/SED-908d53d70d6b95784a92e9f1746ab5b13afcfdf0` under this audit directory. Root `LICENSE` carries GNU GPL version 3; GitHub reports SPDX `GPL-3.0`. That is useful provenance for compatible GPL work; separately review bundled third-party resources and notices before redistribution.

**The native Delphi application has not been removed.** The fork retains the original tree. All unique source additions are under `dotnet/`; the only other unique paths are `.gitignore` and `.DS_Store`. It is one upstream commit behind, so it also lacks current upstream's last plugin SDK fix. The architecture document's phrase about replacing Delphi describes intent, not deletion of the Delphi source.

The archive has 445 files, including 217 under `dotnet/`. The new tree contains 202 code/project/shader files and 28,512 physical lines under the enumerated source extensions (blank/comment lines included). This is real implementation, not just a proposal: `Sed.Core` contains a level model, editing commands, undo, geometry, lighting, queries, and consistency validation; `Sed.Formats` contains JKL, GOB, MAT/CMP, 3DO, KEY, DF/ASC import, COG and episode handling; `Sed.Rendering` constructs render scenes; `Sed.Rendering.Vulkan` uses Silk.NET; `Sed.App` is an Avalonia UI; `Sed.Plugins` provides managed plugins. The UI has file/game workflows and inspectors wired to commands.

Build declarations are concrete: `dotnet/src/Sed.App/Sed.App.csproj:10-12,23` uses Avalonia 12.0.4 and .NET 10; `Sed.Rendering.Vulkan/Sed.Rendering.Vulkan.csproj:27-37` uses Silk.NET 2.23.0, .NET 10, and unsafe code. `dotnet/Sed.slnx` ties the projects and tools together. Vulkan's project file embeds checked-in SPIR-V (`:8-10`) and only recompiles GLSL if one of two Unix `glslangValidator` locations exists (`:12-25`). A Windows source build therefore does not need that compiler merely to use the checked-in shader binaries. Runtime Windows/Linux use the system Vulkan loader (`NativeVulkanLocator.cs:16-19`); macOS searches for Homebrew Vulkan/MoltenVK installations. No runtime availability or successful build is asserted by this audit.

`dotnet/ARCHITECTURE.md:24,30,75` records author claims of rendering on an M4 Pro and 243 passing tests. These are repository claims, not reproduced evidence. The current tree has **278 `[Fact]` attributes**, no `[Theory]` attributes under `dotnet/tests`, and 39 `*Tests.cs` files; attribute counts are not executed test counts. The public GitHub API returned **zero releases and zero Actions runs** for the fork during this check. `dotnet/PLAN.md:275-282` itself acknowledges thin app tests, no Vulkan tests, narrow GOB-writer tests, and missing map-view functions. Source review also exposes a layer round-trip contradiction below, so a historic all-green claim cannot substitute for running the pinned tree.

## Confirmed source-level preservation defects

References in this section are relative to the pinned fork, usually under `dotnet/`. They identify inspected code, not executed repros. Severity describes the consequences if these paths are used.

### F1 — MotS RGB vertex light is reduced to one scalar when saved

`src/Sed.Formats/Jkl/JklParser.cs:282-306` recognizes extra per-corner data, switches version-1 files to MotS, and imports RGB. `src/Sed.Formats/Jkl/GeoResourceWriter.cs:81-94` then always emits one `c.Intensity.R` value per corner and a single extra-light value, without a project-kind branch. The geometry writer always emits a JK colormap block too (`:65-66`). Thus a MotS level's colored vertex lighting is lost on a plain save, and an Infernal Machine file is also emitted using the wrong geometry layout. The MotS in-memory lighting test (`tests/Sed.Core.Tests/LightCalculatorTests.cs:136-146`) does not cover saving and reloading that color. `LightCalculator.cs:34-35` claims no writer changes are necessary; the actual writer disproves that comment.

This is a critical blocker for switching the user's MotS project to the C# editor. Required acceptance coverage would include distinct scalar/R/G/B per vertex, project-kind preservation, no-edit save, topology edit save, and a game parser load.

### F2 — ARCHLIGHTING text survives, but its thing bindings are discarded

A full `dotnet/src` and `dotnet/tests` scan finds no `ARCHLIGHTING` references. `JklParser.cs:49-60` skips unrecognized sections. `JklWriter.cs:26-33,36-48` regenerates THINGS and copies unmatched source lines verbatim. The architectural-lighting block therefore remains as raw text. However, `JklParser.cs:455-459` accepts only `key=value` tokens after the thing's sector and silently discards the positional architectural-lighting index in token 10; `JklWriter.cs:122-125` writes no corresponding positional index. The binding between the thing and its per-model mesh/vertex arrays is thus lost even on an unedited save.

The audited engine contract establishes that ARCHLIGHTING contains model mesh/vertex data and is selected by that positional thing token (`OpenJKDF2-MotS-Fixes` e9e293a7a98c9a665509af96f9a87a9ca2470996, `src/World/sithArchLighting.c:59-120` and `src/World/sithThing.c:1572-1588`; see the parent report `ENGINE_CONTRACT_AUDIT.md`). It is not a sector/surface-index table: a sector split alone does not invalidate it. A typed thing binding plus model mesh/vertex compatibility validation is required; preserving an unknown section as text is insufficient.

The same design warrants independent scrutiny for numbered references held as raw COG values: `JklParser.cs:499-513` keeps these as strings; `JklWriter.cs:197-207` re-emits them while geometry and thing numbering are regenerated. This is a risk area, not a claim that every COG argument is an index.

### F3 — Sector fields are silently dropped by full regeneration

`JklParser.cs:314-362` handles a small list of sector attributes and never imports `THRUST` or collision boxes. `src/Sed.Core/Model/Sector.cs:19,26` has storage for both. `GeoResourceWriter.cs:112-129` reconstructs sectors without THRUST/COLLIDEBOX output. Consequently, an existing level with those fields loses them even on an otherwise uneventful save. Whole unknown sections surviving does not protect unknown fields inside a recognized, rewritten section. The full-tree static inventory also makes clear that the consistency checker does not detect such losses.

### F4 — Layer-name writer and reader disagree

`JklWriter.cs:217-219` writes each layer name as `#<name>`. `JklReader.cs:37-41` strips `#` and skips the resulting empty line. `JklParser.cs:523-535` then uses the next non-comment line as the layer name, so sector/thing assignment lines are consumed in the wrong roles. This is a direct source-level contradiction for any saved nonempty layer block. The fixture in `SectionRoundTripTests.cs:68-75` uses unprefixed input names; the test at `:197-207` only asserts count and one default assignment rather than exact names and all membership. Runtime execution is still needed to record the actual failing behavior, not to establish the parser/writer mismatch.

### F5 — Native JED project import is not implemented; JKL light cardinalities are permissive

The C# editor implements the text JKL format, not a native `.jed` project reader. The file workflow advertises `.jkl`, `.gob`, and `.goo` (`src/Sed.App/MainWindow.cs:259-260`), and the format inventory has no native-JED loader. The legacy twelve-field `.jed` light record is a different format from the JKL LIGHTS section, so its absence is a project-migration gap, not evidence that ordinary JKL lights are decoded with the wrong layout.

Within JKL itself, `JklParser.cs:466-493` accepts any record with at least seven fields and assumes flags/layer/XYZ/range/intensity, optionally RGB when at least ten values are present. Eight/nine fields or surplus values are silently accepted/discarded. That is a malformed-input diagnostic gap. A future native `.jed` importer would separately need the legacy layout classification being implemented in the Delphi branch; this audit does not claim native `.jed` import is an existing supported C# feature.

### F6 — Cleave undo fails to restore the owning sector on moved vertices

`src/Sed.Core/Editing/CleaveSectorCommand.cs:113-118` assigns vertices in the negative half to `newSector`. Its snapshot restore (`:392-416`) restores sector vertex lists and surface owners/corners/adjoins, but never resets each moved vertex's `Sector`. `src/Sed.Core/Model/Sector.cs:53-57` confirms `Renumber` only assigns surface numbers. Undo therefore leaves original-sector vertex lists containing vertices whose owner still points at the removed new sector. This matters because later edits use the owner to add/remove geometry. Existing `CleaveSectorTests.cs:97-115` checks restored surface owners and counts but never checks restored vertex owners; the forward-operation test does check ownership (`:62-78`). Add ownership assertions after undo/redo and an edit-after-undo scenario.

### F7 — Cleaving an existing portal does not preserve its matching polygon

`CleaveSectorCommand.cs:180-238` splits the local surface and creates a second local surface. It does not split an existing opposite-sector adjoin partner. `CopyProps(:332-345)` does not copy/remap adjoins. A preexisting portal that crosses the cleave plane can therefore retain a full opposite polygon matched to only one partial polygon, with the other part disconnected. The whole checker (`src/Sed.Core/Validation/ConsistencyChecker.cs:14-79`) verifies reverse object links but not matching adjoin polygons, so a reciprocal link alone can pass while geometry is incompatible. Existing cleave fixtures build isolated boxes (`CleaveSectorTests.cs:11-17`) rather than a box already adjoined to a neighboring sector. This needs a cross-sector portal fixture before promoting the operation.

### F8 — Save-and-test is incomplete for MotS/OpenJKDF2

`src/Sed.App/MainWindow.cs:485-511,550-560` always writes a `.gob` containing just one JKL. It does not package custom dependencies or choose MotS's normal `.goo` container extension. `AppSettings.cs:101-106` selects retail `jk.exe`, `jkm.exe`, or `ijm.exe`; no OpenJKDF2 profile exists. `MainWindow.cs:536` chooses installed-game mode, defaulting to JK for loose levels, rather than deriving it from the edited level, so a loose MotS level can select the wrong configuration. Ad-hoc resource discovery (`:299-313`) also searches only JK `Res2.gob`, not MotS resource names.

The generated Windows batch quotes game/project directories (`MainWindow.cs:591-598`), which improves space handling relative to some legacy editors, but it is only generated once and can retain stale paths after setup changes. Percent expansion in batch paths is not escaped. The custom-template path inserts raw placeholders (`AppSettings.cs:118-123`) then runs a shell. On non-Windows it selects `/bin/sh` but still passes `/c` (`MainWindow.cs:578-583`), whereas this path requires `-c`. These are concrete portability/integration defects, not reasons to discard the whole C# effort.

### F9 — Parser defaults hide malformed data; validation is narrower than editor parity

`JklReader.cs:100-110` converts failed numeric parses to zero. `JklParser.cs:286-297` treats any intensity count greater than the corner count as RGB and fills absent components with `ElementAtOrDefault`; malformed intermediate counts are accepted. Invalid geometry indices become zero positions/UVs (`:384-401`). Several header branches guard fewer tokens than they subsequently index (`:112-113,121-122,138-141`). These paths mix silent repair with potential exceptions rather than clear line-based diagnostics.

The 80-line consistency checker checks minimum counts, normal length, planarity, reciprocal links, and the 24/64-corner limit. It does not implement full convex-sector validation, matching portal geometry, the complete engine's resource/index invariants, or export preservation checks. Port-completeness claims should distinguish that useful initial checker from the broader legacy checker.

## Useful implementation work that is actually present

The fork is valuable source for a future effort once the data contract is repaired. `EditHistory.cs:28-51` implements command undo/redo; `CompositeCommand.cs:23-30` groups edits and reverses their order on undo. `CalculateLightingCommand.cs:42-78` snapshots old and computed lighting and replays the captured result for redo, a useful deterministic approach. `CleaveSectorCommand.cs:194-225` interpolates UV/RGB at plane crossings and welds cut vertices (`:250-266`). `GeoResourceWriter.cs:19-61` pools world/UV vertices and assigns global/adjoin indices; its game-specific output and reference preservation need repair, not an inference that no serialization exists.

These command implementations are not exception-transactional: `EditHistory.Do` calls `Apply` before recording an undo item, and composite commands do not roll back already applied children if a later child throws. That is an additional robustness concern for future parser/geometry validation work rather than a demonstrated normal-input failure here.

The actual feature inventory is broader than the remaining tasks suggested by stale comments. For example, the parser's opening comment says COGs/lights/layers/adjoins are skipped, but current dispatch and `ResolveAdjoins` implement them. Conversely, the architecture table's blanket completion language is too broad for the preservation defects above. Use code, targeted tests, and engine acceptance as authority.

## Coverage, artifacts, and limits

`Build-SedForkInventory.ps1` is our own inventory generator. It hashes all 445 acquired files, counts physical source lines, inventories all new code/project/shader files, and performs full-tree navigation scans for game modes, ARCHLIGHTING, TODO/stub patterns, process execution, file writes, parser defaults, and geometry/undo hooks. `SED_FORK_SOURCE_FILES.sha256.json` contains those hashes; `SED_FORK_STATIC_SCAN.json` contains 116 file/pattern line-location records. Pattern hits are navigation aids, not semantic verdicts.

Direct semantic review covered the entire JKL writer, geometry writer, token reader, parser's main sections, layer/light/sector handling, command history, lighting undo, whole sector cleave and snapshot behavior, consistency checker, save/test launch, app settings, Vulkan build declarations and loader locator, representative parser/round-trip/lighting/cleave tests, and architecture/plan claims. Render correctness, general UI behavior, all import/export formats, every editing command, plugins, all tests, and all third-party licensing were not exhaustively reviewed. Neither successful compilation nor any passing/failing executed test is claimed. The acquisition and artifact creation do not change SED, the engine, installed tools, or game data.

No redundant behind-only fork checkout was created. Thekovic's small unique patch is retained in the API comparison evidence (`SED_fork_thekovic.json`) and summarized in the manifest; davidagnome's large delta required the single pinned archive acquisition. The older alternatives inventory remains separately bounded and should not be described as containing this later fork supplement's files or line totals.
