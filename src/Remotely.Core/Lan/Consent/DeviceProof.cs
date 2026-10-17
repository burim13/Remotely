using System.Security.Cryptography;
using System.Text;
using Remotely.Core.Crypto;
using Remotely.Core.Protocol;

namespace Remotely.Core.Lan.Consent;

public sealed class ConsentDeniedException(string message) : Exception(message);

/// <summary>A device key the other PC proved it holds.</summary>
public sealed record ProvenDevice(byte[] PublicKey, string DeviceId);

/// <summary>The sharing PC's long-term key, as received inside the token-protected channel. A controller that saves it can later reconnect without a token (<see cref="LanResume"/>).</summary>
public sealed record HostAnnouncement(byte[] PublicKey, string DeviceId, string Name);

/// <summary>
/// How a controller says "I can prove a device key" without changing the token handshake: the name it sends is followed by a marker. A host that knows the marker strips it
/// and runs <see cref="DeviceProof"/>; an older host shows the marker character (an invisible control character) and carries on; an older controller sends no marker, so the
/// host skips the proof and can only offer "Allow once". The 60-character limit of the name field is respected.
/// </summary>
public static class ControllerHello
{
    public const string Marker = "\u001fRMD1";
    public const int MaxNameLength = 60;

    public static string Encode(string name)
    {
        var clean = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length > MaxNameLength - Marker.Length) clean = clean[..(MaxNameLength - Marker.Length)];
        return clean + Marker;
    }

    public static (string Name, bool SupportsDeviceProof) Parse(string raw)
    {
        var supports = raw.EndsWith(Marker, StringComparison.Ordinal);
        var name = supports ? raw[..^Marker.Length] : raw;
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return (name.Length == 0 ? "Unnamed PC" : name, supports);
    }
}

/// <summary>
/// Device-key proof inside the already encrypted, token-authenticated channel, before the host starts the session.
/// Host -> controller: Challenge (fresh 32-byte nonce, the host's public key, the host's name).
/// Controller -> host: Proof (the controller's public key and an ECDSA signature over a label, the nonce, the host's public key and the controller's name).
/// Host -> controller: Granted or Denied, once the person at the host has answered (so the controller can show "waiting for approval").
/// The nonce is fresh per connection and the whole exchange is inside the encrypted channel, so a recorded proof cannot be replayed.
/// </summary>
public static class DeviceProof
{
    public const byte Challenge = 0xE1;
    public const byte Proof = 0xE2;
    public const byte Granted = 0xE3;
    public const byte Denied = 0xE4;

    private static byte[] SignedBytes(byte[] nonce, byte[] hostKey, string controllerName) =>
        new FieldWriter().Field("RMTL-DEV1-controller"u8).Field(nonce).Field(hostKey).Field(Encoding.UTF8.GetBytes(controllerName)).ToArray();

    /// <summary>Host side. Returns the proven key, or null when the controller did not answer in time or closed. Throws <see cref="HandshakeException"/> for a wrong signature.</summary>
    public static async Task<ProvenDevice?> ChallengeAsync(IMessageChannel channel, DeviceIdentity host, string hostName, string controllerName, TimeSpan timeout, CancellationToken ct)
    {
        var nonce = RandomNumberGenerator.GetBytes(32);
        await channel.SendAsync(Prefix(Challenge, new FieldWriter().Field(nonce).Field(host.PublicKey).Field(Encoding.UTF8.GetBytes(hostName.Length > 60 ? hostName[..60] : hostName)).ToArray()), ct).ConfigureAwait(false);
        byte[]? reply;
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            limit.CancelAfter(timeout);
            try { reply = await channel.ReceiveAsync(limit.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        }
        if (reply is null || reply.Length < 2 || reply[0] != Proof) return null;
        try
        {
            var r = new FieldReader(reply.AsSpan(1));
            var publicKey = r.Field().ToArray();
            var signature = r.Field().ToArray();
            if (!r.AtEnd) throw new HandshakeException("Bad proof.");
            if (!DeviceIdentity.Verify(publicKey, SignedBytes(nonce, host.PublicKey, controllerName), signature))
                throw new HandshakeException("The other PC's key proof is wrong.");
            return new ProvenDevice(publicKey, DeviceIds.FromPublicKey(publicKey));
        }
        catch (FormatException ex) { throw new HandshakeException(ex.Message); }
    }

    /// <summary>Host side: tells the controller the answer. <paramref name="remembered"/> says the PC was added to the remembered list.</summary>
    public static Task SendDecisionAsync(IMessageChannel channel, bool allowed, bool remembered, string? reason, CancellationToken ct) =>
        channel.SendAsync(allowed
            ? new byte[] { Granted, (byte)(remembered ? 1 : 0) }
            : Prefix(Denied, Encoding.UTF8.GetBytes(reason ?? "")), ct);

    /// <summary>
    /// Controller side. Call right after <see cref="LanConnector.ConnectAsync"/> when the controller name was made with <see cref="ControllerHello.Encode"/>, and before
    /// opening the session. Waits for the person at the other PC to answer. Throws <see cref="ConsentDeniedException"/> if refused, <see cref="HandshakeException"/> otherwise.
    /// </summary>
    public static async Task<(HostAnnouncement Host, bool Remembered)> RespondAsync(IMessageChannel channel, DeviceIdentity self, string controllerName, CancellationToken ct)
    {
        var first = await channel.ReceiveAsync(ct).ConfigureAwait(false) ?? throw new HandshakeException("The other PC closed the connection.");
        if (first.Length < 2 || first[0] != Challenge) throw new HandshakeException("The other PC did not ask for a device proof.");
        HostAnnouncement host;
        byte[] nonce;
        try
        {
            var r = new FieldReader(first.AsSpan(1));
            nonce = r.Field().ToArray();
            var hostKey = r.Field().ToArray();
            var hostName = Encoding.UTF8.GetString(r.Field(256));
            if (!r.AtEnd || nonce.Length != 32) throw new HandshakeException("Bad challenge.");
            host = new HostAnnouncement(hostKey, DeviceIds.FromPublicKey(hostKey), hostName);
        }
        catch (FormatException ex) { throw new HandshakeException(ex.Message); }

        var plainName = ControllerHello.Parse(controllerName).Name;
        var signature = self.Sign(SignedBytes(nonce, host.PublicKey, plainName));
        await channel.SendAsync(Prefix(Proof, new FieldWriter().Field(self.PublicKey).Field(signature).ToArray()), ct).ConfigureAwait(false);

        var answer = await channel.ReceiveAsync(ct).ConfigureAwait(false) ?? throw new ConsentDeniedException("The other PC closed the connection before answering.");
        if (answer.Length >= 1 && answer[0] == Granted) return (host, answer.Length >= 2 && answer[1] == 1);
        var reason = answer.Length >= 1 && answer[0] == Denied ? Encoding.UTF8.GetString(answer, 1, answer.Length - 1) : "";
        throw new ConsentDeniedException(reason.Length > 0 ? reason : "The other PC did not allow the connection.");
    }

    private static byte[] Prefix(byte kind, byte[] body)
    {
        var m = new byte[1 + body.Length];
        m[0] = kind;
        body.CopyTo(m, 1);
        return m;
    }
}
