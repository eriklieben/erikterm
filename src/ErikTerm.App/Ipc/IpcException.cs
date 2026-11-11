namespace ErikTerm.App;

/// <summary>An `erikterm cli` request that can't be done; the message goes back to the CLI.</summary>
public sealed class IpcException(string message) : Exception(message);
