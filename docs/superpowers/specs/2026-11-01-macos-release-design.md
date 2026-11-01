# macOS release extension design

Status: coordinator accepted; implementation is in progress and native Mac qualification is pending. This extends Andrei's request, “Make a Mac ver too,” without replacing the Windows/Linux consolidation. The inspected baseline is `7642d5277b95347e3fc80af919fdd578c25edf4a`.

Priority correction: Andrei first said, “Well do Mac before the rewrite,” then assigned the C++ catch-up experiment to Muse Code. Complete this Mac extension and the remaining current-editor consolidation/delivery. Further C++/Qt rewrite adoption and Muse's experimental workspace are outside this goal. Preserve existing read-only component artifacts without expanding or porting them.

## Intent and scope

Produce native Apple Silicon (`osx-arm64`) and Intel (`osx-x64`) versions of the shared .NET editor, command-line agent and stdio MCP server. Keep the existing document, undo, retained-source and reviewed-write authority. Preserve the Windows/Linux acceptance evidence already produced from its exact earlier commit; do not relabel it as evidence for later Mac changes.

Both architectures are the coordinator's stated default, not a separately elicited user preference. The initial claimed OS floor is macOS 15, matching the proposed native CI hosts. Runtime compatibility with earlier macOS versions is not inferred from successful cross-publishing. The first deliverables are explicitly identified ad-hoc-signed test distributions; Developer ID signing and notarization require a verified existing identity and separate evidence. No paid service or Apple enrollment is authorized by this plan.

## Approach

Continue the existing `net10.0` Avalonia application and add Mac packaging plus narrow platform corrections. Avalonia already supplies its native Mac backend, and SED renders its scene offscreen into an Avalonia bitmap. A Qt rewrite or a new Metal/NSView surface implementation would replace the reviewed architecture without solving this release task. A Windows-only cross-publish would produce candidate bytes but would not establish Mac behavior. Native builds and execution on matching Mac runners are the acceptance path.

Use two per-architecture archives, rather than merging managed/runtime trees into an unqualified universal bundle. Keep the current optional C++ inspection component separate: no Mac worker binary or Mac runtime/source qualification exists yet, and the existing packager accepts only Windows and Linux. Do not rename an ELF/PE worker or promote either platform's core-test result to macOS. Qualifying a real Mac worker remains a separate follow-on if required for optional-component parity.

## Distribution contract

Each `SED-MotS-<version>-<rid>` directory contains:

- `SED-MotS.app/Contents/Info.plist`, `Contents/Resources/`, and the complete editor publish in `Contents/MacOS/`.
- `agent/` and `mcp/`, each with its self-contained apphost, managed assemblies, runtime and data. Keep their current sibling relationship for the default MCP preview worker.
- `bin/sed-mots`, `bin/sed-agent` and `bin/sed-mcp`: relocatable POSIX launchers that quote paths, preserve argv and caller working directory, and do not depend on GNU `readlink -f` or an installed .NET SDK.
- Shared tools, template/data files, documentation, credits, notices, `PROVENANCE.json` and an exact payload checksum/mode manifest.

The app bundle's executable is `Sed.App`; its complete required runtime and editor support data must remain available if the `.app` is copied alone. Rendering dependencies are placed beside their renderer apphost: `SED-MotS.app/Contents/MacOS/libMoltenVK.dylib` and `agent/libMoltenVK.dylib`. The MCP process itself has no renderer dependency. The `.app` layout must not depend on a package-root Homebrew installation or a library outside the copied app.

`Info.plist` declares a stable project identifier, `APPL`, high-resolution support, executable name, macOS 15 minimum, and valid numeric bundle versions derived from the release's numeric core. Preserve the full release version separately in provenance and `SEDReleaseVersion`. Do not advertise document-type associations until native file-activation handling has been implemented and tested; ordinary in-app Open and explicit command-line file arguments remain supported. A new icon, automatic updater, DMG installer and App Store submission are not required to make these distributions work.

## Source corrections

### Atomic writes and private MCP state

On macOS use Darwin `renamex_np` with `RENAME_EXCL` for same-directory promotion. An unavailable primitive or unsupported filesystem fails closed with `IOException`; no check-then-rename, overwrite or copy fallback is permitted. Keep Linux `renameat2` and Windows behavior. On Mac, reject case-only staged/destination equality conservatively while preserving strict sibling-parent comparison; this avoids the API's same-object case-only rename exception.

Reserve every full-path component equal to `.sed-mcp` under ordinal-ignore-case comparison on macOS, including reads through broader explicit resource roots. Keep root containment comparisons ordinal: changing all containment to case-insensitive could admit a distinct sibling on a case-sensitive volume. Test private-state refusal before file access, unchanged receipts and no output. `.sed-mcp-other` is not the reserved component.

### Vulkan discovery and portability

Prefer checked bundle-local MoltenVK when no explicit `VK_DRIVER_FILES` or `VK_ICD_FILENAMES` override exists. An explicit override must use the generic loader path and must not be silently bypassed by direct MoltenVK; this includes the deliberate no-driver paths used by non-rendering checks. Do not auto-set a Homebrew ICD after choosing direct bundled MoltenVK. Fallback discovery chooses the current process architecture's prefix (`/opt/homebrew` for arm64, `/usr/local` for x64), with stable `opt` paths preferred over versioned Cellar paths.

Enumerate advertised instance extensions. Enable `VK_KHR_portability_enumeration` and its instance flag together only when advertised; direct MoltenVK need not expose this loader extension. Existing conditional device `portability_subset` behavior is retained. No scene geometry or native view rewrite is needed.

## Packaging, dependencies and provenance

Add both Mac RIDs to the MCP project and regenerate its lock file through a coordinator-owned restore, retaining managed dependency versions unless an actual resolution requires review. Publish all three components as self-contained, untrimmed applications with apphosts and deterministic source revision metadata. Build and restore artifacts, caches and temporary files remain outside Git.

Use a separate `tools/macos.py`, not a renamed `linux.py`: the latter assumes Linux hosts, ELF/shared-library names and GNU launchers. New Mac packaging may reuse reviewed algorithms but must not mutate the accepted Linux helper during this increment. Require a clean source snapshot, verify every archived Git blob, and ship matching source and complete history alongside the binary archive.

Supply MoltenVK through an explicit versioned artifact manifest that pins the acquired artifact, selected Mach-O library, architecture slices and notice/source identities. Never bundle an arbitrary current Homebrew dylib without provenance. Audit `otool -L` and Mach-O architectures: required dependencies must be packaged or Apple system libraries, with no build-machine Homebrew path leakage. Record the actual runtime requirement and observed Vulkan capabilities.

Collect exact resolved editor/agent/MCP and runtime-pack notices. Extend the existing strict notice collector with an optional external output directory, preserving its default behavior and cache-completeness/license-text rules. Mac collection writes into the package, not the committed source snapshot. The inherited license inventory, project notices, font and .NET notices remain independently identified. After adding native libraries, sign native code from the inside out with the native Mac tool, use the JIT entitlement for the .NET apphosts, verify signatures, then write final manifests and archives. Ad-hoc signature success is not Developer ID or Gatekeeper/notarization acceptance.

## Native qualification

Use only standard public-repository GitHub hosts: `macos-15` for arm64 and `macos-15-intel` for x64. Verify the repository is public before dispatch. Use read-only repository permissions, a finite job timeout, explicit architecture checks, external runner-temporary outputs, no signing secrets, no paid larger runners, bounded artifacts and short retention. Capture the exact source SHA, runner image/version, SDK, OS and process architecture.

On each native host:

1. Run Core/MCP suites, new atomic/private-state regressions, applicable Python tooling and the headless UI probe. Exercise both case-insensitive and case-sensitive APFS using only newly created task-owned test images when available; preserve the distinction if either filesystem lane is not observed.
2. Publish, inspect Mach-O/runtime closure, sign, package and audit exact members, hashes, modes, source and history.
3. Relocate the package to a path with spaces, remove SDK discovery, then run real CLI editing/refusal/source-preservation, dependency packaging, MCP discovery/editing/default-worker and broad workbench workflows. Recheck package and fixture identities afterward.
4. Require an actual `SceneProbe` image with nonzero rendered pixels and the packaged-agent Wren snapshot suite for graphics qualification. `VulkanSmoke` exit zero alone can describe unsupported devices, and `AppShot` can draw fallback text; neither substitutes for a rendered scene. A runner lacking a usable Metal device records graphics as unavailable or failed, never passed.
5. Exercise native Cocoa application startup separately from Avalonia headless AppShot. A bounded survival check alone is startup evidence; native rendered/editor-interaction acceptance needs actual observable UI or a dedicated native probe. Do not claim it from headless tests.

A native CLI/package pass with an unavailable graphics lane may be retained as a clearly limited candidate. The complete requested GUI Mac version remains unqualified until an actual supported Mac graphics/UI lane passes. Remote workflow completion and artifact download/hash readback are separate gates owned by the coordinator.

## Sources checked during design

- [Avalonia Mac platform architecture and cross-compilation](https://docs.avaloniaui.net/docs/platform-specific-guides/macos): native backend and `net10.0` cross-publish support.
- [Avalonia app-bundle deployment](https://docs.avaloniaui.net/docs/deployment/macos/): apphost/layout, mode and signing considerations.
- [.NET Mac deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/macos): JIT entitlement and per-architecture publishing.
- [GitHub runner labels](https://docs.github.com/en/actions/reference/runners/github-hosted-runners) and [billing scope](https://docs.github.com/en/billing/concepts/product-billing/github-actions): standard public Mac runner route.
- [MoltenVK runtime guide](https://github.com/KhronosGroup/MoltenVK/blob/main/Docs/MoltenVK_Runtime_UserGuide.md): direct library versus loader integration, reviewed by the graphics task.

This document records design decisions and inspected source limitations. It is not a build, runtime, signing or publication receipt.
