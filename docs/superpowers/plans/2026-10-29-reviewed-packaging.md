# Reviewed Packaging Implementation Plan

> **For agentic workers:** Use the normal collaborative implementation workflow
> with focused independent owners and an independent whole-change review.

**Goal:** Create reviewed one-level GOB/GOO packages from explicit resources.

**Architecture:** Pure static dependency readers feed a shared bounded package
graph and deterministic encoder. Dedicated CLI/MCP adapters use reviewed output
and manifest hashes and the existing atomic publication/journal contracts.

**Tech Stack:** C#/.NET10, existing Sed.Core/Sed.Formats, xUnit, native Linux tools.

**Spec:** `docs/superpowers/specs/2026-10-29-reviewed-packaging-design.md`

## Global constraints

- Original synthetic fixtures; no game assets or engine changes.
- Preserve exact JKL bytes and unknown source text during packaging.
- Explicit hash-bound archives only; final archive at most134217728 bytes.
- Both review hashes mandatory; apply creates a fresh destination only.
- Runtime resource closure remains explicitly unverified.
- Keep the existing direct asset diagnostics and patch protocol compatible.

## Review focus

1. Same bytes with different stock roles must invalidate the manifest review.
2. Ambiguous earlier archive must not fall through to a convenient later match.
3. Noncanonical source-entry paths must resolve identically from the output.
4. Parser limits must stop work without publishing a partial dependency set.
5. MCP replay must work after source disappearance and never publish twice.

## Task 1 — Static readers (dependency-format owner)

Files: new `Sed.Formats/Resources/ResourceDependencyReader.cs` and focused
`Sed.Core.Tests/ResourceDependencyReaderTests.cs` (paths under dotnet/src/tests).
Public interface is the resource reader contract recorded in the design and
the implementation handoff. Lookup stays in the caller.

- [x] Add original grammar/budget tests; run and retain the missing-feature failure.
- [x] Implement supported table readers and typed COG defaults with explicit unknowns.
- [x] Run the focused tests, correcting failure semantics rather than hiding syntax.

## Task 2 — Package graph, encoding and commit (root)

Files: new `Sed.Formats/Resources/LevelPackage*.cs`,
`Sed.Formats/Automation/AgentPackages.cs`, and focused Core package tests.
Consumes ResourceDependencyReader and ExplicitArchiveResources. Produces the
PreparedAgentPackage/PlanPackage/CommitPackage API in the design.

- [x] Add end-to-end graph, package-readback and refusal fixtures; record red.
- [x] Implement strict request/options, bounded roots/traversal and manifest identity.
- [x] Implement strict deterministic GOB bytes and preserved-input readback.
- [x] Implement immutable capture and guarded create-only commit.
- [x] Exercise equal-output changed-role review, all input races, limits and collisions.

## Task 3 — Transport and durable outcomes (transport owner)

Files: AgentCommandCatalog, Sed.Agent/Program, Sed.Mcp/ToolCatalog and SedTools,
new MCP package tests, existing process-tool discovery expectation.

- [x] Add tool/schema and end-to-end tests; record missing-feature failures.
- [x] Add dedicated commands/tools with required dual hashes and path preflight.
- [x] Preserve replay-before-read, report bounds and journal-before-promotion.
- [x] Exercise successful publish/reopen, refusal-before-journal and reconnect replay.

## Task 4 — Integrated delivery (root plus independent reviewer)

- [x] Write human command examples and update protocol, workflow and MCP docs.
- [x] Run Core and MCP suites; verify real CLI/MCP processes without a display.
- [x] Independently review the complete diff against the five review risks.
- [x] Record actual results and remaining limits in PROJECT_STATE and WORK_LOG.
- [x] Commit/checkpoint the feature branch; build and verify the native deliverable.
- [x] Push using authenticated CLIs and verify remote commit identities.

Implementation is authorized by the user's continuation instruction; routine
choices and reversible fixes proceed without an additional approval round.
