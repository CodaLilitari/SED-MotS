# Pinned Mac graphics dependency

`dependency-lock.json` records the official public-API MoltenVK 1.4.2 macOS
release, source commit `db66022459ffb663aa2b50f6b018bc2e124f5edf`, its exact
archive and selected library hashes, and the upstream notice files. The release
archive SHA-256 matched the digest returned by the upstream GitHub release API.
The selected `macOS/libMoltenVK.dylib` contains both arm64 and x86_64 Mach-O
slices. This static observation does not establish that either architecture has
executed successfully in SED.

Run from the checkout with an external, new output directory:

```sh
python3 tools/acquire_macos_dependencies.py --output /absolute/external/mac-dependencies
```

The command prints the resulting `moltenvk-manifest.json` path. A previously
downloaded archive can be supplied with `--archive /absolute/MoltenVK-macos.tar`;
the same fixed size and hash are still required. There is no command-line version
or hash override. The helper does not execute downloaded programs, extract the
whole archive, install Homebrew, or alter the Vulkan SDK. It copies only the
allowlisted regular library/license members and downloads the exact notices.
Each acquisition has a finite deadline and preserves a failed attempt without
emitting a success manifest. Use a new output directory for another attempt.

The archive's top-level Apache license is supplemented with notices from the
five dependencies identified by the pinned
[external dependency documentation](https://github.com/KhronosGroup/MoltenVK/blob/db66022459ffb663aa2b50f6b018bc2e124f5edf/ExternalRevisions/README.md):

- cereal, including its bundled external-code license files;
- SPIRV-Cross, including its Khronos free-use license;
- SPIRV-Headers;
- SPIRV-Tools;
- Vulkan-Headers, retaining its copyright and Apache/MIT licensing statement.

Each dependency revision comes from MoltenVK's corresponding
`ExternalRevisions/*_repo_revision` at that exact commit. The lock retains every
selected notice's source URL, bytes, hash and package destination. The Vulkan
Headers statement permits Apache-2.0 for its runtime headers; the complete Apache
text is included in the MoltenVK/SPIRV notice set. The separate SPIRV-Tools VS Code
extension is not part of the selected runtime library. Volk and Vulkan-Tools are
identified upstream as demo dependencies, and the demo binaries are not shipped.
The cereal notice set is deliberately inclusive of its bundled external sources;
it is not a claim that every such source is linked into this dylib.

The package assembler must revalidate the manifest, verify the requested Mach-O
architecture and native dependency closure, copy all notices, and record signing
and actual execution separately. Acquisition success alone is not Mac release
qualification, Developer ID signing, notarization or a legal opinion.
