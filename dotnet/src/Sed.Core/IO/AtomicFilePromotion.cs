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
        // Darwin permits a case-only rename of the same object even with
        // RENAME_EXCL. Such an alias is not a distinct new output. This is a
        // conservative Mac refusal even on a case-sensitive volume; parent
        // directory comparison remains ordinal there.
        var sameFileComparison = OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : comparison;
        if (string.Equals(staged, destination, sameFileComparison) ||
            !string.Equals(Path.GetDirectoryName(staged), Path.GetDirectoryName(destination), comparison))
            throw new IOException("Atomic output creation requires distinct staged and destination files in the same directory.");

        if (OperatingSystem.IsMacOS())
        {
            // The filesystem must support exclusive rename. Do not fall back
            // to ordinary rename or a copy if the primitive is unavailable.
            // https://github.com/apple-oss-distributions/xnu/blob/main/bsd/man/man2/rename.2
            try
            {
                if (RenameExclusive(staged, destination, Exclusive) == 0) return;
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
            {
                throw new IOException("Atomic output creation requires macOS renamex_np support; no fallback was attempted.", error);
            }

            int macError = Marshal.GetLastPInvokeError();
            throw new IOException($"Could not atomically create '{destination}' without replacement; no fallback was attempted.",
                new Win32Exception(macError));
        }

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
    private const uint Exclusive = 4; // Darwin RENAME_EXCL

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "renamex_np", SetLastError = true, ExactSpelling = true)]
    private static extern int RenameExclusive([MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath, uint flags);

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true, ExactSpelling = true)]
    private static extern int RenameNoReplace(int oldDirectory,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath, int newDirectory,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath, uint flags);
}
