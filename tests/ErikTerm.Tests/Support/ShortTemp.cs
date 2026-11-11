namespace ErikTerm.Tests;

/// <summary>
/// Temp directories for tests that put Unix sockets in them. macOS's per-user temp directory
/// (<c>/var/folders/xx/…/T/</c>) is long enough that a socket path in it passes the 104-byte <c>sun_path</c>
/// limit, so there the directory goes under <c>/tmp</c>; elsewhere the usual temp directory is short enough.
/// </summary>
internal static class ShortTemp
{
    public static string Path(string prefix) =>
        System.IO.Path.Combine(OperatingSystem.IsMacOS() ? "/tmp" : System.IO.Path.GetTempPath(),
            prefix + Guid.NewGuid().ToString("N")[..12]);
}
