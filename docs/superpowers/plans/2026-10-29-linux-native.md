# Native Linux SED-MotS Implementation Plan

> **For agentic workers:** Use the task scopes below with independent review and verification before completion. Checkboxes track actual work.

**Goal:** Deliver a native Linux x64 SED-MotS editor plus display-free CLI/MCP and reproducible portable packaging.

**Architecture:** Reuse .NET 10, Avalonia, Vulkan and shared Core/Formats commands. Adapt platform seams and distribution rather than duplicating the editor.

**Tech Stack:** .NET SDK 10.0.401, Avalonia 12.0.4, Silk.NET Vulkan 2.23.0, Python 3.10+ and POSIX shell for tooling.

**Spec:** `docs/superpowers/specs/2026-10-29-linux-native-design.md`

## Global Constraints

- Preserve shared Core/Formats behavior for GUI, CLI and MCP.
- No engine changes or bundled game/community assets.
- CLI/MCP non-render operations require neither DISPLAY nor Vulkan initialization.
- Preserve input hashes, reviewed-output hashes, create-only outputs, source ancestry and attribution.
- Existing Windows behavior and automatic-update limits remain explicit.
- Packaging binds a clean source commit and includes licenses and SHA256 receipts.

## Review Focus

- Package paths containing spaces and invocation from another working directory: arguments and user output paths stay intact.
- Unix case-sensitive filenames: distinct files must not alias; original game resource naming remains compatible.
- Empty desktop directories and read-only installation: settings and projects remain per-user writable.
- No display/graphics device: non-render authoring works and preview failure is explicit.
- Renamed native game executables, explicit Wine templates and invalid launch tokens: direct launch never uses accidental shell parsing.

## Task 1: Portable desktop and game-launch seam

**Files:** `dotnet/src/Sed.Formats/Launching/GameLauncher.cs`, relevant `dotnet/src/Sed.App` settings/startup/dialog files, launch tests and UI probe.
**Interfaces:** Existing LaunchRequest/GameLaunchPlan and AppSettings; maintain defaults and JSON compatibility where possible. New shared platform helpers only where behavior needs them.

- [ ] Add meaningful failing tests for native executable planning and Unix path/config behavior.
- [ ] Implement native names/selection and direct argv launch through shared planner; retain explicit existing shell templates.
- [ ] Verify Windows launch contract plus new Linux cases and headless UI behaviors.

## Task 2: CLI/MCP and Linux regression repair

**Files:** `dotnet/tools/Sed.Mcp/HostOptions.cs`, project runtime declarations, relevant Core/Formats or tests only where Linux failures establish need.
**Interfaces:** Default worker is packaged sibling `../agent/sed-agent` on Linux; preserve explicit `--agent` and version binding.

- [ ] Run baseline Core/MCP tests and record actual portability failures.
- [ ] Add/run a failing default Linux worker regression; implement platform-specific apphost naming.
- [ ] Fix demonstrated Linux parsing/resource/test assumptions with discriminating regressions.
- [ ] Exercise a complete no-display authoring and real stdio MCP workflow.

## Task 3: Native build and portable distribution

**Files:** new Linux build/package/verification/desktop-integration tooling under `tools`, Linux guide, CI workflow.
**Interfaces:** Package root has `Sed.App`, `agent/sed-agent`, `mcp/sed-mcp`, `bin/sed-mots`, `bin/sed-agent`, `bin/sed-mcp`; all self-contained x64. Launchers preserve cwd and argv. Tools use explicit dotnet path or PATH.

- [ ] Implement reproducible build and package scripts with readable usage, deterministic source binding, no existing-output replacement, dependency notices and hashes.
- [ ] Add executable user-level launchers and optional desktop entry installation without root requirements.
- [ ] Verify actual relocated package from paths containing spaces, without DISPLAY; exercise CLI and MCP worker.
- [ ] Add Linux CI using the same scripts and document install/run/dependency/software-render instructions.

## Task 4: Integrated evidence and delivery

**Files:** `docs/LINUX_QUICKSTART.md`, `docs/PROJECT_STATE.md`, append `docs/WORK_LOG.md`, original verification report.

- [ ] Run existing suites, actual UI edit/undo/serialization and native desktop startup.
- [ ] Render synthetic diagnostic and lit snapshots with software Vulkan.
- [ ] Independent code review; resolve material findings and verify affected behavior.
- [ ] Commit, package from clean source, verify source/history/checksums, preserve branch and deliver links with precise limitations.
