using Sed.Core.Model;
using Sed.Formats.Jkl;

namespace Sed.Formats.Resources;

public sealed record LevelAssetOrigin(string Kind, string Field, int? Index = null,
    int? SectorIndex = null, int? SurfaceIndex = null, string? TemplateName = null,
    string? DeclaredByTemplate = null);

public sealed record LevelAssetRow(int RowIndex, LevelAssetOrigin Origin, string? Name,
    string? Folder, string Status, ResourceEntryReceipt? Receipt = null, string? Detail = null);

public sealed record LevelAssetReport(bool DirectScanComplete, int WorkUsed, int MaxRows, int MaxWork,
    IReadOnlyList<LevelAssetRow> Rows, IReadOnlyList<ResourceArchiveReceipt> Archives,
    string? ResourceRevision, IReadOnlyList<string> Diagnostics, IReadOnlyList<string> UnverifiedScopes)
{
    public string Scope => "known-direct-level-resource-references-v1";
    public bool RecursiveClosureVerified => false;
    public string ResolutionSemantics => "ExplicitArchiveResources archive precedence and lookup policy; captured bytes are not a runtime load or format-validity proof";
}

/// <summary>
/// Bounded inspection of the current model's known direct resource references.
/// Resolves only through caller-supplied, hash-bound archives. Does not enumerate
/// installation directories, parse resource contents, or interpret arbitrary
/// parameter values and COG code as filenames.
/// </summary>
public static class LevelAssetDiagnostics
{
    // Core TemplateParams is the supported field contract. Folders correspond to
    // the OpenJKDF2 sithThing resource loaders, not guessed filename extensions.
    private static readonly (string Key, string Folder)[] ResourceFields =
    [
        ("material", "mat"), ("cog", "cog"), ("soundclass", "misc/snd"),
        ("puppet", "misc/pup"), ("sprite", "misc/spr"), ("particle", "misc/par"),
        ("model3d", "3do"), ("aiclass", "misc/ai"),
    ];

    public static LevelAssetReport Describe(JklDocument document, ExplicitArchiveResources? resources,
        int maxRows = 10_000, int maxWork = 100_000)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (maxRows is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(maxRows), "Use 1 through 100000 rows.");
        if (maxWork is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(maxWork), "Use 1 through 1000000 work units.");
        return new Scan(document, resources, maxRows, maxWork).Run();
    }

    private sealed class Scan(JklDocument document, ExplicitArchiveResources? resources, int maxRows, int maxWork)
    {
        private readonly List<LevelAssetRow> _rows = new();
        private readonly List<string> _diagnostics = new();
        private int _work;

        public LevelAssetReport Run()
        {
            bool complete = true;
            try { Enumerate(); }
            catch (ScanLimit error) { complete = false; _diagnostics.Add(error.Message); }
            if (resources is not null) _diagnostics.AddRange(resources.Diagnostics);
            var archives = resources?.Archives.ToArray() ?? Array.Empty<ResourceArchiveReceipt>();
            return new(complete, _work, maxRows, maxWork, _rows.AsReadOnly(), Array.AsReadOnly(archives),
                resources?.ResourceRevision, _diagnostics.AsReadOnly(), Array.AsReadOnly(new[]
                {
                    "COG code, resource-valued COG parameters and dynamically constructed resource names are not inspected.",
                    "Transitive model/material, puppet/keyframe, sprite/particle, soundclass/sound and AI dependencies are not inspected.",
                    "Unmodeled JKL resource tables and unknown template/thing parameters are not inspected.",
                    "Template inheritance describes the current editor model; runtime declaration order, repeated legacy parameters and script mutations are unverified.",
                    "Archive lookup is the explicit provider policy, not verification of retail/OpenJK resource search paths, fallback assets or runtime loading.",
                }));
        }

        private void Spend()
        {
            if (_work >= maxWork) throw new ScanLimit("Direct-reference scan stopped at its work limit; remaining references are uninspected.");
            _work++;
        }

        private void Add(LevelAssetOrigin origin, string? name, string? folder, string? forcedStatus = null, string? detail = null)
        {
            Spend();
            if (_rows.Count >= maxRows) throw new ScanLimit("Direct-reference scan stopped at its row limit; remaining references are uninspected.");
            string status = forcedStatus ?? "unknown";
            ResourceEntryReceipt? receipt = null;
            if (forcedStatus is null)
            {
                if (string.IsNullOrWhiteSpace(name)) detail = "The known resource field is empty; no filename is inferred.";
                else if (name.Length > 1024) detail = "The resource name exceeds the supported 1024-character inspection bound.";
                else if (resources is null) detail = "No explicit resource archives were supplied.";
                else
                {
                    // Read is the only acquisition route. The accessors return its
                    // exact cached result, including misses and ambiguous matches.
                    resources.Read(name, folder!);
                    var lookup = resources.StatusFor(name, folder!);
                    status = lookup switch
                    {
                        ResourceLookupStatus.Resolved => "resolved",
                        ResourceLookupStatus.Missing => "missing",
                        _ => "unknown",
                    };
                    receipt = resources.ReceiptFor(name, folder!);
                    detail = lookup switch
                    {
                        ResourceLookupStatus.Ambiguous => "The explicit archive lookup is ambiguous; no entry was selected.",
                        ResourceLookupStatus.Missing => "No matching entry was found under the explicit provider's lookup policy.",
                        _ => null,
                    };
                }
            }
            _rows.Add(new(_rows.Count, origin, name, folder, status, receipt, detail));
        }

        private void Enumerate()
        {
            var level = document.Level;
            for (int i = 0; i < document.Materials.Count; i++)
                Add(new("materialTable", "material", Index: i), document.Materials[i], "mat");
            for (int i = 0; i < level.ColorMaps.Count; i++)
                Add(new("colormapTable", "colormap", Index: i), level.ColorMaps[i], "misc/cmp");
            if (!string.IsNullOrEmpty(level.MasterCmp)) Add(new("level", "masterCmp"), level.MasterCmp, "misc/cmp");
            for (int i = 0; i < level.Sectors.Count; i++)
            {
                Spend();
                var sector = level.Sectors[i];
                if (!string.IsNullOrEmpty(sector.ColorMap) || sector.ColorMapIndex >= 0)
                    Add(new("sector", "colormap", SectorIndex: i), sector.ColorMap, "misc/cmp");
                if (!string.IsNullOrEmpty(sector.Sound))
                    Add(new("sector", "sound", SectorIndex: i), sector.Sound, "sound",
                        sector.Sound.Equals("none", StringComparison.OrdinalIgnoreCase) ? "null" : null,
                        sector.Sound.Equals("none", StringComparison.OrdinalIgnoreCase) ? "The sound loader's explicit none sentinel." : null);
                for (int j = 0; j < sector.Surfaces.Count; j++)
                {
                    Spend();
                    var surface = sector.Surfaces[j];
                    if (!string.IsNullOrEmpty(surface.Material) || surface.MaterialIndex >= 0)
                        Add(new("surface", "material", SectorIndex: i, SurfaceIndex: j), surface.Material, "mat");
                }
            }
            for (int i = 0; i < level.Cogs.Count; i++) Add(new("placedCog", "script", Index: i), level.Cogs[i].Name, "cog");
            foreach (var pair in level.Templates)
            {
                Spend();
                Fields(new("template", "", TemplateName: pair.Key), null, pair.Key);
            }
            for (int i = 0; i < level.Things.Count; i++)
            {
                Spend();
                var thing = level.Things[i];
                Fields(new("thing", "", Index: i, TemplateName: thing.Template), thing.Values, thing.Template);
            }
        }

        private void Fields(LevelAssetOrigin owner, IReadOnlyDictionary<string, string>? direct, string templateName)
        {
            // A constant-size key set avoids walking arbitrary parameter dictionaries.
            var values = new Dictionary<string, (string Value, string? Template)>(StringComparer.OrdinalIgnoreCase);
            void Gather(IReadOnlyDictionary<string, string> source, string? declaredBy)
            {
                foreach (var field in ResourceFields)
                {
                    Spend();
                    if (!values.ContainsKey(field.Key) && source.TryGetValue(field.Key, out string? value))
                        values[field.Key] = (value, declaredBy);
                }
            }
            if (direct is not null) Gather(direct, null);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int depth = 0;
            while (!string.IsNullOrEmpty(templateName) && !templateName.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                Spend();
                string? problem = null;
                if (!visited.Add(templateName)) problem = "Template inheritance contains a cycle; remaining inherited fields are unknown.";
                else if (depth++ >= 32) problem = "Template inheritance exceeds the supported 32-ancestor bound; remaining fields are unknown.";
                else if (!document.Level.Templates.ContainsKey(templateName)) problem = "The referenced template is not in the current level; inherited fields are unknown.";
                if (problem is not null)
                {
                    Add(owner with { Field = "template" }, templateName, null, "unknown", problem);
                    break;
                }
                var template = document.Level.Templates[templateName];
                Gather(template.Values, templateName);
                templateName = template.Parent;
            }
            foreach (var field in ResourceFields)
            {
                Spend();
                if (!values.TryGetValue(field.Key, out var value)) continue;
                bool nullSoundClass = field.Key == "soundclass" && value.Value.Equals("none", StringComparison.OrdinalIgnoreCase);
                Add(owner with { Field = field.Key, DeclaredByTemplate = value.Template }, value.Value, field.Folder,
                    nullSoundClass ? "null" : null,
                    nullSoundClass ? "The soundclass loader's explicit none sentinel." : null);
            }
        }
    }

    private sealed class ScanLimit(string message) : Exception(message);
}
