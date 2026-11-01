# Optional native inspection component packaging

`package_native_component.py` produces a separate Linux x64 or Windows x64 ZIP
for the read-only `sed-cpp-bridge` preview. It does not compile or execute code,
run Git, modify the .NET editor, or qualify native editing. The main release and
the optional component use the same release version. Current planned version:
`0.4.0-mots.18-consolidated.1`.

## Required evidence

Prepare the final complete source archive from a clean, committed source tree.
It must be a Git-produced TAR or gzip-compressed TAR with its PAX `comment`
containing the exact commit. Windows ZIP source archives are not accepted here.
The packager checks that metadata and the reviewed qualification's archive hash;
it does not independently establish Git cleanliness. The release coordinator
must verify the complete archive against the committed source before attesting
`sourceClean: true`. A forged receipt is not prevented by an unsigned hash.

The final source archive must contain `LICENSE`, `NOTICE.md`, and the exact
eleven `native/bridge/` files recorded by `bridge-source-inputs.json`. All wrapper
file contents and the full wrapper file-name set are compared with that release
inventory. Git blob bytes, rather than worktree CRLF conversions, therefore govern
source alignment. This prevents a newer unreviewed wrapper file being silently
included. That release inventory is separate from the original build witness.

`buildSourceWitness` binds the original platform build evidence. The Windows
`bridge-before.json` is an original nine-row array of `{path, bytes, sha256}`;
the Linux original `bridge-source-inputs.json` is a schema/state/files object
containing eleven files. Every witnessed file is compared to the final archive.
All nine code, protocol, lock and test inputs are mandatory. Only `README.md` and
`.gitattributes` may be absent from a build witness; the eleven-file release
inventory must still cover and match them. Do not manufacture an eleven-row
Windows build witness from this later release inventory.

The original witness bytes are preserved verbatim as
`provenance/build-source-witness.json`; the manifest and README report witness
format, file count and release-only names. An object witness's `state` must
exactly match qualification field `binaryBuildSourceState`. The Windows array
has no state field, so its build-state description is a separate reviewed
qualification statement. A later clean source archive does not relabel the
original working-source binary build as clean or establish binary reproducibility.

The corresponding upstream archive is pinned to:

- Commit: `b82255998fb733c8e8c87104eb7c17f27b48376f`.
- Archive: `SED-upstream-core-b822559.tar`, 8,407,040 bytes.
- Archive SHA-256: `ef4ddb3ef0cee00fd7ddf8da17ff7a6fb058f11c5337b6e3ede366b8b66723f2`.
- `native/bridge/upstream.lock.json` SHA-256:
  `d1f1473d7644e65dc3ec95b2cbdb0743732ef9fd8e45d010a064b7d45ddaf098`.

All 213 upstream file identities and the exact file set are compared to the
pinned lock. The archive must retain its upstream Git commit metadata. The
source archives are included whole, with project/upstream licenses and notices,
the matched bridge build instructions and protocol, and applicable DLL notices.
Game assets and private corpus fixtures must not be present in the final source
archive; the source release review remains responsible for that inventory.

## Request JSON

All listed keys are required, with no extra keys. Artifact paths are absolute.
`bytes` is the exact nonnegative integer length; `sha256` is lowercase SHA-256.
The manifest is a local packaging input and is not copied into the public ZIP.

```json
{
  "schemaVersion": 1,
  "version": "0.4.0-mots.18-consolidated.1",
  "platform": "linux-x64",
  "sourceCommit": "<40 lowercase hex>",
  "sourceArchive": {"path": "<absolute final Git tar or tar.gz>", "bytes": 0, "sha256": "<hash>"},
  "upstreamArchive": {"path": "<absolute pinned tar>", "bytes": 8407040, "sha256": "ef4ddb3ef0cee00fd7ddf8da17ff7a6fb058f11c5337b6e3ede366b8b66723f2"},
  "bridgeSourceInputs": {"path": "<absolute original input receipt>", "bytes": 0, "sha256": "<hash>"},
  "buildSourceWitness": {"path": "<absolute original platform build witness>", "bytes": 0, "sha256": "<hash>"},
  "qualification": {"path": "<absolute reviewed qualification JSON>", "bytes": 0, "sha256": "<hash>"},
  "runtimeFiles": [
    {"path": "<absolute qualified worker>", "bytes": 0, "sha256": "<hash>", "destination": "sed-cpp-bridge"}
  ],
  "dependencyNotices": [],
  "runtimeRequirements": {"path": "<absolute reviewed ELF requirements receipt>", "bytes": 0, "sha256": "<hash>"}
}
```

The zero lengths and angle-bracket values above are placeholders, not usable
qualification data. On Windows set `platform` to `win-x64`, set
`runtimeRequirements` to `null`, and supply exactly these three runtime names:
`sed-cpp-bridge.exe`, `libc++.dll`, and `libunwind.dll`. Every file must have its
own captured length and hash, also listed in the qualification. Other DLLs,
executables, subdirectories and test configs are refused. The worker config is
generated from the captured worker hash and pinned source-manifest hash; an
input config is never copied.

Windows requires one `dependencyNotices` record:

```json
{
  "path": "<absolute verified official LLVM-mingw LICENSE.TXT>",
  "bytes": 0,
  "sha256": "8d85c1057d742e597985c7d4e6320b015a9139385cff4cbae06ffc0ebe89afee",
  "destination": "licenses/runtime/llvm-mingw-LICENSE.txt",
  "covers": ["libc++.dll", "libunwind.dll"]
}
```

The qualified DLL bytes must be those covered by that reviewed license/provenance
record. The packaging coordinator verifies this origin; the tool compares the
runtime hashes to the qualification, rather than inferring provenance from names.

Linux accepts the reviewed static ELF metadata receipt with fields
`workerSha256`, `workerUnchanged`, `elfClass`, `machine`, `elfInterpreter`,
`runpathOrRpath`, `directNeededLibraries`, and
`requiredSymbolVersionsByLibrary`. It validates the worker binding, ELF64 x64
identity, expected glibc loader, four direct libraries and symbol-version names.
Only that public subset and the input receipt hash are included. Local worker,
reader and log paths from the evidence are omitted. A runtime must supply **all**
listed ABI symbols and compatible transitive dependencies. This static evidence
does not establish a distribution support floor or successful host `ldd` resolution.

## Qualification JSON

The qualification is intentionally a small public receipt. Do not put local
paths, URLs or private log extracts in it. The three free-text descriptions
(`environment`, `binaryBuildSourceState`, failure `detail`) conservatively refuse
slash, backslash and drive-prefix characters; use plain descriptions. Runtime
identities in the public qualification allow no input `path` field. Use these
exact fields:

```json
{
  "schemaVersion": 1,
  "scope": "read-only-inspection-preview",
  "version": "0.4.0-mots.18-consolidated.1",
  "platform": "win-x64",
  "sourceCommit": "<final source commit>",
  "sourceClean": true,
  "sourceArchiveSha256": "<final source archive hash>",
  "binaryBuildSourceState": "working-source Windows build; original nine-file before-build witness",
  "sourceAlignment": {
    "method": "exact-wrapper-input-set-and-upstream-lock",
    "bridgeSourceInputsSha256": "<original receipt hash>",
    "buildSourceWitnessSha256": "<original platform build witness hash>",
    "upstreamArchiveSha256": "ef4ddb3ef0cee00fd7ddf8da17ff7a6fb058f11c5337b6e3ede366b8b66723f2"
  },
  "upstreamCommit": "b82255998fb733c8e8c87104eb7c17f27b48376f",
  "upstreamManifestSha256": "d1f1473d7644e65dc3ec95b2cbdb0743732ef9fd8e45d010a064b7d45ddaf098",
  "runtimeFiles": [
    {"destination": "sed-cpp-bridge.exe", "bytes": 0, "sha256": "<worker hash>"},
    {"destination": "libc++.dll", "bytes": 0, "sha256": "<DLL hash>"},
    {"destination": "libunwind.dll", "bytes": 0, "sha256": "<DLL hash>"}
  ],
  "environment": "<concise reviewed platform and toolchain description>",
  "bridgeTests": {"status": "passed", "protocolPassed": 12, "sourceLockPassed": 5, "sourceLockSkipped": 1, "jsonPassed": 1},
  "clientIntegration": {"passed": 8, "failed": 0},
  "upstreamCoreBaseline": {
    "total": 51,
    "passed": 50,
    "failed": 1,
    "skipped": 0,
    "failures": [{"test": "sed_core_tests", "detail": "Eight assertions in extrude_arrangement_registry_tests.cpp line 530 concerning geometry ordering failed; cause remains unestablished."}]
  }
}
```

That Windows failure must remain disclosed for the current qualified binary.
The general helper also accepts a future truthfully recorded 51/51 Windows
baseline. Windows source-lock results may be 5 passed/1 skipped or 6 passed/0
skipped. Linux requires 6 passed/0 skipped and a 51/51 full-core baseline. Both
require 12 protocol tests, the JSON test and the actual eight-case .NET client
integration. These inspection results never promote the optional component into
a qualified native editor replacement.

## Invocation and output

Run with a supported Python 3 interpreter; only standard-library modules are
used. Put output and temporary files outside Git checkouts, without junctions or
symlinks. The output parent must already exist.

```text
python -B tools/package_native_component.py --request ABSOLUTE_REQUEST_JSON --output ABSOLUTE_NEW_COMPONENT_ZIP
```

Successful output is a JSON receipt containing `status: "packaged"`, archive
length and SHA-256. Input/refusal errors return exit code 2. Archive members are
sorted, stored without compression, have a fixed 1980 ZIP timestamp, and use
0644 or 0755 mode as appropriate. Identical input bytes produce identical output
bytes. The path-free package manifest lists every payload member except itself.
The canonical public qualification may have a different byte hash from its
original formatting; the manifest separately records the original input hash.

The tool validates inputs before creating the output. It creates the final
filename exclusively and refuses replacement. A handled write error preserves
any partial output and reports failure; it does not unlink by path, which could
delete another actor's replacement. Publication is **not atomic**: any write
failure or power loss can leave an incomplete file, so a filename alone is not
success. Require successful command completion and verify the returned hash.
The successful receipt's size and hash are computed through the same exclusively
created read/write handle, without reopening or separately statting the path.
It identifies the file written by this invocation; a later path replacement is
outside that identity claim.
No archive extraction, runtime execution or DLL
search is performed. Read-only path checks and bounded input snapshots are not a
hostile-filesystem sandbox.

Bounds: request/receipt JSON and individual runtime notices 1 MiB; final source
archive input 64 MiB, expanded source payload 256 MiB and 20,000 entries; each runtime
64 MiB; corresponding upstream archive the pinned 8,407,040 bytes, expanded at
most 16 MiB/1,024 entries. Tar traversal, duplicate/case-colliding paths, reserved
Windows names, links, special files, sparse files and file/directory conflicts
are refused. Complete gzip decoding is bounded before parsing PAX metadata:
payload limit plus at most 16 MiB metadata allowance and 10 KiB tar padding.
Missing end markers and nonzero trailing tar payload are refused. The request
file is opened and its read is bounded to 1 MiB plus one byte even if it changes
after a size check. Large inputs are captured in memory within these bounds; avoid
running alongside heavy builds on a memory-constrained machine.

## Focused tests and staging limits

```text
python -B -m unittest discover -s tools/tests -p test_package_native_component.py -v
```

Set `SED_NATIVE_PACKAGE_TEST_TEMP`, `TEMP` and `TMP` to a small external scratch
directory. Tests use original synthetic bytes and tiny in-memory tar archives;
they do not copy real runtimes/source archives, invoke Git, build, or execute a
worker. Production pins have no command-line override; tests replace them only
inside scoped in-process mocks to exercise full package assembly with original
fixtures. Archive symlinks are tested as metadata, so those tests work on exFAT
without creating filesystem symlinks. Actual reparse/symlink races and a real
release package are outside this fixture suite's evidence.

Development verification: the first intentionally incomplete implementation run
timed out at its actual 30-second child deadline and is **not red-test evidence**.
The first implementation run passed all 17 tests in 3.157 seconds under an actual
90-second child deadline. Subsequent static review identified and corrected the
public runtime path field, tar metadata decoding bound, request-read race,
free-text absolute-path leakage, close-then-unlink cleanup race and output receipt
path-reopen race. The separate original build witness now preserves the Windows
nine-file evidence without synthesizing an eleven-file build record. After the
final independent static readback, the expanded 22-test suite passed with no
skips in 2.230 seconds under the 90-second child deadline. The separate terminal
receipt is `green-attempt-02.json`; the original timeout remains separately
preserved. No real add-on package has been produced by these fixture tests.
