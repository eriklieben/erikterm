using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ErikTerm.Tests;

/// <summary>
/// CPU time the calling thread has used — what a performance bound should measure. Wall-clock time also counts the
/// time the thread sat waiting for a core, so a busy machine (other test processes, a parallel build) makes a fast
/// algorithm look slow. Falls back to wall-clock time where the OS call isn't available.
/// </summary>
internal static partial class ThreadCpuTime
{
    public static TimeSpan Now()
    {
        if (OperatingSystem.IsLinux())
        {
            const int ClockThreadCpuTimeId = 3;
            if (clock_gettime(ClockThreadCpuTimeId, out var ts) == 0)
                return TimeSpan.FromTicks(ts.Seconds * TimeSpan.TicksPerSecond + ts.Nanoseconds / 100);
        }
        else if (OperatingSystem.IsWindows())
        {
            if (GetThreadTimes(GetCurrentThread(), out _, out _, out long kernel, out long user))
                return TimeSpan.FromTicks(kernel + user); // 100 ns units
        }
        return Stopwatch.GetElapsedTime(0);
    }

    /// <summary>Runs <paramref name="work"/> and returns the CPU time this thread spent on it.</summary>
    public static TimeSpan Measure(Action work)
    {
        var start = Now();
        work();
        return Now() - start;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TimeSpec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    [LibraryImport("libc.so.6", EntryPoint = "clock_gettime")]
    private static partial int clock_gettime(int clockId, out TimeSpec ts);

    [LibraryImport("kernel32")]
    private static partial nint GetCurrentThread();

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetThreadTimes(nint thread, out long creation, out long exit, out long kernel, out long user);
}
