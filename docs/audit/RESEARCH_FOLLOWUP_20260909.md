# Historical editor and original Massassi follow-up

The resumed background research closed the seven remaining installer-extraction gaps, recovered two genuine native JED projects, and exposed one signed-zero preservation defect in JKL export. The corrected corpus audit now passes **122/122 documents** across all **50 original Massassi packages**. These packages remain separate from WBW conversions.

## Original-package coverage

The seven old Clickteam installers were decoded passively into ten GOB/GOO archives totaling **169,319,159 bytes**, containing **28 additional JKLs**. The decoder is source-informed by pinned public ScummVM code and is not described as clean-room. It and its notices remain in the external research directory. **34 decoder/driver tests** and **49 subsequent provenance/structural checks** passed. No historical installer, uninstaller, editor, launcher, or game was executed. See [the recovery report](MASSASSI_WRAPPER_RECOVERY_FOLLOWUP_20260909.md).

All original ZIPs were rechecked against their acquisition sizes and SHA256 hashes. The existing verifier was rebuilt with .NET 10.0.401, Release, zero warnings/errors, then run against the original 50-package manifest and a separate combined manifest of five prior TODOA and ten new Clickteam payloads.

| Category | Packages | JKL documents | Documents with geometry | Parsed | Unchanged text | Regenerated model |
|---|---:|---:|---:|---:|---:|---:|
| JK single player | 13 | 53 | 50 | 53 | 53 | 53 |
| JK multiplayer | 13 | 28 | 28 | 28 | 28 | 28 |
| MotS single player | 12 | 28 | 25 | 28 | 28 | 28 |
| MotS multiplayer | 12 | 13 | 13 | 13 | 13 | 13 |
| Total | 50 | 122 | 116 | 122 | 122 | 122 |

There are zero package, container, parse or round-trip failures, zero bounded skips, and zero packages without a reached JKL. A document count includes resource/static JKLs and multiple entries within a package; it is not a count of independently playtested maps. The unchanged-text comparison normalizes line endings. Regeneration compares the explicit editor model and material table after reparsing. These checks do not establish complete resource resolution, unknown-field preservation, native editor parity, rendering, scripts or gameplay.

The final per-document evidence is [MASSASSI_COMMUNITY_FOLLOWUP_20260909.json](MASSASSI_COMMUNITY_FOLLOWUP_20260909.json). The [build receipt](MASSASSI_COMMUNITY_FOLLOWUP_BUILD_RECEIPT_20260909.json) identifies exact input, implementation, assembly and evidence hashes. The earlier 94-document report is retained as a separate historical checkpoint.

## Fix found by the recovered levels

Liberating Cloud City's `cloudcity.goo!/jkl/bridge.jkl` contains both positive-zero and negative-zero UV values, including a source token `-.00`. The exporter's numeric-equality UV dictionary merged those entries. The existing invariant round-trip number formatter and parser already supported the values; changing numeric formatting would not address the cause.

`GeoResourceWriter` now pools UV pairs by the exact two double bit patterns. Equal bit patterns still share a texture vertex, while different zero signs remain distinct. A direct comparison of this level found nine changed UV components before the fix and **zero changes across all 20,110 components** afterward. This is a fidelity correction; the audit did not establish visible texture distortion or a gameplay fault from the previous behavior.

The new synthetic regressions exercise both games, both coordinate components, both insertion orders, fresh-document regeneration and normal geometry edits. They also verify that identical signed-zero coordinates still deduplicate and that small, large and adjacent finite values retain their precision. Eight signed-zero cases failed before the change; the four controls already passed. Afterward all **12 new cases plus 49 related existing tests passed**. No authored level body was copied into the source or tests.

## Genuine native projects

Two live Massassi tutorial attachments were recovered: Brian Lozier's **JED 0.91** polygon prefab and Dino Delucchi's **JED 0.85 MotS** building. The already packaged development CLI imported, converted, reinspected and validated both. All **56 exposed sector detail records** matched across conversion. The examples also exercise reported limits: native layer visibility is unrepresented, and the MotS light has an independent white intensity that the current model cannot retain. No new native-parser failure was found. See [the native-project report](NATIVE_PROJECT_FOLLOWUP_20260909.md) for original links, hashes and the narrow scope of this two-project result. The authored files and converted derivatives remain external.

## Source archaeology and pending downloads

Attributed developer discussion substantiates the **February 18, 2015 Phantom Menace BAP/Obi.exe release** and identifies `netobi.exe` as a post-shipping LAN experiment. This is not a verified Xbox Obi-Wan editor release or original Sith SDK. No LEIA/ModelX executable, original JKDF2/MotS source, or public Sith SDK was recovered. A genuine Jedi Academy SDK was acquired and inspected, but it is Raven's later idTech3 tooling with its own limited-use agreement. The modern Xbox ObiWanImporter acquisition contains a compiled plugin and a documentation-only repository. See [the cited archaeology supplement](SITH_ARCHAEOLOGY_FOLLOWUP_20260909.md).

Chrome's connection works again. JKEdit 3.24's listing could be read, but its embedded download was blocked; the original BAP share was blocked by browser site-safety policy. Those paths were stopped and their public links set aside for the user. [The JKEdit retry report](JKEDIT_RETRY_20260909.md) records the remaining acquisition gap. No payload from either blocked tool was claimed as downloaded.

The authoritative external evidence root is `C:\Users\Andrei\Documents\ChatGPT\sed-editor-audit\research-followup-20260909`. `DOWNLOADS_FOR_ANDREI.md` there contains the deferred public links and expected listing metadata. Existing source ZIPs, recovered archives, downloaded tools, reference code and native projects remain external; this repository contains reports, hashes and original synthetic tests.
