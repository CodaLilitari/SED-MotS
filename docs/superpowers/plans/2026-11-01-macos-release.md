# macOS Release Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver native Apple Silicon and Intel packages of the existing SED-MotS editor, agent and MCP server with observed Mac qualification.

**Architecture:** Retain the shared .NET/Avalonia model and offscreen Vulkan renderer. Add narrow Darwin write safety and loader corrections, then a Mac-specific bundle/verification pipeline and native CI matrix. Keep optional C++ worker qualification and notarization separate from the main editor.

**Tech Stack:** .NET 10.0.401, Avalonia 12.0.4, Python 3.10+, PowerShell 7, Darwin `renamex_np`, MoltenVK, native Apple signing/inspection tools, standard GitHub Mac runners.

**Spec:** `docs/superpowers/specs/2026-11-01-macos-release-design.md`

## Global Constraints

- Targets are `osx-arm64` and `osx-x64`; the initial claimed OS floor is macOS 15.
- Mac delivery is the first remaining platform milestone. Andrei subsequently assigned C++ catch-up to Muse Code: further C++/Qt adoption and Muse's experimental workspace are outside this goal. No optional native-worker port belongs to these tasks.
- Existing Windows/Linux receipts remain bound to their actual source commit; later changes require scoped regression evidence and fresh Mac evidence.
- Root owns all Git, restores, builds, heavy storage work and remote dispatch/publication. Implementers edit only assigned source files and do not run builds or Git without a later explicit coordinator assignment.
- Keep every generated artifact/cache/temp tree physically outside Git; no junctions or symlinks back. Preserve earlier attempts.
- No new paid services, signing enrollment, Red contact, game assets, main-branch overwrite or notarization claim.
- Native Mac tests, graphics, native UI, ad-hoc signing, Developer ID signing and publication remain separate receipt fields.

## Review Focus

- Competing writers and case-only aliases must never replace an existing destination (Task 1, actual Darwin/APFS checks).
- A broad resource root must not expose `.SED-MCP` private state, and a sensitive sibling root must not pass containment (Task 1).
- Explicit invalid/no-driver Vulkan overrides must not fall through to bundled MoltenVK; an x64 process must not select arm64 Homebrew first (Task 2).
- Moving the `.app` alone or the full CLI package into a path with spaces must not lose libraries, templates or argv (Tasks 3–4).
- A cross-publish, unsupported-device log or headless fallback image must never become a native Mac GUI qualification (Task 4).

---

### Task 1: Darwin create-only publication and MCP private-state protection

**Owner:** `mac_safety`; no other implementer edits these files.

**Files:**
- Modify: `dotnet/src/Sed.Core/IO/AtomicFilePromotion.cs`
- Modify: `dotnet/tools/Sed.Mcp/Infrastructure/McpFilePolicy.cs`
- Test: `dotnet/tests/Sed.Core.Tests/AtomicFilePromotionTests.cs`
- Test: `dotnet/tests/Sed.Mcp.Tests/McpFilePolicyTests.cs`

**Interfaces:** Keep `AtomicFilePromotion.MoveNew(string stagedPath, string destinationPath)` and public MCP policy methods unchanged. macOS dispatch uses `/usr/lib/libSystem.B.dylib` `renamex_np` with UTF-8 arguments, `SetLastError` and `RENAME_EXCL = 0x4`; native errors become `IOException` with no unsafe fallback.

- [ ] Add regression cases for competing creators, existing file/directory/dangling symlink, case-only same-file refusal and successful distinct sibling publication. Extend the existing native-platform cases to macOS.
- [ ] Add Mac reserved-component tests across level/resource/output paths, including an explicitly allowed parent resource root and workspace case alias. Assert refusal before reads, unchanged receipt bytes and no new output; `.sed-mcp-other` remains allowed. Retain case-sensitive sibling-root denial.
- [ ] Root runs the focused existing platform tests to capture their baseline; record Mac-specific cases as pending native execution, not a fabricated local failure.
- [ ] Implement the narrow Mac branches from the spec, retaining strict root containment and existing Windows/Linux dispatch.
- [ ] Root runs affected Windows/Linux tests and native Mac/APFS cases once available; reviewer checks error/alias paths. Root commits exact reviewed files.

### Task 2: Bundle-local MoltenVK and advertised portability extensions

**Owner:** `mac_graphics`; no packaging or safety file edits.

**Files:**
- Modify: `dotnet/src/Sed.Rendering.Vulkan/NativeVulkanLocator.cs`
- Modify: `dotnet/src/Sed.Rendering.Vulkan/VulkanContext.cs`
- Test: `dotnet/tests/Sed.Core.Tests/NativeVulkanLocatorTests.cs` (new)

**Interfaces:** Preserve the existing locator public API. Introduce only a narrow internal pure resolution seam if needed for deterministic tests. Consume `AppContext.BaseDirectory/libMoltenVK.dylib`, process architecture and explicit Vulkan override state. Expose no new user-facing configuration unless existing API cannot represent the required choice.

- [ ] Add deterministic tests for relocated bundle candidate, absent candidate, dual Homebrew installation under each process architecture, stable `opt` ordering, and both explicit Vulkan override variables.
- [ ] Root runs focused tests; implement direct-bundle selection without auto-setting an ICD afterward, plus architecture-specific fallback.
- [ ] Add a testable extension-selection decision: the portability name and flag appear together only when advertised. Preserve existing conditional device portability subset handling.
- [ ] Root runs affected tests and later actual Mac `SceneProbe`/Wren cases; reviewer checks explicit override and no-device paths. Root commits the reviewed files.

### Task 3: Mac bundle, runtime/notice closure and archive verifier

**Owner:** packaging implementer; exclusive ownership of all files below.

**Files:**
- Create: `tools/macos.py`, `tools/package-macos.sh`, `tools/tests/test_macos.py`
- Create: `tools/macos/Info.plist.in`, `tools/macos/entitlements.plist`, `tools/macos/moltenvk-artifact.schema.json`
- Modify: `dotnet/tools/Sed.Mcp/Sed.Mcp.csproj`, `dotnet/tools/Sed.Mcp/packages.lock.json`
- Modify: `tools/Collect-NetNotices.ps1`, `tools/tests/collect_net_notices_fixtures.ps1`, `tools/tests/test_collect_net_notices.py`
- Create: `docs/MACOS_QUICKSTART.md`

**Interfaces:** `python3 tools/macos.py package --rid <osx-arm64|osx-x64> --dotnet <absolute-sdk> --pwsh <absolute-pwsh> --moltenvk-manifest <absolute-verified-artifact-json> --version <semver> --output <new-external-directory>` runs only on the matching native Mac architecture and requires clean source. `verify --package <directory> --output <new-external-directory> --graphics --full-workflows` performs actual native relocated acceptance. Pure metadata/archive helpers remain importable for tests on other hosts. The artifact manifest pins library/artifact hashes, bytes, version, source URLs and notice identities; no implicit download or Homebrew copying occurs in package assembly.

- [ ] Add packaging tests for RID/version validation, clean-source snapshot identity, external/no-reparse output guards, archive traversal/duplicate/membership/mode refusal, Mach-O architecture checks, self-contained runtime configuration and bundle metadata. Use original synthetic bytes; do not mirror implementation assertions blindly.
- [ ] Add portable launcher tests that preserve spaces/argv/caller cwd, and bundle-alone resource closure checks. Do not use GNU `readlink -f` or an SDK-dependent launcher.
- [ ] Add optional `-OutputDirectory` to the notice collector, retaining the old default. Explicit output must be external and link-free. Extend offline fixtures to prove the source notice tree remains unchanged, incomplete caches still fail, and exact runtime/package/license coverage is retained.
- [ ] Implement matching-RID self-contained publishes into the specified bundle/agent/MCP layout; include app data and support files necessary when the `.app` is copied alone. Add both Mac RIDs; root regenerates/reviews the lock without changing managed versions unnecessarily.
- [ ] Implement pinned MoltenVK placement, Mach-O/`otool -L` closure checks and complete runtime/license provenance. Run ad-hoc signing with the JIT entitlement on native Mac, verify it, then create final manifests/archives. Keep signing mode and notarization status explicit.
- [ ] Implement exact source/history/archive audits and native relocated CLI/MCP/editing/package/workbench checks. Existing workflow scripts are invoked against packaged apphosts, not development DLLs.
- [ ] Root runs synthetic tests and reviews the package schema/outputs; native acceptance is Task 4. Root commits only maintained source and small plan/documentation files.

### Task 4: Native Mac qualification and observed artifacts

**Owner:** root/coordinator; delegate code preparation only after Task 3 interfaces settle.

**Files:**
- Create: `.github/workflows/macos.yml`
- Create: `tools/test_macos_runtime.py`, `tools/tests/test_macos_runtime.py`
- Update after actual results: `docs/PROJECT_STATE.md`, `docs/WORK_LOG.md`, a versioned release handoff in `docs/releases/`

**Interfaces:** Native matrix explicitly maps `osx-arm64` to `macos-15` and `osx-x64` to `macos-15-intel`, with `permissions: contents: read`, `fail-fast: false`, bounded jobs and output under `runner.temp`. It consumes Task 3 commands and its reviewed dependency manifest. Runtime verification writes distinct authoring, graphics, Cocoa-startup, filesystem and signing states plus exact hashes.

- [ ] Prepare the two-host workflow, checking native `uname`/process architecture and SDK identity. Acquire only the reviewed pinned MoltenVK artifact into external runner storage. Use standard public hosts, no paid larger runner or new secret, and short artifact retention.
- [ ] Run native Core/MCP/host-tool/headless UI suites and scoped APFS safety cases. On a newly created test disk image only, exercise case-sensitive and insensitive filesystem lanes; do not format or modify an existing disk.
- [ ] Execute clean package creation and full relocated workflows on both architectures. Verify every archive/member/source/history identity after download; passing workflow status alone is insufficient.
- [ ] Execute actual scene rendering and Wren on the packaged agent. Require positive image evidence; record Metal-device absence as a graphics limitation/failure, not a pass. Separately capture bounded native Cocoa startup/UI evidence where the runner supports it.
- [ ] If the hosted runner cannot qualify graphics/UI, preserve its completed authoring/package results and seek an actual authorized Mac execution surface; do not redefine a CLI-only result as the requested GUI version.
- [ ] Root reviews emitted images and receipts, records exactly qualified platforms/limitations, derives branch/publication state and updates the overall consolidation handoff. Do not claim Developer ID/notarization or optional Mac C++ worker acceptance without their own evidence.

## Self-review and handoff

The four tasks cover the spec's source safety, loader behavior, distribution closure and actual native acceptance. File ownership is disjoint except the coordinator's final documentation. The initial Mac app uses ordinary in-app file opening; Finder file association and automatic updates are not silently advertised. Optional native worker, notarization and unsupported runner graphics remain explicit separate states. Root reviews this plan before authorizing product-code edits and retains all Git/build execution control.
