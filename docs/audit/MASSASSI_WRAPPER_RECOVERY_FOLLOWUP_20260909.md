# Passive recovery of the seven remaining Massassi installer wrappers

All seven previously unresolved wrappers yielded their GOB/GOO payloads without running a historical installer, uninstaller, launcher, editor, or game. The recovery produced **10 archives, 169,319,159 bytes, containing 28 JKL entries and no JED entries**. All source ZIPs and wrapper copies were rehashed after recovery and still match the existing corpus provenance. The original acquisition manifests and the earlier TODOA recovery manifest were not edited.

| Corpus identity | Release title | Recovered archive names recorded inside the wrapper | Archive bytes combined | Nested JKL entries |
| --- | --- | --- | ---: | ---: |
| jksp/4 | The New Empire | `Newemp\tne1.gob` | 6,682,306 | 4 |
| jksp/1332 | The New Empire Part 2 | `tne2\tne2.gob` | 25,580,386 | 5 |
| motsmp/2535 | Scream Multiplayer Beta 1.4 | `ScreamMP\screammp.goo` | 26,726,753 | 1 |
| motssp/711 | Leeza's Destiny 3.0 | `leeza\leeza.goo` | 23,836,038 | 7 |
| motssp/1431 | Birth of a Mercenary SE | `BoaM\BoaM.goo`, `BoaM\BMs.goo` | 45,164,183 | 5 |
| motssp/1900 | Rebel Agent | `weapons.goo`, `Imperialbase.goo` | 23,922,196 | 2 |
| motssp/2503 | Liberating Cloud City | `cloudcity.goo`, `e11.goo` | 17,407,297 | 4 |

## Installer identification and reference provenance

All seven specimens use an older **Clickteam Install Maker** container and modified-DEFLATE stream. Evidence includes the shared `wwgT)H` stub marker, `wwINSS`/`wwINSE` markers, compatible tagged overlays, decoded titles, and identical compression behavior. Six also contain a Clickteam URL string. The seventh, Liberating Cloud City, matches the container and decoded record structure despite lacking that URL string. PE version-resource queries returned no product version. Exact marketing release numbers are therefore unresolved; PE timestamps are retained only as header metadata.

The decoder is an original Python implementation **informed by published source and observed format bytes**. It is not a clean-room implementation. The primary reference is [ScummVM revision 210f814bbcbc0bf57fc03187ee75588c9dce2e9c](https://github.com/scummvm/scummvm/tree/210f814bbcbc0bf57fc03187ee75588c9dce2e9c), particularly [clickteam.cpp](https://github.com/scummvm/scummvm/blob/210f814bbcbc0bf57fc03187ee75588c9dce2e9c/common/compression/clickteam.cpp) and [gzio.cpp](https://github.com/scummvm/scummvm/blob/210f814bbcbc0bf57fc03187ee75588c9dce2e9c/common/compression/gzio.cpp). The local `scummvm-reference` directory retains the consulted source, GPL version 3 license text, original header notices, project COPYRIGHT, and exact revision. The new decoder, recovery driver, verifier, and tests carry `GPL-3.0-or-later` identifiers. ScummVM's compression source retains its GRUB/Free Software Foundation and earlier Mark Adler provenance.

[cicdec revision 42c3c9d030d75ed9096e3c84ef5ffd19bde51b37](https://github.com/Bioruebe/cicdec/tree/42c3c9d030d75ed9096e3c84ef5ffd19bde51b37) was also cloned and source-reviewed, with its BSD 3-Clause license retained. Its documented version range and newer file-table assumptions did not establish support for these specimens. It was neither compiled nor executed. ScummVM was also not compiled or run. Published code supplied the compression details; the earlier sequential table layout was inferred and checked against the seven actual wrappers.

## What was decoded and checked

The old container uses tag `0x1243` for a variable-length file table and tag `0x7f7f` for a duplicate-size header followed by sequential compressed members. All record lengths cover their table exactly. All packed member lengths sum exactly to the data block, whose declared extent ends at the wrapper's EOF. Normal records store a name at offset 50, output and packed sizes, timestamps, and an obfuscated rolling checksum. Three generated `Uninstal.exe` records use a shorter header and have no stored checksum.

The modified-DEFLATE reader enforces exact declared output lengths, supported block kinds, bounded Huffman tables, valid backward references, stream truncation checks, and no unused whole bytes after the final block. Bit padding within the final byte is accepted. Compressed input and each output are limited to 256 MiB, metadata to 64 MiB and 256 tags, the file table to 10,000 members, and total declared member output per wrapper to 512 MiB. These are limits in the decoder and parser; the driver reads pinned inputs before applying the decoder cap, so it is not advertised as a general-purpose hostile-file service.

All **45 installer members** were decompressed to memory. Only the ten GOB/GOO payloads were saved; the other members, including historical executables, were discarded after recording size and hash. Output names are generated `passive-recovery-payload-NNN.gob` or `.goo` under the exact approved corpus wrapper directory. Archive-supplied names are provenance fields and are never used as output paths. Existing differing outputs are refused, identical outputs may be reused, and every write is followed by a hash check.

The earlier generation's absolute checksum-key derivation remains unresolved. One small, non-level member per wrapper calibrates a common XOR between the stored checksum and the decoded bytes' Clickteam rolling checksum. That member's agreement is tautological and is explicitly excluded from the independent confirmations. **Thirty-five other normal members, including all ten recovered archives, confirm cross-file checksum consistency; seven members supply calibration; three generated uninstaller records have no checksum to check.** This is evidence of internal consistency, not cryptographic authentication or proof of authorship. SHA-256 records identify the exact observed inputs and decoded outputs.

Every saved archive passed GOB/GOO magic/version, directory size, member-name termination, and member offset/length bounds checks. The directory inventories contain no duplicate names under case-insensitive comparison. All 28 nested JKL byte ranges have recorded SHA-256 values. These checks establish structural readability and payload identity. They do not establish JKL semantic compatibility, in-game behavior, complete installer behavior, or author intent. The parent task is separately running the existing .NET parser and round-trip audit against the recovered JKLs.

## Tests and independent review

`test_passive_recovery.py` passed **34 tests, zero failures and zero errors**. Positive fixtures include stored blocks, multiple blocks, and independently compressed Python-zlib fixed-Huffman data adapted only at the Clickteam block header. Negative cases cover truncation, trailing data, declared output overruns, missing final blocks, invalid block types, oversubscribed Huffman trees, metadata limits, file-table count and extent errors, GOB directory and member bounds, checksum mismatch, attempted archive-name paths, and refusal to overwrite different existing output. The test receipt is `negative-test-results.json` and records the exact tested decoder and driver hashes.

`verify_recovery.py` then passed **49 checks**: recovery-script and identification hashes, original ZIP and wrapper provenance, every recovered payload's location/size/hash, complete recomputed archive inventories and nested JKL hashes, and agreement between the tested and executed decoder/driver versions. Its result is `post-recovery-verification.json`.

An independent agent reviewed the decoder, inspector, and driver read-only. Identified issues concerning aggregate metadata limits, trailing compressed bytes, disabled Python assertions, and PE section bounds were addressed before the final verification. The reviewer found no must-fix blocker in the observed seven-wrapper path. Remaining scope caveats are the pre-cap input read mentioned above and the driver's generic `.jkl`/`.jed` suffix allowance, which is not a content classifier; this run emitted only structurally validated GOB/GOO archives. No claim of universal Clickteam support is made.

## Handoff files

- `payload-manifest.json`: ten rows compatible with the existing supplemental community-audit input. Each has category/fileId, original ZIP hash, original wrapper entry/hash, archive entry, generated absolute localPath, size/hash, packed offset/size/hash, and nested archive inventory.
- `recovery-evidence.json`: all seven wrapper identities, all 45 member records, exact table and compressed extents, source hashes, calibration details, and the same payload rows.
- `passive-identification.json`, `decoded-container-inventory.json`, `pe-version-metadata.json`: classification and decoded-tag evidence. The later-generation checksum seed in the container inventory is explicitly labeled unvalidated and is not used for recovery validation.
- `reference-provenance.json`: reference pins and local file hashes.
- `negative-test-results.json`, `post-recovery-verification.json`: executable test and verification receipts.

The stable payload manifest SHA-256 is `c963071264fa73aadaf235e6eeb204e2ae1ea2edf18656120822080166ea2f8e`. The stable recovery-evidence SHA-256 is `78581c124c91bea7dcbd2b1f1b5e4027cfa0c73ca86599bedaa6c8cdc1c8c879`.

The extraction gap is closed for all seven wrappers in this task. Semantic editor results remain the responsibility of the separate current-commit community audit; no historical executable or gameplay test was performed here.


This metadata-only report is copied from the external research directory: C:\Users\Andrei\Documents\ChatGPT\sed-editor-audit\research-followup-20260909. Referenced local payloads, source snapshots, manifests, and scripts remain there; no recovered third-party file bodies are included in this repository.
