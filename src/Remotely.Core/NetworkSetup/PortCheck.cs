using System.Net;
using System.Net.Sockets;

namespace Remotely.Core.NetworkSetup;

public enum PortStatus
{
    Free,
    /// <summary>Something is already listening on the port (it may be the Remotely server itself).</summary>
    InUse,
    /// <summary>The operating system refused to let this program use the port.</summary>
    NotAllowed,
    /// <summary>Not a valid TCP port number (1 to 65535).</summary>
    Invalid,
    Unknown,
}

public interface IPortProbe
{
    Task<PortStatus> CheckAsync(int port, CancellationToken ct = default);
}

/// <summary>Asks the operating system for the port by briefly listening on it on all addresses, which is what the server does.</summary>
public sealed class SystemPortProbe : IPortProbe
{
    public Task<PortStatus> CheckAsync(int port, CancellationToken ct = default)
    {
        if (port is < 1 or > 65535) return Task.FromResult(PortStatus.Invalid);
        try
        {
            var listener = new TcpListener(IPAddress.Any, port) { ExclusiveAddressUse = true };
            listener.Start();
            listener.Stop();
            return Task.FromResult(PortStatus.Free);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse) { return Task.FromResult(PortStatus.InUse); }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AccessDenied) { return Task.FromResult(PortStatus.NotAllowed); }
        catch (SocketException) { return Task.FromResult(PortStatus.Unknown); }
    }
}
