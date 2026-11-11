using System.Net.Sockets;
using ErikTerm.App.Tmux;

namespace ErikTerm.App.Ipc;

/// <summary>
/// The control socket behind <c>erikterm cli</c>, one per process. The server reads requests on pool threads and
/// posts them to a queue; the event loop drains the queue on the main thread (where all layout/UI state lives), and
/// each reply goes out once its request has run. Panes get the socket in their environment, and the tmux emulation's
/// shim directory lives and goes with it (<see cref="TmuxShimDirectory"/>).
/// </summary>
/// <param name="wake">Wakes the event loop when a request arrives (a pool thread).</param>
internal sealed class ControlSocket(Action wake)
{
    private readonly TmuxShimDirectory _tmuxShim = new();
    private IpcServer? _server;
    private RequestQueue<IpcRequest, IpcResponse>? _queue;
    private string? _socket;
    private string _dir = "";
    private bool _focusMarked;

    /// <summary>The socket's path; null when there is none.</summary>
    public string? SocketPath => _socket;
    /// <summary>Whether the tmux emulation can work in this process (shim directory and socket).</summary>
    public bool TmuxShimAvailable => _tmuxShim.ShimDir is not null && _socket is not null;
    /// <summary>Requests can arrive: <see cref="Drain"/> has something to do.</summary>
    public bool Running => _queue is not null;

    /// <summary>Hook from Run, after SDL is up and before the first pane spawns (panes get the socket in their environment).</summary>
    public void Start() => Start(() => IpcPaths.Directory());

    /// <summary><see cref="Start()"/> in the directory <paramref name="directory"/> gives (tests: a private one).</summary>
    internal void Start(Func<string> directory)
    {
        try
        {
            _dir = directory();
            string path = IpcPaths.SocketFor(_dir, Environment.ProcessId);
            var queue = new RequestQueue<IpcRequest, IpcResponse>(wake);
            var server = new IpcServer(path, queue.Post);
            server.Start();
            (_server, _queue, _socket) = (server, queue, path);
            _tmuxShim.Start(_dir);
        }
        catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException or PlatformNotSupportedException or ArgumentException)
        {
            Console.Error.WriteLine($"erikterm: no control socket ('erikterm cli' won't reach this instance): {ex.Message}");
        }
    }

    /// <summary>
    /// Hook from the event loop: keeps the last-focused marker current (<paramref name="focused"/>: one of this
    /// process's windows has the focus) and runs pending requests through <paramref name="handle"/>.
    /// </summary>
    public void Drain(bool focused, Func<IpcRequest, IpcResponse> handle)
    {
        if (_queue is null) return;
        if (focused != _focusMarked)
        {
            _focusMarked = focused;
            if (focused)
            {
                try { IpcPaths.WriteMarker(_dir, _socket!); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the marker only picks the CLI's default instance; the next focus change writes it again */ }
            }
        }
        if (_queue.Count > 0) _queue.Drain(handle);
    }

    /// <summary>Hook from Run's cleanup.</summary>
    public void Stop()
    {
        _queue?.Close();
        _server?.Dispose();
        _tmuxShim.Stop();
        if (_socket is not null) IpcPaths.ClearMarker(_dir, _socket);
        _server = null;
        _queue = null;
    }

    /// <summary>
    /// ERIKTERM_SOCKET / ERIKTERM_PANE for pane <paramref name="paneId"/>, so `erikterm cli` in the pane targets it,
    /// and the tmux emulation's variables when <paramref name="tmuxEmulation"/> is on for the window.
    /// </summary>
    public void AddEnvironment(Dictionary<string, string> env, bool tmuxEmulation, int paneId)
    {
        _tmuxShim.AddEnvironment(env, paneId, tmuxEmulation, _socket);
        if (_socket is null)
        {
            // Don't let a pane think it belongs to the erikterm that launched us.
            env.Remove(IpcPaths.SocketVariable);
            env.Remove(IpcPaths.PaneVariable);
            return;
        }
        env[IpcPaths.SocketVariable] = _socket;
        env[IpcPaths.PaneVariable] = paneId.ToString();
    }
}
