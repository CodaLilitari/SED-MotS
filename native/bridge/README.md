# Stateless native core inspection bridge

CPP-BRIDGE-01, protocol 1, worker version 0.1.0.

This worker adds direct use of the pinned upstream C++ core for observations.
The .NET editor remains the document authority. The worker owns one temporary
decoded object graph, returns a bounded observation, and exits. It has no edit,
undo, save, recovery-file, resource-root or output-path command.

Read [protocol-v1.md](protocol-v1.md) for the fixed arguments, fields, limits,
rejection semantics and host responsibilities.

## Source and build provenance

The upstream input is smlu/SED commit
b82255998fb733c8e8c87104eb7c17f27b48376f (develop snapshot, 2026-10-11).
The bridge requires an existing **absolute** SED_CPP_UPSTREAM_DIR; it never
fetches source or executes an installer. This directory is external to the
maintained checkout. Do not place build trees or junctions inside a checkout.

upstream.lock.json records the exact SHA-256 and byte length of 213 files:
the root CMake file, LICENSE, THIRD_PARTY_NOTICES.md and all of src/core,
including original upstream tests/fixtures. Its exact-file SHA-256 is the
protocol's sourceManifestSha256. This hash identifies the upstream input
manifest, not the bridge source or executable. The host must separately pin the
actual executable SHA-256.

verify_upstream.py checks coverage, file bytes, case collisions and path
safety. Missing, changed or extra files inside the covered core tree are
refused, as are symlinks/reparse paths. The check runs at configure time and as
a dependency of sed_core on every build, including incremental builds.
verified-upstream.json in the external build directory records the verified
commit, lock hash and file count. No source is rewritten. The lock is maintained
reviewed input; it is not a signature or proof against a malicious build host.

The build lock strategy is:

1. Retain the reviewed upstream lock and exact external source snapshot.
2. Configure/build outside all checkouts; retain the verification receipt,
   CMakeCache, toolchain identity, commands and terminal logs.
3. Bind the resulting executable hash to the bridge source commit (or an
   explicit dirty-source file/hash manifest) and the upstream lock hash.
4. Package the matching bridge and exact upstream corresponding source and
   notices together. A source ZIP containing only selected review excerpts is
   insufficient. Root packaging owns this integration.
5. At runtime the host checks both its expected executable SHA-256 and this
   lock identity; version strings alone are insufficient.

Use an immutable/trusted snapshot while compiling. The pre-build verification
is not an atomic filesystem snapshot and cannot defeat concurrent hostile
source replacement. No claim of bit-for-bit reproducible binaries is made.

The parent release task has produced an external corresponding-core archive,
SED-upstream-core-b822559.tar, 8,407,040 bytes, SHA-256
ef4ddb3ef0cee00fd7ddf8da17ff7a6fb058f11c5337b6e3ede366b8b66723f2.
Its separate upstream-core-archive-verification.json receipt records matching
all 213 regular members to this lock. Final packaging still must include this
archive together with the matching wrapper/host source and notices. The archive
hash is kept outside upstream.lock.json so the worker's source identity stays
stable. The local attributes file preserves the lock's exact checkout bytes.

## Build and focused checks

Requirements: CMake 3.21+, Python 3.10+, and a C++20 toolchain. This target uses
sed_core with SED_BUILD_APP=OFF and SED_BUILD_FUZZERS=OFF. No Qt dependency is
needed for these targets. There are no added downloaded dependencies.

Example Linux commands, with absolute paths chosen by the caller:

    cmake -S /path/to/checkout/native/bridge \
      -B /external/build/native-bridge -G "Unix Makefiles" \
      -DCMAKE_BUILD_TYPE=Release \
      -DSED_CPP_UPSTREAM_DIR=/external/source/upstream-cpp-b822559
    cmake --build /external/build/native-bridge \
      --target sed_cpp_bridge bridge_json_tests -j1
    ctest --test-dir /external/build/native-bridge \
      -R "^bridge_" --output-on-failure -j1

Re-run configure after changing the target set before a multi-target Make
invocation. The root upstream project is added EXCLUDE_FROM_ALL; explicitly
build the targets above. The bridge CTest subset is separate from the
standalone upstream suite. Its nested upstream CTest registrations assume the
upstream root is CMAKE_SOURCE_DIR and are not qualified under this wrapper.

Apply real child-process deadlines when running builds/tests. Tool output
yield timing is not a deadline. These commands themselves do not install or
change system packages.

Source-verifier tests may also run without a C++ compiler:

    python -B tests/test_upstream_lock.py \
      --verifier /path/to/checkout/native/bridge/verify_upstream.py \
      --scratch /external/build/test-temp -v

The initial Linux qualification used GCC 15.2, CMake 4.2.3, Release/C++20 and
serial GNU Make. The worker protocol, source verifier and JSON checks passed.
The upstream geometry.hpp exposes anonymous structs and triggers three
-Wpedantic diagnostics when included by the bridge; upstream was not patched
or warning-suppressed. This is not Windows compilation or runtime evidence.

## What is actually inspected

inspect-native-sed invokes sed::assets::loadSedFileBytes on the captured
stdin buffer. It reports decoded collection sizes, UV space, opaque optional
section type/size metadata, and the exact strict-loader repair indicators.
It never calls permissive salvage or native save. Optional EDIT/HIST payload
bytes are not returned or interpreted as Qt editor state.

inspect-jkl invokes sed::assets::loadFromJKL(std::istream&, options) with
the explicit project hint and resources == nullptr. JKL and NDY inputs are
observed through the upstream loader. The framing/admission check uses its
own public text helpers and does not duplicate geometry decoding. VERSION 1
and VERSION 3 are admitted; VERSION 2 is intentionally outside protocol 1.
Native collection counts are counts after decoding, not a lossless raw parse
inventory. The parser can accept incomplete level sections. Success therefore
means this decoder admitted the input, not that it is a complete playable level.

Both operations return resourceValidation.complete=false. No COG schema
hydration, MAT-backed UV normalization, ARCH/model closure, rendering, gameplay,
multiplayer or retail compatibility has been established. No resource locator
is constructed, and no game archive or third-party level is opened.

## Resource limits and containment

The worker bounds input to 16 MiB, complete stdout to 64 KiB, diagnostic
count/message length, and optional/integrity output arrays. It builds the
complete JSON before writing stdout, correctly escapes control/invalid UTF-8
bytes, and exposes truncation rather than silently dropping status.

The native codec has pre-allocation object/item and decompression budgets
documented in protocol-v1.md and the pinned sed.hpp. It does not expose a
caller-selected lower budget. Small hostile declared counts are checked by
the unchanged native codec.

The JKL loader checks declared geometry counts against real rows/tokens, but
can copy the same global surface into repeated sector spans. The bridge
therefore admits only bounded physical lines, line bytes, token totals,
summed surface references and conservative expansion products. The string
product also bounds repeated material/colormap name copies. Valid large or
unusual files may be refused with input_limit. The host must distinguish an
admission rejection from invalid document data.

These limits are not a measured peak-memory guarantee, OS resource limit,
sandbox or proof of parser safety. Allocator overhead, spare capacity, parser
CPU complexity and platform behavior remain relevant. The host enforces a
deadline, bounded concurrent pipes and confirmed worker exit after attempted
owned-tree termination. This implementation spawns no child processes.

## Focused evidence

Tests use original tiny bridge-authored inputs and upstream's published
synthetic src/core/tests/assets/fixtures/sed_vectors goldens. They cover:

- Fixed argument/version/project-kind/request-id contract and identity fields.
- JK, MotS and IJIM text/native triangle observations.
- Resource-incomplete status and no cwd/file mutation.
- Observable repair of a damaged optional history section without emitting
  its contents, and rejection beyond the strict codec repair budget.
- Empty/random/NUL text, invalid version, bad native data, kind mismatch.
- Declared-count truncation, raw input/line/token limits and repeated geometry
  or string expansion refusal before decoding.
- Changed/missing/extra/linked source inputs and unsafe manifest paths.
- JSON controls, quotes, valid and malformed UTF-8, and exact output bounds.

This is focused contract evidence, not exhaustive fuzzing or whole-app parity.

## Attribution and redistribution

The linked core comes from [smlu/SED](https://github.com/smlu/SED) at the exact
commit above. Its explicit license is GPL-3.0; retain its LICENSE and
THIRD_PARTY_NOTICES.md and provide the matching corresponding source/build
inputs when distributing the combined worker. No upstream source or game
assets are vendored in this directory. The source lock and fixture references
do not replace the corresponding source. The parent release process must
resolve packaging and final source provenance before shipping a binary.
