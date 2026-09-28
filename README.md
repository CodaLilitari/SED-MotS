# SED MotS — Windows editor and agent tools

This development fork modernizes SED for Jedi Knight: Dark Forces II and Mysteries of the Sith. It uses C#, Avalonia and Vulkan, with a separate headless agent executable. The Windows distribution includes the .NET runtime: users do not need Delphi or an SDK.

OpenJKDF2 is the primary compatibility target. Separate retail profiles are included. This is a development build, not a claim of complete original-editor parity or verified gameplay compatibility.

Start with [the Windows guide](docs/WINDOWS_QUICKSTART.md) or [the agent protocol](docs/AGENT_PROTOCOL.md). The [MotS format audit](docs/audit/NET_MOTS_FORMAT_AUDIT.md) records preservation fixes and the precise scope of the stock-level checks.

The fork preserves MotS's independent mono/RGB lighting, architectural arrays and thing bindings, declared thing capacity, sector metadata and repeated parameters such as `frame=`. Copy/paste, lighting undo and sector cleaving preserve the added fields. The agent tool shares Core editing commands with the GUI and binds every patch's indices to its input SHA256. Windows launch profiles build direct executable arguments, and the Windows renderer uses `vulkan-1.dll`.

Agents can discover focused command schemas, query current values in bounded pages, send JSON on stdin, and require an exact reviewed output hash before writing. Native project edits also bind the imported template dependencies. The [agent workflows](docs/AGENT_WORKFLOWS.md) show the inspect, plan, apply and readback loop; no model service or API key is needed.

The local [MCP-01 adapter](docs/MCP_INTEGRATION.md) exposes eleven named tools
for discovery, querying, geometric selection, diagnostics, reviewed edits,
conversion, archive inspection, inline previews and outcome reconciliation.
It runs over stdio without screen control, uses explicit workspace/resource
roots and retains durable edit receipts across reconnection. Conversion binds
reviewed source/template identities as well as output bytes. The renderer runs
in a fixed isolated worker. Neither MCP nor the CLI synchronizes unsaved GUI
documents; configuration and precise limits are in the MCP guide.

The [PLAY-01 authoring workflow](docs/PLAYABLE_AUTHORING.md) adds typed COG
placement and ordered path frames through those same shared commands. Its
original four-room MotS exercise has passed hidden gameplay runs in a separately
instrumented OpenMotS build, including activation, gate motion, traversal and
objective state, with four fault controls. The editor distribution itself does
not bundle that engine or game assets; the guide distinguishes authoring,
static preview and runtime evidence.

[EDIT-01](docs/EDITING_FOUNDATIONS.md) adds shared neighbor texture alignment,
point-containment diagnostics, known direct asset receipts, world bake explanations
and typed COG discovery/rebinding. COG edits require a complete script schema and
the exact reviewed script bytes; imported global surface numbering is preserved.
The GUI uses the same guarded rebinding service and offers opt-in containment
checks. KEY sampling before its first entry is safely unavailable, and 3DO output
preserves nonzero insertion offsets.

Source lights now retain independent white and RGB intensities from supported JED/NED projects, along with legacy RGB-range metadata, color, flags and layers. The inspector and agent `setLight` command can edit these controls; existing `addLight` requests remain valid. A missing RGB override inherits white intensity, while explicit zero and an explicitly equal value remain independent. Supported native projects import into JKL; additional authoring values travel in the versioned `SED_SOURCE_LIGHTS` editor section beside ordinary `LIGHTS` rows. Keep the original project: older editors may discard this section, and retail handling remains unverified. Editing a source light does not automatically rebake stored level lighting; see [the source-light contract](docs/AGENT_PROTOCOL.md#source-light-authoring).

The agent can also register material/colormap references, edit surface UVs, extrude rooms and connect matching faces with checked portals. Queries expose surface normals, geometry bounds and neighboring sectors. Spatial `select` finds a room by world point or a face by outward direction and supplies a token bound to the source and containing-sector geometry; ambiguous and stale targeting never silently chooses another object. The [spatial authoring contract](docs/SPATIAL_SELECTION.md) describes its bounded convex-geometry scope. An explicit-camera `snapshot` produces a PNG using offscreen Vulkan without opening a window: diagnostic mode checks geometry, while lit mode loads explicitly hash-bound archives and previews static world/model lighting, MotS mono/RGB channels, compatible ARCHLIGHTING and sector CMPs.

Lit snapshots select `renderProfile: "openjk"` by default, multiplying the base palette color by textured scalar light; `"palette"` selects CMP light-table rows for comparison. This choice is independent of `lightingMode: "game"`, `"mono"` or `"rgb"`. Ordinary model things keep their mono pass even in an RGB world. The preview and resource-aware exports share checks for available native architectural model fingerprints and topology across LODs. These checks do not rebake lighting or add lower-LOD rendering. Dynamic lights, animations and retail pixel parity remain separate work; see [renderer details](docs/RENDERER_LIGHTING.md). The included [Codex skill](.agents/skills/sed-mots/SKILL.md) supports this workflow through CLI or MCP; see the [agent design roadmap](docs/AGENT_DESIGN_ROADMAP.md).

The [background engine capture tools](docs/ENGINE_CAPTURE.md) extend that loop into an existing OpenMotS capture build: prepare an original architectural-lighting fixture with the agent, stage explicitly hashed local engine resources, capture a requested camera in a hidden process, and compare controlled RGB changes with independently projected geometry masks. Each attempt records its inputs, limits and result. The companion tools use Python's standard library and require locally owned game bootstrap assets; no game assets are bundled. This bounded static check does not qualify a playable mission, retail behavior or deterministic simulation.

The thing inspector and agent share explicit architectural bind, reset/rebind and source-light bake operations. `source-points-v1` writes independent mono/RGB samples for supported single-LOD rest-pose models using unoccluded, non-directional point lights. The separate `source-points-world-occluded-v1` recipe adds world polygon blockers, open/BlockLight portal behavior and NoBlock source flags. Neither recipe includes material-alpha tracing, model/self shadows or retail equivalence. Each edit creates a private allocation, preserves old arrays and supports undo. Saved provenance reports stale inputs or overridden samples; captured model bytes are checked separately. See [the architectural authoring contract](docs/ARCHITECTURE_AUTHORING.md) for exact semantics and supported layouts.

## .NET build and packaging

Use the .NET SDK pinned by `global.json`:

```powershell
./tools/Build-Net.ps1 -DotNet 'C:/path/to/dotnet.exe'
```

This builds the solution and runs its tests, recording unique logs under `build/dotnet`. It does not start the editor or a game. The checked-in shaders permit a Windows build without a Vulkan SDK; running the 3D viewport needs a Vulkan graphics driver.

After committing the reviewed source, `tools/Package-Windows.ps1` creates a self-contained Windows editor, agent and MCP adapter, corresponding source ZIP, Git history bundle, notices and SHA256 manifests. Packaging requires PowerShell 7.4+ and Python 3.10+ (`-Python` selects an interpreter) for the actual packaged stdio workflow. End users do not need Python for normal GUI, CLI or MCP use. `tools/Collect-NetNotices.ps1` collects notices from restored packages and their pinned upstream sources.

## Provenance

This is a GPLv3 source-derived fork based on [davidagnome/SED at 908d53d](https://github.com/davidagnome/SED/tree/908d53d70d6b95784a92e9f1746ab5b13afcfdf0), which builds on [smlu/SED](https://github.com/smlu/SED) and JED by Alexei Novikov and the Code Alliance. It is not represented as a clean-room implementation. Preserve those credits and [LICENSE](LICENSE).

Separate .NET dependency and Phosphor font notices are in [licenses/net](licenses/net). Original game data, downloaded community levels, historical editor executables and restricted archival material remain external research inputs. They are not bundled. Research findings do not establish a right to copy unpublished source into the editor.

The current public source is [CodaLilitari/SED-MotS](https://github.com/CodaLilitari/SED-MotS), on `main`. It is a GitHub fork of [davidagnome/SED](https://github.com/davidagnome/SED) in the [smlu/SED](https://github.com/smlu/SED) network, preserving the complete source ancestry and the Andrei/Coda collaboration. The [0.4.0-mots.13-dev Windows prerelease](https://github.com/CodaLilitari/SED-MotS/releases/tag/v0.4.0-mots.13-dev) includes the editor, agent CLI, MCP host, matching source, complete Git history and SHA256 checksums. Extract the full Windows ZIP and run `Sed.App.exe`; Delphi is not required. See [the GitHub hosting record](docs/GITHUB_HANDOFF.md) for verified publication and access status: anonymous GitHub access still returned 404 on September 27 despite successful authenticated publication. The original upstream release links below describe upstream releases, not this development build.

## Historical upstream description

Yet another port of [JED](https://jkdf2.fandom.com/wiki/JED) level editor for games based on the [Sith game engine](https://starwars.fandom.com/wiki/Sith_Engine).  
The editor supports games: [Star Wars Jedi Knight: Dark Forces II](https://en.wikipedia.org/wiki/Star_Wars_Jedi_Knight:_Dark_Forces_II), [Star Wars Jedi Knight: Mysteries of the Sith](https://en.wikipedia.org/wiki/Star_Wars_Jedi_Knight:_Mysteries_of_the_Sith), and [Indiana Jones and the Infernal Machine](https://en.wikipedia.org/wiki/Indiana_Jones_and_the_Infernal_Machine). 

The codebase has been ported to better support modern Windows systems, as well as fixing many bugs and adding numerous new features. Most notably, the 3D Preview has been rewritten to mimic the original rendering system, added support for mouse control and cleaving from any angle. For a full list of changes, see [CHANGELOG.md](CHANGELOG.md).

![banner](docs/banner.png)

## Special Thanks

Special thanks go to Alexei Novikov and the Code Alliance for creating the original JED.

## Historical Pascal build

The historical Pascal implementation requires Delphi version 11 or later. It remains in the tree for provenance and is not required by the .NET distribution.

## Releases

See the [Releases](https://github.com/smlu/SED/releases) page for the latest version of SED. 

## License

This project is licensed under the terms of the license detailed in [LICENSE](LICENSE).
