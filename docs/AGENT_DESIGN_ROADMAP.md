# Agent level-design integration and roadmap

SED MotS exposes its editing model through a local JSON CLI. The repository now ships the [sed-mots skill](../.agents/skills/sed-mots/SKILL.md) to make that workflow discoverable in Codex. The skill uses the same CLI available to other assistants and scripts; it does not introduce a model service, credentials, network listener or screen-control dependency.

The installed executable's `--version`, `--help` and focused `schema` responses define its actual capabilities. Consult [AGENT_PROTOCOL.md](AGENT_PROTOCOL.md) for guarantees and [AGENT_WORKFLOWS.md](AGENT_WORKFLOWS.md) for executable examples. This roadmap describes priorities, not a promise that every capability below already exists.

## Current integration choice

| Need | CLI with repository skill | Optional stdio MCP adapter |
| --- | --- | --- |
| Discovery | Skill metadata selects the workflow; CLI help and focused schemas describe commands | Named tools and their schemas are advertised through MCP |
| Context size | Existing bounded queries, searches and selective schemas | Reuse the same bounds and queries |
| Reviewed editing | Plan receipts, source hashes, native template revisions and reviewed output hashes | Preserve the same contracts through tools |
| Visual feedback | Request an explicit image artifact and inspect it with the client's image tools | Return an image content block with the same render metadata |
| New design operations | Implemented in the shared editor model and commands | Calls those same operations; transport adds no geometry or game semantics |
| Operational cost | No persistent server | SDK dependency, protocol negotiation, cancellation and client tests |

Codex discovers repository skills in `.agents/skills` and loads their instructions when selected. A concise `AGENTS.md` points contributors and level-design tasks at the relevant protocol. These files belong with the source and complete distribution; a user working in a separate level-project folder can copy the skill there and identify the chosen editor installation. No global configuration edit is needed for this workflow. [Official Codex skill documentation](https://learn.chatgpt.com/docs/build-skills), [project instructions](https://learn.chatgpt.com/docs/agent-configuration/agents-md)

The current development focus extends the CLI toward resource registration, surface texturing/render settings, connected geometry and explicit-camera snapshots. Discover each family in the installed build rather than copying field names from a proposed design. Resource registration, a geometric connection and a successful image each establish different facts; none alone establishes a playable episode.

The reusable [Test-AgentDesign.ps1 workflow](../tools/Test-AgentDesign.ps1) exercises these capabilities in a fresh output directory: two perpendicular MotS room extrusions, fictional resource metadata, surface texturing, optional player-template placement, readback, a separate JK connected-room edit and bounded interior snapshots. It retains exact requests, replies and a receipt, including occupied-portal, stale-image and output-overwrite refusals. Use `-SkipSnapshot` for machines without a Vulkan device; that run reports image checks as skipped. Successful image generation still needs visual inspection, and the example does not include playable resources or an episode.

The static lighting layer also runs without a GUI: `snapshot` with `mode: "lit"` takes explicit hash-bound archives and uses the shared world/model assembler. `lightingMode` (`game`, `mono`, `rgb`) selects scene lighting independently of `renderProfile`: default `openjk` multiplies base palette color by textured scalar light, while `palette` selects CMP light-table rows. Ordinary model things retain the mono pass even in an RGB scene. Diagnostic requests reject these lit-only settings. Receipts identify both selections and the material, colormap and model provenance; keep the profile fixed for a channel comparison.

The preview and resource-aware exports now share all-LOD architectural model validation, including optional native normalized fingerprints. `plan`, `apply`, `validate` and `convert` accept explicit archives; a separate reviewed `resourceRevision` detects model changes even when JKL output bytes stay identical. GUI exports validate current editor archives and recheck document/resource state before promotion. Missing historical fingerprints remain unknown, and no validation rebakes lighting or proves complete runtime resource closure. The [synthetic lit workflow](../tools/Test-AgentLitSnapshot.ps1) exercises the static path, ARCHLIGHTING and resource failures. This is an implemented feedback loop for Codex; runtime dynamic lighting, engine camera capture and retail comparison remain separately versioned engine work. Neither rendering profile establishes full retail parity.

## Optional MCP transport

A future adapter should be a small C# stdio executable referencing the existing automation library. Expose a few named domain tools for querying, validation, planning, applying, archive inspection, conversion and rendering. Keep schemas and behavior shared with the CLI; avoid an arbitrary-command wrapper or a second implementation of edits.

The official C# SDK provides stdio transport and typed tool registration. Send logs to stderr and reserve stdout for protocol messages. The local tool does not need HTTP hosting, OAuth or model sampling. [Official C# SDK getting started](https://csharp.sdk.modelcontextprotocol.io/v1/concepts/getting-started.html)

Codex supports local stdio servers, trusted project configuration in `.codex/config.toml`, tool allowlists and timeouts. Its initialization `instructions` can explain cross-tool constraints; keep the first 512 characters self-contained. An adapter should explain query, plan, reviewed apply and readback, plus the distinction between structural and gameplay evidence. No MCP server is implemented or registered by the skill. [Official Codex MCP documentation](https://learn.chatgpt.com/docs/extend/mcp?surface=cli)

Return actionable domain failures as tool results with `isError: true`, preserving operation indices and field locations. Return structured results with an output schema and a textual JSON representation for compatibility. Preview tools should supply actual image content plus source revision, camera, dimensions and warnings; an image path alone is not an inline image result. [MCP tools specification](https://modelcontextprotocol.io/specification/2025-11-25/server/tools)

Choose and pin an SDK deliberately. The `2025-11-25` protocol negotiates versions through `initialize`; the C# SDK v2 documentation describes `2026-07-28` discovery and fallback changes. Those documents do not establish which revision an installed Codex client will negotiate. Before advertising compatibility, record the actual client and negotiated revision and exercise discovery, readback, a refused stale edit, a successful reviewed edit, cancellation and an image result. Do not require the July revision merely because its documentation exists. [MCP lifecycle](https://modelcontextprotocol.io/specification/2025-11-25/basic/lifecycle), [C# SDK transition behavior](https://csharp.sdk.modelcontextprotocol.io/v2/concepts/stateless/stateless.html)

### Next integration after EDIT-01: MCP first, ACP for a later editor panel

Public-source comparison on 2026-09-25 identified useful independent precedents.
[dcc-mcp-maya at 1eff754](https://github.com/dcc-mcp/dcc-mcp-maya/tree/1eff754a0106cb34007d0c6ebdfbf0ee4fba1419)
offers progressive capability discovery, typed tools, scene resources and readiness
checks. Its background service dispatches Maya work through the UI thread.
[abrahamADSK/maya-mcp at a9f0af1](https://github.com/abrahamADSK/maya-mcp/tree/a9f0af10ad327a052a12b8b7cfc4aa812be265e2)
combines scene snapshots, preview tools and searchable operation documentation.
Both repositories declare MIT licenses. These are source/documentation comparisons;
neither adapter was installed or executed, and neither is a SED implementation.

Apply the useful interface ideas to SED's own services:

1. Expose a compact set of named tools for capability/schema lookup, scene and
   spatial queries, diagnostics, resource inspection, plan, apply and snapshots.
   Return bounded schemas and examples on demand instead of loading every
   operation into each model context.
2. Preserve source and resource revision scopes, plan output hashes, refusal
   details and serialized readback. Serve an inline image with its camera,
   rendering profile, asset receipts and source identity. An undo group alone
   does not establish rollback or all-or-nothing mutation.
3. Start with a local stdio process and explicit workspace/resource paths. Use
   the same domain implementation as the CLI. Arbitrary Python/MEL execution and
   network listeners in other applications are not prerequisites for SED design.
4. Accept only after an actual MCP client discovers the tools, designs a connected
   fixture, changes a typed COG binding, bakes the declared lighting profile and
   receives a preview. Exercise stale-input refusal, output preservation,
   cancellation and progress on bounded long operations. Record actual negotiated
   protocol and executable identities.

ACP here means **Agent Client Protocol**, whose purpose is connecting an editor
to an agent. Its session setup can supply MCP servers to that agent. SED could
later act as an ACP client for an embedded assistant panel while its MCP server
continues to expose editing tools. That also needs an explicit shared-document
revision/undo protocol to handle unsaved human edits. Neither protocol provides
that document model automatically. [ACP introduction](https://agentclientprotocol.com/get-started/introduction),
[ACP session setup](https://agentclientprotocol.com/protocol/v1/session-setup)

This is the next recommended integration, not an implemented MCP/ACP capability.
The delivered EDIT-01 Windows package remains usable through `sed-agent` and
the repository skill without either protocol or screen control.

### Independently reviewed first-adapter boundaries

A bounded external architecture review on 2026-09-25 used the existing
Antigravity Gemini 3.1 Pro / High subscription route and the published source at
`11550ee9`. No tools were executed by that reviewer. Its recommendations were
checked against source and the MCP cancellation rules; this is design review,
not an implemented or client-tested adapter.

- Reuse `Sed.Formats.Automation` directly for text queries, planning and edits;
  its public services already exist. Begin with bounded queued work rather than
  assuming all static services support concurrent use. A semaphore controls
  concurrency, not per-request memory or CPU consumption: retain runtime size
  and work limits as well as schema constraints.
- Keep offscreen Vulkan rendering in a fixed, version-checked snapshot worker
  for the first adapter. Invoke its executable with an argument array, not a
  shell command supplied by a model. Bound process time, captured output and
  image bytes. This contains a rendering-process failure without duplicating
  the editor's geometry or resource semantics.
- Make the reviewed output hash mandatory for MCP apply and recompute the edit
  through the existing service. Preserve no-overwrite promotion. Competing
  final-path writes already become conflicts; they are not evidence of a
  corruption bug. A matching hash proves content identity, not exactly-once
  execution or which request created a file.
- Define commit and response delivery separately. Existing operations lack
  cooperative cancellation. A late canceled response cannot be the recovery
  mechanism because clients may ignore it. Provide scoped outcome receipts and
  subsequent-call reconciliation; never claim rollback or silently retry an
  uncertain write. Cancellation around final promotion needs controlled tests.
  [MCP cancellation rules](https://modelcontextprotocol.io/specification/2025-11-25/basic/utilities/cancellation)
- Explicitly configure project/output and read-only asset roots for the adapter,
  covering companion templates and archive/image paths too. The general local
  CLI does not claim workspace containment. The adapter must validate Windows
  aliases and reparse points if it advertises that boundary; normalization or a
  simple string prefix is insufficient.

A packaged executable check measured 93,828 UTF-8 bytes for the complete schema,
versus 3,975 for `rebindCogReference` and 3,074 for `snapshot` (LF-joined output).
This supports compact discovery with exact details on demand. These are byte
counts, not tokenizer benchmarks. Actual protocol/client negotiation, failure
isolation and the cancellation/receipt tests remain implementation acceptance.

## Priorities for serious level design

[ARCH-01](ARCHITECTURE_AUTHORING.md) supplies explicit bind/reset/rebake commands, private sample allocations, source-point sampling, saved provenance, computed stale status, and bounded sample queries through the shared GUI/CLI model. [EDIT-01](EDITING_FOUNDATIONS.md) adds an explicitly separate world-occluded recipe, preserving the original unoccluded profile. Both refuse unsupported LOD/pivot/hierarchy layouts. Alpha-aware material, model/self shadows and multi-LOD sample correspondence remain separate work; an MCP transport would expose these same domain contracts rather than supply missing lighting semantics.

EDIT-01 also implements bounded point containment, neighbor texture alignment,
per-light world bake explanations, known direct asset receipts, and typed COG
binding discovery/rebinding. Query and plan resource revisions have distinct
scopes. Direct asset resolution is not recursive closure; guarded existing COG
parameter edits do not yet create episodes or safely delete/reindex arbitrary
referenced objects. The remaining priorities below should build on those
implemented contracts rather than recreating them in a wrapper.

| Priority | Capability | Useful outcome and acceptance evidence |
| --- | --- | --- |
| 1 | Spatial queries, picking and stable handles | Find a floor, doorway, region or visible surface by geometry rather than guessing indices. Return coordinates, candidates and ambiguity. Bind handles to a source revision and explicitly invalidate or remap them after topology changes. Test shared vertices, overlapping geometry and stale handles. |
| 1 | Reference-aware geometry and object transactions | Extend connected geometry operations with typed COG, thing, sector, surface and unknown-section reference accounting. An edit either preserves/remaps supported references or explains why it cannot. Test undo, reparse and references whose old numeric index remains valid but identifies a different object. |
| 1 | Controlled legacy repair | Inspect existing invalid geometry, propose specific repairs and report which errors changed. Keep unrelated defects visible. Verify locality, retained data and affected references rather than accepting a file merely because fewer errors were emitted. |
| 2 | Resource resolution and dependency closure | Resolve registered MAT/CMP/3DO/COG/template names against configured game/addon resources, identify missing or ambiguous assets and record provenance. Collect only the authorized custom dependencies needed by a distributable project. Test case differences, overrides, inheritance and transitive COG/template references. |
| 2 | Episode and COG authoring | Build an episode with correct JK/MotS paths and level entries; expose script parameters from available schemas and preserve argument order. Add reviewed script-binding edits before attempting automatic reference renumbering. Verify against the intended engine contracts and authored examples. |
| 2 | Lighting design and resource-aware rebaking | Distinguish stored corner edits, editor lights and architectural lighting. Extend the preview's model-content/topology checks to resource-aware authoring and rebaking, with explicit model/LOD support. Compare independent mono/RGB components and bindings through save/reopen; hold the rendering profile fixed in controlled channel comparisons. |
| 2 | Repeatable engine test loop | Prepare an explicit engine profile, assets and episode; launch only within the task's authorized scope. Collect bounded diagnostics and a reproducible result. Distinguish reaching menus, loading the level, traversing geometry and multiplayer/script checks. |
| 3 | Shared human/agent document session | If agents must edit the currently open GUI document, add an explicit document/revision protocol, conflict handling and shared undo integration. A file-oriented CLI or MCP adapter does not automatically synchronize unsaved GUI state. |

The validation path should grow with these capabilities: source identity, planned semantic change, serialized readback, resource resolution, visual inspection and finally engine behavior. Each stage should report its own result. Passing format tests must not be presented as proof that the level plays correctly.
