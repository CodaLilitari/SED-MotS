# Audit map and provenance

The active development line is the C#/Avalonia/Vulkan fork at upstream `908d53d70d6b95784a92e9f1746ab5b13afcfdf0`, on local branch `codex/mots-dotnet`. Its primary engine contract is the user's OpenJKDF2/MotS checkout at `e9e293a7a98c9a665509af96f9a87a9ca2470996`. Retail behavior is a separate target with narrower evidence.

## Current .NET evidence

- [MotS format preservation](NET_MOTS_FORMAT_AUDIT.md) and [stock corpus receipt](net_mots_format_evidence.json): parsed data and forced regeneration compared independently of unchanged-source retention.
- [Native JED/SED/NED import](NET_NATIVE_JED_IMPORT.md): source-derived layouts, synthetic fixtures, explicit unsupported records and template-library dependencies.
- [Windows and engine contracts](NET_ENGINE_CONTRACTS.md): executable/path/episode handling, validation, recovery, portal editing and headless Vulkan evidence.
- [Agent protocol](../AGENT_PROTOCOL.md): JSON tool behavior, selectors, planning/output rules and limits.
- [Clipboard dependencies](NET_CLIPBOARD_DEPENDENCIES.md): layers, parent-first template snapshots, colormaps and reversible transfer guards.
- [Aggregate build checkpoint](NET_BUILD_CHECKPOINT.json): 25 projects built with zero warnings/errors; 461 tests passed with zero failures or skips before packaging.
- [Distribution verifier fixtures](NET_PACKAGE_VERIFIER_FIXTURES.json): 30 synthetic ZIP/manifest/provenance checks passed; Git transport was mocked for those fixtures. Actual artifact and Git bundle verification runs during packaging.
- [Massassi community format audit](MASSASSI_COMMUNITY_FORMAT_AUDIT.md): 94 reachable JKL documents passed parsing, unchanged-text and forced modeled regeneration checks; seven installer packages remain untested.
- [Legacy editor collection](LEGACY_EDITOR_COLLECTION.md): acquired versions, companion tools, unavailable packages and compatibility priorities.
- [LucasArts/Sith archaeology](SITH_OFFICIAL_SOURCE_ARCHAEOLOGY.md): direct documents, community reconstructions, prototype catalog records and unresolved claims kept distinct.

Build/test receipts are produced by `tools/Build-Net.ps1`; the final package records its source commit and packaged-agent smoke in `PROVENANCE.json`. Counts in individual reports describe their own checkpoints and must not be added together as distinct tests.

## Original Massassi corpus

The external `sed-editor-audit/corpora/massassi-originals-2026-09-09` collection contains 50 original publisher ZIPs: 13 JKSP, 13 JKMP, 12 MotSSP and 12 MotSMP. Each category was ranked by displayed rating, then download count, with at least five votes. This is a reproducible selection rule, not an objective ranking of artistic quality.

The acquisition manifest records authors, file IDs, exact URLs, category, ratings/download snapshot, byte counts, SHA256, ZIP member lists and available readmes. The 50 original ZIPs are 658,328,834 bytes in total with no duplicate SHA256 groups. Some packages contain multiple maps, so package and parsed-level counts differ.

This corpus is separate from the user's WBW conversions. The acquisition/audit tools use explicit manifest paths for the original corpus; no WBW outputs were used. Community assets and recovered installer payloads stay outside the editor source and distribution. Installer wrappers are inspected as data, never executed.

## Historical reports

The `historical` folder preserves the prior Pascal SED audit and broader editor/fork source inventory. It includes JED, NJED, NED, ZED, Blender-related importers, conversion libraries and all discovered public branches of the inspected SED forks. Inventory/static-scan counts include dependencies and overlapping sources; they do not mean every line was manually audited.

Historical recommendations to retain Delphi describe an earlier decision. The user subsequently authorized the .NET pivot; the historical reports remain provenance, not the current build instructions. The successful Pascal GUI build and its FPC test evidence belong to the separate `SED-MotS` milestone and are not evidence that the C# GUI passed those tests.

## Open work and nonclaims

The current source is a GPL fork with attributed source history, not a strict clean-room reconstruction. No original LucasArts Sith source/SDK leak was verified. Recovered design documents are research evidence, not an implied source-code license.

Native project import still needs real authored JED/NJED/ZED samples and additional dialect coverage. JKEdit's proprietary native format, complete editor-metadata round-tripping, automatic paired portal splitting, arbitrary loose-resource resolution, dependency-complete level packaging, architectural relighting and same-name model topology/hash verification need further work. Engine gameplay and multiplayer validation remain unexecuted. These gaps are stated explicitly instead of equating format tests or a rendered image with a production-ready editor.
