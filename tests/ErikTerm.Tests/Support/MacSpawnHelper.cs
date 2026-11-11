using System.Runtime.CompilerServices;

namespace ErikTerm.Tests;

/// <summary>
/// macOS: a pty child starts as a helper that takes the tty as its controlling terminal and then becomes the program
/// (ErikTerm.Pty/UnixPty.Mac.cs). In the app the helper is erikterm itself; here it is this test executable, which
/// runs the helper and exits before the test framework starts when its arguments say so.
/// </summary>
internal static class MacSpawnHelper
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (!OperatingSystem.IsMacOS()) return;
        string[] args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, Pty.Pty.SpawnHelperArgument);
        if (at >= 0) Environment.Exit(Pty.Pty.RunSpawnHelper(args.AsSpan(at + 1)));
        if (Environment.ProcessPath is not { } exe) return;
        // `dotnet test` starts the test app's own executable, or `dotnet ErikTerm.Tests.dll`: the helper is started the same way.
        if (Path.GetFileNameWithoutExtension(exe) == "dotnet") Pty.Pty.UseSpawnHelper(exe, typeof(MacSpawnHelper).Assembly.Location);
        else Pty.Pty.UseSpawnHelper(exe);
    }
}
