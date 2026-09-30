using System.Security.Cryptography;
using System.Text.Json;

namespace Sed.App;

/// <summary>Local process handoff acknowledgement, outside the immutable prepared installation.</summary>
internal sealed record UpdateHandoff(string Version, string Nonce, string? RecoverySha256)
{
    public static UpdateHandoff Expected(string readyPath, string version, string? recoveryPath)
    {
        string? hash = null;
        if (recoveryPath is not null)
        {
            using var input = File.OpenRead(recoveryPath);
            hash = Convert.ToHexString(SHA256.HashData(input));
        }
        return new(version, Path.GetFileNameWithoutExtension(readyPath), hash);
    }

    public void Write(string path)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(output, this);
            File.Move(temporary, path, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public bool Matches(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > 4096 || (info.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        return JsonSerializer.Deserialize<UpdateHandoff>(File.ReadAllBytes(path)) == this;
    }
}
