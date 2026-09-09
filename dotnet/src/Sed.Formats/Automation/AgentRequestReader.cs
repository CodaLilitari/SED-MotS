using System.Text;

namespace Sed.Formats.Automation;

/// <summary>Bounded UTF-8 request transport shared by files and redirected stdin.</summary>
public static class AgentRequestReader
{
    public static string Read(Stream input)
    {
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[8192];
        while (true)
        {
            int count = input.Read(buffer, 0, Math.Min(buffer.Length, AgentWorkspace.MaxRequestBytes + 1 - (int)bytes.Length));
            if (count == 0) break;
            bytes.Write(buffer, 0, count);
            if (bytes.Length > AgentWorkspace.MaxRequestBytes)
                throw new AgentRequestException("JSON request exceeds 1 MiB.");
        }
        var content = bytes.GetBuffer().AsSpan(0, (int)bytes.Length);
        if (content.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) content = content[3..];
        try { return new UTF8Encoding(false, true).GetString(content); }
        catch (DecoderFallbackException) { throw new AgentRequestException("JSON requests must use valid UTF-8 encoding."); }
    }
}
