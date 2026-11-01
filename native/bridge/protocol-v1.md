# Native inspection worker protocol 1

Status: implementation contract for CPP-BRIDGE-01. Version 0.1.0. This is a
read-only, one-request process. It owns no live editor document or undo history.

Executable name: sed-cpp-bridge (sed-cpp-bridge.exe on Windows).
Fixed argument order:

    sed-cpp-bridge --protocol 1 capabilities
    sed-cpp-bridge --protocol 1 inspect-jkl --project-kind mots --request-id 0123456789abcdef0123456789abcdef
    sed-cpp-bridge --protocol 1 inspect-native-sed --project-kind jk --request-id 0123456789abcdef0123456789abcdef

Project kinds are exactly jk, mots, ijim. Request identifiers contain exactly
32 lowercase hexadecimal characters. Unknown, reordered, duplicate or extra
arguments are rejected. Both inspection operations require an expected project
kind; a decoded different kind is rejected. JK and MotS both use JKL VERSION 1,
so the explicit hint is significant; content can still identify another kind.

Inspections consume raw file bytes on stdin until EOF. No path, archive root,
output filename, shell command or JSON request is accepted. Capabilities does
not read stdin. Maximum input is 16,777,216 bytes; an over-limit result reports
16,777,217 observed bytes and does not consume the rest. An empty document is
rejected. The JKL envelope requires a HEADER section with VERSION 1 or 3 and
rejects NUL bytes; this intentionally avoids accepting arbitrary text as an
empty default Level. No game resources or external template libraries are read.

Stdout contains exactly one UTF-8 JSON object followed by LF, at most 65,536
bytes including LF. There are no banners. An unexpected process failure,
including allocation failure, may prevent a complete JSON response: the caller
must validate both exit status and the complete response. Stderr is not a
protocol channel and must be bounded by the host.

Every response contains:

    schemaVersion: 1
    name: "sed-cpp-bridge"
    version: "0.1.0"
    upstreamCommit: "b82255998fb733c8e8c87104eb7c17f27b48376f"
    sourceManifestSha256: SHA-256 of the exact upstream.lock.json used to build
    operation: "capabilities", "inspect-jkl", "inspect-native-sed", or "unknown"
    requestId: valid supplied identifier, or null
    status: "ok", "rejected", or "failed"
    readOnly: true
    inputBytes: observed stdin byte count (zero for capabilities/argument errors)

Capabilities additionally contains capabilities (the two inspection operation
names), projectKinds, writesFiles:false, and limits with maxInputBytes,
maxOutputBytes, maxDiagnostics:32, maxDiagnosticBytes:256,
maxOptionalSections:32, maxIntegrityItems:32, maxJklLines:131072,
maxJklLineBytes:65536, maxJklTokens:1048576, maxJklSurfaceReferences:131072,
maxJklSurfaceExpansionTokens:1048576 and maxJklExpansionBytes:67108864.

JKL admission applies before the unchanged upstream loader. It caps physical
lines and raw bytes per line, then uses the pinned upstream comment stripping
and tokenization helpers. Meaningful lines contribute to a total token budget.
All lines beginning with SURFACES contribute their nonnegative third token to
the surface-reference budget. The maximum meaningful-line token count times
that summed reference count must fit maxJklSurfaceExpansionTokens. The maximum
meaningful-line byte count times (meaningful-line count plus summed surface
references) must fit maxJklExpansionBytes. These deliberately conservative
bounds cover repeated geometry and cloned material/colormap strings. Large
valid documents can be refused with input_limit; refusal is not a claim that
the document is invalid. Numeric malformed SURFACES counts are parse_error.

The pinned native codec additionally enforces its own fixed limits: 128 MiB
per decoded semantic section, 256 MiB combined expanded payload, 128 MiB of
logical decoded owned objects, and 2,000,000 decoded collection items. Those
budgets exclude allocator overhead, vector spare capacity, and raw section
buffers. The worker's 16 MiB input bound is tighter than the codec's 256 MiB
file-size limit. These checks do not establish a peak RSS or OS memory cap.

A successful inspection additionally contains:

    projectKind: "jk" | "mots" | "ijim"
    uvCoordinateSpace: "texture-pixels" | "normalized"
    summary: {
      sectors, surfaces, vertices, things, templates, cogs, lights,
      materials, colormaps, architecturalLightingObjects
    }
    resourceValidation: {
      status: "not-performed", complete: false,
      cogHydration: "deferred", uvNormalization: "not-performed"
    }
    diagnostics: [{severity, message}]
    diagnosticsTruncated: boolean
    integrity: {
      status: "not-applicable" | "clean" | "repaired",
      hadCorruption: boolean, repairedChunks: integer,
      repairedSections: [string],
      unreadableRanges: [{offset, length}],
      truncated: boolean
    }
    optionalSections: [{type: four-byte identifier rendered as four characters,
                        payloadBytes: integer}]
    optionalSectionsTruncated: boolean

Counts and offsets are nonnegative integers. Vertices and surfaces are summed
over decoded sectors, not original global JKL tables. Native optional section
payloads are never emitted. Their integrity is not Qt workspace/history semantic
validation. JKL integrity is not-applicable with empty/zero native fields.
Each optional identifier byte maps to one U+00xx character, with non-ASCII bytes
escaped. This preserves its four-byte identity independently of UTF-8 text.
Resource validation is always incomplete: no ARCH/model compatibility, gameplay,
COG hydration or MAT-backed UV completion is implied.

The native decoder can repair corruption in memory within its parity budget.
Such success is reported as repaired with the original decoder fields. It never
saves repaired bytes. Damage beyond strict load's budget is a rejection; no
salvage/defaulted partial Level is returned.

Diagnostics contain at most 32 entries and each retains at most 256 source
bytes. Severity is verbose, trace, debug, info, warning, error or fatal.
Truncation is explicit, including a clipped message. Valid UTF-8 is retained;
invalid source bytes are represented as individual U+00xx JSON escapes.
Authored/error text is data, never executable content.

Errors additionally contain error:{code,message}, with a bounded message.
They do not contain a success summary, integrity or optional-section results.

| Exit | Status | Error code |
| --- | --- | --- |
| 0 | ok | no error |
| 2 | rejected | invalid_request, input_limit |
| 3 | rejected | parse_error, project_kind_mismatch |
| 5 | failed | io_error |
| 6 | failed | internal_error, output_limit |

The host chooses a fixed configured executable, verifies its binary identity
and this source-manifest identity, provides captured input bytes, and binds the
observation to their SHA-256 and operation/options. Request IDs are correlation,
not authentication. The host enforces a deadline, bounded pipes, and confirmed
worker exit after attempted owned-tree termination. This worker spawns no
children; broader containment is not established. The worker is not an OS
sandbox and input-byte bounds
are not a hard process-memory limit. No observation is a committed edit.
