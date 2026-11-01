# Upstream C++ adoption decision — 2026-11-01

## Decision

Use the upstream C++ core directly through a versioned, stateless local worker.
Keep the current .NET GUI, CLI and MCP as the product interfaces during this
stage. The first implementation is read-only native `.sed` integrity/summary
inspection and comparative JKL inspection. It must use actual upstream codecs,
report incomplete resource validation, and never replace the live document or
write a user's source file.

This is a deliberate path toward broader C++ adoption. It avoids maintaining
translations of the native codec and document machinery while preserving the
current retained-source and reviewed-edit contracts. A future source-preserving
candidate mutation is a separate gate. Broad Qt adoption is not selected for
this consolidation release.

## Source and independent review

The upstream source pin is `smlu/SED` develop commit
`b82255998fb733c8e8c87104eb7c17f27b48376f`. A 36-file source selection and manifest
were reviewed; the original packet ZIP SHA-256 is
`78bce7c35fb3e271429cf142092ad7cf25d172ed0a506f95a404e2e8015b9ac5`.

Lilitari reviewed the complete selected packet in the existing SED project
conversation using the requested available Pro fallback. She recommended direct
C++ core use through a stateless sidecar, then a gated single-authority native
document pilot. Her review distinguished semantic native serialization from
retained JKL source fidelity.

Gemini Argon High initially favored selective translation, while explicitly
disclosing incomplete ZIP inspection. After receiving nine complete requested
source files as plaintext plus the completed Linux proof, it revised its
recommendation to direct use of a stateless C++ worker. The original and revised
recommendations are retained separately in the external review record. Its
claim that a small retained-section map could by itself settle migration was
not adopted: changed sections, noncanonical references, opaque fields, recovery
and undo identity need independent evidence.

Both reviewers were thanked in their saved project conversations. Red was not
contacted. Existing Red source was integrated through its published histories.
Reviewer conclusions are advisory; source inspection and executed tests govern
implementation acceptance.

## Ownership during the first stage

| Concern | Owner |
|---|---|
| Original bytes, retained sections and source revision | .NET `JklDocument` and orchestration |
| Live semantic document and undo/redo | .NET only |
| Reviewed edits, resource revisions and final file promotion | Existing .NET plan/apply path |
| Durable operation outcomes | Existing external operation journal |
| Temporary native parse/integrity observation | One short-lived C++ worker process |
| Worker identity and input/output limits | Host process adapter and package provenance |
| Viewport and human interaction | Existing renderer/UI pending a separate comparative evaluation |

The worker receives captured bytes on stdin with fixed operation arguments. It
does not receive arbitrary shell commands or output paths. Host observations
bind input hash, options, request identity and executable hash. Parse rejection,
incomplete resource context and worker failure are distinct outcomes. A process
boundary does not itself provide an operating-system sandbox.

## Why this amount of C++

The upstream core contains native binary `.sed` encoding, corruption handling,
transactional document snapshots, persisted history, geometry operations and
resource readers. Translating those systems would duplicate substantial
semantics and maintenance. The .NET path retains source lines and opaque
sections, binds selectors and plans to revisions, verifies reviewed output and
tracks uncertain write outcomes. Those contracts must survive any later core
transition.

The Qt-free OBJ/3DO sector importer is a useful next target. Its location under
the app directory does not imply a Qt dependency, but its compilation and tests
were not part of the core-only proof. Adoption requires a separately built
target and shared hostile-mesh fixtures. Canonical C++ JKL export is not a
substitute for the current source-preserving writer.

Legacy text `.sed` and upstream binary `.sed` share an extension. Format
recognition must use content signatures, and no implicit conversion or overwrite
between the two formats is permitted.

## Evidence and later gates

The unchanged pinned core configured and built on Linux with GCC 15.2/CMake 4.2.3,
C++20, app/fuzzers disabled and serial compilation. CTest passed all 51 registered
tests with zero failures/skips in 12.50 s. These registrations include an aggregate
suite and 49 allocation-sweep registrations; they are not an assertion count.
A fixture warning writes `[26,30)` into a fixed 49-byte vector and is statically
in bounds; no sanitizer claim follows from that analysis.

The unchanged Windows baseline built with LLVM-MinGW Clang 23.1.4, CMake 4.4.5
and Ninja 1.13.2, but passed only 50/51 CTest registrations. The aggregate suite
reported eight failures of the generated-crossing directed-order assertion in
`extrude_arrangement_registry_tests.cpp`. The
[bounded diagnosis](CPP_WINDOWS_ORDERING_20261101.md) localizes this fixture's
disagreement to a one-ULP frame-normalization difference before exact ordering.
Identical-bit predicate replays agree across both compilers. The intended
portable geometry contract and correction remain open. Windows core
qualification has not passed. A successful read-only worker test cannot erase
that independent failure or authorize a topology mutation.

The inspection worker passed its three focused CTest registrations on Linux and
Windows. Windows passed 12 protocol cases, five source-lock cases and the C++
JSON checks; its symlink-creation case was skipped. Actual CLI-to-client-to-worker
integration passed eight cases on each platform, covering native integrity,
rejection, JKL inspection and identity/configuration refusals. The Windows worker
also started and ran focused tests with a system-only PATH, using its two
archive-verified LLVM runtime DLLs. The shared client passed 53 tests on Linux
and 50 on Windows with three Linux-only skips.

These checks do not qualify the Qt application, importer, final packaging,
graphics, gameplay or retained-source interoperability. Final source-aligned
package results belong in the consolidation delivery record as they complete.

Before a mutation worker is accepted, prove unchanged source/opaque text,
noncanonical surface and COG identity, mono/RGB and ARCH preservation, stale
source/resource refusal, no authoritative output after worker failure, reviewed
output hashes, and exactly one undo owner. A boundary requiring arbitrary
per-command model repairs is evidence against promoting it.

Before native document authority, define and test a versioned source envelope,
native save/reopen/recovery behavior, migration and downgrade rules, and a
single history owner. Evaluate Qt versus Avalonia only with complete Windows
and Linux application packages and representative human/agent workflows.
