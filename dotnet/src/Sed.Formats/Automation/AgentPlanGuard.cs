using System.Security.Cryptography;
using System.Text;

namespace Sed.Formats.Automation;

public static partial class AgentWorkspace
{
    /// <summary>Commit only if recomputing a reviewed patch produces exactly its planned output bytes.</summary>
    public static object Commit(PreparedAgentEdit edit, string outputPath, string? expectedOutputSha256,
        CancellationToken cancellationToken = default)
    {
        if (expectedOutputSha256 is not null)
        {
            if (expectedOutputSha256.Length != 64 || !expectedOutputSha256.All(Uri.IsHexDigit))
                throw new AgentRequestException("expected-output-sha256 must contain exactly 64 hexadecimal digits.");
            string actual = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(edit.OutputText)));
            if (!string.Equals(actual, expectedOutputSha256, StringComparison.OrdinalIgnoreCase))
                throw new AgentConflictException("The recomputed output differs from the reviewed plan. Plan again and review its outputSha256.");
        }
        return Commit(edit, outputPath, cancellationToken);
    }
}
