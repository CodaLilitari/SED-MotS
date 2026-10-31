# Notice: JK / MotS Level Workshop, Red's fork

Forked from Coda's JK / MotS Level Workflow 1.0.0 (the copy in Andrei's Codex install; the files kept verbatim are identical to the public repository's `.agents/skills/jk-mots-level-workflow`). Andrei directed it; Coda built it. `LICENSE` is upstream's (MIT). Upstream's notice follows, verbatim.

## What changed in the fork

- **Verbatim:** `references/compatibility-and-testing.md`, `findings.json`, `headless-design.md`, `private-corpus.md`, `research-index.md`, and `scripts/corpus.py`.
- **Adjusted:** `scripts/test_corpus.py` is upstream's test with its import path changed to this layout.
- **New, by Red (MIT):** `SKILL.md` (rewritten for Red's routes), `references/red-routes.md`, `references/openmots-linux.md`, `scripts/sed_cloud_setup.sh`, `scripts/openmots_cloud_setup.sh`, `scripts/openmots_capture.py` and its tests.
- **SED MotS on Linux** is Coda's native Linux distribution (branches `feature/linux-native` and `feature/reviewed-packaging`, 29 October 2026). Red's branch `claude` adds one commit on top: the capture runner's Linux engine profile, ported from Red's earlier `linux-port` branch (27–28 October), which Coda's port supersedes. None of SED is bundled here. SED MotS keeps its own licence (GPL-3.0).
- **Coda's agent workspace** for OpenJKDF2-MotS-Fixes (branch `agent/linux-workspace-20261030`) is built from its public repository by `openmots_cloud_setup.sh --agent`. No engine source or binary is bundled.
- **OpenJKDF2-MotS-Fixes** is built from its public repository by `openmots_cloud_setup.sh`; no engine source or binary is bundled. The engine keeps its own licence. The two configuration files `openmots_capture.py` writes are byte-identical to Coda's ENGINE-01 inputs, and it records her bootstrap hashes only to compare copies.
- **Not included:**
  - `probe_tools.py`, which runs the Windows `sed-agent.exe`;
  - `research_updates.py` and `build_bundle.py` (release and update tooling), and `references/updates.md`;
  - `PACKAGE-MANIFEST.json`, `research-update-state.json` and `agents/openai.yaml`;
  - the private `local-config.json`, which never leaves Andrei's computer.
- **Read only:** the source files were copied from Andrei's computer; Coda's install and checkout were not modified. SED MotS itself is a separate program under its own license and is not bundled.

---

# Provenance and scope

The independent JK / MotS Level Workflow helpers and original reference prose are MIT licensed under the included LICENSE. The SED editor itself retains its own project license.

`scripts/research_updates.py` and the baseline update tests are adapted from the MIT-licensed [JK / MotS Model Workflow](https://gitlab.com/codalilitari/jk-mots-model-workflow), inspected at commit `27c55eabdc5ef3293dba9f558dd2b4543517999d`. The original updater file SHA256 was `6c52c16f43f88b95c69ebf96aa24853b57459042b057c247952f3ad59e30f007`. Changes select the level-workflow publication prefixes, add a bounded GitLab availability fallback and retain reference-only update/conflict protections. The original copyright/permission terms are included in LICENSE.

Other scripts are original workflow tooling. The research references summarize independently assessed behavior and clearly distinguish source inspection, author reports, proposed designs and runtime verification. Linked upstream projects keep their own licenses. This archive does not include their source files, raw Discord/forum posts, private receipts, game files, historical scans or community level downloads.
