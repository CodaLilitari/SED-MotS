# Alternative JKDF2 / MotS editor source audit

Audit date: 2026-09-08. Purpose: identify useful behavior, defects, and source-provenance limits for a MotS/OpenJKDF2-focused SED fork. This is a bounded static source audit. No downloaded editor, archive executable, add-on, build script, or test suite was executed. No SED or game-engine source was changed by this audit.

## Findings that should drive the SED work

1. Check both public automation interfaces for the inherited `NewLevel(mots)` inversion. NJED and the archived JED 0.951 code dispatch `true` to the JK new-project action and `false` to MotS. This is present in both the native plug-in bridge and OLE implementation.
2. JKL surface extra light is parsed but dropped during JED/NJED object construction. NED independently includes the missing assignment. The SED data-audit agent reports that SED already fixes this; retain it as a regression case, not a new patch claim.
3. Require exact surface-light cardinality before reading either N scalar values or 4*N MotS values. JED, NJED, and NED all use a weaker `count > N` heuristic before indexing a 4*N sequence.
4. Legacy editor-light records contain distinct scalar intensity and RGB intensity. They cannot be indiscriminately collapsed without changing MotS lighting. The secondary RGB range is persisted but unused by the audited NJED lighting calculation; it is also hidden in that editor's UI. Preserve this distinction during SED migration.
5. Learn from NJED's OBJ/UV importer, surface tools, and editable vertex lighting, but do not transplant them unreviewed: negative OBJ indexes are unsupported, inset has fixed-size arrays, and manual light editing has undo disabled and omits RGB controls.
6. A modern engine launch profile must replace the old assumption that every MotS test is `jkm.exe`, optionally overwritten by `inject.exe`, with a cached batch file and an unquoted short project path. Neither NJED nor NED supplies an OpenJKDF2 launch contract.

These are source-level findings and proposed regression targets. They are not GUI reproduction claims or proof that each issue also exists in SED.

## Recovered sources and provenance

| Source | Pinned revision or archive | What was recovered | Reuse evidence |
| --- | --- | --- | --- |
| [anov/JED](https://github.com/anov/JED) | `8993c1dea4ba766a08cb5269d165b4c808f25824`, 2021-11-09 UTC | Original author's published Delphi source tree and historical revision notes | No repository-wide license file or explicit general source grant was found in the checked tree. GitHub's repository license field is null. Treat it as historical source evidence rather than automatically GPL-compatible material. |
| [JED 0.951 source mirror](https://github.com/JKBoBoFett/NJED/releases/tag/JED) | `Jed0951_source.zip`; SHA256 `9879302EC0D5118451A0E6C2CCCC5A22443231AA9622CF3A641D910BC06FCF83` | 131 archive entries, extracted to `sources/JED0951` | No general license file in archive. `MemCheck.pas:43-52` grants modification with credit but separately prohibits commercial reuse of identified TD32-derived portions. That restriction belongs to this component, not a blanket statement about every JED file. |
| [NJED Community](https://github.com/JKBoBoFett/NJED/tree/Community) | `6fa7ac5be28638fda7495ff62fe6834d86600e39`, 2025-10-11 UTC | Full Delphi Community branch, including OBJ import and newer editor controls | No repository-wide license found; GitHub license is null. `dglOpenGL.pas:35-42` carries an MPL notice; dependencies have their own terms. New NJED contributions are not automatically covered by SED's license. |
| [ZED 2008-01-31](https://www.massassi.net/programs/jed/ZED_2008-01-31.zip) | SHA256 recorded in `alternative-source-inventory.json` | Binary and `ZED Version.txt`; changelog extracted into notes, executable left inside archive | No ZED source code in this public package. The original JKHub project is unavailable; no independently pinned ZED source tree was recovered. |
| [NED author archive](https://smith.bah.wtf/NED.zip) | SHA256 `B8BDE6E00F79F4082417C571B29419E52AC72E23C0655FF81721B1D7CA908285` | 95 C# / designer / resource files in `sources/NED` | No license, project file, solution, or complete Smith dependencies in archive. The [author's page](https://smith.bah.wtf/) explicitly calls Smith closed source and says NED was hand-transcribed from JED Delphi. Public source access alone is insufficient evidence for unrestricted copying. |
| [jedlib](https://github.com/colin-hurley/jedlib) | `cbf97a3673ab5e712d76e57824fd06991e8c8c76`, 2020-07-10 UTC | Java/SWT wrappers for JED OLE automation | MIT, Colin Hurley, 2020 (`LICENSE:1-12`); preserve notice if reused. |
| [JKL2OBJ](https://github.com/tyduptyler13/JKL2OBJ) | `03e207df13e2e8f9825bd0961cd3d15d6bca0334`, 2017-11-07 UTC | JavaScript JKL geometry-to-OBJ converter and MAT parser | MIT, Tyler Scott, 2016 (`LICENSE:1-12`). This is the named GitHub implementation; do not equate it with every historically named JKL2OBJ tool. |
| [go-jk](https://github.com/joelhays/go-jk) | `2fea40a1d1716ef580b51621639522e24a907924`, 2020-03-09 UTC | Go parsers, static-mesh renderer, model export | No repository-wide license found. Repository also includes `_testfiles` with game-like asset fixtures; they were not used as authoritative game data or copied into SED tests. |
| [jediknight2blender](https://github.com/Ecki81/jediknight2blender) | `88aed6f7182dad72d5042d2f70d5eef0b7ed6b78`, 2021-10-28 UTC | Blender importer for JK/MotS JKLs and assets | `__init__.py:1-12` states GPL version 3 or later. No standalone license file was found. The entry-point notice is positive licensing evidence; retain attribution and check component/material provenance before copying. |
| [smlu/blender-sith](https://github.com/smlu/blender-sith) | `ecc90131bcfdeabd400225e8a72ce94cbff1ec5c`, 2026-04-05 UTC | Current Blender 3DO/KEY import/export and MAT import code | GPL-3.0-or-later explicitly stated in `sith/blender_manifest.toml:15`, with LICENSE and per-file notices. Manifest version is `1.0.0-rc4`, minimum Blender `5.0.0`. |
| [smlu/Matt](https://github.com/smlu/Matt) | `dd92c0d386fdb5f0e9c488eb14e7c08e9bdea394`, 2021-10-27 UTC | C# MAT conversion utility, GOB/CMP readers, bitmap rendering and serialization | GPL version 3 LICENSE is included. No broader later-version choice was established. GitHub identifies [bahstrike/Matt](https://github.com/bahstrike/Matt) as its parent; the parent was not separately acquired or compared. |
| [smlu/jediknight2blender](https://github.com/smlu/jediknight2blender) | `3b9ed8a8757f78ef6c8f040f2b7124e0feec4141`, 2021-10-25 UTC | SED author's fork of Ecki81's importer | GPLv3-or-later entry-point notice; GitHub identifies Ecki81's repository as the parent. It predates the audited Ecki81 head, which incorporates this branch's material-parsing/backface-culling work. Inventoried separately to avoid assuming current code is distinct. |

NJED's default `main` branch contains documentation rather than the editor implementation. The source branch is capitalized **Community**. Its latest release metadata identifies [V0.974](https://github.com/JKBoBoFett/NJED/releases/tag/V0.974), published 2025-10-10, with `NJed_974WIP.zip`. The current Community head is a later archive-cleanup commit. The tag named `JED` points to an initial README-only commit; the actual JED source is a **release asset**, not that tag's Git tree. The mirrored 0.951 archive and `anov/JED` tree are kept as separate provenance entries.

## Detailed source findings

### A1. Both JED automation entry points reverse JK and MotS

Evidence: NJED `JED_COM.pas:1308-1313` and `JED_OLE.pas:2055-2061`; archived JED0951 `JED_COM.pas:1308-1313` has the same native plug-in implementation. In each NJED interface, `true` clicks `New1` and `false` clicks `NewMotsProject1`. The actual UI actions establish the opposite semantic mapping in NJED `Jed_Main.pas:4375-4391`: the ordinary new-project path calls `SetMots(false)` and MotS calls `SetMots(true)`.

Impact: a plug-in or automation caller expressing `NewLevel(true)` can create a JK project even though the argument is named `mots`. `jedlib` forwards its boolean directly to OLE (`automation/src/main/java/net/jkhub/jedlib/automation/JedApp.java:46-53`), so a wrapper cannot repair this contract implicitly.

SED action: verify and correct native and OLE methods together if still present. A useful test must inspect the resulting game's resource/template mode, rather than merely proving which click handler was invoked.

### A2. Parsed JKL surface extra light is lost in JED/NJED

Evidence: NJED `level_io.inc:210-235` parses temporary surface attributes, but `level_io.inc:363-375` copies material, flags, geometry/light/texture mode, and adjoin without copying extra light. NED `JKLevel.cs:1742-1748` includes `surf.extraLight = tsurf.extra` and explicitly comments on the JED omission.

Impact: loading and saving a level can change lighting even if no user edits are made. The sibling SED audit confirmed its current loader already has the assignment. This is evidence for a preservation test using nonzero surface extra light, colored per-vertex light, and sector light independently.

### A3. Malformed intensity counts are treated as MotS without enough values

Evidence: NJED and JED0951 `level_io.inc:255-275` choose MotS whenever intensity count is greater than N and then read `j*4` through `j*4+3`. NED `JKLevel.cs:1595-1617` repeats the same logic. For a 3-vertex surface, 4 through 11 light values enter the MotS branch but cannot supply 12 reads. Fewer than N values also fail in the scalar branch. NED additionally uses `double.Parse` without an explicit culture at this site; missing surrounding Smith startup means a whole-application locale policy could not be established.

SED action: accept the exact supported layouts, reject malformed cardinality with surface/line context, and avoid partially changing game mode or document state before validation completes. Test N, 4*N, N-1, N+1, and 4*N-1 values, plus non-finite/invalid values if the parser supports them.

### A4. Legacy light intensity and RGB intensity are semantically different

Evidence: NJED `level_io.inc:802-807` saves 12 editor-light fields: XYZ, layer, scalar intensity, primary range, flags, RGB range, RGB intensity, and RGB. `level_io.inc:1084-1095` reads that layout and resets RGB intensity/color to scalar intensity/white only for JK. `Item_edit.pas:841-847` exposes RGB intensity in MotS; the RGB range control at 844 and setter at 910 are commented out. In `lev_utils.pas:6555-6566`, both channels use primary `range`, but scalar output multiplies `Intensity` while color output multiplies `RGBIntensity` and RGB color.

Only `LF_NoBlock=1` is defined in `J_level.pas:20`; this flag controls occlusion and does not select scalar versus RGB light. The constructor at `J_level.pas:284-290` defaults intensity and RGB intensity to 10, primary range to 2, and color to white.

SED action: when migrating an old 12-field editor record, preserve channel semantics or give a concrete migration warning for values the new representation cannot retain. A field-count parser fix must not reinterpret RGB range as color or RGB intensity as a flag. Separate editor-source compatibility from runtime JKL format support.

### A5. Legacy light copy corrupts blue and omits state

Evidence: NJED `J_level.pas:292-300`, `TJedLight.Assign`, assigns `b := l.g` rather than `l.b`; it copies intensity, range, RGB intensity, and layer, but does not copy flags or RGB range. The source record is distinct from downstream placement operations, so the exact visible effect depends on callers.

SED action: inspect its light-copy implementation and test copy/paste/duplicate with unequal RGB channels and nonzero flags. Do not assume a viewport-only issue: copied source light parameters can change later baked lighting.

### A6. NJED OBJ import is useful but not a robust arbitrary-mesh level compiler

Evidence: `obj_import.inc:208-239` creates one sector and reads vertices/UVs, with configurable texture dimensions and U/V inversion. `obj_import.inc:249-272` supports vertex-only faces but reports normal-only `v//n` faces as unsupported. `obj_import.inc:258-261` and `289-292` convert indexes by subtracting one directly; OBJ's negative relative-index form is not resolved. Texture indexing uses a nil-returning lookup at `202-205`, so imported UV references also need validation before dereference.

Value: it supplies a real geometry/UV bridge from external modeling tools. Limitation: a renderable mesh is not automatically a closed, convex, portal-connected Sith level. NJED's consistency checker still expects sector convexity (`Cons_checker.pas:166-170`), at most 24 vertices per face (`179`), reciprocal overlapping adjoins (`182-186`), planarity (`189-196`), and surface convexity (`199`). These legacy profile limits should not silently become OpenJKDF2 limits.

SED action: if OBJ import is added or expanded, make geometry-only import explicit, validate indices and finite coordinates first, preserve per-material UV units, and run profile-aware sector checks. Include negative indexes, missing UVs, `v//n`, n-gons, concave meshes, and disconnected components in fixtures.

### A7. NJED surface tools need safer data structures and context

Evidence: `lev_utils.pas:1404-1480` implements inset by scaling vectors from the surface center, creating a new inner loop and surrounding faces. It uses arrays indexed 0..25 (`1411-1417`, `1426`) but iterates the input surface count (`1439-1443`) without a local bound check. A checker warning elsewhere does not guard this operation. `NJEDScaleSurface` receives a surface argument but looks up vertex data through global current selection (`lev_utils.pas:1537-1558`), creating a context mismatch if later used for batch operations.

Value: center-based inset/scale, collapse, and move-along-normal are relevant editing conveniences. SED action: use dynamic structures, perform topology-preserving preflight, bind every operation to the passed surface/sector, and commit a single complete undo record after success. This is a feature reference, not a recommendation to copy these implementations.

### A8. Manual vertex-light editing lacks undo and full MotS controls

Evidence: NJED `U_VertexLight_edit.pas:35-72` provides a grid of vertex intensities, a master-vertex fill option, a 0..5 clamp, and a redraw. The undo calls at `42-43` are commented out. The setter at `61` changes scalar intensity only; no RGB channel controls appear in the form logic. Its 0..5 range is justified by a code comment, not a runtime contract proved by this audit.

SED action: provide scalar and RGB controls appropriate to project type, validate all requested cells before mutating, and ensure one-step undo. Avoid changing runtime numeric limits on the basis of the comment.

### A9. Test-launch design is still tied to older engines

Evidence: NJED `Jed_Main.pas:5488-5531` chooses `jkm.exe`/`jk.exe`, silently changes either to `inject.exe` if that file exists, computes an 8.3 project path, and writes a batch file only if it does not already exist. Its project `-path` argument is not quoted (`5526`). A stale test batch can survive project/engine configuration changes. NED instead generates arguments for Smith (`NedMain.cs:7345-7376`), with Smith's `-mots`, `-ignorejed`, cache, and direct-JKL options; these are not established OpenJKDF2 arguments.

SED action: model engine executable, game/data directory, project directory, and arguments as explicit settings. Generate arguments at launch, quote Windows arguments correctly, and use the OpenJKDF2 contract established by the separate engine audit. Do not automatically prioritize an injector merely because it is present.

### A10. Dependency closure and lighting are useful architectural references

NJED `Savejkl.inc:315-350` seeds resource dependencies from placed COGs, things, and inventory COGs, then repeatedly visits newly found COG/template dependencies until closure. NED `JKLevel.cs:2677-2726` provides a clearer typed C# version of resource collection. However, both contain swallowed load errors: NJED `Savejkl.inc:357-362`, NED `JKLevel.cs:2698-2701`. A modern packaging validator should report missing dependencies rather than silently claiming a complete package.

NED's lighting path is a useful readable reference: `Utils.cs:755-784` recursively traverses adjoins that permit light, tests ray intersections, and marks visited sectors; `Utils.cs:858-876` computes squared distance falloff and distinct scalar/RGB contributions. The background calculation mutates light values after clearing them (`818`) and may return at cancellation (`829-830`), while progress divides by `scs.Count-1` (`828`). A single-sector selection or cancellation therefore deserves explicit tests before adapting this workflow. A cancellable bake should compute into temporary buffers and publish a complete result or restore the previous light data.

### A11. Current blender-sith is a better asset reference, with parser hardening targets

This is actual import/export source for models and animations, not a JKL level authoring bridge. `sith/model/model3doWriter.py:80-86` varies color serialization by 3DO version; `sith/model/model3doExporter.py:149-174` preserves vertex color alongside position when identifying shared vertices and handles UV image-size conversion plus vertical inversion. Hierarchy construction appears at `229-273`. These are useful references for predictable model export settings and semantic preservation.

MAT handling validates magic/version/type/record counts (`sith/material/mat.py:120-139`) and treats the indexed texture header as the source of transparency (`240-243`), rather than assuming a true-color alpha field also describes 8-bit MATs. That distinction is a useful SED import/export regression target.

Two source-level hardening issues need independent tests before using this parser as a trusted validator. The color-depth predicate at `mat.py:138` combines invalid divisibility and out-of-range checks with `and`, permitting depths such as 9, 12, 0, or 64; validation should explicitly recognize supported depths. In `_read_mipmap`, an indexed texture without a CMP follows `237-238` without consuming the pixel payload, while `importMat` calls the reader for each cell at `345-346`. For a multi-cell indexed texture with no palette, the next texture header can therefore be read from the prior cell's pixel data. Both are static findings; Blender import was not run.

Matt supplies a compact 8/16-bit MAT writer (`Material.cs:340-426`) and a loader that repairs a legacy JKPaint header with missing RGB bit counts (`451-466`). Its load loop (`488-502`) derives mip byte sizes by dividing by four, uses unvalidated dimension/count fields, and accepts `BinaryReader.ReadBytes` results without exact-length checks. Its README's pending-feature list is historical and must be read alongside code: the source does contain color-material and multi-texture structures. It is a material-tool reference, not evidence of a complete level editor or a hardened binary parser.

## Related-tool limitations and usable ideas

- **jedlib:** typed, named OLE wrappers, not a standalone JKL serializer. `automation/.../OleObject.java:3-16` uses SWT Win32 OLE; `57-80` invokes methods and properties by dispatch ID. `JedApp.java:17` defaults to `JED.App`. Useful for documenting an automation contract, but SED compatibility depends on its actual ProgID, method signatures, and COM registration. No Java/SWT bridge was run. The MIT license is explicit.
- **JKL2OBJ:** `Convert.js:45-51` drops all adjoin faces; `92-100` writes sector groups and `v//vn` faces. It does not write texture-coordinate references or materials in that output path even though it parses UV data elsewhere. This can remove visible textured portal surfaces and cannot preserve sector topology, COGs, or MotS lighting. Use it as a simple OBJ-export reference, not a level round-trip solution. The MIT license is explicit.
- **go-jk:** `jk/jkparsers/jklLineParser.go:49-65` leaves sectors, header, and sounds unparsed in its section dispatcher; `244-270` stores only one scalar intensity per vertex and ignores conversion errors. It is a static rendering/conversion experiment, not a MotS-preserving authoring representation. No explicit project license was found, and bundled fixtures were not promoted into test authority.
- **jediknight2blender:** meaningful MotS import support includes 4-value vertex lighting (`import_jkl.py:324-327`, `594-597`) and GOO resources, but no JKL export exists in the checked tree. `import_jkl.py:54-55` reads both custom directory names from `prefs.jkdf_path`, a concrete copy/paste error that can misidentify a custom MotS directory. The manual override branch at `83-89` is separate. Its shader/geometry import is a companion reference; Blender APIs from this 2021 code were not validated against current Blender.
- **smlu/blender-sith and Matt:** subsequently acquired and inspected at the parent's request; see A11. The author's public repository inventory returned no separate `jk*`-named authoring-script repository. The related `smlu/jediknight2blender` fork is preserved and classified above; general game engines and Indiana Jones-only tooling remain outside this alternative JK/MotS editor component audit.

## Unavailable or non-source entries

- **JKEdit:** [3.24 archival mirror](https://www.moddb.com/downloads/jkedit-324) and [recovered official tutorials](https://www.moddb.com/tutorials/jkedit-official-tutorials) establish the editor's history and workflow. No public JKEdit source repository or source archive was recovered after exact-name/creator and GitHub repository searches. A binary download is not evidence that source is open. Its interface can inspire requirements, but no code-level JKEdit audit was possible.
- **ZED:** the Massassi download contains `ZED.exe`, `ZED Version.txt`, and a tiny unnamed entry, with no `.pas` files. The retained changelog documents World Things Limit, episode defaults, display adjustments, and testing flags. It is release-author documentation, not inspected source. The changelog's stated limits should be checked against the engine before reuse.
- **Smith:** the author's page calls the engine closed source. The linked `bahstrike/Smith` repository returns not found. NED's public source excerpt is distinct from the complete engine and depends on unavailable Smith types and entry points.
- **LEIA:** no public source recovered. A [2015 developer exchange](https://www.jkdf2.net/articles/yves-borckmans-conversation-2015/) records Yves Borckmans saying LEIA was not publicly released and identifying its embedded engine preview as an advantage. This is historical testimony, not a current code distribution.
- **ModDB JED source mirror:** the source listing is readable, but direct download returned an anti-bot challenge. No challenge was bypassed. The separately available NJED GitHub release asset provided the preserved 0.951 source instead.

## Validation and boundaries

Read-only searches covered the named editors, Massassi's current tool index, author repositories/pages, GitHub exact-topic repository searches, and the related JKL tools returned by those searches. This does not certify that every unindexed archive, private fork, or plug-in on the internet was found.

The final acquisition inventory contains 12 entries, 914 hashed files, and 492 source files comprising 280,324 physical lines. These totals include duplicate forks/archived revisions, generated files, and third-party code. They describe inventory and pattern-scan coverage, not 280,324 lines of manual review. The nine Git checkouts were clean after the audit.

`alternative-source-inventory.json` records source URLs, pinned Git revisions, archive hashes, file/line counts, license status, coverage classifications, and repository clean-state checks. `alternative-source-files.sha256.json` records files in the acquired trees, excluding `.git` internals. `alternative-static-scan.json` records full-tree source-pattern locations for game mode, serialization, geometry, asset IO, launch operations, license notices, and defect markers. A pattern hit is not a reviewed defect; those results are inventory/navigation evidence. Every candidate source file was hashed and counted, but only the report's named components received manual semantic review. No claim is made that every source line was manually read.

Acquisition hashes and static inspection provide reproducible evidence; they do not prove the old editors build or run on this machine. No installed game assets were read or modified in this subtask. The next-stage tests should be newly authored synthetic fixtures plus explicitly authorized installed game data, not unproven downloaded fixtures.
