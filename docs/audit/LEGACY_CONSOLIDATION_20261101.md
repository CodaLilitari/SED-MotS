# Consolidation legacy compatibility checks — 2026-11-01

These checks ran against the new consolidation worktree based on
`ea0b351ce8a1cab46da5e9e44a068ee17fdaf920`, including the uncommitted final-frame
serialization guard. They are working-source evidence, not a clean release
qualification. Original archives, extracted author data, binaries and full logs
remain in external test storage; none is redistributed by this report.

## Original level documents

Two original Massassi packages and one previously recovered, hash-verified
passive Cloud City payload supplied four geometry documents. The installer was
not executed. Every input matched its historical acquisition identity.

| Document | Massassi ID | SHA-256 |
|---|---:|---|
| Derra IV `impsiege.jkl` | 6 | `a25dd1c8254baf5538a569946479b35cfb3100e4cc4c5786dd7322e04310cb48` |
| Cloud City `bridge.jkl` | 2503 | `6b2393fa09650d8790612954c0970d47e2b088098ea83d3bb6e1f3efdf9fb520` |
| Cloud City `cloudcity.jkl` | 2503 | `dde62b3eb134f78ffb87539f1ff7964134abda9435f824d2cc8a833ffaae0611` |
| Cloud City `prologue5.jkl` | 2503 | `b1ed1b4607c1746c0d4c1d00aee39ac40e9011b91c4028821abd40ef6d638e85` |

`Sed.CommunityAudit` passed unchanged-text checks for all four documents and
forced regeneration through a fresh `JklDocument`, followed by reparse and
modeled-data comparison. All material tables matched. Package, container,
parse and round-trip failure counts were zero; no bounded skips or empty
packages were accepted. Derra's 2,006 sectors and 458 things retained the
specific warning for its final six-coordinate frame without a closing
parenthesis. The original spelling remains supported.

The new writer guard refuses adding a parameter after that final-only malformed
frame, before creating an output file. It does not silently move frame records
or repair their spelling. Separate regression fixtures cover thing/template
owners, JKL/native import, unchanged destination bytes on refusal, supported
existing-field edits and Caesar-style extra-parenthesis handling. The focused
green run passed 101 tests; this corpus run alone does not exercise those edits.

## Real native projects

The same two hashed projects in the [historical recovery report](NATIVE_PROJECT_FOLLOWUP_20260909.md)
were imported, converted to new JKL files, validated and inspected using a fresh
Windows .NET10 build. Exact bundled template-library hashes were retained;
missing project companion libraries were explicitly recorded.

| Project | Sectors | Surfaces | Vertices | Things | Lights |
|---|---:|---:|---:|---:|---:|
| Brian Lozier's JED0.91 polygon prefab | 17 | 90 | 112 | 1 | 1 |
| Dino Delucchi's JED0.85 MotS building | 39 | 431 | 706 | 1 | 1 |

All 56 sector detail records and both complete light collections matched before
and after conversion. Counts also matched. The building's native light retained
white intensity10, range2, RGB intensity10, legacy RGB range0 and color `[1,1,1]`.
This closes the earlier light-model limitation for these fields. The native
importer reads them independently; intensity2 in the old report was an incorrect
interpretation of the range field.

Both projects still report unsupported native layer visibility. The prefab's
authored walkplayer sector `-1` remains intact with a validation warning.

## Additional Caesar, Con Krape and DBZ checks

A second current-source audit acquired exactly three original Massassi packages
into a separate external corpus and verified the original archive and all five
reached JKL hashes before testing. All five documents passed unchanged-text and
fresh-document regeneration checks, including material-table preservation, with
zero package, container, parse or round-trip failures and no bounded skips.

| Original package | Reached geometry documents | Result |
|---|---|---|
| Caesar's Palace, Massassi1400 | `caesars.jkl` | Preserved; precise warning for six valid frame coordinates followed by stray unmatched-parenthesis text |
| Con Krape Canyon, Massassi3068 | `conkrape.jkl` | Preserved |
| DBZ: TDIR Tournament Level Pack, Massassi1814 | `cellarena.jkl`, `heaventourney.jkl`, `budoukai.jkl` | Preserved |

The DBZ package contains the reported `door2017.mat` material row in
`budoukai.jkl`, supporting identification of the compatibility case. Its byte
identity with the archive used in Red's separate study has not been established.
The tests preserve authored data; they do not establish resource availability or
silently repair suspicious material references.

The archives remain private local compatibility fixtures. Their README terms do
not establish permission to redistribute them with the editor. ConKrape's
`patcher.exe` was only inventoried inside its original ZIP and was never run.
The acquisition manifest, passive expected-case hashes and report are
`missing-legacy-corpus-20261101/manifest.json`, `expected-cases.json` and
`missing-legacy-audit-01.json` in external storage.

## Limits and receipts

The run is `legacy-run-20261101-015750`. It contains `narrow-audit.json`, exact
tool/template hashes, per-command stdout/stderr and `acceptance-summary.json`.
The implementation driver and byte-verification plan are retained beside it.

Sector inspection covers exposed structural fields; it is not a complete native
model digest. The community audit's modeled comparison does not certify opaque
section regeneration, resources, rendered appearance, COG execution, game
loading or multiplayer. Cloud City's separate static `e11.goo` payload and the
full 50-package corpus were excluded from this narrow run. The separate three
package run above adds five documents, for nine checked JKLs in total. Native
project writing remains unsupported.
