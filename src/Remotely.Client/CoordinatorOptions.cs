namespace Remotely.Client;

public sealed class CoordinatorOptions
{
    /// <summary>wss://host:port/ws for real use.</summary>
    public required Uri ServerUri { get; init; }

    /// <summary>Lowercase hex SHA-256 of the server certificate's public key, printed by the server at startup. Required for wss.</summary>
    public string? PinnedServerKey { get; init; }

    /// <summary>Permit unencrypted ws://. For local testing only.</summary>
    public bool AllowInsecureTransport { get; init; }

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);
}
