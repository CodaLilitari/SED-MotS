# JK/MotS editor compatibility audit

This is a focused source audit dated 2026-09-08. It compares SED's level and
consistency-checking contracts with the local OpenJKDF2-MotS-Fixes checkout at
`e9e293a7a98c9a665509af96f9a87a9ca2470996`. It does not certify all editor code,
retail behavior, campaign gameplay, multiplayer, or every OpenJKDF2 build.
Original game assets and private test lanes are not included in this fork.

## Engine evidence and editor implications

The file and line references below use the audited engine commit, not the SED
tree. Its source is available at
[OpenJKDF2-MotS-Fixes](https://github.com/andreipemberton/OpenJKDF2-MotS-Fixes/tree/e9e293a7a98c9a665509af96f9a87a9ca2470996).

| Contract | Evidence | Editor consequence |
| --- | --- | --- |
| Surface polygons have 3 through 24 vertices. The upper limit remains a hard parser check in the desktop build. | `src/World/sithSurface.c:301-310` | A JK/MotS surface above 24 vertices is an error, including OpenJKDF2 targets. Larger desktop render buffers do not remove this file-format check. |
| Colored surface intensities are stored in JKL as **mono, red, green, blue for each vertex**. | `src/World/sithSurface.c:409-438`; render channel use at `src/Engine/sithRender.c:2231-2241` | Preserve four values per vertex, including the separate mono component. With fewer than four values per vertex the loader takes the mono path and duplicates it across channels. |
| Architectural model lighting is a separate MotS section containing objects, meshes, and per-vertex mono/R/G/B values. | `src/World/sithWorld.c:99-100`; `src/World/sithArchLighting.c:59-120` | Surface RGB round trips alone do not preserve MotS model lighting. Validate mesh and vertex counts before attaching lighting to a changed model. |
| Thing token 10, after sector, can contain an architectural-lighting index before key=value overrides. | `src/World/sithThing.c:1572-1588` | Preserve the positional token separately from template properties; exporting every MotS thing with `-1` loses the binding. |
| The engine's text reader lowercases input, removes `#` comments, and joins supported backslash continuations. | `src/General/stdConffile.c:262-305` | Use invariant numeric formatting and canonical resource names. Editor parsing should agree with emitted engine syntax and preserve meaningful unknown data. |
| World sections are dispatched by name; templates and placed COGs are different loaders. | `src/World/sithWorld.c:82-100,168-207` | Do not infer that a syntactically accepted unknown section was interpreted by the game. Preserve supported section ordering and dependencies. |
| Templates resolve parents immediately and retain the first existing template with a matching name. | `src/World/sithTemplate.c:180-208` | Parents must be exported before children. Duplicate names and missing parents need diagnostics rather than relying on a later declaration to repair them. |
| MotS adds `treesize` and `respawnfactor` thing parameters. | `src/World/sithThing.c:69-74,114-118` | Keep MotS property descriptions and template editing distinct from JK. |
| MotS registers the `preblock`, `escaped`, `attachkilled`, and `playeraction` messages. Enhanced COG verbs are a separate opt-in. | `src/Cog/sithCog.c:116-120,143-146`; `src/Main/Main.c:720-726` | A MotS target and an enhanced-verbs target are separate capabilities. Do not assume OpenJKDF2 mode enables every extension. |
| Placed COG initialization values follow script symbol-reference order, excluding local references. | `src/Cog/sithCog.c:596-605` | Keep symbol ordering and local-variable handling exact. Inserting or dropping an exported token can redirect later references. |
| COG IDs with bit `0x8000` refer to the static world; lookup is then checked against that world's COG count. | `src/Cog/sithCog.c:1832-1849` | A static COG ID cannot be rejected against the placed level COG count. The checker must distinguish unknown static availability from a proven invalid number. |
| COG loads also create instances beyond the placed list, including template-bound COGs. | `src/Cog/sithCog.c:619-641`; `src/World/sithThing.c:1855-1863` | An unplaced positive COG ID needs runtime verification; the editor's placed-list count alone does not prove it invalid. |
| MotS selects `.goo`, JK selects `.gob`; both are read by the GOB reader. | `src/engine_config.h:255`; `src/Win95/stdGob.c:97-108` | Package with the target game's extension. Both containers require `GOB ` signature and version 20. |
| GOB entry lookup lowercases paths and normalizes `/` to `\`. | `src/Win95/stdGob.c:231-241` | Diagnose normalized duplicate resource paths and preserve predictable archive lookup. |

## Launching from the editor

`src/Main/Main.c:594-728` accepts `-path`, `-devMode`, `-debug log`,
`-autostart`, `-sp`/`-mp`, `-episode`, `-map`, `-headless`, and `-motsCompat`
(the extra OpenJKDF2 options are within `QOL_IMPROVEMENTS`). Its command-line
parser uses whitespace `strtok`, with no quoted-string handling. Quoting a
space-containing `-path` argument does not make it safe for that parser.

The game working directory and executable path can use Windows launch APIs;
the engine's own `-path` value must be a representable single token. The copy
uses a 128-byte destination. Resource-path construction also uses 128-byte
buffers (`src/Main/jkRes.c:525`), so a 127-character root leaves no room for a
resource suffix. Long-path and Unicode handling must be validated before launch.

The loose-project workflow is supported by registering `-path` as resource
directory slot 0 (`src/Main/jkRes.c:33-35,64-69`). The episode menu discovers
entries under the game's `episode` directory, then reads each `episode.jk`
through the resource resolver (`src/Main/jkEpisode.c:73-181`). A project overlay
can therefore supply `episode.jk` and `jkl/...` for that menu workflow. This is
source-supported expected behavior; it has not been exercised in a GUI during
this audit. A data install without discoverable episodes is a separate case.

For a future direct-map launch, `-autostart -sp -episode <name> -map <map.jkl>`
needs both values explicitly in MotS, whose defaults are empty. Autostart strips
the episode and map at their first period before restoring `.jkl`
(`src/Main/Main.c:117-139`). Use plain basenames, not arbitrary paths or names
with embedded periods. Internal world and episode names are also truncated to
31 characters (`src/World/sithWorld.c:155-159`; `src/Main/jkRes.c:99`).

## Resource precedence needs its own regression corpus

`src/Main/jkRes.c:503-557` tries a loose file in the working directory first,
then iterates five resource slots. Within a slot it tries the loose file and
then the archives. Mods archives are appended before resource archives
(`src/Main/jkRes.c:268-299`). The implementation keeps iterating after a match:
a later loose file may replace an earlier result, even though later archive
lookups are suppressed. Consequently a simple claim that an overlay always
wins is stronger than the inspected implementation supports.

A robust editor resource browser should show the selected provider and shadowed
alternatives. Before changing precedence, create fixtures with deliberate loose
and archived duplicate names, including normalized path case and separators.

## Limits and target profiles

`src/engine_config.h:73-114` distinguishes baseline, retro, and desktop QOL
configurations. The desktop QOL configuration has `SITH_MAX_THINGS=32000`,
1024 visible sectors, 1280 entries in the second visible-sector buffer, and
1024 visible alpha surfaces. The non-QOL values are 641, 128, 160, and 32.
These are different kinds of limits: visible-sector budgets are not a limit on
total authored sectors. The non-QOL thing-pointer validator accepts indices
0 through 639 (`src/World/sithThing.c:1930-1942`). This supports a conservative
warning above 640 placed things, not a complete retail capacity guarantee.

COG VM stack, symbols, linked symbols, links, and node-stack depth also vary by
configuration. Desktop values cannot be used as retail guarantees, and a
successful parse does not establish safe runtime spawning or multiplayer limits.
SED currently exports `World things` as placed things plus 256; declared storage,
placed objects, and the running engine's free-list capacity are distinct values.

The checker added in this fork displays the retail thing warning only when the
corresponding game's test-engine setting is explicitly **Retail** (value 2).
Auto mode may eventually fall back to retail, but does not imply a retail
checker profile. The warning explicitly identifies the non-QOL source baseline.
The 24-vertex surface error applies to both JK and MotS regardless of engine.
IJIM keeps its separate surface limits and warning behavior.

## Changes and validation in this fork

- `src/CONS_CHECKER.PAS` makes a JK/MotS surface above 24 vertices an error.
- COG sector, surface, and thing pointers are checked by live collection
  membership without dereferencing the candidate pointer. Nil remains the
  supported unassigned reference. A thing's sector is also checked before the
  geometry containment test.
- COG indices distinguish malformed values, negative null IDs, placed references,
  references outside the placed list, and static-world references. The last two
  produce verification warnings, preserving possible template/runtime and
  static COG usage. The normal null sentinel is `-1`, but the engine returns no
  COG for other negative IDs too, so they remain accepted. Hex and partial
  numeric strings are diagnosed because the
  engine's decimal reader would interpret them differently.
- Named template COG parameters receive a missing-template diagnostic. Resource
  parameters using `-1` or `none` no longer receive a false extension warning.
- `src/CompatibilityChecks.pas` contains GUI-independent rules used directly by
  the native consistency checker. `tests/test_compatibility.dpr` exercises 33
  behavioral checks covering index boundaries, sentinel/static cases, malformed
  decimal values, removed and foreign object references, null resources, and
  retail warning thresholds.

The helper was compiled and executed with native Windows Free Pascal 3.2.2 in
`delphiunicode` mode: **PASS: 33 compatibility checks**. FPC emitted three
Unicode-to-ANSI conversion warnings. The test is an execution of the shared
Pascal implementation, not a source-text assertion. Full Delphi/VCL compilation
and interactive checker verification remain separate gates.

Compile from the repository root without `-Fusrc`; adding that directory to the
unit search path shadows FPC's RTL Math unit with SED's custom `src/math.pas`.
Use an empty output directory for a fresh check: this FPC build reused the
explicitly referenced unit's old PPU after a source change, even with `-B`.
The commands below assume `build\tests\compat` exists and contains no old PPUs:

```powershell
fpc -Mdelphiunicode -FUbuild\tests\compat -FEbuild\tests\compat tests\test_compatibility.dpr
.\build\tests\compat\test_compatibility.exe
```

Private baseline and runtime directories were located and inspected read-only
for future isolated tests. No retail or OpenJKDF2 game process was launched and
no licensed data was copied into the editor repository by this audit. A previous
local game-launch hold concerning firewall setup was surfaced to the main task;
its state must be verified before any game-runtime testing resumes.

## Remaining work

Exact editor/engine resource precedence, template dependency diagnostics,
command-line round trips, and complete COG symbol schemas merit dedicated
fixtures. Nil object references may already have
been normalized from an unresolved import by existing SED code; this checker
cannot reconstruct the lost numeric intent. The checker's conclusions concern
current object membership and exported syntax, not complete COG program validity.

## Architectural lighting preservation and stock corpus

The original SED loader skipped the positional architectural-lighting binding,
and its MotS exporter emitted `-1` on every thing and `Num ArchObjects 0`.
This is a material roundtrip loss: all **39 playable levels** in the inspected
original MotS episode archives contain architectural lighting. Together they
contain **2,353 objects and 2,353 thing bindings**, with **38,622 vertices** and
**154,488 separate mono/R/G/B components**. This affects both retail MotS and
OpenJKDF2's MotS loader.

The new GUI-independent `src/ArchitecturalLighting.pas` preserves the nested
object/mesh/vertex arrays, supports deep copying, reads atomically, and writes
numbers with an invariant decimal separator. The parser requires complete
four-component rows and sequential numeric object/mesh/vertex labels. Counts
are bounded by the supplied text before allocating, and nonfinite values are
rejected. The section header and `end` wrapper are optional on input; the
serializer emits the section body for the calling file writer to wrap.

Preserving a raw section and an index is insufficient when a model changes.
The renderer attaches the same baked mesh arrays to every model geometry/LOD
without checking their lengths (`src/Engine/sithRender.c:2448-2463`). The pure
helper therefore reads all declared 3DO geometries and validates mesh and vertex
coverage. It also supports an exact comparison for the highest LOD. The 3DO
reader enforces the engine's four geometry slots and 512 vertices per mesh
(`src/Primitives/rdModel3.c:212-268`). It checks topology declarations and vertex
row availability, rather than claiming to validate every coordinate, face,
material, or hierarchy field in a model.

All **2,353** stock bindings exactly match their model's highest-LOD mesh and
vertex counts. All **172 distinct model payloads** used by these bindings have
one geometry/LOD; there were no missing resolved models or coverage mismatches.
Multiple LODs are additionally covered by synthetic tests. An exact shape alone
does not prove unchanged vertex order or model identity; the editor integration
also records the resolved model name and normalized model-content hash.

`tests/test_archlighting.dpr` passed **71 synthetic behavioral checks** under
native Windows FPC 3.2.2 with range, overflow, and assertion checks enabled.
These cover mono preservation, numerical round trips under a comma-decimal
locale, deep copy independence, atomic failures, sentinel/bounds cases,
nonfinite input, malformed and oversized counts, and model topology/LOD rules.
FPC reports expected string-conversion warnings because its `TStrings` and
`SysUtils` interfaces are ANSI while Delphi's corresponding interfaces are
Unicode; two managed-result initialization warnings are also reported.

`tools/audit_archlighting.py` performed **39 native Pascal section round trips**
and **172 native model topology parses** against the user-supplied licensed
archives. An independent Python reader compared every numeric component and
model shape. All checks passed. The script extracts only to a temporary
directory outside the repository, removes that directory after testing, and
emits only counts, asset entry names, and SHA-256 hashes. Reproducible evidence
is stored in [retail_archlighting_evidence.json](retail_archlighting_evidence.json).
No licensed payload is included in that report or in the synthetic fixtures.

Example invocation after compiling the test executable:

```powershell
python tools\audit_archlighting.py <licensed-MotS-directory> <test_archlighting.exe> --output <evidence.json>
```

This validates the preservation helper and stock topology contracts. It does
not replace a full Delphi editor import/save/reopen test, renderer comparison,
or a game launch. Editing new baked model lighting remains outside this change.
