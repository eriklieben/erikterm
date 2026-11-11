using System.Runtime.InteropServices;
using Demos;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

// demo COMMAND ARGS: the README demo tooling (scripts/demos/README.md).
//   world WORLD_DIR ERIKTERM_BIN_DIR         build the demo world (World.cs)
//   steps CLIP.steps FRAMES_DIR              a clip's steps -> an ERIKTERM_DEBUG_SCRIPT_FILE (Steps.cs)
//   assemble FRAMES_DIR OUT_STEM [options]   recorded frames -> OUT.webp, OUT.gif, OUT.png (Assemble.cs)
//   helper TASK                              the fake coding agent, inside the demo world (Agent.cs)
//   dotnet build|test|run ...                the stand-in dotnet, inside the demo world (FakeDotnet.cs)
// Run through a link named helper or dotnet (the demo world's ~/.local/bin), the link's name is the command.
string invokedAs = Native.InvokedAs();
if (invokedAs is "helper" or "dotnet") args = [invokedAs, .. args];
if (args.Length == 0)
{
    Console.Error.WriteLine("usage: demo world|steps|assemble|helper|dotnet ARGS");
    return 2;
}
string[] rest = args[1..];
switch (args[0])
{
    case "world": return World.Run(rest);
    case "steps": return Steps.Run(rest);
    case "assemble": return Assemble.Run(rest);
    case "helper":
        Native.SetProcessName("helper"); // a terminal that shows the process name shows "helper", not "demo"
        return Agent.Run(rest);
    case "dotnet":
        Native.SetProcessName("dotnet");
        return FakeDotnet.Run(rest);
    default:
        Console.Error.WriteLine($"demo: unknown command {args[0]}");
        return 2;
}

namespace Demos
{
    internal static partial class Native
    {
        private const int PrSetName = 15;

        [LibraryImport("libc", EntryPoint = "prctl", StringMarshalling = StringMarshalling.Utf8)]
        private static partial int Prctl(int option, string arg2, nint arg3, nint arg4, nint arg5);

        /// <summary>The file name this process was started as (argv[0], before the apphost resolves links).</summary>
        public static string InvokedAs()
        {
            try
            {
                byte[] raw = File.ReadAllBytes("/proc/self/cmdline");
                int end = Array.IndexOf(raw, (byte)0);
                return Path.GetFileName(System.Text.Encoding.UTF8.GetString(raw, 0, end < 0 ? raw.Length : end));
            }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
        }

        /// <summary>The name in /proc/self/comm (Linux; ignored elsewhere).</summary>
        public static void SetProcessName(string name)
        {
            if (!OperatingSystem.IsLinux()) return;
            try { Prctl(PrSetName, name, 0, 0, 0); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }
    }
}
