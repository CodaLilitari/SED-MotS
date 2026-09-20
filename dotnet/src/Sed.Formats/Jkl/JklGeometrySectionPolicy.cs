namespace Sed.Formats.Jkl;

/// <summary>Section capabilities required before geometry is regenerated.</summary>
internal static class JklGeometrySectionPolicy
{
    private static readonly string[] SupportedSections =
    [
        "COPYRIGHT", "HEADER", "GEORESOURCE", "SECTORS", "THINGS", "MATERIALS", "TEMPLATES", "LIGHTS",
        "LAYERS", "COGS", "ARCHLIGHTING", "MODELS", "SPRITES", "SOUNDS", "COGSCRIPTS", "KEYFRAMES",
        "ANIMCLASS", "AICLASS", "SOUNDCLASS",
    ];

    internal static void EnsureSupported(JklDocument document)
    {
        // Serialized index assignment cannot establish the meaning of opaque
        // references. Reject their sections even when current indices happen
        // to remain stable; those sections can impose other geometry contracts.
        bool hasSourceLights = false;
        foreach (string raw in document.SourceLines)
        {
            string header = raw.Split('#')[0].Trim();
            if (!header.StartsWith("SECTION:", StringComparison.OrdinalIgnoreCase)) continue;
            var fields = JklReader.Tokens(header);
            // The reader takes the next token as a name whereas the writer
            // identifies the complete suffix. Do not append a second section
            // when an extra token or missing separator makes those disagree.
            if (fields.Length != 2 || !fields[0].Equals("SECTION:", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Geometry authoring refuses malformed section header '{header}': expected exactly SECTION: followed by one section name.");
            if (fields[1].Equals(SourceLightMetadataFormat.SectionName, StringComparison.OrdinalIgnoreCase))
            {
                hasSourceLights = true;
                continue;
            }
            if (!SupportedSections.Contains(fields[1], StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Geometry authoring refuses unmodeled section '{fields[1]}' because it may contain topology references. No opaque references were remapped.");
        }

        if (!hasSourceLights) return;
        // The extension binds ordinary light rows, not geometry. Recognize it
        // only after successful parsing, then recheck retained source text so
        // a changed public SourceLines array cannot acquire this capability.
        if (document.OriginalSourceLightTextHash is null ||
            !document.OriginalSections.ContainsKey(SourceLightMetadataFormat.SectionName))
            throw new InvalidOperationException("Geometry authoring requires validated source-light metadata. Reopen the document before regenerating geometry.");
        SourceLightMetadataFormat.ValidateRetainedSource(document);
    }
}
