# Native Linux SED-MotS

## Intent

Andrei requested conversion of our SED-MotS fork to Linux, retaining a human-usable editor while optimizing the authoring workflow for the assistant's Linux environment. Source baseline: GitHub CodaLilitari/SED-MotS `ff88182`. Work is isolated on `feature/linux-native`.

## Approach

Preserve the existing .NET 10 / Avalonia / Vulkan application and shared Core/Formats operations. This is the builder's choice: a native Linux distribution minimizes behavior drift and keeps the established CLI/MCP contracts. A Wine wrapper retains Windows integration problems; a browser rewrite duplicates an already portable GUI and loses established functionality. Neither is needed.

## Deliverables and boundaries

- A runnable native Linux x64 editor, CLI and local stdio MCP host; source plus reproducible packaging with licenses and SHA256 receipts.
- Human editing retains the existing menus, viewports, dialogs, undo and JKL serialization. Linux-native executable selection and safe direct game-launch planning join the shared GUI/agent path. Existing explicit Wine command templates remain available.
- CLI and MCP read/edit/validate operations must run without DISPLAY or a GPU. Only preview operations initialize Vulkan. A software Vulkan ICD is supported explicitly, never imposed on normal desktop users.
- Native Linux apphost names, case-sensitive host paths, shell-safe arguments, and per-user writable directories. No dependence on a writable installation directory, PowerShell, a graphical login, or an API key for agent operations.
- Package launchers preserve caller working directory, arguments, exit codes and MCP stdout. A portable tarball and optional per-user desktop integration retain human usability.
- No game-engine changes, bundled game data, rewritten legacy editor, or claims of retail/gameplay parity. The Windows-only automatic updater remains explicitly Windows-only; Linux packages are installed alongside previous versions.
- Preserve source ancestry, credits, strict edit/source hashes and no-overwrite gates. Original synthetic fixtures only in source/deliverables.

## Evidence

Run the existing Core and MCP suites and meaningful regression tests. Exercise real packaged CLI creation, inspection, reviewed edits, refusal and readback; real stdio MCP including its default sibling preview worker; offscreen Vulkan snapshots; headless UI edit/undo/serialization; and native X11 startup under Xvfb where available. Record evidence types separately. Pin package source to a clean commit and retain matching source/history.
