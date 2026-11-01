# Optional native core inspection preview

The consolidation includes a readonly C++ inspection boundary using pinned
upstream `smlu/SED` core code. It is an experimental capability with explicit
configuration; the ordinary .NET editor remains the document owner. Native
worker and host qualification results must accompany each distributed component.

The CLI interface is:

```text
sed-agent native-inspect <input> <jk|mots|ijim> <jkl|native-sed> <worker-config.json>
```

`native-sed` means the new binary upstream format. It does not mean legacy text
`.sed`; use the ordinary `inspect`/`convert` commands for JED/SED/NED text projects.
The file extension alone does not select the format.

Use the trusted configuration supplied with the matching native component. It
must sit beside `sed-cpp-bridge` on Linux or `sed-cpp-bridge.exe` on Windows:

```json
{
  "schemaVersion": 1,
  "executableSha256": "<exact component executable SHA-256>",
  "sourceManifestSha256": "<exact upstream.lock.json SHA-256>",
  "timeoutSeconds": 20
}
```

The placeholders above are documentation, not usable hashes. The component
build/packaging process supplies actual values. Unknown configuration properties,
duplicate properties and invalid schema/timeout values are refused. The config
does not accept executable names, paths, command arguments or shell fragments.
This is an operator-selected local CLI configuration, not an MCP request field.

The command captures at most 16 MiB of input. The shared host client verifies the
configured binary and source identity, uses a bounded protocol, and distinguishes
an inspected document, a document rejected by the codec, and process failure.
The outer CLI result names that status; exit 0 means inspected, exit 3 means
document rejection, and exit 5 means a worker/I/O failure. Invalid CLI/configuration
requests use exit 2. Exact input and worker hashes accompany observations.

Successful results contain structural counts and bounded diagnostics. Binary
native inspection additionally reports observable corruption and in-memory
repair status. It does not save repaired bytes. Resource-dependent validation
is explicitly incomplete, including COG hydration and MAT-backed UV completion.
Native integrity does not certify a Qt workspace, history semantics, game
loading or retained-source interoperability.

No source file, .NET document, undo state or operation journal is changed.
The shared client attempts owned-process-tree termination on a deadline or caller
cancellation and reports whether the direct worker's exit was confirmed. An
unconfirmed exit remains an explicit `termination_unconfirmed` failure. The CLI
does not wire console interruption to the shared client's cancellation token;
CLI interruption cleanup is not independently qualified. Process separation is
not a general OS sandbox or proof of descendant containment. The GUI and MCP do
not gain a second mutable document model.

The [architecture decision](audit/CPP_ADOPTION_DECISION_20261101.md) describes the
later mutation, source-envelope and native-authority gates. The worker's exact
build/input protocol is [native/bridge/protocol-v1.md](../native/bridge/protocol-v1.md).
Release coordinators can use the [component packaging workflow](../tools/NATIVE_COMPONENT_PACKAGING.md)
to bind the original build witness, final source archive, runtime files and
qualification evidence without enlarging the inspection preview's scope.
