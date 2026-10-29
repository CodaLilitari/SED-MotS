using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Sed.Core.IO;

/// <summary>Promotes a completely written sibling file without replacing an existing destination.</summary>
public static class AtomicFilePromotion
{
    public static void MoveNew(string stagedPath, string destinationPath)
    {
        string staged = Path.GetFullPath(stagedPath), destination = Path.GetFullPath(destinationPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(staged, destination, comparison) ||
            !string.Equals(Path.GetDirectoryName(staged), Path.GetDirectoryName(destination), comparison))
            throw new IOException("Atomic output creation requires distinct staged and destination files in the same directory.");

        if (!OperatingSystem.IsLinux())
        {
            File.Move(staged, destination, overwrite: false);
            return;
        }

        // .NET's Unix File.Move(false) can check that the destination is absent
        // and then use rename(), which replaces a competing creator's output.
        // Linux RENAME_NOREPLACE performs both decisions in one filesystem
        // operation. Never fall back to a check/rename or a partial-file copy.
        // https://man7.org/linux/man-pages/man2/rename.2.html
        try
        {
            if (RenameNoReplace(CurrentDirectory, staged, CurrentDirectory, destination, NoReplace) == 0) return;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new IOException("Atomic output creation requires Linux renameat2 support; no fallback was attempted.", error);
        }

        int errno = Marshal.GetLastPInvokeError();
        throw new IOException($"Could not atomically create '{destination}' without replacement; no fallback was attempted.",
            new Win32Exception(errno));
    }

    private const int CurrentDirectory = -100; // AT_FDCWD
    private const uint NoReplace = 1; // RENAME_NOREPLACE

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true, ExactSpelling = true)]
    private static extern int RenameNoReplace(int oldDirectory,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath, int newDirectory,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath, uint flags);
}
