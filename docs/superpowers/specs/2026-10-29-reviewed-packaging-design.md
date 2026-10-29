# Reviewed dependency packaging

## Intent and authorization

The maintainer authorized continued development after the Holocron comparison with
"Go ahead as you'd like." The selected increment is a usable, reviewed package
planner and create-only GOB/GOO writer. Implementation choices below are the
builder's decisions under that authorization, not additional user requirements.

Keep domain work in Sed.Formats, with the native Linux CLI and MCP as adapters.
Use original fixtures. Game/retail behavior remains a separate evidence class.

## Approach

The alternatives considered were extending existing JKL patch operations,
wrapping the legacy GobWriter directly, and a dedicated shared binary package
plan. Choose the dedicated plan: patch commits are text-JKL-specific, while the
legacy writer silently normalizes/truncates names and overwrites destinations.
Reuse exact resource captures, atomic new-file promotion and the MCP journal.

The first increment takes a saved JKL and explicitly supplied, hash-bound
GOB/GOO archives. It creates a new one-level archive; existing-archive update,
loose-directory discovery, arbitrary local-file inclusion and native-project
conversion are separate operations. Existing conversion and GOB tools remain
available for preparing those inputs. The original JKL bytes are preserved.

## Request contract

`schemaVersion: 1`, `inputSha256`, and `levelFileName` are required.

- `resourceArchives`: optional ordered list of up to 16 `{path, sha256}` values.
  Existing explicit-provider precedence applies; no installation discovery.
- `stockArchives`: optional list of up to 16 absolute path strings, each an
  exact normalized member of resourceArchives. Resources resolved from these
  caller-declared baselines are omitted from output, with receipts and reasons.
  A custom override remains included even if a stock file has equal bytes.
- `episodeTitle`: optional printable ASCII title, maximum 80 characters;
  quotes, comment marker and line/control characters are refused.
- `episodeType`: 1 (single player) or 2 (deathmatch), default 1. This describes
  the archive and does not verify player starts or gameplay.
- `extraResources`: optional explicit `{name, folder}` roots, maximum 256,
  for dependencies the caller knows about. Names use safe relative ASCII paths;
  folders are a fixed supported resource-kind list.
- `allowUnverifiedReferences`: false by default. True acknowledges disclosed
  runtime/unsupported-reference uncertainty; it never bypasses missing assets,
  ambiguity, malformed supported dependency tables, collisions or work limits.
- Bounded options: maxNodes 1..4096 (default4096), maxEdges 1..16384
  (default16384), maxDepth 0..32 (default32), maxPackageBytes 1..134217728
  (default134217728). Archive overhead counts against the byte limit.

Reject unknown/duplicate fields and invalid value kinds. Hashes are normalized
64-digit SHA-256 values. Source paths and original caller order are bound to
the reviewed manifest. Stock membership is a declaration, not automatic proof
of a retail installation or license status.

## Shared services

`ResourceDependencyReader.Read(name, folder, bytes, limits)` is a pure bounded
reader, with separate lookup and inspection states. It returns references,
relations, locations, diagnostics and unverified scopes. Initial grammars:
3DO materials, PUP keyframes, SND sounds, SPR/PAR materials, typed COG resource
defaults. MAT/CMP/WAV/KEY are leaves for dependency inspection only. AI has an
explicit uninspected runtime-variant scope. Unknown syntax is never silently
called complete. The existing direct `assets` contract remains unchanged.

The package planner parses captured JKL bytes and traverses known direct roots,
retained standard resource-table roots where safely understood, and explicit
extra roots. It records parent/root provenance, normalized requests, exact
source entry/hash, inclusion or stock omission, and inspection state. Cycles
and repeated requests are bounded and deduplicated; distinct origins remain
visible. All current templates are scanned conservatively, not just reachable
playtime templates. COG code and runtime lookup/fallback are unverified.

Package names must survive the actual 127-byte ASCII GOB name field without
replacement/truncation. Normalize separators/case once, refuse traversal,
absolute paths, empty segments, forbidden characters and destination collisions.
Package resources under the canonical requested resource path so the packaged
lookup can be checked; do not silently substitute an unrelated source path.
Original source identity is still retained in the manifest.

The parser's documented empty-colormap-table/slot-zero scaffold is treated as
having no named palette dependency. Report that scope; never infer a stock
palette. Other invalid/empty authored references remain unresolved.

The archive encoder uses deterministic entry order and validates readback of
every included entry. `episode.jk` uses deterministic LF text. The JKL is copied
byte-for-byte. A package plan reports structural errors rather than repairing
or rewriting the input.

Review identified differences between retained engine text and the current
editor model. Duplicate template declarations and repeated known resource
parameters are hard refusals. The supported JKL subset also refuses template
or thing continuations/comma syntax, template slash/semicolon rows, and
indented semicolon rows in supported sections where native/editor parsing
differs. These are packaging interpretation limits, not declarations that an
engine rejects every such file. They cannot be bypassed by acknowledging
unverified references. Retained parameter inspection is capped at100000 records.

Resource-cache identity retains the folder/name boundary: `(3do,key/foo)` and
`(3do/key,foo)` have different fallback behavior even though their first lookup
path is equal. The private provider key now preserves that distinction, including
when a shared caller reuses a previously read provider. Complete normalized COG
symbol schemas are reused for placed values even when one default literal is
unsupported; acknowledgement cannot hide a separately understood missing value.

## Review, publication and interfaces

The report distinguishes `canBuild`, completion of the declared static scan,
resolved known references, and `runtimeClosureVerified: false`. It includes
entries, stock omissions, provenance, issues, scopes, archive byte count and
both `outputSha256` and `manifestSha256`. A blocked plan remains reviewable;
it has no publishable output hash. Reports contain metadata, not asset payloads.

The canonical manifest binds planner/parser versions, exact JKL identity,
ordered source archives and roles, options/limits, full dependency graph,
included/omitted entries, and unresolved findings. No timestamps or output path
enter the identity. Both review hashes are mandatory for apply.

- CLI: `package-plan <level.jkl> <package.json|->`
- CLI: `package-apply <level.jkl> <package.json|-> <new-output.gob|goo>
  <expected-output-sha256> <expected-manifest-sha256>`
- Discovery: `schema package`
- MCP: `sed_package_plan(level, request)`
- MCP: `sed_package_apply(level, request, output, expectedOutputSha256,
  expectedManifestSha256, operationId)`; recovery uses `sed_outcome`.

Root adapter API: `AgentWorkspace.PackageRequestSchema()`,
`PlanPackage(string path, string requestText) -> PreparedAgentPackage`,
`CommitPackage(PreparedAgentPackage, string outputPath,
string expectedOutputSha256, string expectedManifestSha256,
CancellationToken token = default) -> object`.
PreparedAgentPackage exposes InputPath, InputSha256, nullable OutputSha256,
ManifestSha256, ArchiveExtension, CanBuild, and Report. Captured bytes and
requests are privately owned, never caller-mutable payloads.

Apply checks review identities, source and archive freshness, output extension,
directory and destination absence. Write/flush a sibling temporary file,
recheck source/resources/cancellation immediately before atomic no-replace
promotion, and clean up staging on refusal. The 128 MiB output cap fits the
existing durable MCP observer. MCP preflights file roots, bounds the report
before journaling, checks replay before rereading the source, and retains the
existing uncertain-outcome/reconnect behavior.

## Acceptance

Original fixtures must prove custom transitive inclusion, stock omission,
custom-over-stock precedence, missing/ambiguous refusal, malformed dependency
records, name collision/traversal/non-ASCII refusal, deterministic order and
readback, exact original JKL bytes, graph/output budget enforcement, changed
source/archive/review refusal, equal-output changed-manifest refusal, no
overwrite, cancellation cleanup, and durable MCP replay after source removal.
Exercise both actual CLI processes and MCP stdio with a packaged native build.

No engine modifications, Holocron code reuse, game-asset redistribution or
runtime-closure claim is part of this increment.
