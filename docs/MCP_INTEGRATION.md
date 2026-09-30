# MCP-01: local headless design tools

Status: completed and independently audited, 2026-09-27. Final source/package
commit `ce093bdeb2c568b3717a1649775af9721e0a0ddc`, Windows version
`0.4.0-mots.13-dev`. Completion documentation follows that tested source.
Andrei accepted the reviewed MCP-first recommendation; ACP remains deferred.

## Final Windows package checkpoint (2026-09-27)

The clean detached checkout passed 1,329 Core and 265 MCP tests, without build
warnings/errors. Six packaged CLI workflows passed 272 calls/866 checks plus
smoke. The actual packaged MCP host passed 65 completed calls and two
interrupted-write probes, including inline diagnostic/lit previews. Distribution
verification matched 924 manifested files, exact ZIP membership, corresponding
source and complete Git history at the same commit. Local handoff:
`build/packages/SED-MotS-0.4.0-mots.13-dev-ce093bd/`.

Lilitari's initial source review produced the additional preview validation
checks below. Her final changed-source follow-up accepted those corrections and
identified two narrow remaining issues: preview cancellation guidance and
inbound versus outbound size wording. Both are corrected in this package, with
an additional cancellation regression and an independent local diff review.
Review input was checked against source and executed evidence; she did not
claim to run our tests. The original `1b784ae` candidate is retained separately.
Independent package audit passed: all 783 source files match Git blobs, all 924
distribution hashes match disk and ZIP, and a standalone bundle clone passes
strict Git validation with the exact 345-commit/3,647-object reachable history.
The normal authenticated push updated remote `main` to `ce093bd`; GitHub metadata
remains public while anonymous access returns 404. The later explicitly requested
[Windows prerelease](https://github.com/CodaLilitari/SED-MotS/releases/tag/v0.4.0-mots.13-dev)
was published with matching source/history/checksums on September 27; its tag
targets the package commit. Uploaded hashes match, but anonymous release/asset
access still returns 404. See [the hosting record](GITHUB_HANDOFF.md).

## Local source checkpoint (2026-09-27)

The corrected adapter passes 265 MCP tests and 1,329 Core tests; the clean
package repeated both suites successfully.
The full solution build has no warnings/errors; the actual headless UI probe
also passes. An actual stdio client negotiated with the official C# SDK host
and passed 65 completed tool calls plus two intentionally uninspected write
requests, including geometric selection, connected extrusion,
reciprocal portal readback, independent MotS white/RGB source lights, reviewed
writes, typed COG rebinding, analytic architectural bakes for both declared
profiles, refusal controls, actual cancellation notification, recovery after
terminating a host with a committed but uninspected reply, and inline diagnostic
and resource-backed lit PNGs. Both modes were visually inspected. The adapter
now validates the fixed worker's RGBA PNG chunks/CRC, bounded decoded pixels,
dimensions and pixel hash, plus source/request/camera/resource receipt bindings.
This accepts the owned encoder subset, not arbitrary PNG encoders or a proof of
canonical compressed-stream encoding. The clean package repeated the unit and
wire gates above. Neither run is a model-driven Codex design session, engine
behavior or retail qualification; UI acceptance is the separate 126-check
headless control probe, not an interactive GUI playtest.

## Run locally or configure Codex

The Windows distribution includes `mcp/sed-mcp.exe` and its matching
`agent/sed-agent.exe`. Keep both directories and their bundled runtime files
together. Create a level-project directory before starting the server:

```powershell
& 'C:/SED-MotS/mcp/sed-mcp.exe' --workspace 'E:/JKProjects/outpost' --resource-root 'E:/Games/MotS/Resource'
```

That command speaks MCP on stdin/stdout and waits for a client. It does not show
a window. `--help` and `--version` exit immediately. Resource-root arguments may
repeat. They grant read scope; writes always create a new file inside workspace.
The packaged sibling worker is automatic; an explicit `--agent` must name a
matching `sed-agent.exe`, with `--agent-version` only when intentionally selecting
an exact different worker build. Missing worker files affect previews, not text
tools. Native projects use the adapter's shipped templates and same-directory
companion template libraries. MCP previews accept JKL only: use `sed_convert`
to review and save a native conversion first.

For a project-scoped Codex setup, merge this example into the level project's
`.codex/config.toml`, replacing both example paths:

```toml
[mcp_servers.sed]
command = 'C:/SED-MotS/mcp/sed-mcp.exe'
args = ['--workspace', 'E:/JKProjects/outpost', '--resource-root', 'E:/Games/MotS/Resource']
startup_timeout_sec = 20
tool_timeout_sec = 240
```

Codex supports local stdio launchers and project-scoped configuration. The longer
tool timeout accommodates the preview worker's 180-second main deadline and
bounded cleanup. Client timeout never proves that an edit did not commit. This
example does not change the user's existing tool approval policy. See the
[official Codex MCP configuration guide](https://learn.chatgpt.com/docs/extend/mcp).
No model/API account or service is bundled in SED. The CLI skill remains usable
without installing MCP. Configuration instructions are source-checked, not a
claim that a model-driven Codex design session was executed.

## Tools and editing loop

| Tool | Purpose |
| --- | --- |
| `sed_discover` | Compact capability list or exact focused domain schema |
| `sed_query` | Paginated source-identified readback |
| `sed_select` | Geometry-based selectors, with explicit ambiguity |
| `sed_diagnose` | Validation, direct assets, lighting, [MotS vertex-color storage](DYNAMIC_VERTEX_COLORS.md) (`kind: "vertexColors"`) or typed COG bindings |
| `sed_plan` | Recompute edits on an isolated document; return effects/hash |
| `sed_apply` | Commit a reviewed patch into a new JKL |
| `sed_new` | Plan or commit a JK/MotS room scaffold |
| `sed_convert` | Plan or commit a native/JKL conversion |
| `sed_archive` | Bounded GOB/GOO level-directory inspection |
| `sed_snapshot` | Static offscreen JKL preview and inline PNG |
| `sed_outcome` | Durable edit receipt and current output evidence |

Use discovery, query/select, then plan. Review the effects and copy the exact
plan's `outputSha256` into `expectedOutputSha256` for a new output and GUID
`operationId`. If resources were consumed, copy that plan's `resourceRevision`
into the domain request too. `sed_new`/`sed_convert` plan when all three commit
arguments are absent; partial commit arguments are refused. `sed_apply` always
requires all three. Planning is an engineering check; it does not impose an
extra human approval step beyond the client's/user's actual policy.

Conversion commits additionally require `expectedInputSha256` and
`expectedSourceRevision` from that conversion plan. Normalization can produce
the same output bytes after the source or a native template changes; the output
hash alone cannot bind the reviewed input. Patch requests already carry their
input/source guards in the shared domain request.

Each response has matching text/structured JSON: `schemaVersion`, `ok`, and
`result` or `error`; tool failures set MCP `isError`. A validation failure
contains diagnostics. MCP file-policy denials currently use `invalid_request`
with a `MCP file policy:` message. Preview results add an image content block.

Repeat the identical operation ID/request only to retrieve its outcome; the
adapter never reruns an existing intent. A changed request under the same ID is
a conflict. After disconnection, query `sed_outcome`: a historical committed
receipt and the output's present hash are distinct evidence. An orphaned running
record is observed as unknown, even if matching bytes exist. The journal is a
local record, not exactly-once execution, cryptographic authorship, or proof
against other processes modifying files. A preview writes a new PNG but does
not use the level-edit journal; its result/error includes separate output state.
Durable preview recovery is not implemented: a lost preview reply cannot be
reconciled by `sed_outcome`. Retain the intended PNG path and inspect it through
the client's authorized file/image tools before requesting another preview.
Never assume a lost response means no file was produced. Retry identity uses
the exact argument values (object property order is normalized); preserve path,
GUID and hash spelling rather than treating equivalent spellings as a new call.
Preview stderr is bounded separately. If a full renderer receipt cannot fit the
response, a small error still retains the output path/state and verified hash
when available; receipt omission does not imply that no PNG was written.

## Bounds and isolation

The adapter admits one domain operation and one independent outcome observation,
with no queued domain work. Discovery remains small and independent. Limits are
2 MiB inbound request frames, 1 MiB domain JSON, 16 MiB level/template inputs, 128 patch
operations, 512 MiB per resource archive/2 GiB aggregate, and 2 MiB structured
envelopes. The domain's focused schemas may advertise higher CLI limits; these
adapter limits still apply. Images are separately bounded to 16 MiB and the
domain's pixel/triangle limits. Complete outbound responses can exceed 2 MiB:
they include both text and structured representations of the envelope, plus
base64-encoded image data for a preview. No 2 MiB total outbound frame cap is
promised. Large jobs should be split into reviewed steps.

Synchronous text/domain calls run on a worker thread and retain admission until
they finish. Cancellation is cooperative, checked before work and immediately
before output promotion. This is not a hard CPU-time or process-memory sandbox.
Vulkan previews use a separate, fixed executable with version and executable/
managed companion identities. Termination targets only that child tree; if its
termination cannot be confirmed, this host refuses subsequent previews.
This quarantine lasts for that host process. Resolve the old child before
replacing the host; restarting alone does not prove the child was stopped.
The six pinned worker components are session identity checks, not an attestation
of the entire OS, driver or runtime. The distribution has a separate complete
file manifest; keep its verified installation intact.

Paths reject parent traversal, reserved receipt access, device/ADS aliases,
DOS short-name spellings and reparse-point ancestors. These checks are not an OS
sandbox against hostile concurrent filesystem replacement or an agent that has
independent shell/file tools. Protecting the whole agent requires its execution
environment's own permissions. No arbitrary-code tool or HTTP listener exists.

The first adapter uses shared SED automation services, a fixed isolated preview
worker, explicit project/resource paths, structured tool errors, bounded
discovery and inline PNG results. Level commits require an operation ID and
reviewed output hash. Durable outcomes distinguish committed files from reply
delivery and reconcile interrupted requests without automatically replaying them.

Acceptance requires actual stdio MCP discovery/calls, unchanged inputs,
source/resource conflict tests, concurrent-output refusal, cancellation and
restart reconciliation, path-policy checks and an image result. Independent
source review, a Windows package and exact source/binary identities precede
publication. No raw Discord/forum research or game assets enter the package.

Maya remains a possible later model/UV asset authoring tool, outside MCP-01 as
recommended in the reviewed consultation. Its usefulness does not establish a Sith level codec, sector/COG
reference model or MotS lighting behavior. No Maya purchase or installation is
part of the adapter's dependency set.

Current decisions and executed results will be recorded here and in WORK_LOG.md;
implementation plans are not completed capabilities or retail/gameplay evidence.

## Resume checkpoint, 2026-09-27

Lilitari's main Maya recommendation and ACP correction were retrieved on
2026-09-27. Keep Maya outside MCP-01; a later optional asset bridge must pass
semantic round-trip and measured-task gates. ACP may serve a separate background
controller as well as an embedded assistant; defer it until it solves an actual
coordination problem. Official ACP session/client documentation independently
supports that correction. Her current-commit GitHub access failed, so this was
architecture advice, not inspection of the MCP source. Exact replies and receipt
remain ignored/local under `build/mcp01-20260925/lilitari-maya/`.

Andrei authorizes resumed work and Review Hub delegation/review, and reports a
GitHub restriction. Actual Lilitari code review requires exact direct source
attachments in the existing project conversation. Root owns dispatcher, protocol
tests and integration; separate workers own file policy/outcomes and snapshot
worker/tests. A review coordinator owns only ignored review artifacts. Preserve
the source/asset privacy boundary and unrelated deletions.

## Historical pause checkpoint (2026-09-25)

All local implementation/review workers were interrupted. No MCP code was
committed, pushed, built or tested. The SDK dependency restore succeeded, which
is not a build or interoperability result. The scaffold pins the official
ModelContextProtocol.Core 1.4.1 SDK for initialize-based client compatibility;
actual Codex/client negotiation remains to be tested.

Uncommitted root work: Sed.Mcp and Sed.Mcp.Tests projects/solution entries;
HostOptions, ToolCatalog, BoundedInputStream and Program; shared PrepareNew and
pre-promotion cancellation hooks in AgentWorkspace/AgentPlanGuard. Program
references SedTools, which has not yet been implemented. Worker-owned
McpFilePolicy, OperationStore and SnapshotWorker files exist but require review
and integration. Their intended test files were not present at the pause.
Preserve the three unrelated pre-existing .DS_Store deletions.

Lilitari consultation was delivered in the existing Editor Recommendations
Review conversation in Lilitari Identity, observed UI model 6 Pro. Conversation
reference and delivered-message identity are recorded in the private local
receipt. Her response was still pending at the
last observation. Do not resend on resume: inspect that conversation first.
Private local receipt and exact approved prompt are in
`build/mcp01-20260925/lilitari-maya/`. No raw research or attachments were sent.
Local checks found no standard Maya install; no purchase, install or licensed
Maya run occurred. Public-source comparison remains a proposal, not validation.

Resume by inspecting Git and these partial files, recovering the already-sent
consultation, and re-establishing disjoint worker ownership. Implement the host
service and meaningful tests before any build/package/publication claim.
