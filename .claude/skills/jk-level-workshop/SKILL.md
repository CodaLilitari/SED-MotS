---
name: jk-level-workshop
description: Design, recover, edit and validate Jedi Knight Dark Forces II and Mysteries of the Sith levels (JKL, GOB/GOO, JED/NED projects) with SED MotS's headless tools, which Red runs herself through her Linux port, and check them in the OpenMotS engine through private Linux captures; plus the maintained level research findings and Andrei's private research index. Covers geometry, UVs, lighting, resources, mission logic and testing. Use when Andrei asks Red to build, fix, light, inspect or test a JK/MotS level or to research editor and engine level behaviour. Red's Claude fork of Coda's JK/MotS Level Workflow.
metadata:
  version: "0.3.0"
  forked_from: "JK / MotS Level Workflow 1.0.0 (Andrei and Coda, MIT), September 2026"
---

# JK / MotS Level Workshop

Build the requested level, or improve its weakest part. Use the same SED domain operations the human editor uses, through its MCP adapter or CLI; routine design doesn't need screen control. Infer the game, target engine and existing project before asking consequential questions. Prefer OpenJKDF2/OpenMotS when Andrei hasn't chosen another target, and report retail evidence separately.

This is Red's fork of Coda's workflow. Red runs SED's headless agent herself, on Linux, through Coda's native Linux distribution (29 October 2026). She uses the `claude` branch, which adds the capture runner's Linux engine profile and carries this skill under `.claude/skills/`:

- **In the cloud workspace,** with offscreen snapshots and reviewed dependency packaging. `scripts/sed_cloud_setup.sh` builds, tests, packages and verifies it with the repository's own tools.
- **In the device shell,** beside the levels, for text operations, from a self-contained package Red keeps in Andrei's `SED-MotS-Linux` folder.
- **In the engine:** `scripts/openmots_cloud_setup.sh` builds OpenJKDF2-MotS-Fixes for Linux.
  - **Posed stills:** `scripts/openmots_capture.py` prepares captures that SED's runner takes on a private display.
  - **Scripted play:** `--agent` builds Coda's agent workspace for live single-player play-and-test sessions with screenshots and scenario reports.

  Both need Andrei's own game files ([openmots-linux.md](references/openmots-linux.md)).

The same distribution gives people the editor on Linux (`bin/sed-mots`). Red doesn't drive the GUI for routine work. Windows-only work and gameplay judgments go to Andrei or Coda. Details are in [red-routes.md](references/red-routes.md).

## Choose a practical route

| Starting point | Route |
|---|---|
| Existing JKL or native JED/SED/NED project | Inspect the source and companion templates, query exact targets, and plan supported edits into new outputs |
| GOB/GOO package | List entries (`sed_archive`), extract the chosen JKL exactly, and keep the source package and asset provenance |
| New layout | Scaffold (`sed_new`), use geometric selectors with connected extrusion and adjoins, then add traversal, materials and light. Boxes placed side by side are not connected |
| Mission or moving architecture | Ordered frames and typed COG bindings; validate identity and portal topology; test the interaction in a declared runtime |
| Compatibility or historical-tool question | The research index first, the private corpus for exact context, then current source or a discriminating fixture. Announcement, demonstration, source release and tested capability are different states |

- **COG language, parsing or sync** go to the **jk-cog-workshop** skill; **original models** go to **jk-model-workshop**. Keep the level task moving while those parts are handled.
- **Bounded reviews** of a layout or lighting decision go through **coastline-review**, with only material Andrei authorized for that destination.

## Discover the actual installation

- **Discover first:** `sed-agent --version` (capabilities), then `schema commands` and `schema <operation>`. Use `sed_discover` when working through an MCP client.
- **Read the installed build's documentation:** `docs/AGENT_PROTOCOL.md`, `docs/AGENT_WORKFLOWS.md` and the focused schemas. In the source checkout, also read `docs/PROJECT_STATE.md` and `.agents/skills/sed-mots/SKILL.md`, read-only.
- **The installed executable is authoritative.** Don't copy fields or capabilities from a newer roadmap into an older build.

## Design, inspect, apply, inspect again

1. **Establish identity:** the input hash, native source revision and resource identities. Query bounded pages and keep exact indices and selector tokens. Spatial selections must be unique, since a boundary can match more than one room.
2. **Make the change concrete:** `schema <operation>`, plan, and read before and after. Preserve unselected geometry, UVs, references and independent white/RGB lighting. Use the build's own arrays, batch UV, binding and light diagnostics; never text substitution.
3. **Apply the reviewed output hash into a new file.** Bind resources with the plan's resource revision where required. If the source, resources or intent changed, plan again. Re-select after geometry changes, because old tokens bind old geometry.
4. **Query and validate the actual output.** For visual work, take an offscreen snapshot with an explicit camera and look at the image. Lit snapshots need exact resource archives, hashes and a declared render profile, and an incomplete-asset preview is labelled as such.
5. **Test the behaviour Andrei asked for.** Structural validation, preview images, engine capture and gameplay establish different things. An engine still from Red's OpenMotS route shows what the engine drew from one camera; it isn't gameplay or retail parity. Keep request and result receipts for handoff.

Planning is a machine check, not a reason to ask permission at every reversible edit. Commits carry an operation ID: keep it across uncertain outcomes and consult `sed_outcome`. A timeout isn't proof that the write failed, and reusing an ID doesn't resume an edit. For command routing read [headless-design.md](references/headless-design.md); for MotS lighting, mission and runtime boundaries, [compatibility-and-testing.md](references/compatibility-and-testing.md).

## Research that survives a restart

[research-index.md](references/research-index.md) and [findings.json](references/findings.json) hold Coda's original findings: stable IDs, evidence states, next checks and public sources.

- **The private corpus:** for exact community context, use `scripts/corpus.py` against Andrei's private index, read-only unless he asks for a change ([private-corpus.md](references/private-corpus.md), [red-routes.md](references/red-routes.md#the-private-research-corpus)).
- **Search hits establish acquisition only.** They don't show review, source inspection, runtime verification or reuse.
- **Record useful findings** with the exact source and version and the next acceptance case.
- **Keep private material out of anything shareable:** raw community logs, copied posts, game payloads and private locators. Shareable packets hold separately written conclusions and public links.
- **Don't present source-informed work as clean-room work.**
- **Keep the editors distinct:** Urgon's announced Qt/C++ editor, Delphi SED, Mudhorn, NED/Smith and the separate lighting utilities.

Tests for the corpus and capture helpers: `python3 -B -m unittest discover -s scripts -p "test_*.py"`. Set `SED_TOOLS` to a SED checkout's `tools/` directory to also validate composed capture requests with SED's own runner.
