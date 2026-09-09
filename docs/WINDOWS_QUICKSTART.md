# Windows quick start

Extract the complete `SED-MotS-…-win-x64.zip` into a writable folder and open `Sed.App.exe`. Keep its neighboring runtime files, `data`, `licenses` and `agent` folders together. The distribution includes .NET; Delphi, Ubuntu and WSL are not required.

The 3D viewport needs a Vulkan-capable graphics driver. `agent/sed-agent.exe` works without a graphics device or desktop window. This is an unsigned development build. Exact file hashes are in `SHA256SUMS.txt`, and source provenance is in `PROVENANCE.json`.

## Open or create a level

1. Configure your owned game installation through the Game menu. Set JK and MotS folders separately so the editor can find the correct models, textures, colormaps and COG scripts.
2. Open a loose JKL, supported native JED/SED/NED text project, or GOB/GOO archive through File > Open. Choose the level in the archive level list. Native import reports unsupported metadata and exports JKL; retain the original project and companion template library as the recovery source.
3. Use File > New MotS level or New JK level for an editable room scaffold. It still needs materials, templates, a player start and an episode before gameplay testing.
4. Edit through the 3D viewport, 2D map and property inspector. Both views share selection and undo history. Save an edited JKL to your own project folder.

MotS's separate corner-light channels, architectural arrays and bindings survive supported edits. Moving a baked model keeps its old lighting; it does not calculate new architectural lighting. Replacing a same-name model file is not yet checked against its old geometry/hash. Template edits involving repeated keys are rejected if the single-value control cannot express the original list without ambiguity.

The vertex inspector edits MotS's mono, red, green and blue channels independently. The thing inspector can explicitly unbind baked architectural lighting before changing a model. Both operations support undo. Clipboard transfers require matching game types and check layer, template and colormap dependencies before modifying the destination.

## Test with OpenJKDF2 or retail

Use Test Setup to choose the executable and engine profile for the selected game. Auto recognizes standard names; explicitly choose a profile for renamed executables. OpenJKDF2 MotS launches receive `-motsCompat`; retail launches do not.

Choose a short project folder with ASCII characters and no spaces, for example `E:/JKProjects/test01`. The engine's resource-path budget includes `jkl/<filename>.jkl`, so a path can be too long even if the folder alone fits.

Save and Test writes a loose `episode.jk` and `jkl/<name>.jkl`, plus a GOB or MotS GOO. An existing episode that does not reference the selected level is left in place and the operation reports the mismatch. The executable runs from its game folder. For OpenJKDF2, `resource/jk_.cd` must be present so its installation helper does not redirect the working directory.

The validation and launch-contract checks do not constitute a campaign or multiplayer playtest. No game was launched during the development audit. Retain original sources and test exports in the intended engine before distributing a finished level.

## Use an AI agent

Point the assistant or script at `agent/sed-agent.exe` and [AGENT_PROTOCOL.md](AGENT_PROTOCOL.md). No AI service, credentials or model is bundled; this is a local executable it can call.

`schema` returns JSON commands and a patch JSON Schema. `inspect` reports the source SHA256 and indices. `plan` checks edits without writing. `apply` creates a new output and refuses to overwrite existing work. `launch-plan` returns an engine invocation without launching it.

## Source and notices

The accompanying `SED-MotS-source.zip` corresponds to the commit in `PROVENANCE.json`; `SED-MotS-history.bundle` preserves the local branch history. Build instructions are in README.md. Keep the GPL license and dependency notices with redistributed binaries and supply corresponding source under the license terms.
