# Original Massassi corpus format audit

Recorded 2026-09-09T03:38:52.1215446Z. All **50 original ZIPs** matched acquisition sizes and SHA256 hashes. The verifier reached **94 JKL documents**, parsed **94**, and passed unchanged-text and forced modeled regeneration for **94**. There are **0 parse failures**, **0 roundtrip failures**, **0 container failures**, and **0 bounded skips**. There are **91 geometry documents**; resource-only/static JKLs are included in document totals but not represented as playable maps.

The original acquisition is 13 JK single player, 13 JK multiplayer, 12 MotS single player, and 12 MotS multiplayer: category rating descending, downloads descending, ID ascending, minimum five votes. Fifty unchanged public ZIPs total 658,328,834 bytes; no WBW conversion paths were read. Packages can contain several levels, resource JKLs, or installers. Thus package count, document count, geometry count and successful compatibility checks are separate measures.

| Category | Reached JKLs | Parsed | No-edit text passes | Modeled regeneration passes |
|---|---:|---:|---:|---:|
| jksp | 44 | 44 | 44 | 44 |
| jkmp | 28 | 28 | 28 | 28 |
| motssp | 10 | 10 | 10 | 10 |
| motsmp | 12 | 12 | 12 | 12 |

## What the checks establish

The verifier normalizes CRLF/CR to LF and compares an unchanged JklWriter result with the source. It snapshots explicitly modeled data before either writer call, forces regeneration through a fresh JklDocument, reparses, and compares modeled data and material tables. The digest includes ordered geometry, scalar/RGB light values, flags, bounds, lights, thing/template parameter order, repeated parameters, and architectural lighting bindings/data; editor IDs and global pooling indexes are excluded. Each document records source hash, phases, warnings, both model digests and a first differing property when available.

A passing no-edit check alone would not establish that the model understands a field. The regeneration check adds meaningful coverage of the declared model; it still does not certify unknown fields/sections, resources, gameplay, COG behavior, rendering or engine load. No GUI or game was launched.

## Findings and limits

- Every reached JKL passed both preservation checks after reparsing.
- Retained source warning for Massassi 6, `impsiege.gob!/jkl/impsiege.jkl`: Line 143520: a legacy final frame parameter has six finite coordinates but no closing parenthesis. Its original value is retained, matching the engine's six-conversion parser.
- Retained source warning for Massassi 3123, `TODOA.exe!/TODOA.gob!/jkl/00_exit_manoeuvre.jkl`: Line 77421: a legacy eyeoffset parameter has three finite coordinates but no closing parenthesis. Its original value is retained, matching the engine's three-conversion parser; following parameters remain separate.
- Retained source warning for Massassi 3123, `TODOA.exe!/TODOA.gob!/jkl/01b_drowning.jkl`: Line 177048: a legacy eyeoffset parameter has three finite coordinates but no closing parenthesis. Its original value is retained, matching the engine's three-conversion parser; following parameters remain separate.
- Retained source warning for Massassi 3123, `TODOA.exe!/TODOA.gob!/jkl/02_nexus.jkl`: Line 9444: a legacy eyeoffset parameter has three finite coordinates but no closing parenthesis. Its original value is retained, matching the engine's three-conversion parser; following parameters remain separate.
- Retained source warning for Massassi 3123, `TODOA.exe!/TODOA.gob!/jkl/01_suffocation.jkl`: Line 203691: a legacy eyeoffset parameter has three finite coordinates but no closing parenthesis. Its original value is retained, matching the engine's three-conversion parser; following parameters remain separate.

The first run found a missing final parenthesis in Imperial Siege on Derra IV's six-coordinate frame. A narrow, engine-confirmed tolerance now preserves that spelling and reports a warning. The initial failure receipt is retained externally. The recovered TODOA levels also exposed missing closing parentheses in a three-coordinate eyeoffset followed by another parameter. The final run confirms all four affected documents now pass both preservation checks: the engine-confirmed tolerance preserves the spelling, reports a warning, and keeps the following COG parameter separate. Malformed variants remain outside these narrow exceptions.

**7 packages still have no reachable JKL**: The New Empire (4), The New Empire Part 2 (1332), Scream Multiplayer Beta 1.4 (2535), Leeza's Destiny 3.0 (711), Birth of a Mercenary SE (1431), Rebel Agent (1900), Liberating Cloud City (2503). Installed 7-Zip could not decode six of these legacy installer formats; Birth of a Mercenary SE exposed only a partial font ZIP with a header error. See MASSASSI_WRAPPER_INSPECTION.md. These are unresolved container formats, not failed JKL parses or compatibility passes.

TODOA was recovered passively from four CRC-validated embedded 7z archives. Only five contained GOBs were streamed to generated safe paths. The verifier accepts those only with matching original ZIP provenance and payload hashes, direct package inspection paths, no reparse points, and bounded file streams. The original ZIP remains unchanged. No `.jed` project sample was found in the original/editor-package inventories or final nested GOB inventories.

## Reproducibility and artifacts

Source: `dotnet/tools/Sed.CommunityAudit/Program.cs` and its project/README. Build: .NET 10.0.401, Release, isolated `build/community-audit-isolated`, zero warnings and errors. Build and verifier exit codes, input and assembly hashes are in MASSASSI_COMMUNITY_BUILD_RECEIPT.json. The corpus itself and recovered payloads stay outside this repository.

- MASSASSI_COMMUNITY_FORMAT_AUDIT.json: per-package/document results, failures, warnings and coverage limits.
- MASSASSI_ORIGINAL_CORPUS_MANIFEST.json: original public URLs, IDs, titles, authors, ranking metrics, bytes, SHA256 and metadata paths.
- MASSASSI_WRAPPER_PAYLOAD_MANIFEST.json: parent ZIP/wrapper/embedded-archive/payload provenance; unsupported wrappers.
- MASSASSI_WRAPPER_INSPECTION.md: passive recovery procedure and exact remaining gaps.

These checked-in artifacts contain metadata only. No third-party ZIP, GOB, GOO, JKL body, installer or game asset is included.
