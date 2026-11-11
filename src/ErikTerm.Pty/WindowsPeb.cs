using System.Buffers.Binary;

namespace ErikTerm.Pty;

/// <summary>
/// Where a 64-bit Windows process (x64 or arm64, which share the layout) keeps its command line and current directory:
/// NtQueryInformationProcess(ProcessBasicInformation) → PROCESS_BASIC_INFORMATION.PebBaseAddress → PEB.ProcessParameters
/// → RTL_USER_PROCESS_PARAMETERS.CommandLine / .CurrentDirectory.DosPath, each a UNICODE_STRING whose Buffer is read
/// with ReadProcessMemory. Pure (offsets and parsing of the bytes read), so it is unit-tested on every platform.
/// <para>
/// Sources. Windows SDK <c>winternl.h</c> (documented, with reserved fields): PROCESS_BASIC_INFORMATION is
/// <c>{ PVOID Reserved1; PPEB PebBaseAddress; PVOID Reserved2[2]; ULONG_PTR UniqueProcessId; PVOID Reserved3; }</c>
/// (PebBaseAddress at 0x08, 0x30 bytes); PEB starts <c>BYTE Reserved1[2]; BYTE BeingDebugged; BYTE Reserved2[1];
/// PVOID Reserved3[2]; PPEB_LDR_DATA Ldr; PRTL_USER_PROCESS_PARAMETERS ProcessParameters;</c> (0x20);
/// RTL_USER_PROCESS_PARAMETERS is <c>BYTE Reserved1[16]; PVOID Reserved2[10]; UNICODE_STRING ImagePathName;
/// UNICODE_STRING CommandLine;</c> (ImagePathName 0x60, CommandLine 0x70). The reserved part, which holds
/// CurrentDirectory, is spelled out in ReactOS (<c>sdk/include/ndk/rtltypes.h</c>), System Informer's phnt
/// (<c>ntrtl.h</c>) and Geoff Chappell's RTL_USER_PROCESS_PARAMETERS page: MaximumLength, Length, Flags (0x08),
/// DebugFlags, ConsoleHandle (0x10), ConsoleFlags, StandardInput/Output/Error (0x20/0x28/0x30), CURDIR
/// CurrentDirectory { UNICODE_STRING DosPath (0x38); HANDLE Handle (0x48) }, UNICODE_STRING DllPath (0x50) — which
/// lands ImagePathName at 0x60, matching winternl.h, so the two agree. UNICODE_STRING: USHORT Length (bytes, no NUL),
/// USHORT MaximumLength, PWSTR Buffer at 0x08 (0x10 bytes).
/// </para>
/// </summary>
internal static class WindowsPeb
{
    /// <summary>NtQueryInformationProcess's PROCESSINFOCLASS for PROCESS_BASIC_INFORMATION.</summary>
    public const int ProcessBasicInformation = 0;
    public const int ProcessBasicInformationSize = 0x30;
    public const int PebBaseAddressOffset = 0x08;
    /// <summary>PEB.ProcessParameters (x86 would be 0x10).</summary>
    public const int ProcessParametersOffset = 0x20;
    /// <summary>RTL_USER_PROCESS_PARAMETERS.Flags.</summary>
    public const int FlagsOffset = 0x08;
    /// <summary>RTL_USER_PROCESS_PARAMETERS.CurrentDirectory.DosPath (the CURDIR's first member).</summary>
    public const int CurrentDirectoryOffset = 0x38;
    /// <summary>RTL_USER_PROCESS_PARAMETERS.ImagePathName.</summary>
    public const int ImagePathNameOffset = 0x60;
    /// <summary>RTL_USER_PROCESS_PARAMETERS.CommandLine.</summary>
    public const int CommandLineOffset = 0x70;
    /// <summary>How much of RTL_USER_PROCESS_PARAMETERS is read: through the end of CommandLine.</summary>
    public const int ParametersReadSize = CommandLineOffset + UnicodeStringSize;
    public const int UnicodeStringSize = 0x10;
    public const int UnicodeStringBufferOffset = 0x08;
    /// <summary>
    /// RTL_USER_PROC_PARAMS_NORMALIZED: the Buffer fields are addresses. Without it (parameters not yet normalized by
    /// RtlNormalizeProcessParams) they are offsets from the start of the structure.
    /// </summary>
    public const uint ParamsNormalized = 0x1;

    /// <summary>
    /// The UNICODE_STRING at <paramref name="offset"/> in a copy of RTL_USER_PROCESS_PARAMETERS read from
    /// <paramref name="parametersAddress"/>: where its text is in the process and how many bytes it has. Null when it is
    /// empty, malformed (an odd length) or has no buffer.
    /// </summary>
    public static (ulong Address, int ByteLength)? UnicodeString(ReadOnlySpan<byte> parameters, int offset, ulong parametersAddress)
    {
        if (offset < 0 || parameters.Length < FlagsOffset + 4 || offset + UnicodeStringSize > parameters.Length) return null;
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(parameters[FlagsOffset..]);
        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(parameters[offset..]);
        ulong buffer = BinaryPrimitives.ReadUInt64LittleEndian(parameters[(offset + UnicodeStringBufferOffset)..]);
        if (length == 0 || (length & 1) != 0 || buffer == 0) return null;
        if ((flags & ParamsNormalized) == 0) buffer += parametersAddress;
        return (buffer, length);
    }

    /// <summary>
    /// The current directory as a path: RTL keeps it with a trailing backslash (<c>C:\src\</c>), which is dropped except
    /// for a drive root (<c>C:\</c>).
    /// </summary>
    public static string TrimDirectory(string dosPath) =>
        dosPath.Length > 1 && dosPath[^1] == '\\' && dosPath[^2] is not (':' or '\\') ? dosPath[..^1] : dosPath;
}

/// <summary>The subsystem field of a PE image (PE/COFF specification, "Optional Header Windows-Specific Fields").</summary>
internal static class PeImage
{
    public const ushort SubsystemWindowsGui = 2;
    public const ushort SubsystemWindowsCui = 3;

    /// <summary>How much of the file <see cref="Subsystem"/> wants: the DOS header, the PE signature and headers.</summary>
    public const int HeaderBytes = 4096;

    /// <summary>
    /// The Subsystem of the image whose first bytes are <paramref name="image"/>: "MZ", e_lfanew at 0x3C, "PE\0\0", the
    /// 20-byte COFF header, then the optional header (magic 0x10B PE32 / 0x20B PE32+) with Subsystem at 68 in both.
    /// Null when it isn't a PE image or the headers aren't in the bytes given.
    /// </summary>
    public static ushort? Subsystem(ReadOnlySpan<byte> image)
    {
        if (image.Length < 0x40 || image[0] != 'M' || image[1] != 'Z') return null;
        int pe = BinaryPrimitives.ReadInt32LittleEndian(image[0x3C..]);
        const int optional = 4 + 20, subsystem = optional + 68;
        if (pe < 0x40 || pe > image.Length - (subsystem + 2)) return null;
        if (!image.Slice(pe, 4).SequenceEqual("PE\0\0"u8)) return null;
        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(image[(pe + optional)..]);
        if (magic is not (0x10B or 0x20B)) return null;
        return BinaryPrimitives.ReadUInt16LittleEndian(image[(pe + subsystem)..]);
    }
}
