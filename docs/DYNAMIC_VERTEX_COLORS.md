# MotS dynamic vertex-color readiness

This read-only diagnostic estimates RGB storage for the inspected retail MotS
and current OpenMotS surface loaders. It evaluates the exact JKL text that the
editor would save and reports the candidate UTF-8 SHA256. The GUI entry is
**Tools → Check Consistency → MotS dynamic vertex-color readiness**. Re-check
after editing; an open report does not refresh automatically.

For agents, discover `schema vertexColors`, then use:

```powershell
'{"schemaVersion":1,"offset":0,"limit":50}' | & 'C:/SED-MotS/agent/sed-agent.exe' vertex-colors 'E:/JKProjects/example.jkl' -
```

The equivalent MCP call is `sed_diagnose` with `kind: "vertexColors"`, the level
path and the same `request` object. `sed_discover` accepts `topic: "vertexColors"`.
Optional `sector` filters the report; `offset` and `limit` page the filtered
surfaces, with a maximum page size of 200. Each response binds the input hash,
native source revision/dependencies when applicable, and proposed output hash.
Follow `nextOffset` until null, checking source and output identity between pages.
Normal MotS patch plans include an aggregate `dynamicVertexColors` result whose
candidate hash matches the plan's `outputSha256`.

## What the result means

| Serialized lighting row | Inspected retail storage | Current OpenMotS storage |
| --- | --- | --- |
| `4N`, every finite float32 corner has `W == R == G` | `Compacted`: RGB storage is removed; the RGB setter cannot write it | `Retained` |
| `4N`, at least one finite float32 corner has `W != R` or `W != G` | `Retained` for the whole surface | `Retained` |
| `N`, scalar data | `Unknown`: retail scalar-row handling is unverified | `Scalar`: RGB flag cleared; RGB setter unavailable |

`N` is the corner count. `4N` stores white, red, green and blue for each corner.
Blue does not affect the inspected retail compaction predicate. A blue-only
difference therefore does not establish retail RGB mutability. One differing
corner is enough to retain the whole surface; separate surfaces remain separate
even when their corners reference the same world vertex.

The check uses finite float32 values from final serialized tokens, exact equality,
and no clamping or epsilon. Decimal differences can disappear when converted to
float32. If direct float32 and double-to-float32 parsing disagree on a retained
W/R/G token, the report keeps retail `Unknown`: that legacy decimal parser edge
has not been qualified. OpenMotS `4N` storage still follows row width. For newly
authored model corners with no independent white component,
the writer inherits white from red. An explicit white value, including zero,
remains independent. The diagnostic preserves these choices.

Unchanged GEORESOURCE sections can retain `N`, `4N`, or mixed rows. Regenerated
MotS GEORESOURCE rows use `4N`. Thus an unrelated geometry edit can change an
OpenMotS storage inference even when modeled corner values stay equal. The
diagnostic reads the candidate layout, not just the current in-memory colors.

Each surface carries its global output index, sector/local surface address,
layout, independent loader outcomes, first distinguishing corner when present,
render modes/adjoin status, and a reason. `unavailable` means serialization,
geometry, identities, non-finite values or row layout prevented assessment.
`unsupported-game` means the document is outside the MotS scope. A successful
tool call can still contain either status; inspect `status` and per-surface
outcomes before making an authoring decision.

These are static storage inferences, not a visibility or gameplay test. Fullbright,
hidden geometry and adjoins do not promote RGB allocation; a manually set flag
is not a substitute for allocation. There is no automatic color offset, flag
repair or COG insertion. A retained allocation does not prove a particular COG,
networking behavior or complete retail compatibility. Runtime comparison remains
a separate qualification step.
