using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using static ErikTerm.Pty.Win32;

namespace ErikTerm.Pty;

/// <summary>
/// kernel32 bindings for ConPTY (Windows 10 1809+) and for looking at the processes in a pane (Toolhelp snapshots,
/// process times, image paths, reading another process's memory). BOOL is returned as int (nonzero = success)
/// all strings are UTF-16 and passed as pinned char*. Struct layouts follow the Windows SDK headers
/// (natural alignment, so <see cref="LayoutKind.Sequential"/> matches on both 32- and 64-bit).
/// </summary>
[SupportedOSPlatform("windows")]
#pragma warning disable S6640 // Win32 signatures take raw pointers
internal static unsafe partial class Kernel32
#pragma warning restore S6640
{
    private const string Lib = "kernel32.dll";

    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_INVALID_HANDLE = 6;
    public const int ERROR_BROKEN_PIPE = 109;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int ERROR_NO_DATA = 232;
    public const int ERROR_OPERATION_ABORTED = 995;

    public const uint INFINITE = 0xFFFFFFFF;
    public const uint WAIT_OBJECT_0 = 0;
    public const uint WAIT_TIMEOUT = 0x102;

    public const uint PROCESS_VM_READ = 0x0010;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint SYNCHRONIZE = 0x00100000;
    public const uint TH32CS_SNAPPROCESS = 0x00000002;
    public const nint INVALID_HANDLE_VALUE = -1;

    public const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    public const uint STARTF_USESTDHANDLES = 0x00000100;

    /// <summary>ProcThreadAttributeValue(22, FALSE, TRUE, FALSE) = 22 | PROC_THREAD_ATTRIBUTE_INPUT.</summary>
    public const nuint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

    // BOOL CreatePipe(PHANDLE hReadPipe, PHANDLE hWritePipe, LPSECURITY_ATTRIBUTES lpPipeAttributes, DWORD nSize)
    [LibraryImport(Lib, SetLastError = true)]
    public static partial int CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, void* lpPipeAttributes, uint nSize);

    // HRESULT CreatePseudoConsole(COORD size, HANDLE hInput, HANDLE hOutput, DWORD dwFlags, HPCON* phPC)
    [LibraryImport(Lib)]
    public static partial int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out nint phPC);

    // HRESULT ResizePseudoConsole(HPCON hPC, COORD size)
    [LibraryImport(Lib)]
    public static partial int ResizePseudoConsole(nint hPC, COORD size);

    // void ClosePseudoConsole(HPCON hPC)
    [LibraryImport(Lib)]
    public static partial void ClosePseudoConsole(nint hPC);

    // BOOL InitializeProcThreadAttributeList(LPPROC_THREAD_ATTRIBUTE_LIST, DWORD dwAttributeCount, DWORD dwFlags, PSIZE_T lpSize)
    [LibraryImport(Lib, SetLastError = true)]
    public static partial int InitializeProcThreadAttributeList(void* lpAttributeList, uint dwAttributeCount, uint dwFlags, nuint* lpSize);

    // BOOL UpdateProcThreadAttribute(LPPROC_THREAD_ATTRIBUTE_LIST, DWORD dwFlags, DWORD_PTR Attribute,
    //                                PVOID lpValue, SIZE_T cbSize, PVOID lpPreviousValue, PSIZE_T lpReturnSize)
    [LibraryImport(Lib, SetLastError = true)]
    public static partial int UpdateProcThreadAttribute(void* lpAttributeList, uint dwFlags, nuint attribute,
        void* lpValue, nuint cbSize, void* lpPreviousValue, nuint* lpReturnSize);

    // void DeleteProcThreadAttributeList(LPPROC_THREAD_ATTRIBUTE_LIST)
    [LibraryImport(Lib)]
    public static partial void DeleteProcThreadAttributeList(void* lpAttributeList);

    // BOOL CreateProcessW(LPCWSTR lpApplicationName, LPWSTR lpCommandLine, LPSECURITY_ATTRIBUTES, LPSECURITY_ATTRIBUTES,
    //                     BOOL bInheritHandles, DWORD dwCreationFlags, LPVOID lpEnvironment, LPCWSTR lpCurrentDirectory,
    //                     LPSTARTUPINFOW lpStartupInfo, LPPROCESS_INFORMATION lpProcessInformation)
    [LibraryImport(Lib, SetLastError = true)]
    public static partial int CreateProcessW(char* lpApplicationName, char* lpCommandLine, void* lpProcessAttributes,
        void* lpThreadAttributes, int bInheritHandles, uint dwCreationFlags, char* lpEnvironment, char* lpCurrentDirectory,
        STARTUPINFOEXW* lpStartupInfo, PROCESS_INFORMATION* lpProcessInformation);

    // BOOL PeekNamedPipe(HANDLE, LPVOID lpBuffer, DWORD nBufferSize, LPDWORD lpBytesRead, LPDWORD lpTotalBytesAvail,
    //                    LPDWORD lpBytesLeftThisMessage); works on anonymous pipes too.
    [LibraryImport(Lib, SetLastError = true)]
    public static partial int PeekNamedPipe(SafeFileHandle hNamedPipe, void* lpBuffer, uint nBufferSize, uint* lpBytesRead, uint* lpTotalBytesAvail, uint* lpBytesLeftThisMessage);

    // BOOL ReadFile(HANDLE, LPVOID, DWORD nNumberOfBytesToRead, LPDWORD lpNumberOfBytesRead, LPOVERLAPPED)
    [LibraryImport(Lib, SetLastError = true)]
    public static partial int ReadFile(SafeFileHandle hFile, byte* lpBuffer, uint nNumberOfBytesToRead, uint* lpNumberOfBytesRead, void* lpOverlapped);

    // BOOL WriteFile(HANDLE, LPCVOID, DWORD nNumberOfBytesToWrite, LPDWORD lpNumberOfBytesWritten, LPOVERLAPPED)
    [LibraryImport(Lib, SetLastError = true)]
    public static partial int WriteFile(SafeFileHandle hFile, byte* lpBuffer, uint nNumberOfBytesToWrite, uint* lpNumberOfBytesWritten, void* lpOverlapped);

    // BOOL CancelIoEx(HANDLE hFile, LPOVERLAPPED lpOverlapped)
    [LibraryImport(Lib, SetLastError = true)]
    public static partial int CancelIoEx(SafeFileHandle hFile, void* lpOverlapped);

    // BOOL CloseHandle(HANDLE)
    [LibraryImport(Lib, SetLastError = true)]
    public static partial int CloseHandle(nint hObject);

    // DWORD WaitForSingleObject(HANDLE, DWORD dwMilliseconds)
    [LibraryImport(Lib, SetLastError = true)]
    public static partial uint WaitForSingleObject(SafeProcessHandle hHandle, uint dwMilliseconds);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

    // BOOL GetExitCodeProcess(HANDLE, LPDWORD)
    [LibraryImport(Lib, SetLastError = true)]
    public static partial int GetExitCodeProcess(SafeProcessHandle hProcess, uint* lpExitCode);

    // BOOL TerminateProcess(HANDLE, UINT)
    [LibraryImport(Lib, SetLastError = true)]
    public static partial int TerminateProcess(SafeProcessHandle hProcess, uint uExitCode);

    // HANDLE OpenProcess(DWORD dwDesiredAccess, BOOL bInheritHandle, DWORD dwProcessId); NULL on failure.
    [LibraryImport(Lib, SetLastError = true)]
    public static partial nint OpenProcess(uint dwDesiredAccess, int bInheritHandle, uint dwProcessId);

    // BOOL GetProcessTimes(HANDLE, LPFILETIME lpCreationTime, LPFILETIME lpExitTime, LPFILETIME lpKernelTime, LPFILETIME lpUserTime)
    [LibraryImport(Lib, SetLastError = true)]
    public static partial int GetProcessTimes(nint hProcess, long* lpCreationTime, long* lpExitTime, long* lpKernelTime, long* lpUserTime);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int GetProcessTimes(SafeProcessHandle hProcess, long* lpCreationTime, long* lpExitTime, long* lpKernelTime, long* lpUserTime);

    // BOOL QueryFullProcessImageNameW(HANDLE hProcess, DWORD dwFlags, LPWSTR lpExeName, PDWORD lpdwSize)
    // dwFlags 0: a Win32 path. *lpdwSize: the buffer's size in characters in, the length without the NUL out.
    [LibraryImport(Lib, SetLastError = true)]
    public static partial int QueryFullProcessImageNameW(nint hProcess, uint dwFlags, char* lpExeName, uint* lpdwSize);

    // BOOL IsWow64Process(HANDLE hProcess, PBOOL Wow64Process)
    [LibraryImport(Lib, SetLastError = true)]
    public static partial int IsWow64Process(nint hProcess, int* wow64Process);

    // BOOL ReadProcessMemory(HANDLE hProcess, LPCVOID lpBaseAddress, LPVOID lpBuffer, SIZE_T nSize, SIZE_T* lpNumberOfBytesRead)
    [LibraryImport(Lib, SetLastError = true)]
    public static partial int ReadProcessMemory(nint hProcess, void* lpBaseAddress, void* lpBuffer, nuint nSize, nuint* lpNumberOfBytesRead);

    // HANDLE CreateToolhelp32Snapshot(DWORD dwFlags, DWORD th32ProcessID); INVALID_HANDLE_VALUE on failure.
    [LibraryImport(Lib, SetLastError = true)]
    public static partial nint CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    // BOOL Process32FirstW(HANDLE hSnapshot, LPPROCESSENTRY32W lppe); dwSize must be set.
    [LibraryImport(Lib, SetLastError = true)]
    public static partial int Process32FirstW(nint hSnapshot, PROCESSENTRY32W* lppe);

    // BOOL Process32NextW(HANDLE hSnapshot, LPPROCESSENTRY32W lppe); FALSE with ERROR_NO_MORE_FILES at the end.
    [LibraryImport(Lib, SetLastError = true)]
    public static partial int Process32NextW(nint hSnapshot, PROCESSENTRY32W* lppe);
}

/// <summary>ntdll: NtQueryInformationProcess, for the PEB address (see <see cref="WindowsPeb"/>) and the console host.</summary>
[SupportedOSPlatform("windows")]
#pragma warning disable S6640 // the ntdll signatures take raw pointers
internal static unsafe partial class Ntdll
#pragma warning restore S6640
{
    // NTSTATUS NtQueryInformationProcess(HANDLE ProcessHandle, PROCESSINFOCLASS ProcessInformationClass,
    //                                    PVOID ProcessInformation, ULONG ProcessInformationLength, PULONG ReturnLength)
    // ProcessBasicInformation needs PROCESS_QUERY_LIMITED_INFORMATION. A negative NTSTATUS is an error.
    [LibraryImport("ntdll.dll")]
    public static partial int NtQueryInformationProcess(nint processHandle, int processInformationClass, void* processInformation,
        uint processInformationLength, uint* returnLength);

    // The same for a SafeHandle: the marshaller keeps the handle alive (AddRef / Release) for the call.
    [LibraryImport("ntdll.dll")]
    public static partial int NtQueryInformationProcess(SafeProcessHandle processHandle, int processInformationClass, void* processInformation,
        uint processInformationLength, uint* returnLength);
}

/// <summary>
/// Win32 structs used by <see cref="Kernel32"/>. Kept outside the Windows-only class because they are
/// plain data (and their layouts are unit-tested on every platform).
/// </summary>
internal static class Win32
{
    [StructLayout(LayoutKind.Sequential)]
#pragma warning disable S101 // keeps the name from the Windows headers, so it matches the Win32 documentation
    public struct COORD
#pragma warning restore S101
    {
        public short X, Y;
    }

    /// <summary>STARTUPINFOW: 104 bytes on 64-bit, 68 on 32-bit.</summary>
    [StructLayout(LayoutKind.Sequential)]
#pragma warning disable S101 // keeps the name from the Windows headers, so it matches the Win32 documentation
    public struct STARTUPINFOW
#pragma warning restore S101
    {
        public uint cb;
        public nint lpReserved;
        public nint lpDesktop;
        public nint lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public nint lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
    }

    /// <summary>STARTUPINFOEXW: 112 bytes on 64-bit, 72 on 32-bit.</summary>
    [StructLayout(LayoutKind.Sequential)]
#pragma warning disable S101 // keeps the name from the Windows headers, so it matches the Win32 documentation
    public struct STARTUPINFOEXW
#pragma warning restore S101
    {
        public STARTUPINFOW StartupInfo;
        public nint lpAttributeList;
    }

    /// <summary>PROCESSENTRY32W (tlhelp32.h): 568 bytes on 64-bit (szExeFile at 44), 556 on 32-bit (at 36).</summary>
    [StructLayout(LayoutKind.Sequential)]
#pragma warning disable S101, S6640 // keeps the name from the Windows headers, so it matches the Win32 documentation; unsafe for its fixed szExeFile buffer, part of the native layout
    public unsafe struct PROCESSENTRY32W
#pragma warning restore S101, S6640
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nuint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        public fixed char szExeFile[MAX_PATH];
    }

    public const int MAX_PATH = 260;

    /// <summary>PROCESS_BASIC_INFORMATION (winternl.h): 48 bytes on 64-bit, PebBaseAddress at 8 (<see cref="WindowsPeb"/>).</summary>
    [StructLayout(LayoutKind.Sequential)]
#pragma warning disable S101 // keeps the name from the Windows headers, so it matches the Win32 documentation
    public struct PROCESS_BASIC_INFORMATION
#pragma warning restore S101
    {
        public int ExitStatus;
        public nint PebBaseAddress;
        public nuint AffinityMask;
        public int BasePriority;
        public nuint UniqueProcessId;
        public nuint InheritedFromUniqueProcessId;
    }

    /// <summary>PROCESS_INFORMATION: 24 bytes on 64-bit, 16 on 32-bit.</summary>
    [StructLayout(LayoutKind.Sequential)]
#pragma warning disable S101 // keeps the name from the Windows headers, so it matches the Win32 documentation
    public struct PROCESS_INFORMATION
#pragma warning restore S101
    {
        public nint hProcess;
        public nint hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }
}
