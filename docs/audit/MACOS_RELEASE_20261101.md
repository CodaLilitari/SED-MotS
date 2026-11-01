# Mac release work and qualification record

This extends the current .NET/Avalonia editor to `osx-arm64` and `osx-x64`.
Andrei assigned further C++ catch-up to Muse Code; that experiment and its
workspace are outside this release task. Red is not being contacted. The
[design](../superpowers/specs/2026-11-01-macos-release-design.md) and
[implementation plan](../superpowers/plans/2026-11-01-macos-release.md) define the
remaining native acceptance requirements.

## Observed source and shared regressions

The inspected committed baseline is
`7642d5277b95347e3fc80af919fdd578c25edf4a`. The Mac additions are an implementation
candidate, not a frozen platform release. The implementation adds Darwin exclusive
publication, Mac private MCP-state component checks, a bundle-local MoltenVK
resolver with explicit-driver override preservation, and conditional advertised
portability extension selection.

The Windows Core build completed with zero warnings and errors. Its full suite
passed 2,038 tests with three platform-specific skips, 2,041 total. It includes
the new loader decisions and shared atomic-publication tests. It does not
execute Darwin syscalls, APFS aliases or Metal rendering.

The MCP project now declares both Mac runtime identifiers. A coordinator-owned
restore regenerated its lock: all existing managed/Linux/Windows entries stayed
identical, and only `net10.0/osx-arm64` and `net10.0/osx-x64` were added. The MCP
build passed with zero warnings and errors. Its first test run used an exFAT
temporary directory and failed six existing junction-fixture setups with
`Win32Exception: Incorrect function`. Live volume inspection identified that
filesystem limitation. The unchanged binaries and tests then ran with a new
external NTFS temporary directory: 320 passed, three Mac-only skips, 323 total.
The failed and corrected runs are both retained; no test was suppressed or
product behavior changed to make the rerun pass.

The pinned dependency acquisition helper first failed all 13 original fixtures
while absent. After implementation, 12 passed and the filesystem-link fixture
was explicitly skipped because this Windows process lacks symlink-creation
privilege. Coverage includes exact hashes/sizes, cached archive preservation,
refusal of existing/Git output locations, archive traversal, duplicate and
nonregular selected members, unsafe manifest paths, case collisions, HTTPS
sources and failure before success-manifest publication. Unselected framework
links are never materialized.

The shared notice collector now accepts `-OutputDirectory` for a new external
directory. Its old default remains `licenses/net`. Six new fixtures first
failed because the option was absent; after implementation all 29 offline
notice cases passed, including unchanged source notices, incomplete-cache
refusal, source/Git output refusal, preserved existing output and junction
refusal. The harness is now portable to PowerShell on Mac/Linux, using a symbolic
link there instead of a Windows junction; those hosts still need execution.

A full Python tooling run passed 161 tests with ten explicit skips, 171 total,
after the acquisition helper and external-output behavior were added. The final
focused notice-harness rerun also passed all 29 cases after the fixture-portability
changes. Receipts and logs are retained outside Git in the task's
`mac-shared-regression-01` storage.

## Acquired graphics dependency

The maintained [dependency lock and notice rationale](../../tools/macos/DEPENDENCIES.md)
pin the public-API MoltenVK 1.4.2 release and upstream dependency revisions.
Production acquisition from the previously downloaded, hash-verified archive
completed into a new external directory. Independent readback verified the
artifact, selected library and all 11 notices (13 files total).

| Input | SHA-256 |
| --- | --- |
| Official macOS archive, 59,563,008 bytes | `f95765a6229cb7b915990a2890ce12ebe36a730b021545d3d52ae69ce4c4024e` |
| Selected library, 10,925,552 bytes | `aef00b13bcc808adf15b85bef9ae67393d92be7ed5dfe41cad16fa809e4a4c5f` |
| Portable acquisition manifest | `86469570dde0cefb61a16b4212f28622a9b0b1a66b257841cb969b8a8c59c2a7` |

Static FAT Mach-O inspection found x86_64 and arm64 slices. No downloaded Mac
program was executed on Windows. The artifact is an input to packaging, not a
SED release binary or native Mac qualification result.

## Open release gates

- Finish the bundle assembler, portable launchers, signing/closure and archive
  checks; use the regenerated MCP lock for both Mac runtime identifiers.
- Run native builds/tests on both standard public Mac runner architectures.
  Exercise both APFS case modes, including same-file aliases; retain actual
  filesystem evidence separately from Windows regressions.
- Verify relocated packaged CLI/MCP/editor support data, a copied-alone `.app`,
  actual rendered scene/Wren images and observable native Cocoa editor UI.
- Produce final common-source Linux/Windows distributions and parity branches;
  preserve the earlier candidate's exact source and evidence identities.
- Publish persistent platform binaries and branch download links, then download
  them back and verify checksums. CI artifacts and source-only branches do not
  fulfill Andrei's delivery request.

No native Mac runtime, complete Mac bundle, GUI acceptance, Developer ID signing,
notarization, branch publication or downloadable SED Mac binary is claimed by
this record. The goal remains active.
