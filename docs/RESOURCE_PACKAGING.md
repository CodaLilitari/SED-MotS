# Reviewed resource packaging

Create a new one-level JK GOB or MotS GOO from a **saved JKL** and explicitly
supplied resource archives. Planning reports resources, origins, stock
omissions, unresolved references, and exact output and manifest hashes.
Applying requires both review hashes.

The workflow runs through the CLI or local stdio MCP server without a display,
GPU, game installation, or API key. This increment adds no GUI dialog and does
not read an editor's unsaved document. Convert a native JED/SED/NED project to a
reviewed JKL first; packaging preserves that JKL's original bytes.

Start with the selected installation's capabilities:

```sh
sed-agent help package-plan
sed-agent help package-apply
sed-agent schema package
```

## Request and resource selection

The smallest request is:

```json
{
  "schemaVersion": 1,
  "inputSha256": "SOURCE_SHA256",
  "levelFileName": "mission.jkl"
}
```

Replace `SOURCE_SHA256` with the saved JKL's 64-digit SHA-256 value. The example
below calculates it automatically. Without `resourceArchives`, the plan can
build only if its known references require no external resources. Unresolved
resources block publication.

`resourceArchives` is an ordered array of explicit absolute archive paths and
SHA-256 values. The first matching archive has precedence. Within an archive,
lookup tries the resource's standard directory, then its exact archive path,
then a unique basename. An ambiguous match refuses resolution; it does not
fall through to a lower-priority archive.

`stockArchives` is an optional array of absolute path strings, each already
present in `resourceArchives`. A resource resolved from a declared stock archive
is omitted, with its exact source receipt retained in the plan. This declares
a baseline; it does not verify a game installation, identify retail content
automatically, or determine redistribution rights. A higher-priority custom
override remains included even if its bytes equal a stock asset.

No installation or loose directory is searched. `extraResources` can add known
roots from the supplied archives:

```json
[
  { "name": "custom_model.3do", "folder": "3do" },
  { "name": "custom_sound.wav", "folder": "sound" }
]
```

Supported folders are `mat`, `3do`, `3do/key`, `cog`, `sound`, `misc/cmp`,
`misc/pup`, `misc/snd`, `misc/spr`, `misc/par`, and `misc/ai`. An extra resource
names an archive resource, not an arbitrary local file.

## CLI: hash, plan, review, apply

Choose an existing project directory and replace these placeholder paths.
The example gives the custom archive higher priority and declares the second
archive as stock:

```sh
sed_agent="/absolute/path/SED-MotS/bin/sed-agent"
sed_source="/absolute/path/project/mission.jkl"
sed_custom="/absolute/path/project/custom.goo"
sed_stock="/absolute/path/resources/base.goo"

python3 - "$sed_source" "$sed_custom" "$sed_stock" <<'PY'
import hashlib
import json
from pathlib import Path
import sys

def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(65536), b""):
            digest.update(block)
    return digest.hexdigest()

level, custom, stock = [Path(value).resolve(strict=True) for value in sys.argv[1:]]
request = {
    "schemaVersion": 1,
    "inputSha256": sha256(level),
    "levelFileName": "mission.jkl",
    "episodeTitle": "My mission",
    "episodeType": 1,
    "resourceArchives": [
        {"path": str(path), "sha256": sha256(path)}
        for path in (custom, stock)
    ],
    "stockArchives": [str(stock)],
}
with open("package-request.json", "x", encoding="utf-8") as output:
    json.dump(request, output, indent=2)
    output.write("\n")
PY

(
  set -C
  "$sed_agent" package-plan "$sed_source" package-request.json > package-plan.json
)
```

The request and report filenames above must be new. A request may also be
supplied on stdin by replacing its filename with `-`.

CLI planning writes **the plan itself** to stdout. Its fields include
`canBuild`, `inputSha256`, `archiveExtension`, `outputSha256`,
`manifestSha256`, `outputBytes`, `nodes`, `edges`, `entries`, `stockOmissions`,
`issues`, and `unverifiedScopes`. There is no MCP `ok`/`result` wrapper in CLI
stdout.

Read the saved report before applying:

- Check `canBuild`, every issue, and the disclosed unverified scopes.
- Review included entries, source archive/entry identities, and stock omissions.
- Confirm the level filename, episode title/type, precedence, and byte count.
- Retain both reported review hashes with the request.

After that review, use the two identities from the saved report:

```sh
python3 - "$sed_agent" "$sed_source" <<'PY'
import json
import subprocess
import sys

with open("package-plan.json", encoding="utf-8") as source:
    plan = json.load(source)
if not plan["canBuild"]:
    raise SystemExit("Package plan is blocked; review its issues before applying.")

subprocess.run([
    sys.argv[1], "package-apply", sys.argv[2], "package-request.json",
    "mission-reviewed" + plan["archiveExtension"],
    plan["outputSha256"], plan["manifestSha256"],
], check=True)
PY
```

The equivalent command shape is:

```text
sed-agent package-apply <level.jkl> <package.json|-> <new-output.gob|goo> <expected-output-sha256> <expected-manifest-sha256>
```

Both hashes are mandatory. Apply recomputes the plan and refuses changed
identities, source bytes, or resource archives. A successful CLI apply returns
`schemaVersion`, `written` (the absolute output path), and `plan` (the reviewed
plan). Expected failures return `error`, `message`, and any diagnostics:
exit 2 for invalid requests, 3 for blocked validation, 4 for conflicts, and
5 for IO failures.

The output is `.gob` for a JK JKL and `.goo` for a MotS JKL. Both use the GOB
container format. Publication stages and flushes a sibling temporary file,
rechecks inputs and resources, then atomically creates the new destination.
Existing files and directories are refused; no source or existing archive is
updated. The parent directory must already exist.

## What the two hashes mean

`outputSha256` identifies the exact proposed archive bytes. It is null when
the plan is blocked. Entries have deterministic canonical names and order,
and the encoder verifies their readback. Generated `episode.jk` uses LF
endings; included JKL and resource payloads retain their original bytes.

`manifestSha256` identifies the canonical review manifest: planner/parser
versions, JKL identity, ordered archive identities and stock roles, options
and budgets, dependency graph, included/omitted entries, issues, and uncertainty.
It is a reported field, **not the SHA-256 of the pretty-printed stdout file**.

An unused archive or stock declaration can change the manifest while leaving
the output archive identical. Both hashes must match to bind the reviewed
decision. Package manifest hashes are separate from patch `resourceRevision`
and diagnostic `assetResourceRevision` values.

## Dependency scope and blocked plans

The planner follows known direct references, retained standard JKL resource
tables, and `extraResources`. Supported readers inspect 3DO materials, PUP
keyframes, SND sounds, SPR/PAR materials, and typed COG resource defaults.
Known resource-valued placed COG parameters are followed when their script
schema and parameter count are understood. MAT, CMP, WAV, and KEY are leaves
for dependency inspection; presence is not full format or rendering validation.

The graph records root/parent relationships, level-field or source-line
provenance, normalized requests, exact source entries, byte counts, hashes,
and inspection states. Repeated requests are deduplicated while distinct
origins remain visible. Every declared level template is scanned, including
templates not used by placed things. This is not playtime reachability analysis.

Because packaging keeps the source bytes unchanged, it refuses source forms
whose meaning differs between the current editor parser and the engine:
duplicate template names, repeated known resource fields, comma-delimited
tokens or continuation in template/thing rows, `//` or `;` template rows, and
indented semicolon rows in supported sections. These guards cannot be overridden
by `allowUnverifiedReferences`. They identify unsupported source semantics for
this workflow; they do not assert that the engine considers the files malformed.

A blocked plan remains reviewable: `canBuild: false`, an issue list, and no
publishable output hash. Apply refuses it.

| Finding | Effect |
|---|---|
| Missing/ambiguous resource, malformed supported table, invalid path, collision, incompatible ARCH binding, or exceeded budget | Blocks publication |
| Unsupported syntax or runtime uncertainty marked as requiring acknowledgement | Blocks by default; review before requesting `allowUnverifiedReferences: true` |
| Acknowledged uncertainty | Remains in the manifest; never bypasses errors or work limits |

COG code can construct/load names at runtime. AI variants, engine fallbacks,
and unmodeled references may remain unverified. Acknowledgement accepts those
disclosed limits; it does not resolve them. `staticScanComplete`,
`allReferencesResolved`, and `canBuild` answer different questions.
`runtimeClosureVerified` remains false.

## Names and limits

`levelFileName` permits 1–27 ASCII letters, digits, underscores, or hyphens,
followed by `.jkl` (31 characters total). `episodeTitle` permits 1–80 printable
ASCII characters without quotes, `#`, or line/control characters.
`episodeType` defaults to 1 (single player); 2 (deathmatch) is also supported.

Archive resource paths normalize case/separators and permit at most 127 ASCII
bytes plus a null terminator in the 128-byte GOB name field. They refuse absolute
paths, dot/parent segments, empty segments, spaces, reserved characters, and
non-ASCII text. Different requests colliding
at an output path are refused. Names are never silently truncated or replaced.
Filesystem source/output paths can contain spaces.

| Limit | Default / maximum |
|---|---|
| `resourceArchives` | 0 / 16 archives |
| `stockArchives` | 0 / 16 distinct members of `resourceArchives` |
| `extraResources` | 0 / 256 roots |
| `maxNodes` | 4,096 / 4,096 |
| `maxEdges` | 16,384 / 16,384 |
| `maxDepth` | 32 / 32; zero permits roots only |
| `maxPackageBytes` | 134,217,728 / 134,217,728 bytes, including header and directory |
| Source JKL | CLI 128 MiB; MCP 16 MiB |
| Request JSON | 1 MiB |
| Resource archives | 512 MiB each; 2 GiB combined |
| Captured resource bytes | 128 MiB per entry; 256 MiB combined |
| Dependency-bearing text | 4 MiB per resource; 32 MiB cumulatively |
| Text lines | 100,000 per dependency resource; 1,000,000 in the JKL |
| Physical text line length | 16,384 characters |
| Retained template/thing source parameters | 100,000 records combined |
| CLI plan manifest | 8 MiB |
| MCP structured envelope | 2 MiB; commits reserve 128 KiB for result/outcome metadata |

Lower graph/byte budgets may be requested. Exceeding a budget blocks the plan
or refuses the request; it never silently publishes a partial dependency set.
MCP bounds the plan report before creating an operation receipt or output.
Its path policy permits reads only in configured roots and writes only under
its workspace.

## MCP planning, apply, and recovery

Discover `sed_discover` with `{"topic":"package"}`. Then call:

| Tool | Arguments |
|---|---|
| `sed_package_plan` | `level`, `request` |
| `sed_package_apply` | Same `level` and `request`, plus `output`, `expectedOutputSha256`, `expectedManifestSha256`, `operationId` |
| `sed_outcome` | `operationId` |

`request` is the JSON object, not a filename. Source levels must be JKL.
Resource paths remain absolute/hash-bound; relative level/output paths use
the workspace. Stock declarations do not grant another read root.

MCP returns matching text/structured JSON. A plan is under `result`:

```text
{
  "schemaVersion": 1,
  "ok": true,
  "result": <package plan>
}
```

Use `result.outputSha256` and `result.manifestSha256` from that plan as the
expected hashes. Generate one GUID for the intended write and retain its exact
arguments. Successful apply returns `result.operationId`, `result.result`
(the shared commit result with `written` and `plan`), and `result.outcome`
(durable receipt and current output evidence). Errors set `isError` and return
an `error` object. A blocked plan still uses `ok: true`; inspect `canBuild`.

After timeout, cancellation, or reconnect, call `sed_outcome` with the original
ID. Identical `sed_package_apply` arguments with an existing ID retrieve history;
they never resume/repeat the write, even if the inputs have disappeared.
Changed arguments under that ID conflict. Preserve path, hash, and GUID spelling.

Historical state and current output evidence are separate. A `committed`
receipt can coexist with a subsequently missing/changed output. A lost running
owner is observed as `unknown`; matching bytes do not change that historical
state into a commit. Inspect the outcome and intended output before deciding
on a new ID or path. Never infer rollback from a lost response.

## Validation boundary

A successful package establishes this bounded static plan, exact archive bytes,
and guarded publication. It does not establish engine loading, runtime closure,
correct player starts, rendering, mission behavior, or retail compatibility.
Resources omitted as stock must still be supplied by the intended runtime.

The original process harness,
[tools/test_package_workflow.py](../tools/test_package_workflow.py), exercises
actual CLI and stdio MCP processes, independent archive readback, refusal
controls, and reconnect replay. It preserves raw requests/streams and hashes.
Invoke it with explicit native `--agent`, `--mcp`, and a new `--work` directory.
Executed results belong to that run's `verification.json`; a harness file
alone is not a completed acceptance result.

See [the agent protocol](AGENT_PROTOCOL.md), [MCP integration](MCP_INTEGRATION.md),
and [Linux quickstart](LINUX_QUICKSTART.md) for the surrounding editing workflow.
