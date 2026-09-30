---
name: jk-mots-level-workflow
description: Design, recover, edit and validate Jedi Knight Dark Forces II and Mysteries of the Sith levels with SED's headless CLI or MCP tools; use the maintained research library and optional private corpus for compatibility and editor comparisons. Includes geometry, lighting, resources and mission integration. For character rigging or standalone COG language work, use the specialized model or COG workflow.
---

# JK / MotS level workflow

Build the requested level or improve its weakest part. Use the same SED domain operations as the human editor, through its CLI or connected MCP adapter; routine design does not require screen control. Infer the game, target engine and existing project from the request before asking consequential questions. Prefer OpenJKDF2/OpenMotS when the user has not chosen another target; report retail evidence separately.

This pack includes original research findings in [research-index.md](references/research-index.md) and [findings.json](references/findings.json). On first use in a task, `python scripts/research_updates.py` can check for newer reference material unless offline work is requested. The check is optional and failure does not block work. Applying references is a separate explicit command; it preserves local changes. Read [updates.md](references/updates.md) when updating or packaging.

## Choose a practical route

| Starting point | Route |
|---|---|
| Existing JKL or native JED/SED/NED project | Inspect source and companion templates; query exact targets; plan supported edits into new outputs. |
| GOB/GOO level package | List archive entries, extract the chosen exact JKL, preserve the source package and asset provenance. |
| New layout | Create a scaffold, use geometric selectors and connected extrusion/adjoins, then add useful traversal, materials and lighting. Boxes placed next to each other are not automatically connected. |
| Mission or moving architecture | Author ordered frames and typed COG bindings, validate identity and portal topology, then test the actual interaction in a declared runtime. |
| Compatibility or historical-tool question | Search the research index first, retrieve exact context where privately available, then check current source or a discriminating fixture. A demonstration, source release and tested capability are distinct. |

For original-model creation use `jk-mots-model-workflow`; for COG language/parser/synchronization work use `jk-mots-cog-workflow`. Continue the level task while those specialized parts are handled. External reviewers are optional; use the external-reviewers skill when a bounded source or visual review would change the result. Only share project material authorized for that destination.

## Discover the actual installation

Use `sed_discover` if the SED MCP adapter is connected. Otherwise resolve the intended `agent/sed-agent.exe` inside the installed Windows distribution. A separately installed skill can carry a private `local-config.json` with `sedRoot`, `repositoryRoot` and `corpusHome`; this file is never included in a release. `python scripts/probe_tools.py --config local-config.json` checks exact executable paths, hashes, versions and command discovery without editing a level or starting a window. `--sed-root` works without local configuration.

Read the selected installation's `docs/AGENT_PROTOCOL.md`, `docs/AGENT_WORKFLOWS.md` and relevant focused schema. Inside the source repository, also read `docs/PROJECT_STATE.md` and `.agents/skills/sed-mots/SKILL.md`. The installed executable is authoritative; do not copy fields or capabilities from a newer roadmap into an older build. The existing sed-mots entrypoint remains compatible.

## Design, inspect, apply, inspect again

1. Establish input hash, native source revision and any resource identities. Query bounded pages and retain exact indices/selector tokens. For spatial selections, accept only unique results; a boundary may match more than one room.
2. Make the next change concrete with `schema <operation>`, `plan`, and before/after readback. Preserve unselected geometry, UVs, references and independent white/RGB lighting. Discover arrays, batch UVs, bindings and light diagnostics in the installed build rather than reimplementing them with text substitution.
3. Apply the reviewed output hash into a new file. Bind resources separately with the plan's resource revision where required. If source, resources or intended operations changed, plan again. Re-select after geometry changes; old tokens bind old geometry.
4. Query and validate the actual output. For visual work take an offscreen snapshot with an explicit camera and inspect the image. Lit snapshots require exact resource archives/hashes and a declared render profile; an incomplete asset preview must be labelled accordingly.
5. Test the behavior the user asked for. Structural validation, preview images, engine capture and gameplay establish different things. Use the existing original fixtures where appropriate, then adapt acceptance to this level. Preserve exact request/result receipts for durable handoff.

Planning is a machine check, not a reason to ask for human permission at every reversible edit. The MCP commit protocol additionally requires an operation ID; retain it across uncertain outcomes and consult `sed_outcome`. A timeout does not prove the write failed, and reusing an ID does not resume an edit. File edits do not synchronize a GUI's unsaved document automatically.

Read [headless-design.md](references/headless-design.md) for focused command routing and [compatibility-and-testing.md](references/compatibility-and-testing.md) for MotS lighting, mission and runtime boundaries. They link maintained project docs instead of copying the protocol.

## Research that survives a restart

Use [research-index.md](references/research-index.md) to choose a topic. If this installation has an authorized private corpus, read [private-corpus.md](references/private-corpus.md) and run the `corpus.py` helper with its explicit home directory. The index references existing captures in place; it does not fetch Discord or upload evidence. It distinguishes acquisition, reviewed text, source inspection, runtime verification and reuse. Search hits alone establish none of the latter states.

Record useful findings with exact source/version and the next acceptance case. Keep raw community logs, copied posts, third-party/game payloads and private locators outside public repositories and release packs. Shareable packets contain separately written original conclusions and public source links. Do not present source-informed work as formal clean-room work. Keep Urgon's announced Qt/C++ editor, Delphi SED, Mudhorn, NED/Smith and separate lighting utilities distinct during piecemeal comparisons.
