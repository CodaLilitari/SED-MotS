# Authored native JED fixture recovery — 2026-09-09

This bounded follow-up recovered two genuine author-distributed native JED projects from live Massassi tutorial links. They are historical authored files, not synthetic fixtures or JKL-to-JED reconstructions. No downloaded code, browser, GUI or game was executed. No release source or Git state was modified.

The previous audit's 65 editor/plugin packages and 50 community map archives were consulted to avoid repeating their inventory scans. This pass concentrated on original tutorial attachments and a few specifically documented prefab releases. No Wayback Machine path was used.

## Recovered projects and provenance

| Project | Author and primary source | Native signature | Game marker | Bytes | SHA256 |
|---|---|---|---|---:|---|
| `perfect_polygon_prefab.jed` | Brian Lozier, [Advanced Architecture Techniques](https://www.massassi.net/tutorials/advanced_architecture_1/); [original project](https://www.massassi.net/tutorials/advanced_architecture_1/perfect_polygon_prefab.jed) | JED 0.91 | JK, no MOTS marker | 12,091 | `77901aa4f8887e9c3de00e568b9a4e014e71d95d4a3b40984e853899db35b590` |
| `building.jed` | Dino Delucchi, [A New Twist on Sectors within Sectors](https://www.massassi.net/tutorials/outdoor-3/); [original project](https://www.massassi.net/tutorials/outdoor-3/building.jed) | JED 0.85 | `SEC: MOTS` | 68,405 | `339849792cfaead32604a56ca1c119c131645f3742739b15aa97ca63a92f2b83` |

The current tutorial index dates the building tutorial to 1998-09-20 and Advanced Architecture Techniques to 1999-05-03; these page dates do not establish the creation dates of the attached projects. The native signatures are direct file evidence. Source-page snapshots are in `source-pages/`. The project files contain original author data and resource names; they are retained only in this external research directory.

Rights status: the authors/site deliberately offer the project downloads as tutorial examples. The fetched pages carry the Massassi contributors' copyright notice, and no explicit blanket license granting redistribution as editor test assets was established. Do not include these files, their converted derivatives or page snapshots in the editor source/release without resolving that permission. Authorship and public availability are recorded separately from licensing.

## Executed compatibility checks

The existing self-contained Windows `sed-agent.exe` was used from the verified development package for source commit `5febeb351710730dd86ced1cc787bb07778cfffc`. Its SHA256 is `b30d37387173aa9095cea9465238c5f1ee9cee8d272da028fe69c7ddcf7eb612`. The companion template libraries were the package's existing `data/master.tpl` and `data/mots.tpl`; no project-specific template overrides were added. Their hashes are recorded in `manifest.json`.

For each original project, these commands succeeded with exit code 0:

1. `sed-agent inspect original.jed`
2. `sed-agent convert original.jed new-output.jkl`
3. `sed-agent inspect new-output.jkl`
4. `sed-agent validate new-output.jkl`

| Project | Sectors | Surfaces | Vertices | Things | Lights | COGs | Architectural objects | Validation result |
|---|---:|---:|---:|---:|---:|---:|---:|---|
| Polygon prefab | 17 | 90 | 112 | 1 | 1 | 0 | 0 | Valid; original player has sector -1 and produces a warning |
| MotS building | 39 | 431 | 706 | 1 | 1 | 0 | 0 | Valid; no structural issues |

All listed counts are unchanged after conversion. For every sector, the CLI's detailed inspection record was compared before and after conversion: **56/56 exact matches**. These records expose vertex positions, sector flags, material names/indices, surface flags, adjoining-sector references, corner indices, UVs, and mono/RGB corner values. `sector-detail-roundtrip.json` records sector IDs and hashes instead of duplicating the authored geometry.

This is a check of the packaged import/export model and exposed inspection fields. It is not an independent proof that every native field was interpreted correctly, and it does not compare an original editor rendering or execute either engine.

## Fidelity limits observed

- Both imports report that native `LVIS` layer-visibility metadata is not represented.
- The MotS building's native light row is `0 0 0 0 10 2 0 0 10 1 1 1`. Its legacy white intensity is 2 while each RGB component is 1. The importer reports that independent white intensity and unused RGB range are not represented, while baked surface components remain separate. This is an observed instance of the previously documented light-model limitation.
- The polygon project's `walkplayer` is already assigned sector -1 in the original file; conversion retains it and validation warns. This is source state, not an introduced dangling reference.
- The native-to-JKL preservation warnings are retained in the saved conversion receipts. Reopening the generated JKL does not reconstruct the native metadata warning history; retain those receipts alongside the originals.
- Neither fixture exercises COG parameters, architectural-lighting bindings, modern SED/NED dialects, custom template libraries, or embedded model/resource changes. No broader native-project compatibility claim follows from these two successes.

No source fixes were indicated by these checks.

## Other bounded candidates

- The [May 1998 original Sample JED Files announcement](https://www.massassi.net/oldnews/old2.htm) explicitly names `stairs.jed`, `tutor.jed`, and `untitled.jed`. Its linked `https://www.massassi.net/oldnews/samples.zip` returned HTTP 404. Those three projects were not recovered.
- The [Rotating Doors tutorial](https://www.massassi.net/tutorials/rd/) archive `rotdoor1.zip` downloaded successfully, but contains only `RotDoor1.gob` and `RotDoor1.txt`, not native editor source.
- The [Creating MotS Style Cutscenes tutorial](https://www.massassi.net/tutorials/cutscene2/) archive downloaded successfully, but contains only `tutorial.cog` and `jabba.wav`, not native editor source. Neither payload was executed or played.
- Specifically named dome/sphere/skydome prefabs from original announcements were checked at their inferred live Massassi locations; those URLs returned HTTP 404. The inferred URLs and failures are distinguished in `manifest.json` from directly observed links.

`manifest.json` records source URLs, hashes, rights notes, rejected candidates, tool identity, template-library inputs and check scope. `SHA256SUMS.txt` covers the local evidence files except itself.


This metadata-only report is copied from the external research directory: C:\Users\Andrei\Documents\ChatGPT\sed-editor-audit\research-followup-20260909. Referenced local payloads, source snapshots, manifests, and scripts remain there; no recovered third-party file bodies are included in this repository.
