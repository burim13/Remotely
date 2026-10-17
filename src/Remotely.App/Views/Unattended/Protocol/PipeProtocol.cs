using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Remotely.Unattended;

// This folder is compiled into three projects: the app (it is in the app's tree), Remotely.Host (Remotely.Host.csproj links it) and the unit tests.
// It must stay free of WinUI and of Windows-only APIs. See docs/unattended.md.

/// <summary>Name and limits of the local pipe between the app (signed-in user) and the host service.</summary>
public static class UnattendedPipe
{
    /// <summary>Opened as <c>\\.\pipe\Remotely.Unattended</c>.</summary>
    public const string Name = "Remotely.Unattended";
    public const int Version = 1;
    /// <summary>A request or reply is never larger than this; the reader refuses a bigger announced length before allocating.</summary>
    public const int MaxMessageBytes = 16 * 1024;
    public const int MaxServerInstances = 4;
}

public static class PipeCommands
{
    /// <summary>Read the state. Always allowed to the signed-in user.</summary>
    public const string Status = "status";
    /// <summary>Start listening for same-network tokens. Refused unless an administrator has allowed it (<see cref="PipeErrors.NotAllowed"/>).</summary>
    public const string Start = "start";
    public const string Stop = "stop";
    public const string NewToken = "new-token";
    /// <summary>Answer the question in <see cref="UnattendedStatus.Question"/>.</summary>
    public const string Answer = "answer";
    /// <summary>Disconnect the PC that is in a session.</summary>
    public const string EndSession = "end-session";

    public static readonly IReadOnlyList<string> All = [Status, Start, Stop, NewToken, Answer, EndSession];
}

public static class PipeDecisions
{
    public const string Deny = "deny";
    public const string AllowOnce = "once";
}

/// <summary>Short machine-readable reasons in <see cref="PipeReply.Error"/>. The app turns them into sentences from its string table.</summary>
public static class PipeErrors
{
    public const string BadRequest = "bad-request";
    public const string NotAllowed = "not-allowed";
    public const string ListenFailed = "listen-failed";
    public const string NotListening = "not-listening";
    public const string NoSuchQuestion = "no-such-question";
    public const string Internal = "internal";
}

public sealed class PipeRequest
{
    public string Cmd { get; set; } = "";
    /// <summary>For <see cref="PipeCommands.Answer"/>: the <see cref="ConsentQuestion.Id"/> being answered.</summary>
    public string? QuestionId { get; set; }
    /// <summary>For <see cref="PipeCommands.Answer"/>: one of <see cref="PipeDecisions"/>. Anything else counts as a refusal.</summary>
    public string? Decision { get; set; }
}

public sealed class PipeReply
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    /// <summary>Set for every successful command and for most failures, so the page can redraw from one reply.</summary>
    public UnattendedStatus? Status { get; set; }
}

/// <summary>The question the person at this PC is asked when a PC with the right token wants in (relayed from the service, which has no screen).</summary>
public sealed class ConsentQuestion
{
    public string Id { get; set; } = "";
    public string ControllerName { get; set; } = "";
    public string Address { get; set; } = "";
    public DateTimeOffset ExpiresUtc { get; set; }
}

/// <summary>What the service tells the signed-in user. Contains the live token, so it is only ever sent over the access-controlled pipe.</summary>
public sealed class UnattendedStatus
{
    public int Version { get; set; } = UnattendedPipe.Version;
    /// <summary>An administrator allowed same-network sharing through the service (<c>Remotely.Host.exe unattended lan on</c>).</summary>
    public bool LanAllowed { get; set; }
    public bool AskBeforeConnect { get; set; } = true;
    public bool KeepAwake { get; set; } = true;
    /// <summary>The service is listening for a token right now.</summary>
    public bool Listening { get; set; }
    public string? Token { get; set; }
    public DateTimeOffset? TokenExpiresUtc { get; set; }
    public int Port { get; set; }
    /// <summary>The process in the signed-in session that captures the screen is connected to the service.</summary>
    public bool HelperAttached { get; set; }
    /// <summary>Desktop of the newest frame the service captured in the last few seconds ("Default", "Winlogon"), or null when nobody is looking.</summary>
    public string? CaptureDesktop { get; set; }
    public string? Controller { get; set; }
    public string? ControllerAddress { get; set; }
    public DateTimeOffset? SessionSince { get; set; }
    public ConsentQuestion? Question { get; set; }
    /// <summary>The service is holding a "do not sleep" request for Windows.</summary>
    public bool AwakeHeld { get; set; }
}

public static class PipeJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] Encode<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        if (bytes.Length > UnattendedPipe.MaxMessageBytes) throw new InvalidDataException("Message too large.");
        return bytes;
    }

    /// <exception cref="InvalidDataException">Not valid JSON for <typeparamref name="T"/>.</exception>
    public static T Decode<T>(ReadOnlySpan<byte> bytes) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(bytes, Options) ?? throw new InvalidDataException("Empty message."); }
        catch (JsonException ex) { throw new InvalidDataException("Not a valid message.", ex); }
    }
}

/// <summary>One message = 4-byte big-endian length, then that many bytes of UTF-8 JSON. A length of 0 or above <see cref="UnattendedPipe.MaxMessageBytes"/> ends the conversation.</summary>
public static class PipeFraming
{
    public static async Task WriteAsync(Stream stream, byte[] payload, CancellationToken ct)
    {
        if (payload.Length is 0 or > UnattendedPipe.MaxMessageBytes) throw new InvalidDataException("Bad message size.");
        var buffer = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)payload.Length);
        payload.CopyTo(buffer, 4);
        await stream.WriteAsync(buffer, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The next message, or null when the other side closed the pipe between messages.</summary>
    /// <exception cref="InvalidDataException">The announced length is 0 or too large (nothing is allocated or read for it).</exception>
    public static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        var read = 0;
        while (read < header.Length)
        {
            var n = await stream.ReadAsync(header.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (read == 0) return null;
                throw new EndOfStreamException();
            }
            read += n;
        }
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length is 0 or > UnattendedPipe.MaxMessageBytes) throw new InvalidDataException("Bad message size.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return payload;
    }
}
