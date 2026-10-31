# Red's routes to SED and the level research

SED MotS (the C#/.NET line) has an Avalonia editor for people, a headless agent (`sed-agent`) and an MCP host (`sed-mcp`). Andrei's Windows distributions ship them as `.exe` files.

**On Linux, use Coda's native distribution.** On 29 October 2026 Coda built and verified it on two branches of the public repository: `feature/linux-native` and `feature/reviewed-packaging` (release `0.4.0-mots.17-linux.2`). It provides:

- self-contained editor, agent and MCP host, with relocatable launchers in `bin/`;
- reviewed dependency packaging (`package-plan` and `package-apply`), which brings the MCP host to thirteen tools;
- native OpenJKDF2 launching from the editor, and a desktop installer.

It supersedes Red's own earlier `linux-port` branch (27–28 October). One piece of Red's port isn't in Coda's: a Linux engine profile for SED's capture runner. It sits on the **`claude` branch**, on top of Coda's `feature/reviewed-packaging`, together with this skill under `.claude/skills/`. See `docs/ENGINE_CAPTURE_LINUX.md` there.

These are Red's routes, best first.

## 1. SED on Linux, in the cloud workspace (default)

Use this for anything that needs snapshots, packaging, and bigger edits.

1. **Run** `bash scripts/sed_cloud_setup.sh <scratch>/sed`. By default it clones the `claude` branch from `gitlab.com/codalilitari/SED-MotS`; pass another URL or bundle and ref as the second and third arguments.

   The script installs what's missing and then runs the repository's own `tools/package-linux.sh --graphics`:
   - **Installs:** the SDK pinned in `global.json`, the desktop libraries from `docs/LINUX_QUICKSTART.md`, Mesa's software Vulkan driver (lavapipe), Xvfb and xauth. It selects lavapipe for the run.
   - **Packaging does:** a clean source snapshot; the Core, MCP, Linux-tooling and Python suites; the headless UI probe, the Vulkan smoke test, SceneProbe and AppShot; a self-contained publish; and relocated CLI/MCP verification, including a real snapshot and the dependency-package workflow.
   - **Finally** it extracts the package to `<scratch>/sed/sed/` and prints the launchers.

   **The verified run** (31 October 2026, branch `claude` at `61f9867`): Core 1,801, MCP 293, Linux tooling 23 and Python 127 (two Windows-only skips), all passing; build and package receipts passed. It took about 5 minutes on two CPUs with the SDK and package cache already present; a fresh workspace downloads those first. The archive is about 51 MB.
2. **Stage the levels** into a job folder: a JKL, or the GOB/GOO plus exact entry names, and the resource archives a lit snapshot needs.
3. **Work with the CLI** exactly as `docs/AGENT_PROTOCOL.md` describes, through `bin/sed-agent`.
   - The launchers keep the caller's working directory, so relative paths refer to the job folder.
   - For snapshots, export `VK_ICD_FILENAMES` and `VK_DRIVER_FILES` to the lavapipe ICD the script printed. The renderer needs Vulkan 1.2 with at least 160 bytes of push constants: lavapipe qualifies, SwiftShader doesn't. The result's `deviceName` names the renderer.
   - Outputs are created only if absent (`renameat2` without replace). A filesystem that can't do that fails loudly instead of overwriting.
4. **Commit new outputs back** beside the source under new names (`device_commit_files`), with their receipts. Never overwrite a level.

## 2. SED on Linux, in the device shell (beside the levels)

Use this for reading, querying, planning and applying text edits without moving the files.

- **The package.** Red keeps a current self-contained package in Andrei's `SED-MotS-Linux` folder, split into parts under 30 MB because the file bridge is limited to that size per file.
- **Setup, once per session:**
  1. Reassemble the parts into the device shell's scratch home, outside the connected folders.
  2. Check the archive against `SHA256SUMS.txt`.
  3. Extract it into `$HOME/.red-tools/sed/`.

  It needs no .NET installation.
- **Run** `bin/sed-agent` from the folder that holds the level, so relative paths stay there.
- **For a picture,** stage the reviewed output to the cloud and snapshot it there. The device shell has no Vulkan loader.
- **Outputs** are new files in the connected folder, never replacements.

## 3. The MCP host in a client

`bin/sed-mcp --workspace PROJECT [--resource-root READ_ONLY_DIR …]` works on Linux. On Windows the equivalent is `mcp\sed-mcp.exe`.

- **The thirteen tools:** `sed_discover`, `sed_query`, `sed_select`, `sed_diagnose`, `sed_plan`, `sed_apply`, `sed_new`, `sed_convert`, `sed_archive`, `sed_snapshot`, `sed_outcome`, `sed_package_plan` and `sed_package_apply`.
- **On Windows,** Andrei can register `sed-mcp.exe` in the Claude desktop app's MCP configuration. Red doesn't change that configuration herself.
- **Red doesn't need MCP herself:** the CLI exposes the same domain operations.
- **Commit discipline:** plan, review the effects, then commit with the plan's `outputSha256` as `expectedOutputSha256`, a new output path and a fresh GUID `operationId` (plus `resourceRevision` when resources were consumed). Package apply also needs the plan's `expectedManifestSha256`.
- **Timeouts:** a timeout never proves that nothing committed, so ask `sed_outcome` first. An identical replay returns the stored outcome; a changed request under the same ID is a conflict. Previews aren't journaled: if a reply is lost, look for the intended PNG first.
- **Bounds:** one domain operation at a time, 128 patch operations, 16 MiB level inputs, 128 MiB package output.

## 4. Reviewed dependency packages

To hand a level to someone, or to test it in an engine as one archive, package it instead of zipping files by hand. `docs/RESOURCE_PACKAGING.md` has the contract.

- **Input:** a saved JKL plus explicit, hashed resource archives. Convert native projects to JKL first.
- **Plan:** `sed-agent package-plan <level.jkl> <package.json>`. It reviews the static dependencies (3DO materials, PUP keyframes, sounds, sprite and particle materials, typed COG declarations), the included entries, stock omissions, and anything blocked or unverified, without writing.
- **Apply:** `sed-agent package-apply <level.jkl> <package.json> <new.gob|goo> <outputSha256> <manifestSha256>`. Both review hashes are required: a changed stock role or provider order can change the meaning while the bytes stay the same.
- **Limits:** unknown runtime references need acknowledgement; missing or ambiguous resources and malformed declarations always block. `runtimeClosureVerified` stays false, so COG code paths, AI variants, engine fallbacks and gameplay need their own evidence.

## 5. The editor, for people

The editor is Andrei's (and anyone's) tool, on Windows or on a Linux desktop. When he asks how to run it on Linux, point him to the package's guide (`docs/LINUX_QUICKSTART.md`):

- **Starting it:** verify `SHA256SUMS.txt`, extract the archive, and run `bin/sed-mots`, optionally with a level file. Nothing needs installing beyond the system libraries the guide lists.
- **Menus:** `tools/install-linux-desktop.sh --package <package>` adds a menu entry and command links for the current user. It never overwrites anything.
- **Graphics:** Vulkan 1.2 with at least 160 bytes of push constants. Without a suitable driver the editor still opens and edits, and its 3D view reports the problem.
- **Testing in a game:** Save GOB and Test finds a native `openjkdf2` or `openjkdf2-64` in the game folder, or takes the executable chosen in Test Setup. Explicit Wine templates still run Windows engines.
- **Updates:** the automatic updater is Windows-only. Linux versions go side by side in their own folders.

**Red works through the CLI, not the GUI.** She drove the earlier Linux editor under Xvfb only to verify a port. Driving a GUI that way is slow and brittle; use it only for a GUI-only check Andrei asks for, with screenshots as receipts.

**Tested so far:** Xvfb and Mesa's software renderers, in Coda's and Red's workspaces. Not yet tested: a physical GPU desktop, native Wayland, HiDPI, or anything with Andrei's game files. Report it that way.

## 6. Windows-only work

- **The portable updater and the PowerShell workflow scripts** stay on Windows. So does anything in Andrei's own editor session.
  - Use computer use only when Andrei asks, and never while he's working in the editor.
  - Otherwise hand the work to Coda with a `red-to-coda-<date>-<topic>.md` file: the objective, exact input hashes, the intended operations (as discovered schemas, not guesses), the acceptance cases, and what must be preserved.
- **Engine captures and play sessions** run in Red's cloud workspace through the OpenMotS route ([openmots-linux.md](openmots-linux.md)), once Andrei provides his game files. **Gameplay judgments** (feel, timing, sound, multiplayer) stay with Andrei's and Coda's playtests.

## Never hand-edit levels

Don't change geometry, UVs, lights or bindings by text substitution, even when SED isn't reachable. SED's operations preserve unselected data, independent white/RGB lighting, references and revisions in ways text edits don't.

Without SED, Red can still:

- read JKL text byte-preservingly;
- check scripts and models with the jk-cog-workshop and jk-model-workshop skills;
- prepare patch JSON ready to plan.

## The private research corpus

`scripts/corpus.py` (standard library, SQLite FTS5) indexes Andrei's private level-research captures. The index home is private: ask Andrei where it lives (Coda's local configuration records it). In the device shell:

```text
python3 -B $HOME/.red-tools/jk-level/corpus.py --home "<corpusHome>" stats
python3 -B $HOME/.red-tools/jk-level/corpus.py --home "<corpusHome>" search "vertex lighting" --limit 10
python3 -B $HOME/.red-tools/jk-level/corpus.py --home "<corpusHome>" show MESSAGE_ID --context 3 --raw
python3 -B $HOME/.red-tools/jk-level/corpus.py --home "<corpusHome>" leads --id qt-sed-rewrite
```

- **Reading** (`stats`, `search`, `show`, `leads`) changes nothing.
- **Writing** (`build`, `refresh-source`, `evidence`, `update-lead`, `export-findings`) happens only when Andrei asks for that change.
- **Privacy:** raw messages, posts, attachments and local paths stay private. Quote them only as far as the task needs, and never put them in a shareable packet or anything sent outside.
- **Search hits establish acquisition only** ([private-corpus.md](private-corpus.md)).

## Testing in the target game

Structural validation, snapshots, engine capture, scripted play and real gameplay each establish something different ([compatibility-and-testing.md](compatibility-and-testing.md)). Report retail separately from OpenJKDF2/OpenMotS evidence, and name the renderer for engine images (llvmpipe in Red's workspace). The engine routes, their receipts and their limits are in [openmots-linux.md](openmots-linux.md).
