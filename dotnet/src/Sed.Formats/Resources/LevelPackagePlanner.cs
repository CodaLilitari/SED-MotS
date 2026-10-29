using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sed.Core.Model;
using Sed.Core.Validation;
using Sed.Formats.Automation;
using Sed.Formats.Cogs;
using Sed.Formats.Game;
using Sed.Formats.Gob;
using Sed.Formats.Jkl;

namespace Sed.Formats.Resources;

/// <summary>Known static dependency discovery and deterministic package preparation, without filesystem writes.</summary>
public static class LevelPackagePlanner
{
    public const string Version = "sed-reviewed-package-v1";

    public static PreparedLevelPackage Prepare(ReadOnlyMemory<byte> levelBytes,
        ExplicitArchiveResources? resources, LevelPackageOptions options)
    {
        if (levelBytes.Length > AgentWorkspace.MaxLevelBytes)
            throw new AgentRequestException("The level exceeds the 128 MiB input limit.");
        byte[] original = levelBytes.ToArray();
        var document = JklParser.ParseDocument(LegacyText.Decode(original));
        if (document.Level.Kind is not (ProjectType.JediKnight or ProjectType.MysteriesOfTheSith))
            throw new AgentRequestException("Reviewed packaging supports JK and MotS JKL files.");
        return new Scan(document, original, resources, options.Capture(resources?.Archives ?? [])).Run();
    }

    private sealed class Node(int id, string name, string folder, string destination, int depth)
    {
        internal int Id { get; } = id;
        internal string Name { get; } = name;
        internal string Folder { get; } = folder;
        internal string Destination { get; } = destination;
        internal int Depth { get; } = depth;
        internal string Lookup { get; set; } = "not-inspected";
        internal string Inspection { get; set; } = "not-inspected";
        internal string Disposition { get; set; } = "unresolved";
        internal string? Format { get; set; }
        internal ResourceEntryReceipt? Source { get; set; }
        internal object Describe() => new { Id, Name, Folder, Destination, Depth,
            lookupStatus = Lookup, inspectionState = Inspection, Disposition, Format, Source };
    }

    private sealed class Scan(JklDocument document, byte[] original, ExplicitArchiveResources? resources, LevelPackageOptions options)
    {
        private readonly List<Node> nodes = [];
        private readonly Dictionary<string, Node> lookup = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> destinations = new(StringComparer.Ordinal);
        private readonly List<PackageDependencyEdge> edges = [];
        private readonly List<PackageIssue> issues = [];
        private readonly HashSet<string> scopes = new(StringComparer.Ordinal)
        {
            "Known direct fields and supported static resource declarations only; arbitrary runtime lookups and fallback assets are not proven.",
            "All declared level templates are scanned conservatively; inclusion is not a playtime reachability result.",
            "Unknown template/thing parameters, runtime declaration order and script-created template relationships remain unverified.",
            "Resource lookup follows ExplicitArchiveResources precedence, not observed retail/OpenJK engine file opens.",
            "Captured asset existence and dependency inspection do not prove format validity, rendering, player starts or gameplay."
        };
        private readonly Dictionary<string, PackagePayload> payloads = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ResourceDependencyReadResult> inspections = new(StringComparer.Ordinal);
        private bool limited;
        private bool staticComplete = true;
        private long decodedTextBytes;

        internal PreparedLevelPackage Run()
        {
            string extension = document.Level.Kind == ProjectType.MysteriesOfTheSith ? ".goo" : ".gob";
            string inputHash = AgentWorkspace.Hash(original);
            var archives = resources?.Archives.ToArray() ?? [];
            if (document.Level.Sectors.Count == 0) Issue("emptyLevel", "error", "The JKL has no sectors.");
            foreach (var issue in ConsistencyChecker.Check(document.Level))
                Issue("levelValidation", issue.Severity == IssueSeverity.Error ? "error" : "warning", issue.Message);
            if (document.PreservationWarnings.Count > 0)
                Issue("unmodeledSourceFields", "warning", "The source parser reports unmodeled or preservation-sensitive fields; their resource semantics are unverified.", ack: true);

            CollectRoots();
            for (int next = 0; next < nodes.Count && !limited; next++) Inspect(nodes[next]);

            // A blocked traversal must not acquire further resources through a
            // secondary validator after its graph or text budget has stopped it.
            var architecture = ArchitectureResourceValidation.Evaluate(document.Level,
                limited || issues.Any(i => i.Severity == "error") ? null : resources);
            if (architecture.HasErrors) Issue("architectureBinding", "error", "Stored architectural lighting is incompatible with the captured model resources.");
            else if (architecture.Status == "unverified-no-resources")
                Issue("architectureUnverified", "warning", "Architectural model bindings were not checked against explicit resources.", ack: true);

            var episode = new EpisodeFile { Name = options.EpisodeTitle, GameType = options.EpisodeType };
            episode.Sequences.Add(new EpisodeSequence { Line = 0, Cd = 1, LevelNum = 1, File = options.LevelFileName });
            AddPayload("episode.jk", Encoding.ASCII.GetBytes(episode.Build().Replace("\r\n", "\n")), null);
            AddPayload("jkl/" + options.LevelFileName, original, null);
            var entries = payloads.Values.OrderBy(e => e.Path, StringComparer.Ordinal).ToArray();
            long outputSize = LevelPackageArchive.Size(entries);
            if (outputSize > options.MaxPackageBytes)
                Issue("packageByteLimit", "error", "Complete package bytes, including directory overhead, exceed maxPackageBytes.");

            byte[]? output = null;
            if (Ready())
            {
                output = LevelPackageArchive.Build(entries, options.MaxPackageBytes);
                VerifyPackagedLookup(output);
                if (!Ready()) output = null;
            }
            resources?.VerifyUnchanged();
            string? outputHash = output is null ? null : AgentWorkspace.Hash(output);
            object manifest = new
            {
                schemaVersion = 1, version = Version, dependencyReaderVersion = ResourceDependencyReader.Version,
                inputSha256 = inputHash, sourceRevision = inputHash,
                game = document.Level.Kind == ProjectType.MysteriesOfTheSith ? "mots" : "jk", archiveExtension = extension,
                options, archives = archives.Select(a => new { a.Path, a.Sha256, a.Bytes, stock = IsStock(a.Path) }).ToArray(),
                canBuild = Ready(), staticScanComplete = staticComplete && !limited,
                allReferencesResolved = staticComplete && !limited && nodes.All(n => n.Lookup == "resolved") &&
                    !issues.Any(i => i.Code is "unresolvedRoot" or "invalidResourcePath" or "destinationCollision"),
                runtimeClosureVerified = false, outputSha256 = outputHash, outputBytes = outputSize,
                nodes = nodes.Select(n => n.Describe()).ToArray(), edges,
                entries = entries.Select(e => new { path = e.Path, bytes = e.Bytes.LongLength, sha256 = AgentWorkspace.Hash(e.Bytes) }).ToArray(),
                stockOmissions = nodes.Where(n => n.Disposition == "stock").Select(n => n.Describe()).ToArray(),
                issues, unverifiedScopes = scopes.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
                architectureValidation = architecture,
                note = "A reviewed static package is not a runtime dependency-closure, asset-validity or gameplay result. Original JKL bytes are preserved."
            };
            var canonicalOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(manifest, canonicalOptions);
            var report = JsonNode.Parse(canonical)!.AsObject();
            report.Add("manifestSha256", AgentWorkspace.Hash(canonical));
            return new PreparedLevelPackage(JsonSerializer.SerializeToElement(report, canonicalOptions), output);
        }

        private bool Ready() => !issues.Any(i => i.Severity == "error" ||
            i.RequiresAcknowledgement && !options.AllowUnverifiedReferences);

        private bool IsStock(string path) => (options.StockArchives ?? []).Contains(path,
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        private void Issue(string code, string severity, string message, int? node = null, int? line = null, bool ack = false)
        {
            // Diagnostic growth is tied to the graph budget; no unbounded error transcript.
            if (issues.Count >= options.MaxEdges)
            {
                if (!limited) issues.Add(new("diagnosticLimit", "error", "Package diagnostics exceeded the work budget."));
                limited = true; staticComplete = false; return;
            }
            issues.Add(new(code, severity, message, node, line, ack));
        }

        private void CollectRoots()
        {
            CheckRepeatedResourceParameters();
            var direct = LevelAssetDiagnostics.Describe(document, null, options.MaxEdges, 100000);
            if (!direct.DirectScanComplete) { Issue("rootScanLimit", "error", "The known direct-root scan exceeded its budget."); staticComplete = false; }
            foreach (var row in direct.Rows)
            {
                if (limited) break;
                if (row.Status == "null") continue;
                // Match the parser's explicit empty-table slot-zero scaffold case.
                if (row.Origin.Kind == "sector" && row.Origin.Field == "colormap" && string.IsNullOrEmpty(row.Name)
                    && document.Level.ColorMaps.Count == 0 && row.Origin.SectorIndex is int si
                    && document.Level.Sectors[si].ColorMapIndex == 0)
                { scopes.Add("The editor scaffold declares no palette; no palette filename is inferred or included."); continue; }
                if (string.IsNullOrWhiteSpace(row.Name) || row.Folder is null)
                { Issue("unresolvedRoot", "error", row.Detail ?? "A known direct resource or template relationship is unresolved."); continue; }
                Add(row.Name, row.Folder, 0, null, "direct:" + row.Origin.Field, origin: row.Origin);
            }
            var tables = JklResourceTableReader.Read(document.SourceLines, options.MaxEdges);
            ReadState(tables, null);
            foreach (var reference in tables.Dependencies)
                Add(reference.Name, reference.Folder, 0, null, reference.Relation, reference.LineNumber, reference.SymbolName);
            foreach (var reference in options.ExtraResources ?? [])
                Add(reference.Name, reference.Folder, 0, null, "explicitExtra");
        }

        private void CheckRepeatedResourceParameters()
        {
            int work = 0;
            void Check(IEnumerable<KeyValuePair<string, string>> parameters, string owner)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var parameter in parameters)
                {
                    if (++work > 100000)
                    { Issue("sourceParameterLimit", "error", "Retained parameter inspection exceeds 100000 records."); limited = true; staticComplete = false; return; }
                    if (TemplateParams.KindOf(parameter.Key) is not (TemplateParamKind.Material or TemplateParamKind.Cog or
                        TemplateParamKind.SoundClass or TemplateParamKind.Puppet or TemplateParamKind.Sprite or
                        TemplateParamKind.Particle or TemplateParamKind.Model3do or TemplateParamKind.AiClass)) continue;
                    if (!seen.Add(parameter.Key))
                    {
                        Issue("repeatedResourceParameter", "error", $"{owner} repeats resource parameter '{parameter.Key}'; exact source execution cannot be represented by its effective value alone.");
                        staticComplete = false;
                    }
                    if (limited) return;
                }
            }
            foreach (var template in document.Level.Templates.Values)
            { if (limited) return; Check(template.SourceParameters, $"Template '{template.Name}'"); }
            for (int i = 0; i < document.Level.Things.Count && !limited; i++)
                Check(document.Level.Things[i].SourceParameters, $"Thing {i}");
        }

        private void Add(string name, string folder, int depth, int? parent, string relation,
            int? line = null, string? symbol = null, LevelAssetOrigin? origin = null)
        {
            if (limited) return;
            if (edges.Count >= options.MaxEdges)
            { Issue("edgeLimit", "error", "Dependency edges exceed maxEdges.", parent, line); limited = true; staticComplete = false; return; }
            (string Name, string Folder, string Destination) normalized;
            try { normalized = PackageResourcePath.Resource(name, folder); }
            catch (AgentRequestException e) { Issue("invalidResourcePath", "error", e.Message, parent, line); return; }
            string key = normalized.Folder + "\0" + normalized.Name;
            if (!lookup.TryGetValue(key, out var node))
            {
                if (destinations.TryGetValue(normalized.Destination, out var previous) && previous != key)
                {
                    Issue("destinationCollision", "error", $"Distinct resource requests map to '{normalized.Destination}'.", parent, line);
                    staticComplete = false;
                    return;
                }
                if (depth > options.MaxDepth)
                { Issue("depthLimit", "error", "Dependency traversal exceeds maxDepth.", parent, line); staticComplete = false; return; }
                if (nodes.Count >= options.MaxNodes)
                { Issue("nodeLimit", "error", "Dependency nodes exceed maxNodes.", parent, line); limited = true; staticComplete = false; return; }
                node = new(nodes.Count, normalized.Name, normalized.Folder, normalized.Destination, depth);
                nodes.Add(node); lookup.Add(key, node); destinations.Add(normalized.Destination, key);
            }
            edges.Add(new(parent, node.Id, relation, line, symbol, origin));
        }

        private void Inspect(Node node)
        {
            var bytes = resources?.Read(node.Name, node.Folder);
            if (bytes is null)
            {
                staticComplete = false;
                bool ambiguous = resources?.StatusFor(node.Name, node.Folder) == ResourceLookupStatus.Ambiguous;
                node.Lookup = ambiguous ? "ambiguous" : "missing";
                Issue(ambiguous ? "ambiguousResource" : "missingResource", "error",
                    $"{node.Folder}/{node.Name} is {node.Lookup} in the explicit resource archives.", node.Id);
                return;
            }
            node.Lookup = "resolved";
            node.Source = resources!.ReceiptFor(node.Name, node.Folder)!;
            node.Disposition = IsStock(node.Source.Archive) ? "stock" : "include";
            if (node.Disposition == "include") AddPayload(node.Destination, bytes.Value.ToArray(), node.Id);
            string inspectionKey = node.Source.Archive + "\0" + node.Source.Entry + "\0" + node.Source.Sha256 + "\0" + node.Folder;
            if (!inspections.TryGetValue(inspectionKey, out var inspected))
            {
                if (node.Folder is "3do" or "cog" or "misc/pup" or "misc/snd" or "misc/spr" or "misc/par")
                {
                    decodedTextBytes += bytes.Value.Length;
                    if (decodedTextBytes > 32L * 1024 * 1024)
                    { Issue("textWorkLimit", "error", "Dependency-bearing text exceeds the 32 MiB cumulative budget.", node.Id); limited = true; staticComplete = false; return; }
                }
                inspected = ResourceDependencyReader.Read(node.Name, node.Folder, bytes.Value,
                    new(MaxReferences: options.MaxEdges));
                inspections.Add(inspectionKey, inspected);
            }
            node.Format = inspected.Format;
            node.Inspection = JsonNamingPolicy.CamelCase.ConvertName(inspected.State.ToString());
            ReadState(inspected, node.Id);
            if (node.Folder is "cog" or "misc/ai")
                Issue("runtimeReferencesUnverified", "warning", node.Folder == "cog"
                    ? "COG code can load or construct resource names; static declarations do not prove runtime closure."
                    : "AI difficulty variants and runtime fallback files are uninspected.", node.Id, ack: true);
            foreach (var reference in inspected.Dependencies)
                Add(reference.Name, reference.Folder, node.Depth + 1, node.Id, reference.Relation, reference.LineNumber, reference.SymbolName);
            if (!limited && inspected.CogSchema is { } schema) CogParameters(node, schema);
        }

        private void ReadState(ResourceDependencyReadResult result, int? node)
        {
            foreach (var scope in result.UnverifiedScopes) scopes.Add(scope);
            if (result.State is ResourceDependencyReadState.Complete or ResourceDependencyReadState.Leaf) return;
            staticComplete = false;
            bool unsupported = result.State == ResourceDependencyReadState.Unsupported;
            foreach (var diagnostic in result.Diagnostics)
                Issue(diagnostic.Code, unsupported ? "warning" : "error", diagnostic.Message, node, diagnostic.LineNumber, unsupported);
            if (result.Diagnostics.Count == 0)
                Issue("dependencyInspectionIncomplete", unsupported ? "warning" : "error", $"Dependency inspection is {result.State} for {result.Format}.", node, ack: unsupported);
            if (result.State == ResourceDependencyReadState.LimitExceeded) limited = true;
        }

        private void CogParameters(Node node, CogScript script)
        {
            for (int ci = 0; ci < document.Level.Cogs.Count; ci++)
            {
                if (ci >= options.MaxEdges) { Issue("cogWorkLimit", "error", "Placed COG inspection exceeds its work budget.", node.Id); limited = true; return; }
                var cog = document.Level.Cogs[ci];
                if (!cog.Name.Replace('\\', '/').Equals(node.Name, StringComparison.OrdinalIgnoreCase)) continue;
                if (cog.Values.Count != script.LevelValues.Count)
                { Issue("cogParameterCount", "error", $"Placed COG {ci} has {cog.Values.Count} values for {script.LevelValues.Count} external symbols.", node.Id); continue; }
                for (int i = 0; i < script.LevelValues.Count && !limited; i++)
                {
                    var symbol = script.LevelValues[i];
                    string? folder = symbol.Type switch
                    {
                        CogSymbolType.Sound => "sound", CogSymbolType.Material => "mat", CogSymbolType.Model => "3do",
                        CogSymbolType.Keyframe => "3do/key", CogSymbolType.Ai => "misc/ai", _ => null
                    };
                    if (folder is null) continue;
                    string value = cog.Values[i];
                    if (folder == "sound" && value.Equals("none", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.IsNullOrEmpty(value))
                    { Issue("cogResourceValueUnknown", "warning", $"Placed COG {ci} resource symbol '{symbol.Name}' has no explicit filename.", node.Id, symbol.LineNumber, true); continue; }
                    Add(value, folder, node.Depth + 1, node.Id, "placedCogResource", symbol.LineNumber, symbol.Name,
                        new("placedCogParameter", symbol.Name, Index: ci));
                }
            }
        }

        private void AddPayload(string destination, byte[] bytes, int? node)
        {
            if (payloads.TryGetValue(destination, out var previous))
            {
                if (!previous.Bytes.AsSpan().SequenceEqual(bytes))
                    Issue("destinationCollision", "error", $"Different captured resources map to '{destination}'.", node);
                return;
            }
            payloads.Add(destination, new(destination, bytes));
        }

        private void VerifyPackagedLookup(byte[] output)
        {
            using var stream = new MemoryStream(output, writable: false);
            using var package = new GobArchive(stream);
            foreach (var node in nodes.Where(n => n.Lookup == "resolved"))
            {
                var matches = package.FindResourceEntries(node.Name, node.Folder);
                if (matches.Count == 0 && node.Disposition == "stock") continue;
                if (matches.Count != 1 || AgentWorkspace.Hash(package.ReadBytes(matches[0])) != node.Source!.Sha256)
                    Issue("packagedLookupCollision", "error", "The new archive changes this resource's resolution or makes it ambiguous under the declared provider policy.", node.Id);
            }
        }
    }
}
