using System.Net.Sockets;
using System.Text;

namespace ErikTerm.App.Ipc;

/// <summary>Sends one request to an instance's socket and waits for its reply.</summary>
internal static class IpcClient
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <exception cref="SocketException">Nothing is listening.</exception>
    /// <exception cref="IOException">The connection broke, or the reply was not valid.</exception>
    /// <exception cref="TimeoutException">No reply in time.</exception>
    public static IpcResponse Send(string socketPath, IpcRequest request, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? DefaultTimeout);
        try
        {
            return SendAsync(socketPath, request, cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("erikterm did not answer in time");
        }
    }

    private static async Task<IpcResponse> SendAsync(string socketPath, IpcRequest request, CancellationToken token)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), token).ConfigureAwait(false);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(IpcJson.Encode(request) + "\n"), token).ConfigureAwait(false);
        string? line = await new LineReader(stream).ReadLineAsync(token).ConfigureAwait(false);
        if (line is null) throw new IOException("erikterm closed the connection without answering");
        return IpcJson.DecodeResponse(line) ?? throw new IOException("erikterm sent an invalid reply");
    }
}
