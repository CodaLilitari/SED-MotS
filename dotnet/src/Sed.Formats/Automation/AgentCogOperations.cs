using System.Text;
using System.Text.Json;
using Sed.Core.Editing;
using Sed.Formats.Cogs;
using Sed.Formats.Jkl;
using Sed.Formats.Resources;

namespace Sed.Formats.Automation;

public static partial class AgentWorkspace
{
    private static (CogScript? Script, string? Sha256) ReadCogScript(ExplicitArchiveResources resources, string name)
    {
        var bytes = resources.Read(name, "cog");
        if (bytes is null) return (null, null);
        if (bytes.Value.Length > MaxRequestBytes)
            throw new AgentRequestException("COG script exceeds the 1 MiB schema-inspection budget.");
        return (CogScript.Parse(name, Encoding.Latin1.GetString(bytes.Value.Span)), Hash(bytes.Value.ToArray()));
    }

    /// <summary>One source-bound placed COG's typed binding page, without executing script code.</summary>
    public static object Bindings(string path, string requestText)
    {
        var snapshot = LoadWithProvenance(path);
        var options = ParseExportResources(requestText, snapshot, "cog", "offset", "limit", "bindingResourceRevision")!;
        using var json = JsonDocument.Parse(requestText);
        var root = json.RootElement;
        if (root.TryGetProperty("resourceRevision", out _))
            throw new AgentRequestException("bindings uses bindingResourceRevision for this query's read set. Patch resourceRevision must come from the reviewed plan.");
        int index = Integer(root, "cog");
        int offset = root.TryGetProperty("offset", out _) ? Integer(root, "offset") : 0;
        int limit = root.TryGetProperty("limit", out _) ? Integer(root, "limit") : AgentQueries.DefaultLimit;
        if (offset < 0 || limit is < 1 or > AgentQueries.MaxLimit)
            throw new AgentRequestException("offset must be nonnegative; limit must be 1 through 200.");
        var cog = At(snapshot.Document.Level.Cogs, index, "cog");
        using var resources = new ExplicitArchiveResources(options.Archives);
        var script = ReadCogScript(resources, cog.Name);
        var description = CogBindings.Describe(snapshot.Document, index, script.Script);
        resources.VerifyUnchanged();
        if (root.TryGetProperty("bindingResourceRevision", out _) &&
            ResourceManifest.NormalizeHash(String(root, "bindingResourceRevision"), "bindingResourceRevision") != resources.ResourceRevision)
            throw new AgentConflictException("The binding query's resource revision changed. Inspect the current script and archives again.");
        var selected = description.Rows.Skip(offset).Take(limit).Select(DescribeBindingRow).ToArray();
        object Page(int count) => new
        {
            schemaVersion = 1, snapshot.InputSha256, snapshot.SourceRevision, snapshot.Dependencies,
            bindingResourceRevision = resources.ResourceRevision, archives = resources.Archives,
            usedResources = resources.UsedResources, resourceDiagnostics = resources.Diagnostics,
            cog = index, description.ScriptName, scriptSha256 = script.Sha256,
            description.ScriptStatus, description.SchemaComplete, description.ActualParameterCount,
            description.ExpectedParameterCount, description.ParameterCountMatches, description.Diagnostics,
            description.EventLinkSemantics,
            total = description.Rows.Count, offset, limit, returnedCount = count,
            nextOffset = (long)offset + count < description.Rows.Count ? (int?)(offset + count) : null,
            rows = selected.Take(count).ToArray(),
        };
        for (int count = selected.Length; count >= 0; count--)
        {
            if (count == 0 && selected.Length > 0) break;
            var page = Page(count);
            if (JsonSerializer.SerializeToUtf8Bytes(page, JsonOptions).Length <= AgentQueries.MaxResponseBytes) return page;
        }
        throw new AgentRequestException("One COG binding or its schema/provenance exceeds the 1 MiB response limit; fields were not truncated.");
    }

    private static object DescribeBindingRow(CogBindingRow row) => new
    {
        row.CogIndex, row.ParameterIndex, row.SymbolName, type = row.Type.ToString().ToLowerInvariant(), row.RawValue,
        modifiers = row.Symbol is null ? null : new
        {
            row.Symbol.RawDeclaration, row.Symbol.RawModifiers, row.Symbol.Local, row.Symbol.NoLink,
            row.Symbol.ExplicitLinkId, row.Symbol.ExplicitMask, row.Symbol.EffectiveLinkId, row.Symbol.EffectiveMask,
        },
        target = new { kind = row.Target.Kind.ToString().ToLowerInvariant(), status = row.Target.Status.ToString(),
            row.Target.Index, row.Target.SectorIndex, row.Target.SurfaceIndex, row.Target.Detail },
        row.EventLinkCandidate,
    };

    private static bool TryPrepareCogOperation(JklDocument doc, JsonElement op,
        ExplicitArchiveResources? resources, out IEditCommand? command)
    {
        command = null;
        if (String(op, "op") == "addCog")
        {
            Fields(op, ["op", .. OperationFields["addCog"]]);
            if (resources is null) throw new AgentRequestException("COG creation requires explicit resourceArchives and scriptSha256.");
            string name = Token(op, "script");
            string expectedHash = ResourceManifest.NormalizeHash(String(op, "scriptSha256"), "scriptSha256");
            var resolved = ReadCogScript(resources, name);
            if (resolved.Sha256 != expectedHash)
                throw new AgentConflictException("The COG script is missing or differs from the reviewed bytes.", jsonPointer: "/scriptSha256");
            var rows = op.GetProperty("bindings");
            if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > CogBindings.MaxAuthoredBindings)
                throw FieldError($"bindings must be an array of at most {CogBindings.MaxAuthoredBindings} named references.", "bindings");
            var bindings = new List<CogNamedReference>();
            int rowIndex = 0;
            foreach (var row in rows.EnumerateArray())
            {
                try
                {
                    Fields(row, "symbol", "kind", "targetIndex");
                    bindings.Add(new(Token(row, "symbol"), CogReferenceType(row), Integer(row, "targetIndex")));
                }
                catch (AgentRequestException error)
                {
                    throw new AgentRequestException(error.Message, jsonPointer: $"/bindings/{rowIndex}" + error.JsonPointer);
                }
                rowIndex++;
            }
            try { command = CogBindings.PrepareAdd(doc, name, resolved.Script, bindings,
                existingName => ReadCogScript(resources, existingName).Script); }
            catch (CogBindingException error) { throw new AgentRequestException($"{error.Code}: {error.Message}"); }
            return true;
        }
        if (String(op, "op") != "rebindCogReference") return false;
        Fields(op, ["op", .. OperationFields["rebindCogReference"]]);
        if (resources is null) throw new AgentRequestException("Typed COG edits require explicit resourceArchives and a reviewed scriptSha256 from bindings.");
        int index = Integer(op, "cog");
        var cog = At(doc.Level.Cogs, index, "cog");
        string expected = ResourceManifest.NormalizeHash(String(op, "scriptSha256"), "scriptSha256");
        var script = ReadCogScript(resources, cog.Name);
        if (expected != script.Sha256)
            throw new AgentConflictException("The resolved COG script is missing or differs from the reviewed schema. Query bindings again.", jsonPointer: "/scriptSha256");
        var kind = CogReferenceType(op);
        try { command = CogBindings.PrepareRebind(doc, index, Integer(op, "parameter"),
            Token(op, "symbol"), kind, Integer(op, "targetIndex"), script.Script); }
        catch (CogBindingException error) { throw new AgentRequestException($"{error.Code}: {error.Message}"); }
        return true;
    }

    private static CogSymbolType CogReferenceType(JsonElement value) => String(value, "kind") switch
    {
        "thing" => CogSymbolType.Thing, "sector" => CogSymbolType.Sector,
        "surface" => CogSymbolType.Surface, "cog" => CogSymbolType.Cog,
        _ => throw FieldError("kind must be thing, sector, surface or cog.", "kind"),
    };

    private static object CogOperationEffect(JklDocument doc, JsonElement op, int? created = null)
    {
        int index = created ?? Integer(op, "cog");
        var cog = At(doc.Level.Cogs, index, "cog");
        return new { target = new { kind = "cog", index }, state = new { cog.Name, values = cog.Values.ToArray() } };
    }

    public static object BindingsRequestSchema()
    {
        var schema = (Dictionary<string, object>)ResourceRequestSchema();
        schema["description"] = "Read-only typed bindings for one placed COG. Query cogs first. Scripts are resolved only from explicit hash-bound archives; missing/incomplete schemas leave values untyped. No code execution or inferred gameplay. Pages preserve original parameter indices.";
        schema["required"] = new[] { "schemaVersion", "inputSha256", "resourceArchives", "cog" };
        var properties = (Dictionary<string, object>)schema["properties"];
        properties.Remove("resourceRevision");
        properties["bindingResourceRevision"] = new { type = "string", pattern = "^[0-9a-fA-F]{64}$",
            description = "Optional identity from this same binding query. For a patch, use scriptSha256 plus explicit archives, then copy resourceRevision from its plan; read sets differ." };
        properties["cog"] = new { type = "integer", minimum = 0, maximum = int.MaxValue };
        properties["offset"] = new { type = "integer", minimum = 0, maximum = int.MaxValue, @default = 0 };
        properties["limit"] = new { type = "integer", minimum = 1, maximum = AgentQueries.MaxLimit, @default = AgentQueries.DefaultLimit };
        return schema;
    }
}
