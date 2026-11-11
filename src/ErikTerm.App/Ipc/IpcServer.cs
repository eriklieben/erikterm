using System.Net.Sockets;
using System.Text;

namespace ErikTerm.App.Ipc;

/// <summary>
/// Listens on a Unix domain socket and answers newline-delimited JSON requests. Each connection may
/// send any number of requests; each gets one response line, in order. Socket I/O happens on pool
/// threads; <paramref name="handler"/> decides where the work runs (the app posts to its
/// <see cref="RequestQueue{TRequest,TResponse}"/> so it runs on the main thread). The socket file is
/// created 0600 (in a 0700 directory) and removed on <see cref="Dispose"/>.
/// </summary>
internal sealed class IpcServer(string path, Func<IpcRequest, Task<IpcResponse>> handler) : IDisposable
{
    /// <summary>Requests longer than this are refused (and the connection closed).</summary>
    public const int MaxLineBytes = 16 << 20;

    private Socket? _listener;
    private readonly CancellationTokenSource _stop = new();
    private bool _disposed;

    public string Path { get; } = path;

    public void Start()
    {
        IpcPaths.CheckSocketPath(Path); // an IOException (which StartIpc reports), not an ArgumentOutOfRangeException from Bind
        if (System.IO.Path.GetDirectoryName(Path) is { Length: > 0 } dir) IpcPaths.EnsureDirectory(dir);
        // A file left by a crashed instance with our pid (pids get reused) is ours to replace.
        if (File.Exists(Path) && !IpcPaths.IsAlive(Path)) File.Delete(Path);

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(Path));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen(16);
        }
        catch
        {
            listener.Dispose();
            throw;
        }
        _listener = listener;
        var token = _stop.Token; // taken here: the loop may first run after Dispose
        _ = Task.Run(() => AcceptLoop(token), CancellationToken.None);
    }

    private async Task AcceptLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener!.AcceptAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = Task.Run(() => Serve(client, token), CancellationToken.None); // Serve must run even when stopping: it owns the client socket
        }
    }

    private async Task Serve(Socket client, CancellationToken token)
    {
        using var _ = client;
        await using var stream = new NetworkStream(client, ownsSocket: false);
        var reader = new LineReader(stream);
        Task<string?>? nextLine = null; // already being read: the way to notice a client leaving during a late reply
        try
        {
            while (!token.IsCancellationRequested)
            {
                string? line = await (nextLine ?? ReadLine(reader, token)).ConfigureAwait(false);
                nextLine = null;
                if (line is null) return;
                if (line.Length == 0) continue;

                IpcResponse response = await Respond(line).ConfigureAwait(false);
                if (response.Later is { } later)
                {
                    nextLine = ReadLine(reader, token);
                    if (await ClientLeft(later, nextLine).ConfigureAwait(false)) return;
                    response = await later.Task.ConfigureAwait(false);
                }
                byte[] bytes = Encoding.UTF8.GetBytes(IpcJson.Encode(response) + "\n");
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidDataException)
        {
            // Client went away or sent garbage: drop the connection.
        }
    }

    private static Task<string?> ReadLine(LineReader reader, CancellationToken token) => reader.ReadLineAsync(token).AsTask();

    /// <summary>Waits for a late reply; true (and the reply is cancelled) when the client hung up first.</summary>
    private static async Task<bool> ClientLeft(TaskCompletionSource<IpcResponse> later, Task<string?> nextLine)
    {
        await Task.WhenAny(later.Task, nextLine).ConfigureAwait(false);
        if (!nextLine.IsCompleted || nextLine is { Status: TaskStatus.RanToCompletion, Result: not null }) return false; // no, or it just sent another request
        later.TrySetCanceled();
        return true;
    }

    private async Task<IpcResponse> Respond(string line)
    {
        if (IpcJson.DecodeRequest(line) is not { } request)
            return IpcResponse.Failure("malformed request (expected one JSON object per line)");
        try
        {
            return await handler(request).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return IpcResponse.Failure("erikterm is shutting down");
        }
        catch (Exception ex)
        {
            return IpcResponse.Failure(ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        _stop.Dispose();
        _listener?.Dispose();
        _listener = null;
        try { File.Delete(Path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* nothing listens on the leftover file any more */ }
    }
}

/// <summary>Reads '\n'-terminated UTF-8 lines with a length cap (StreamReader has none).</summary>
internal sealed class LineReader(Stream stream, int maxBytes = IpcServer.MaxLineBytes)
{
    private readonly byte[] _buffer = new byte[8192];
    private int _start, _end;
    private readonly MemoryStream _line = new();

    /// <summary>The next line without its terminator, or null at end of stream (a final unterminated line is returned first).</summary>
    public async ValueTask<string?> ReadLineAsync(CancellationToken token = default)
    {
        while (true)
        {
            int nl = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            if (nl >= 0)
            {
                KeepBuffered(nl);
                _start = nl + 1;
                return Take();
            }
            KeepBuffered(_end);
            _start = _end = 0;
            if (_line.Length > maxBytes) throw new InvalidDataException("line too long");
            int n = await stream.ReadAsync(_buffer, token).ConfigureAwait(false);
            if (n == 0) return _line.Length > 0 ? Take() : null;
            _end = n;
        }
    }

    /// <summary>Memory only, so nothing here to await.</summary>
    private void KeepBuffered(int end) => _line.Write(_buffer, _start, end - _start);

    private string Take()
    {
        var bytes = _line.GetBuffer().AsSpan(0, (int)_line.Length);
        if (bytes.Length > 0 && bytes[^1] == '\r') bytes = bytes[..^1];
        string s = Encoding.UTF8.GetString(bytes);
        _line.SetLength(0);
        return s;
    }
}
