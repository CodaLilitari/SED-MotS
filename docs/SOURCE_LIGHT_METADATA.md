# Source-light metadata format, version 1

`SED_SOURCE_LIGHTS` preserves editor source-light state that ordinary JKL `LIGHTS` rows cannot represent. It is an additive authoring section, not an engine lighting format or a new native JED/SED writer. The supported preservation path is native import or authoring in this editor, followed by JKL save and reopen here. Retail, other-editor and engine-runtime compatibility of this extension remains unverified.

## Stored fields and ordinary rows

`Light.Intensity` is the white/mono intensity and the fallback shared intensity. Nullable `RgbIntensity` is an independent MotS RGB value: `null` inherits `Intensity`; explicit zero and explicit values equal to `Intensity` remain explicit. Nullable `LegacyRgbRange` retains native legacy metadata; neither its presence nor its value changes baking. Baking continues to use primary `Range`. Existing JK/Infernal Machine calculation behavior remains unchanged.

Ordinary JKL rows retain the existing forms below, after their optional textual index prefix:

| Form | Fields |
|---|---|
| Legacy, six fields | `layer x y z intensity range` |
| JK, seven fields | `flags layer x y z range intensity` |
| MotS/Infernal Machine, ten fields | `flags layer x y z range intensity r g b` |

The writer generates seven-field JK or ten-field MotS/Infernal Machine rows when a rewrite is required. Flags in ordinary rows are hexadecimal. An unchanged supported source section can retain its original text and six-field form. The parser reads exactly the declared number of lights, then permits `END`, the next `SECTION: name`, or EOF; comments and blank lines are ignored. Extra rows or other noncomment content before the next section, including after `END`, are rejected rather than silently dropped on later export.

The native importer accepts the existing ten-field SED row and the legacy twelve-field row. The latter retains `x y z layer intensity range flags legacyRgbRange rgbIntensity r g b` independently. Current ten-field rows have no explicit independent RGB intensity or legacy RGB range. Imported inactive JK RGB/color data is retained as authoring metadata without activating MotS calculation semantics.

## Extension grammar and validation

```text
SECTION: SED_SOURCE_LIGHTS
VERSION 1
LIGHTS_SHA256 <64 hexadecimal characters>
SOURCE_LIGHTS <N>
0: <rgbIntensity|inherit> <legacyRgbRange|none> <r> <g> <b>
...
N-1: <rgbIntensity|inherit> <legacyRgbRange|none> <r> <g> <b>
END
```

Keywords and sentinel words are case-insensitive; the writer uses the spelling shown. `VERSION` must be exactly `1`. Count is an integer from zero through 1,000,000. Every record has exactly six tokens including its index, and indices must be sequential `0:` through `N-1:` without duplicates, gaps or unknown records. All numeric values must be finite. After `END`, only comments, blank lines, the next section header or EOF are accepted. Duplicate metadata sections, unsupported versions, mismatched counts, malformed records and stale checksums fail before a document is returned.

The extension is normally emitted only when an independent field is explicit or an inactive JK color would otherwise be lost. When an existing extension is rewritten after fields are cleared or lights deleted, an all-inherited or zero-row extension can remain. Its presence alone does not imply an independent RGB value.

The reader validates the checksum against the parsed ordinary rows before applying metadata. If an ordinary row already supplied RGB, its color and redundant metadata color must have identical binary32 component bits, including the sign of zero. Metadata can carry inactive color for six/seven-field ordinary rows, which have no RGB tokens. The metadata count covers all lights, not only lights with overrides.

## Canonical checksum bytes

`LIGHTS_SHA256` is SHA-256 of the following canonical string, encoded as UTF-8 **without a BOM**. Every newline is one LF byte (`0A`), including the final row's newline. There are no CR bytes, indentation, comments or trailing spaces.

1. The domain line is exactly `sed-source-light-base-v1` followed by LF.
2. The light count is invariant decimal followed by LF.
3. One line per ordinary light, in parsed/writer list order, contains these ten values separated by one ASCII space: `flags layer x y z range intensity r g b`, followed by LF.

Canonical flags use the model's signed 64-bit value in invariant decimal, not the hexadecimal spelling of ordinary JKL rows. Layer uses invariant signed decimal. Neither integer has separators or a leading plus. Positions, range and primary intensity are finite binary64 values formatted using .NET `ToString("R", CultureInfo.InvariantCulture)`: the invariant round-trip representation. RGB components are first stored as binary32 `ColorF` values, then **promoted to binary64 before the same `R` formatting**; they are not formatted directly with binary32 `R`. For example, binary32 `0.1f` contributes `0.10000000149011612`. Native/source decimal colors are therefore preserved at the model's binary32 precision, not as arbitrary-precision decimals or original numeric spellings.

Negative zero contributes `-0`; positive zero contributes `0`. Their hashes differ. The nullable extension values use the same finite binary64 formatting, with `inherit`/`none` representing absence. Nullable values, extension RGB, section spelling, comments and textual index labels do not themselves enter the base checksum. Extension RGB must still pass the redundant-color consistency check described above.

For generation of seven-field JK rows, canonical ordinary RGB is white (`1 1 1`), matching the color implied by those rows. Parsing instead hashes the RGB actually supplied by each ordinary row, or its default white when omitted, irrespective of later project-kind inference. Legacy six-field rows imply flags zero. This binds metadata to the serialized base rather than to a different in-memory projection.

### Independent fixed vector

The original synthetic test vector uses flags `2147483649`, layer `2`, adjacent binary64 position values `1` and `1.0000000000000002`, a negative-zero position, the next binary64 above `5`, and binary32 RGB bit patterns `3DCCCCCD`, `3DCCCCCE`, `80000000`. Its exact canonical string is the following three lines, with LF after each line:

```text
sed-source-light-base-v1
1
2147483649 2 1 1.0000000000000002 -0 5.000000000000001 0.1 0.10000000149011612 0.10000000894069672 -0
```

The UTF-8 length is **129 bytes**, and the SHA-256 is **`63d045cfe6c98062d8cbd2daf5a37676af67ddab6698f2d5186f76bbad9bc1d0`**. This constant was calculated from the manually assembled string with Python 3 `hashlib`, separately from production canonicalization. `SourceLightFormatTests.CanonicalChecksumMatchesAnIndependentFixedPrecisionVector` checks the constant against generated output. Companion tests preserve adjacent native values and signed-zero bits and reject redundant RGB with a different zero sign. This is format-level evidence, not engine-runtime evidence.

## Evidence and loss boundaries

The checksum establishes consistency between current ordinary rows and metadata; it is neither authentication nor historical provenance. Metadata indices are positional consistency checks, not persistent light identities. Identical ordinary records cannot establish which historical light owned a particular override; a party that changes the file can also recompute its checksum.

An open document captures the original light-section text hash. Before export, retained metadata is reparsed, and direct mutation or removal of previously captured metadata text is rejected instead of copied around model validation. Make supported changes through the document model, or reopen deliberately changed source. This guard is local to that document instance.

If another editor strips the entire extension and the resulting file is opened anew, absence is indistinguishable from a truly legacy file that never had independent values. The editor cannot reconstruct those values or prove they never existed. An absent extension supplies inherited defaults only; a validated extension proves current consistency only. Preserve the authoring JKL and its receipts when using other editors that may drop unknown sections.

The ordinary layouts remain established layouts, but no retail run or other-editor retention guarantee follows from this design. Engine source handling of unknown sections is source-backed compatibility evidence only until separately tested with an explicit runtime witness.
