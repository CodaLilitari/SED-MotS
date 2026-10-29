# SED MotS on Linux

The native Linux x64 distribution contains the Avalonia editor, the JSON CLI,
and the local stdio MCP server. The .NET runtime is included. Ordinary CLI and
MCP authoring works without a graphical session, Vulkan device, API key, or
game installation. The desktop viewport and explicit snapshot requests require
Vulkan. The renderer requires a Vulkan 1.2 device with at least **160 bytes of
push-constant storage** (`maxPushConstantsSize`). A game engine and game assets
are separate user-provided inputs.

## Start the portable editor

Verify the handoff checksums before extracting its binary archive:

```sh
sha256sum -c SHA256SUMS.txt
tar -xzf SED-MotS-VERSION-linux-x64.tar.gz
```

Replace `VERSION` with the delivered version. Keep the extracted directory
together; the three launchers locate their executables relative to that
directory. You can move it, including to a path containing spaces.

```sh
"/absolute/path/SED-MotS-VERSION-linux-x64/bin/sed-mots"
"/absolute/path/SED-MotS-VERSION-linux-x64/bin/sed-mots" "/path/to/level.jkl"
```

Open **File > New MotS Level** to create an original editable room. The editor
uses the same model, commands, undo and serialization as the CLI. Configure
game resources separately when your project needs them. In **Game > Test
Setup**, choose a native engine executable and a writable project directory.
Automatic native discovery recognizes `openjkdf2` and `openjkdf2-64`. For a
renamed native executable, choose the OpenJKDF2 profile and its full path; the
file needs execute permission. The shared launch planner still checks the game
resource directory and its `jk_.cd` marker. Test-project tokens retain the
engine's ASCII, no-space, 127-byte limit even though application and file paths
can contain spaces.
Existing explicit Wine command templates remain available for Windows engines.
Selecting an engine does not install one or supply its game data.

Settings live in the user's application-data directory, normally
`~/.config/SED/settings.json`. The default test project directory is
`~/Documents/SED`, or `~/SED` when no Documents directory is configured. The
installation directory need not be writable. Native Linux filenames are case
sensitive: `Room.jkl` and `room.jkl` can be different files.

## System dependencies

The package targets Linux x86-64 with glibc. It is not an Alpine/musl or ARM64
build. On Ubuntu 24.04, the following supplies the normal desktop libraries and
Mesa's software Vulkan driver:

```sh
sudo apt-get update
sudo apt-get install libicu74 libssl3t64 zlib1g \
  libx11-6 libxcb1 libxext6 libxrender1 libxrandr2 libxi6 libxcursor1 \
  libice6 libsm6 libxkbcommon0 libxkbcommon-x11-0 \
  libfontconfig1 libfreetype6 libvulkan1 mesa-vulkan-drivers
```

Use your distribution's corresponding packages on other glibc distributions.
A working vendor Vulkan driver is suitable on a normal desktop. Do not select
a software driver unless you want CPU rendering. For a headless environment,
find the installed lavapipe ICD and set its exact path for the command:

```sh
ls /usr/share/vulkan/icd.d/*lvp*.json
export VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/lvp_icd.x86_64.json
export VK_DRIVER_FILES="$VK_ICD_FILENAMES"
```

The filename varies by distribution. Prefer Mesa lavapipe for CPU rendering.
An ICD can expose a sufficiently new Vulkan API and still lack the required
160-byte push-constant limit; SwiftShader builds limited to 128 bytes cannot
render SED previews and are refused. No driver is bundled or selected by the
launchers. A missing or incompatible driver should affect a preview request, not non-rendering
authoring. Snapshot rendering is an editor preview, not proof of gameplay or
retail-renderer parity.

Create-only file promotion on Linux requires filesystem support for
`renameat2(RENAME_NOREPLACE)`. Unsupported filesystems fail the operation rather
than falling back to an overwrite-prone rename. Use a normal local Linux
filesystem for authoring and inspect any reported I/O error.

## Use the CLI from your project directory

The launchers preserve your current directory, every argument, stdin/stdout,
and the executable's exit code. Relative input and output paths therefore
refer to your project, not the application directory.

```sh
SED_PACKAGE="/absolute/path/SED-MotS-VERSION-linux-x64"
mkdir -p "$HOME/levels/example"
cd "$HOME/levels/example"
"$SED_PACKAGE/bin/sed-agent" help
"$SED_PACKAGE/bin/sed-agent" schema
"$SED_PACKAGE/bin/sed-agent" new "room with spaces.jkl" mots
"$SED_PACKAGE/bin/sed-agent" inspect "room with spaces.jkl"
"$SED_PACKAGE/bin/sed-agent" validate "room with spaces.jkl"
```

For edits, inspect the current source, put its `inputSha256` in a patch, run
`plan`, review the result, then pass the planned `outputSha256` as the final
argument to `apply`. Existing outputs are refused. A JSON request argument of
`-` reads a single JSON document from stdin. See [the agent protocol](AGENT_PROTOCOL.md)
for the exact commands, schemas, preservation limits and exit codes.

## Connect the MCP server

Create the workspace directory first. Configure your client's stdio server
using an absolute executable path and an argument array, for example:

```json
{
  "mcpServers": {
    "sed-mots": {
      "command": "/absolute/path/SED-MotS-VERSION-linux-x64/bin/sed-mcp",
      "args": ["--workspace", "/absolute/path/to/level-project"]
    }
  }
}
```

The server defaults to the matching `agent/sed-agent` beside its own `mcp`
directory. It needs no explicit worker override in a normal package. Stdout
contains only MCP protocol messages; diagnostics use stderr. Add explicit
`--resource-root` arguments only for resource directories the project needs.
See [MCP integration](MCP_INTEGRATION.md) for file policy and reviewed-write
contracts. Reading and editing do not start a preview worker; snapshots do.

## Optional per-user desktop integration

Python 3.10+ is needed for this optional installer and for the verification
tools. The editor and launchers themselves do not require Python.

```sh
"$SED_PACKAGE/tools/install-linux-desktop.sh" --package "$SED_PACKAGE"
```

This creates `sed-mots.desktop` under
`${XDG_DATA_HOME:-$HOME/.local/share}/applications` and links `sed-mots`,
`sed-agent`, and `sed-mcp` in `~/.local/bin`. No root access is required. Add
`~/.local/bin` to your shell's PATH if it is not already there. The installer
refuses existing entries or commands instead of replacing them. Relocating the
package after installation requires reinstalling those links.

To remove this optional integration, remove only the entry and three links
created by the installer. Keep the portable package and your project files.
When changing versions, remove the old integration explicitly, then install
the new version's entry.

**The automatic updater is Windows-only.** Install Linux releases into new
versioned directories and retain the previous directory until the new build
works for your project. Linux packaging does not modify the Windows update
feed or install a background updater.

## Build and test from source

Use the .NET SDK pinned by [`global.json`](../global.json), Python 3.10+, Git,
and the native libraries above. The scripts use `dotnet` on PATH by default;
`--dotnet` accepts an explicit SDK executable, including a path with spaces.

```sh
tools/build-linux.sh --dotnet /absolute/path/to/dotnet \
  --output build/linux-check
```

This builds the editor, agent, MCP server, two test projects and three probes
for `linux-x64`. It runs the Core and MCP suites, Python tooling tests, and the
headless UI input probe with Vulkan disabled. Builds are framework-dependent to avoid copying
runtime assets for every operating system into every output. Compiler servers
and parallel project builds are disabled to keep resource use predictable.

Add `--graphics` to run the Vulkan smoke test and the actual editor's offscreen
AppShot edit/undo/serialization probe. With the appropriate software driver
selected, these require neither DISPLAY nor a GPU. An ordinary X11 startup can
also be checked under `xvfb-run`; install `xvfb` and `xauth` for that environment.

The first build restores dependencies. After a successful restore for the same
runtime, `--no-restore` reuses it. `NUGET_PACKAGES` and `DOTNET_CLI_HOME` are
respected, so a portable SDK and shared package cache can remain outside the
repository. Each run requires a new output directory and leaves logs plus a
`build-receipt.json`, including failures.

## Create and verify a portable handoff

Commit the reviewed source first. Packaging requires a clean Git worktree and
checks that its commit remains unchanged. It compiles from a Git archive of
that commit, excluding ignored files and private scratch inputs even when a
project would otherwise include them through a source glob.

```sh
tools/package-linux.sh --dotnet /absolute/path/to/dotnet \
  --version 0.4.0-mots.17-linux.1 \
  --graphics --full-workflows \
  --output build/packages/linux-handoff
```

Choose the actual release version. By default, packaging runs its own build
and test gate. To reuse the build above, add
`--build-receipt build/linux-check/build-receipt.json`; it must confirm passing
Linux Release tests for the same clean commit and SDK. Publishing still
restores the new source snapshot's build metadata and reuses the NuGet cache.
Allow several GiB for the SDK, restore cache and compilation; the tools do not
delete another build or cache to make room.

The package layout is:

| Path | Purpose |
|---|---|
| `Sed.App` | Native self-contained editor apphost |
| `agent/sed-agent` | Native self-contained JSON CLI |
| `mcp/sed-mcp` | Native self-contained stdio MCP server |
| `bin/sed-mots`, `bin/sed-agent`, `bin/sed-mcp` | Relocatable shell launchers |
| `data`, `docs`, `.agents`, `licenses`, `tools` | Tracked support files and acceptance tools |
| `licenses/linux` | Resolved Linux dependency metadata, licenses and runtime notices |
| `PROVENANCE.json`, `SHA256SUMS.txt` | Commit/dependency identity and exact payload hashes |

Byte-identical runtime files are hardlinked within the package, and the tar
archive preserves those links. Archive entry times and ownership are
normalized. Exact image output can still differ across graphics drivers;
this is not a promise of identical rendering on all machines.

The handoff also includes a matching `SED-MotS-source.tar.gz`, standalone
`SED-MotS-history.bundle`, outer artifact checksums, and build/package/verification
receipts. It preserves source ancestry and attribution. No game engine,
retail game data, community levels or credentials are added.

Verification runs a hardlinked relocated copy in a path containing spaces,
with a different caller directory. It creates an original room, inspects it,
plans and applies a reviewed edit, validates/reopens it, and checks hash and
overwrite refusals. Ordinary CLI/MCP checks run with DISPLAY removed and a
deliberately absent Vulkan ICD. With `--graphics`, a real MCP snapshot verifies
the default sibling worker. `--full-workflows` adds the existing broad MCP and
editing-workbench scripts. None of these runs a game.

To repeat verification on an extracted package:

```sh
"$SED_PACKAGE/tools/verify-linux.sh" --package "$SED_PACKAGE" \
  --graphics --full-workflows --output "$HOME/sed-linux-check"
```

Omit `--graphics` on a machine without Vulkan; the receipt then explicitly says
the preview worker was not exercised. The CI workflow uses the same build,
package and verification commands with Mesa software rendering, and retains
the logs and receipts for inspection.
