# Legacy editor acquisition and compatibility priorities

Acquired 2026-09-09, without running the downloaded tools. The collection has **62 newly downloaded archives/installers, three previously acquired archives reused in place, and three failed plugin-source downloads**. These totals include editor versions, plugins, asset companions and related-engine tools; they are not 65 distinct level editors. The authoritative package inventory is [LEGACY_EDITOR_MANIFEST.json](LEGACY_EDITOR_MANIFEST.json), with URLs, versions, exact bytes, SHA256, available source files, document paths and licensing status. Original packages remain unchanged outside the editor repository.

## What was recovered

| Family | Acquired packages / evidence | Status and relevance |
|---|---|---|
| JED | 0.92, 0.94, 0.951 binaries; existing 0.951 source archive and anov/JED checkout | Complete community level editor. Legacy native project fidelity is a primary compatibility target. [Massassi programs](https://www.massassi.net/programs/) |
| SED | 0.2.0 and 0.3.0 release ZIPs; working Pascal and C# forks retained separately | Modern JED descendant. 0.3.0 release ZIP matches GitHub's SHA256 digest. [Publisher releases](https://github.com/smlu/SED/releases) |
| NJED | 0.97, 0.971, 0.972, 0.973 test, 0.973 and 0.974 WIP ZIPs; 2016 0.97 source archive | Version history and newer editing features available for comparison. 0.974 ZIP matches GitHub's digest. The redundant 0.973-test 7z representation was not downloaded. [Publisher releases](https://github.com/JKBoBoFett/NJED/releases) |
| ZED | Existing 2008-01-31 binary ZIP reused | Changelog and binaries recovered; no source or general license recovered. [Massassi release](https://www.massassi.net/programs/jed/ZED_2008-01-31.zip) |
| NED / Smith | Existing NED.zip source reused; Smith_Setup.exe downloaded | NED is a C# JED transcription. Smith's NSIS installer was listed with 7-Zip, not installed; Smith.exe is stamped 2024-05-02, which is a file timestamp rather than a verified product version. Publisher calls Smith closed source. [Publisher](https://smith.bah.wtf/) |
| JKEdit | 3.24 listing and recovered tutorials; 2.57 mirror lead | **Package not acquired.** ModDB HTTP returned 403 challenge; the old Tripod mirror returned 502. ModDB lists jkedit.zip, 1,095,847 bytes, MD5 133265ac1064653410ac85157e9b3689. Those are publisher metadata, not our verification. [Listing](https://www.moddb.com/downloads/jkedit-324), [archived original tutorials](https://www.moddb.com/tutorials/jkedit-official-tutorials) |
| LEIA / ModelX | Original-looking internal LEIA design PDF and developer testimony; ModelX described in DroidWorks postmortem | No publicly downloadable executable, editor source or official Sith SDK was located in this search. See the separate archaeology report. |
| JED plugins | 36 plugin/archive downloads from the current Massassi index; JED Objects Reference documentation | Useful algorithm and workflow history, but each plugin has its own license and host expectations. The source links jktree-src.zip, SecCleave_10_src.zip and vinkelsrc_10.zip return 404. [Plugin index](https://www.massassi.net/jedplugs/) |
| Asset/archive companions | Container Manager 0.9a, GOB Explorer, JKGOB 0.4b, Puppet Jedi 0.7, JKPaint 0.8, Mat16 0.9, CogWriter 0.3 | Support archive, model/animation, material/palette and script workflows. They do not replace a full level editor. |
| Related modern tools | Pinned Urgon and SmithPlugin source ZIPs; blender-ijim current archive | Urgon handles Infernal Machine CND/NDY and assets. SmithPlugin exposes the community Smith engine plugin interface. blender-ijim now consists only of a redirect README to the previously acquired blender-sith; it is not an additional implementation. [Urgon](https://github.com/smlu/Urgon), [SmithPlugin](https://github.com/bahstrike/SmithPlugin), [Blender redirect](https://github.com/smlu/blender-ijim) |
| Related official Raven tools | JK2EditingTools.zip and JK2EditingTools2.exe | Preserved in `related-raven/`. Different idTech3 family, not Sith/JKDF2 compatibility code. The update is a self-extracting ZIP; passive inspection found 136 C/C++/header files. Its tools/readme is retained. [Massassi archive](https://www.massassi.net/programs/) |

## Native-format compatibility priorities

| Input or workflow | Evidence and practical priority |
|---|---|
| JED / SED / NJED / ZED native `.jed` | Preserve the editor project separately from exported JKL: lights, layers, selection/editor metadata and authoring state may not have a game-format equivalent. The prior pinned source audit examines JED/JKL readers and the legacy light layout. Importing these projects requires a deliberate native parser, not renaming the file. |
| NED-generated `.jed` | Important signature variant: recovered `sources/NED/JKLevel.cs:2153` writes `NED <version>`, while `NedMain.cs:1718` offers `*.JED` and `:3437` opens `.JED`. The extension alone does not prove a JED header. NativeJED owner has been notified. |
| Exported JK and MotS `.jkl` | Highest broad compatibility value across editors and community levels. Preserve both game dialects and unknown/raw sections where possible; distinguish a byte-preserving no-edit save from regeneration. Test semantic preservation after regeneration, including MotS scalar-plus-RGB intensities, thing parameters, ARCHLIGHTING and sector bounds. |
| `.gob` and `.goo` | Containers may hold several JKLs and shared assets. Inventory and resolve nested paths; do not treat every container as one level. The recovered corpus includes installer-only wrappers, so an unreadable wrapper must be reported rather than counted as a parser failure or success. |
| JKEdit native project | Native extension/schema **not established from an acquired package**. Tutorials confirm MotS level creation, GOO resource browsing, COG/template editing and testing, but do not justify a native-format implementation. Prioritize exported JKL/GOB/GOO and retain the native importer as an explicit gap. |
| JED `jed2prj` plugin | Not a JKEdit converter. Its bundled `Jed2Prj_Readme.doc` identifies ACE_DXF/X-Wing OPT authoring; it exports geometry without texture information. `.prj` from this plugin must not be assumed to be a JKEdit project. |
| 3DO / KEY / MAT / CMP / OBJ / DXF | Asset interchange has different loss boundaries. Blender Sith is primarily 3DO/KEY import-export and MAT import; a JKL-to-Blender/OBJ importer does not provide complete editable level roundtrips. |
| Infernal Machine CND / NDY | Related Jones3D formats, documented by Urgon's libim and cndtool. Keep a distinct format profile; do not feed these into a JK/MotS JKL parser merely because both descend from Sith. |

## Inspection and rights boundaries

ZIP central directories were inventoried and text documentation up to 1 MiB per file was read into an inspection directory. All original package hashes were recorded. Smith and Raven installer containers were listed with installed 7-Zip; the Raven SFX ZIP was also read with Python ZipFile. No installer, plugin, downloaded script or editor was executed. This work establishes acquisition and selected source/document findings, not runtime compatibility or a manual audit of every line.

The existing source audit remains `alternative-editor-source-audit.md` plus `alternative-source-inventory.json`; the C# fork audit remains `SED_FORK_DELTA_AUDIT.md`. Many legacy editor sources have no repository-wide reuse grant. Example plugin notices impose noncommercial distribution conditions. Smith is proprietary; SmithPlugin has no repository-wide license in API metadata. These packages are reference material and are not being copied into the GPL fork or redistributed with the editor. SED's GPL license does not relicense the other editors.

This is a documented collection of the packages discovered and reachable during a bounded search, not a claim that every historical editor version, private prototype or disappeared mirror has been recovered.

The raw archives, source checkouts and inspection documents referenced above are external research material under `C:\Users\Andrei\Documents\ChatGPT\sed-editor-audit\`. Local companion reports in this directory retain their exact baseline revisions; they do not describe the current modified fork as unmodified upstream.
