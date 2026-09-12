# Resource-aware architectural lighting validation

Implemented in the C# editor and agent on 2026-09-12. The engine's hidden rendered capture is a separate source change in OpenJKDF2-MotS-Fixes. Neither feature establishes retail pixel parity or calculates a new lighting bake.

## Shared capture and validation

[ExplicitArchiveResources](../dotnet/src/Sed.Formats/Resources/ExplicitArchiveResources.cs) now owns the archive capture previously private to snapshots. Inputs are explicit absolute paths and SHA256 hashes, with first-archive precedence, bounded input/decoded bytes, ambiguous-name refusal, cached entries, raw entry receipts and held-handle plus named-path rechecks. No installation or loose-directory discovery occurs. The snapshot class is a rendering adapter over this provider.

[GobArchive.FindResourceEntries](../dotnet/src/Sed.Formats/Gob/GobArchive.cs) supplies shared folder/exact-name/unique-basename resolution. The GUI model library and verified exports use the same selection rule. File-opened archives retain their absolute source path; anonymous stream archives cannot claim file provenance.

[ThreeDoTopologyReader](../dotnet/src/Sed.Formats/ThreeDo/ThreeDoTopologyReader.cs) captures every declared JK/MotS geometry set without rendering lower LODs. It checks ordered geometry/mesh declarations, complete vertex/UV/face/normal tables, referenced array bounds and aggregate budgets. The limit is four geosets. Data-row numeric labels are ignored as in rdModel3: physical row order defines the slot. This matters for stock crt6-1.3do, whose normal rows repeat a printed label. Original synthetic tests cover that behavior; retail source text is not committed.

[ArchitectureBindingValidator](../dotnet/src/Sed.Formats/ThreeDo/ArchitectureBindingValidator.cs) is side-effect-free. It checks the bound thing, resolved instance/template flags, model names, optional normalized native fingerprint and topology:
- Geoset zero must match all stored ARCH mesh and vertex counts exactly.
- Later geosets may consume a prefix of those mesh and vertex arrays.
- Missing topology cannot authorize a binding.
- Missing historical baselines remain unknown even when current topology fits.
- Unsupported normalized text is distinct from a demonstrated content mismatch.

Typed diagnostics, current topology compatibility, binding usability and historical-baseline verification are separate result fields. The normalized historical fingerprint is distinct from the raw resource hash. Existing ASCII/line-length normalization limits remain in force.

The preview uses the same validator. It still renders geoset zero and the rest pose; the additional metadata does not add lower-LOD rendering. Incompatible bindings produce a diagnosed base-light fallback in permissive previews and refusal in strict snapshots.

## Agent transactions and GUI exports

[ResourceValidatedExport](../dotnet/src/Sed.Formats/Resources/ResourceValidatedExport.cs) validates the live document before JKL serialization loses native fingerprint provenance. Its document revision includes both serialized data and native per-thing architectural source metadata.

[AgentWorkspace](../dotnet/src/Sed.Formats/Automation/AgentWorkspace.cs) and [AgentResources](../dotnet/src/Sed.Formats/Automation/AgentResources.cs) add optional explicit resource archives to plan/apply/validate/convert. Native sourceRevision retains its document/template meaning. A separate resourceRevision binds ordered archives, used entry identities/hashes and validator version.

A reviewed apply copies the plan's resourceRevision into the request and supplies its output hash. Changed resource bytes can leave JKL text identical; the independent revision detects that case. Without the optional expected revision, the command performs fresh validation without asserting that an earlier resource result was reviewed.

Prepared edits retain an internal immutable resource capture and exact output hash. Commit rejects replaced output text, reopens and verifies archives, resolves the same model requests and checks resource revision. After staging/flushing the new file, it rechecks source/template and resource dependencies immediately before no-overwrite promotion. Windows read handles prevent archive writes while open; hashing alone cannot eliminate every final path race on all filesystems.

Resource-free operations remain available and report unverified-no-resources when ARCH bindings exist. Invalid or unverified arrays are not silently erased.

[GuiResourceExport](../dotnet/src/Sed.App/GuiResourceExport.cs) and MainWindow route Save As, GOB/GOO export and Save and Test through the same live-document validation. All batch files are staged before promotion. Fresh document/resource state and destination identities are checked before each replacement; a later failure attempts rollback while preserving external subsequent edits and reporting retained backups. Each replacement is atomic; a batch is not a filesystem transaction.

Recovery, autosave and backup remain available for unresolved projects. GUI validation covers the current editor archives. The engine may additionally resolve loose files and other runtime overrides; Save and Test does not certify that complete resource search.

## Validation evidence

The full Release build passed 801 unit tests with zero warnings/errors. Focused original fixtures cover all-LOD overflow and prefixes, malformed/truncated tables, historical baseline distinctions, same-name changed bytes, stale reviewed revisions, missing/ambiguous models, unchanged input and stored arrays on refusal, prepared-output replacement, GUI staging, target conflicts and guarded rollback.

The end-to-end synthetic lit/resource workflow passed 111 assertions across 34 direct agent calls, including reviewed apply/conversion, complete ARCH section preservation, changed resource bytes with unchanged planned JKL, and refused missing-model exports.

The original-stock corpus checks passed for all 39 MotS levels: 2,353 bound things, 172 unique archive/model entry identities, no topology/resource/flag failures. All historical baselines correctly remain Missing. A strict Rebel Base offscreen snapshot validated all 83 bindings and retained the previous image hash. These are structural/current-resource and static-rendering checks, not engine gameplay or retail comparison.

Local receipts are generated under ignored build directories. The checked-in synthetic tests and command workflow can be rerun without game assets; stock checks require separately owned original archives.

## Remaining authoring and renderer work

An explicit shared bind/rebind command, full independent mono/RGB rebaking and historical-baseline authoring remain future work. The current clearThingArchitecture command removes a binding while retaining its arrays. Validation does not prove sensible vertex correspondence or that the bake remains correct after moving/rotating an object.

Lower-LOD rendering, runtime dynamic lighting, pivot-aware rendering, exact sky projection/depth and complete material/renderer parity remain separate tasks. Preserve the distinction between model-array safety, static preview, hidden engine capture and retail comparison.
