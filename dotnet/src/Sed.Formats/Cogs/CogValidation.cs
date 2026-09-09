using Sed.Core.Validation;

namespace Sed.Formats.Cogs;

/// <summary>Supplies script-derived types to the core validator without coupling Core to archive I/O.</summary>
public static class CogValidation
{
    public static ConsistencyOptions Options(CogScriptLibrary library, bool retailProfile = false) => new()
    {
        RetailProfile = retailProfile,
        ResolveCogParameters = cog => library.Get(cog.Name)?.LevelValues.Select(s => new CogParameterDefinition(s.Name, s.Type switch
        {
            CogSymbolType.Cog => CogReferenceKind.Cog,
            CogSymbolType.Thing => CogReferenceKind.Thing,
            CogSymbolType.Sector => CogReferenceKind.Sector,
            CogSymbolType.Surface => CogReferenceKind.Surface,
            _ => CogReferenceKind.Other,
        })).ToArray(),
    };
}
