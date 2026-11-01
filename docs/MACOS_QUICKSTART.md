# SED-MotS for macOS

The Mac distribution contains the current .NET/Avalonia editor, command-line
authoring agent and MCP server. Choose `osx-arm64` for Apple Silicon or `osx-x64`
for Intel. The initial supported system floor is macOS 15. The .NET runtime and
MoltenVK are included; running the package does not require a .NET SDK or Homebrew.

## Open the editor

Extract the ZIP with Archive Utility. Move `SED-MotS.app` to a location you own,
such as `~/Applications`, and open it. The app includes its own libraries,
templates, help and notices, so it can be moved independently of the rest of the
distribution. Use the editor's Open command to select a level. Finder document
associations are not installed by this release.

These initial builds use ad-hoc code signatures. They are **not Developer ID
signed or notarized**. macOS may require explicit approval in System Settings →
Privacy & Security after an attempted launch. Check the downloaded archive's
SHA-256 against the release's `SHA256SUMS.txt` and use Apple's documented
per-app approval flow; do not disable Gatekeeper system-wide.

No commercial game assets are included. Point the editor at your own legally
obtained Jedi Knight / Mysteries of the Sith resources as needed. Work on a copy
of an old or damaged level and keep its original bytes for comparison.

## Command-line and MCP use

Keep the whole extracted folder together to use its `bin` launchers. Paths with
spaces are supported, and relative document paths use your current directory.

```sh
"/path/to/SED-MotS-version-osx-arm64/bin/sed-agent" --help
"/path/to/SED-MotS-version-osx-arm64/bin/sed-agent" new "My room.jkl" mots
"/path/to/SED-MotS-version-osx-arm64/bin/sed-mcp" --help
```

For an MCP client, use the absolute path to `bin/sed-mcp` as its command and
supply `--workspace` followed by the absolute project directory. The adjacent
agent supplies authoring and preview operations. See `docs/MCP_INTEGRATION.md` for the server
contract and `docs/AGENT_PROTOCOL.md` for authoring commands in the
distribution. PowerShell is needed only to run the included verification scripts,
not to use the editor, agent or MCP server.

## Verify the package

The release includes `PROVENANCE.json`, `PAYLOAD.json`, complete third-party
notices, an exact committed source archive and a Git history bundle. `PAYLOAD.json`
lists the installed files, their hashes and executable modes. A release's native
verification receipts distinguish authoring, Metal/MoltenVK rendering, Cocoa
window startup, APFS safety and signing. An assembly receipt alone does not prove
that graphics or the native editor were exercised.

The Windows updater is not used on macOS; download a later Mac release to update.
The optional C++ inspection worker is not included in this Mac package. The C++
rewrite is a separate experiment and this package does not claim rewrite parity.

If reporting a problem, include the release version, architecture, macOS version
and relevant receipt or error log. Do not attach your game installation or a
private level unless you intend to share it.
