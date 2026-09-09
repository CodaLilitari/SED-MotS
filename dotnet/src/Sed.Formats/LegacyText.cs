using System.Text;

namespace Sed.Formats;

/// <summary>Decode modern UTF-8/BOM files or retain all byte values in legacy Latin-1 text.</summary>
public static class LegacyText
{
    public static string ReadFile(string path) => Decode(File.ReadAllBytes(path));
    public static string Decode(byte[] bytes)
    {
        // StreamReader recognizes UTF BOMs. Strict UTF-8 prevents replacement
        // characters from silently erasing bytes in historical editor files.
        try
        {
            using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, true), true);
            return reader.ReadToEnd();
        }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }
}
