using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ErikTerm.App.Native;

/// <summary>File-system calls Windows has and .NET doesn't expose.</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsFiles
{
    /// <summary>
    /// A hard link at <paramref name="link"/> for <paramref name="existing"/>: a second directory entry for the same
    /// file, so it costs no disk space and <c>CreateProcess</c> sees an ordinary executable. Unlike a symbolic link
    /// it needs neither Developer Mode nor elevation, but both paths must be on the same NTFS volume. False when
    /// Windows refuses (another volume, FAT/exFAT, no permission); the caller falls back to copying.
    /// </summary>
    public static bool CreateHardLink(string link, string existing)
    {
        try
        {
            return CreateHardLinkW(link, existing, 0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [LibraryImport("kernel32", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLinkW(string link, string existing, nint securityAttributes);
}
