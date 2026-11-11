using System.Runtime.InteropServices;

namespace ErikTerm.Pty;

/// <summary>
/// NUL-terminated UTF-8 copies of strings in native memory, for the argv and envp arrays of execve and posix_spawn.
/// <see cref="Dispose"/> frees every copy <see cref="Add"/> made.
/// </summary>
#pragma warning disable S6640 // Native interop: hands out the char* strings execve and posix_spawn take in argv and envp
public sealed unsafe class NativeUtf8Strings : IDisposable
#pragma warning restore S6640
{
    private readonly List<nint> _copies = [];

    /// <summary>A native UTF-8 copy of <paramref name="s"/>, valid until <see cref="Dispose"/>.</summary>
    public byte* Add(string s)
    {
        nint p = Marshal.StringToCoTaskMemUTF8(s);
        _copies.Add(p);
        return (byte*)p;
    }

    public void Dispose()
    {
        foreach (nint p in _copies) Marshal.FreeCoTaskMem(p);
        _copies.Clear();
    }
}
