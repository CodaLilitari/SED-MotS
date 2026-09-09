# Passive inspection of installer-only Massassi packages

Eight of the original 50 packages had no directly accessible JKL in the ZIP or its direct GOB/GOO entries. Original ZIP bytes are unchanged. Only their already inventoried installer entries were copied into package-specific external working directories as `wrapper.archive`, preventing accidental launch by file extension. No installer was executed or installed.

Installed 7-Zip 26.02 was used first to list each container. Six wrappers could not be opened as archives; one exposed only a partial embedded font ZIP with a headers error. TODOA exposed an embedded 7z archive, but that initial result was not assumed to cover the entire installer.

| Original Massassi ID | Title | Passive result |
|---:|---|---|
| 4 | The New Empire | 7-Zip exit 2: cannot open as archive. No supported level payload recovered. |
| 1332 | The New Empire Part 2 | Same unsupported-container result. |
| 3123 | TODOA | Four embedded 7z archives validated and listed. Five GOB payloads recovered; see below. |
| 2535 | Scream Multiplayer Beta 1.4 | 7-Zip exit 2: cannot open as archive. |
| 711 | Leeza's Destiny 3.0 | Same unsupported-container result. |
| 1431 | Birth of a Mercenary SE | 7-Zip recognized a partial ZIP for AURABESH.TTF, then reported headers error/unconfirmed start. No level payload recovered; the font was not extracted. |
| 1900 | Rebel Agent | 7-Zip exit 2: cannot open as archive. |
| 2503 | Liberating Cloud City | Same unsupported-container result. |

TODOA's PE metadata identifies an installer version 1.0.5.0; that is the nested installer's metadata, not a reinterpretation of the Massassi package's published date. The installer contains NSIS-identifying strings, but an explicit NSIS 7-Zip attempt did not recognize the outer container. Instead, four embedded 7z byte ranges were located by signature, bounded by the 7z next-header offset/size, and validated against both start-header and next-header CRC32 values. Those exact ranges were retained as separate `.7z` archives and listed successfully. Offsets, byte lengths and SHA256 hashes are in `working/installer-inspection/jksp/3123/embedded-archives.json`.

Only the following listed GOBs were streamed out using installed 7-Zip's stdout into generated filenames, never archive-controlled filesystem paths: `CutscenesDisabled.gob` (91,419 bytes), `EnableSaves.gob` (39,837), `MODULE_ENGINE.gob` (4,609,549), `MODULE_JKE.gob` (44,560,933), and `TODOA.gob` (595,501,371). Runtime executables, DLLs, batch files and other loose assets were not extracted from the embedded archives. The recovered GOBs remain outside the editor repository/release. Every payload is linked to its original ZIP hash, wrapper hash, embedded archive hash/offset and member name, with payload size/SHA256 and extraction exit code.

The verifier reads these five hash-verified payloads directly using file streams. It does not need to execute or further unpack the installer. Seven outer installer formats remain unresolved: they are coverage gaps, not parser passes or proof that the underlying levels are corrupt. No native `.jed` fixture was found in the original ZIP/editor package inventories; nested GOB native-project entries are also inventoried in the final format audit.

Evidence is under the external corpus directory `C:\Users\Andrei\Documents\ChatGPT\sed-editor-audit\corpora\massassi-originals-2026-09-09\working\installer-inspection\`: `manifest.json` records all eight wrapper hashes, list exits/types and list paths; `payload-manifest.json` links the five recovered GOBs. Original acquisition remains governed by the untouched corpus `manifest.json`. `Inspect-MassassiWrappers.ps1` and `Recover-MassassiWrapperPayloads.py` document passive listing/payload recovery; no game or legacy binary was run.
