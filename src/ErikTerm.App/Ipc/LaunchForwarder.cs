using System.Net.Sockets;
using ErikTerm.Config;

namespace ErikTerm.App.Ipc;

/// <summary>
/// single_instance, the launching side: a second <c>erikterm</c> (no --new-process) asks a running one for a window
/// instead of starting a process. The running instance only accepts it with the same config file, --set overrides and
/// --class (IpcRequests.NewWindow); otherwise, or when none answers, this launch starts its own process.
/// </summary>
internal static class LaunchForwarder
{
    /// <summary>Hands this launch to a running erikterm. True when one took it.</summary>
    public static bool TryForward(string[]? command, string configPath, IReadOnlyList<ConfigOverride> overrides, string? windowClass)
    {
        string dir;
        try { dir = IpcPaths.Directory(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        var request = Request(command, Environment.CurrentDirectory, configPath, overrides, windowClass);
        List<string> sockets;
        try { sockets = IpcPaths.LiveSockets(dir); }
        catch (ArgumentException) { return false; } // a socket path too long for the platform
        foreach (string socket in sockets)
        {
            try
            {
                var response = IpcClient.Send(socket, request, TimeSpan.FromSeconds(3));
                if (response.Ok)
                {
                    Console.Error.WriteLine($"erikterm: opened window {response.WindowId} in the running instance ({socket}); --new-process starts a separate one");
                    return true;
                }
            }
            catch (Exception ex) when (ex is SocketException or IOException or TimeoutException or ArgumentException) { /* no usable instance to forward to: the caller starts its own */ }
        }
        return false;
    }

    /// <summary>The <c>new-window</c> request a launch sends: what to run, where, and what the instance must match.</summary>
    internal static IpcRequest Request(string[]? command, string cwd, string configPath, IReadOnlyList<ConfigOverride> overrides, string? windowClass) => new()
    {
        Command = "new-window",
        Argv = command,
        Cwd = cwd,
        ConfigFile = configPath,
        Overrides = overrides.Count > 0 ? overrides.Select(o => o.Text).ToArray() : null,
        WindowClass = windowClass,
    };
}
