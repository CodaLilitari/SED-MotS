# Community format audit

This console tool verifies a manifest-selected original Massassi corpus without starting the editor, game or installers. It verifies every original ZIP's size and SHA256 before reading its loose JKLs and direct GOB/GOO members. It never follows archive resource paths or extracts files. Optional supplemental payloads must identify the original ZIP hash, have their own matching size/SHA256, and reside directly in that package's `working/installer-inspection/<category>/<fileId>/` directory without reparse points.

Run with the .NET SDK used by the repository:

```powershell
dotnet run --project dotnet/tools/Sed.CommunityAudit -c Release -- <corpus-manifest.json> <report.json> [verified-wrapper-payload-manifest.json]
```

The acquisition manifest records original source URLs and files. Supplemental GOB/GOO/JKL files can be recovered by a separately reviewed passive archive inspector; this verifier does not run that inspector or any installer. Only supply a supplemental manifest for the same corpus and packages. Original ZIPs must remain unchanged.

For every reachable JKL the tool compares an unchanged write against source text after normalizing line endings. It also snapshots the explicit modeled data before writing, forces regeneration via a fresh `JklDocument`, reparses the result, and compares modeled data and the material table. The modeled digest derives from the stock MotS audit's own helper and additionally includes lights. It preserves vertex/topology order, flags, scalar/RGB light values, bounds, thing parameters and repeated parameter order, and architectural lighting bindings/data. Bookkeeping IDs and pooled world-vertex indices are excluded.

The report records integrity, document counts, precise failure phase, first differing modeled JSON property, warnings, wrapper gaps, source hashes and verifier/format assembly hashes. Empty/static JKLs are counted separately from geometry documents. An installer-only package is an explicit coverage gap, not a compatibility pass. A zero exit code means all reached documents passed the modeled checks and no bounded input was skipped; it does not assert all wrappers were understood or the levels run in a game.

Bounds: original ZIPs and in-memory GOB/GOO inputs up to 512 MiB, individual JKLs up to 64 MiB, 100,000 entries per container, and 1 GiB total expanded/read payload bytes per package. Known supplemental containers use file streams and may be up to the 1 GiB package bound; this accommodates the recovered 568 MiB TODOA GOB without loading all its assets into memory. Unsupported formats and bounds are reported explicitly.

No complete unknown-section/field regeneration, resource resolution, COG execution, rendering or engine compatibility is certified. The JSON report contains metadata and hashes, not third-party level bodies or assets.
