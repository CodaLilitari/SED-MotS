# Windows quick start

Extract the complete `SED-MotS-…-win-x64.zip` into a writable folder and open `Sed.App.exe`. Keep its neighboring runtime files, `data`, `licenses`, `agent` and `mcp` folders together. The distribution includes .NET; Delphi, Ubuntu and WSL are not required.

The 3D viewport needs a Vulkan-capable graphics driver. Agent editing works without a graphics device or desktop window; the optional `snapshot` command uses offscreen Vulkan for diagnostic or textured, lit images. This is an unsigned development build. Exact file hashes are in `SHA256SUMS.txt`, and source provenance is in `PROVENANCE.json`.

## Open or create a level

1. Configure your owned game installation through the Game menu. Set JK and MotS folders separately so the editor can find the correct models, textures, colormaps and COG scripts.
2. Open a loose JKL, supported native JED/SED/NED text project, or GOB/GOO archive through File > Open. Choose the level in the archive level list. Native import reports unsupported metadata and exports JKL; retain the original project and companion template library as the recovery source.
3. Use File > New MotS level or New JK level for an editable room scaffold. It still needs materials, templates, a player start and an episode before gameplay testing.
4. Edit through the 3D viewport, 2D map and property inspector. Both views share selection and undo history. Save an edited JKL to your own project folder.

MotS's separate corner-light channels, architectural arrays and bindings survive supported edits. Moving a baked model keeps its old lighting; it does not calculate new architectural lighting. Preview and explicit exports check model names, every LOD's array coverage, and the historical normalized model fingerprint when a native project supplies one. Plain JKL does not carry that historical fingerprint, so current compatibility is reported separately from original bake identity. Save As and GOB/GOO export require configured archives when architectural bindings are present and refuse incompatible bindings. Recovery, autosave and backup remain available for unresolved projects. Template edits involving repeated keys are rejected if the single-value control cannot express the original list without ambiguity.

The vertex inspector edits MotS's mono, red, green and blue channels independently. The thing inspector can bind compatible existing architectural samples, reset/rebind to the current model, bake source lights with the explicit unoccluded profile, or unbind before changing a model. Configure game archives first. The inspector reports local bake status and visible refusal reasons; each authoring edit uses a private allocation and supports undo. See [architectural authoring](ARCHITECTURE_AUTHORING.md) for layout limits, resource checks and provenance. Clipboard transfers require matching game types and check layer, template and colormap dependencies before modifying the destination.

The light inspector also exposes separate white and RGB source intensity. Check "RGB follows white" for inherited intensity; uncheck it to enter an independent value, including zero to disable only the RGB contribution. Legacy RGB range is preserved metadata; leave that field blank to remove it. Both channels use the primary range during a MotS bake. These edits support undo and do not automatically rebake the level. Additional controls imported from native projects are retained in the JKL's `SED_SOURCE_LIGHTS` editor section. Keep the original project: older editors may drop that section, and retail acceptance remains unverified.

## Test with OpenJKDF2 or retail

Use Test Setup to choose the executable and engine profile for the selected game. Auto recognizes standard names; explicitly choose a profile for renamed executables. OpenJKDF2 MotS launches receive `-motsCompat`; retail launches do not.

Choose a short project folder with ASCII characters and no spaces, for example `E:/JKProjects/test01`. The engine's resource-path budget includes `jkl/<filename>.jkl`, so a path can be too long even if the folder alone fits.

Save and Test writes a loose `episode.jk` and `jkl/<name>.jkl`, plus a GOB or MotS GOO. An existing episode that does not reference the selected level is left in place and the operation reports the mismatch. The executable runs from its game folder. For OpenJKDF2, `resource/jk_.cd` must be present so its installation helper does not redirect the working directory.

The validation and launch-contract checks do not constitute a campaign or multiplayer playtest. Export checks the current editor archives; loose files and other runtime overrides may change which resources the engine resolves. Retain original sources and test exports in the intended engine before distributing a finished level.

## Use an AI agent

The MCP-enabled package also provides `mcp/sed-mcp.exe` for local clients such as
Codex. See [MCP setup and workflow](MCP_INTEGRATION.md) for explicit workspace
paths, project-scoped configuration, typed tools, inline previews and interrupted
operation recovery. This interface uses the same shared editing services as the
CLI below; it does not synchronize an open GUI's unsaved document.

Point the assistant or script at `agent/sed-agent.exe` and [AGENT_PROTOCOL.md](AGENT_PROTOCOL.md). No AI service, credentials or model is bundled; this is a local executable it can call.

For Codex, the full distribution includes `.agents/skills/sed-mots/SKILL.md` and `AGENTS.md`. Open the distribution/source folder as the project, or copy the skill into your level project's `.agents/skills` folder and give Codex the exact path to your installed agent executable. The skill needs no MCP server or global configuration change. It discovers supported commands from the selected build.

`--help` gives compact command discovery, and `schema <operation>` gives a focused patch schema. `query` reads current values and template candidates in bounded pages. JSON requests can arrive on stdin using `-`. `plan` checks edits without writing; passing its output hash to `apply` binds the new file to those reviewed bytes. Native projects also bind their imported template dependencies. `launch-plan` returns an engine invocation without launching it. See [AGENT_WORKFLOWS.md](AGENT_WORKFLOWS.md) for a complete read, plan, apply and readback example.

For architectural model validation, add exact archive paths and hashes as `resourceArchives` in the patch, or use `schema resources` with `validate`/`convert`. Copy the reviewed plan's `resourceRevision` into the patch before applying it. That separate check catches changed model bytes even when the JKL output would be identical. Results distinguish usable current topology from a matching historical model baseline; missing historical information remains unknown. Resource-free edits remain available with an explicitly unverified ARCH result.

The supplied `tools/Test-AgentDesign.ps1` demonstrates a connected room layout, material/UV assignment, readback and offscreen images using the installed executable. It uses fictional resource names to test registration, so its output is a geometry fixture rather than a playable episode. Run it with `-AgentPath` and a new `-OutputDirectory`; use `-SkipSnapshot` on a machine without Vulkan. The normal path never opens the editor or a game.

For source lights, discover `schema setLight` and query the current `lights` collection. `tools/Test-AgentSourceLights.ps1` demonstrates native conversion, reviewed control edits, nullable RGB intensity and preservation readback using synthetic input. It needs no graphics device; see [the workflow guide](AGENT_WORKFLOWS.md#repeatable-source-light-workflow).

For a lit preview, use `schema snapshot` and supply `mode: "lit"`, an explicit camera, and exact GOB/GOO paths with their SHA-256 values. The default `renderProfile: "openjk"` follows the accelerated engine's static lighting; `"palette"` is a separate CMP comparison profile. `lightingMode` selects game-default, mono or RGB world lighting, while ordinary models remain mono. Strict asset validation is on by default. `tools/Test-AgentLitSnapshot.ps1` demonstrates the complete process with synthetic assets. These are static previews: runtime lights, COG, animations and full retail transparency are still outside the tested behavior. See [MOTS_LIGHTING_STATUS.md](MOTS_LIGHTING_STATUS.md) for the tested features and remaining work.

## Source and notices

The accompanying `SED-MotS-source.zip` corresponds to the commit in `PROVENANCE.json`; `SED-MotS-history.bundle` preserves the local branch history. Build instructions are in README.md. Keep the GPL license and dependency notices with redistributed binaries and supply corresponding source under the license terms.
