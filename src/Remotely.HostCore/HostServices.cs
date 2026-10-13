namespace Remotely.HostCore;

/// <summary>Injects viewer input into the machine. Implementations must target the desktop that currently has input.</summary>
public interface IInputSink
{
    Task InjectAsync(IReadOnlyList<InputEvent> events, CancellationToken ct);
}

/// <summary>Sends Ctrl+Alt+Del. Only possible from a Windows service.</summary>
public interface ISecureAttention
{
    Task SendAsync(CancellationToken ct);
}

public interface IClipboardSync
{
    /// <summary>Current clipboard text (null if it holds no text) and a number that changes whenever the clipboard does.</summary>
    Task<(long Sequence, string? Text)> ReadAsync(CancellationToken ct);

    /// <summary>Replaces the clipboard text; returns the sequence number after the write so the caller does not echo it back.</summary>
    Task<long> WriteAsync(string text, CancellationToken ct);
}

/// <summary>Everything a host offers. Anything left null is simply not offered to viewers.</summary>
public sealed class HostServices(IFrameSource frames)
{
    public IFrameSource Frames { get; } = frames;
    public IInputSink? Input { get; init; }
    public ISecureAttention? SecureAttention { get; init; }
    public IClipboardSync? Clipboard { get; init; }
    /// <summary>Folder where uploads from viewers are stored. Null disables uploads.</summary>
    public string? ReceiveDirectory { get; init; }
    /// <summary>Folder whose files viewers may list and download. Null disables downloads.</summary>
    public string? ShareDirectory { get; init; }
    public required Func<string, bool> IsTrusted { get; init; }
    public Func<string, bool> CanControl { get; init; } = _ => false;
}
