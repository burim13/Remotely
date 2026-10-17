using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Remotely.HostCore;

/// <summary>What the clipboard of this machine holds, for one change. <see cref="Bundle"/> is null when nothing we carry is on it, or when it was too large (<see cref="Note"/> says so).</summary>
public sealed record ClipReadResult(long Sequence, ClipBundle? Bundle, string? Note = null);

/// <summary>A clipboard that can carry more than text. Implemented by the Windows clipboard; the host agent offers it when the implementation of <see cref="IClipboardSync"/> also implements this.</summary>
public interface IRichClipboardSync
{
    /// <summary>Reads the clipboard if its sequence number differs from <paramref name="knownSequence"/>; otherwise returns quickly with no bundle.</summary>
    Task<ClipReadResult> ReadBundleAsync(long knownSequence, ClipLimits limits, CancellationToken ct);

    /// <summary>Replaces the clipboard with everything in the bundle. Returns the sequence number after the write so the caller does not echo it back.</summary>
    Task<long> WriteBundleAsync(ClipBundle bundle, CancellationToken ct);
}

/// <summary>Lets the viewer learn which keyboard layout the host's focused window uses, to decide between key codes and typed text.</summary>
public interface IHostKeyboardInfo
{
    /// <summary>The keyboard layout handle (HKL) of the window that has input, or 0 when unknown.</summary>
    Task<uint> GetLayoutAsync(CancellationToken ct);
}

public enum ClipSendStatus { Sent, Refused, SkippedEcho, Declined, Failed }

public sealed record ClipSendOutcome(ClipSendStatus Status, string? Message = null)
{
    public bool Ok => Status == ClipSendStatus.Sent;
}

/// <summary>
/// Remembers the clipboard that was last written because the other side sent it. When the local clipboard then changes to exactly that content,
/// this is our own write coming back, not a new copy, and it must not be sent again.
/// </summary>
public sealed class ClipEchoGuard
{
    private byte[]? _applied;

    public void Applied(ClipBundle bundle) => Volatile.Write(ref _applied, bundle.Fingerprint());

    /// <summary>False for the content we just received. Any other content ends the protection, so copying the same thing again later is sent normally.</summary>
    public bool ShouldSend(ClipBundle bundle)
    {
        var applied = Volatile.Read(ref _applied);
        if (applied is not null && applied.AsSpan().SequenceEqual(bundle.Fingerprint())) return false;
        Volatile.Write(ref _applied, null);
        return true;
    }
}

/// <summary>
/// One end of the clipboard transfer protocol, the same code on the host and on the viewer. It sends bundles (offer, wait for the answer, chunks, end with a
/// SHA-256, wait for the result) and receives them (check the limits, ask for confirmation above the threshold, collect, verify, apply).
/// Messages the peer is not meant to handle are not touched: call <see cref="Handles"/> first.
/// </summary>
public sealed class ClipPeer(ClipWire outbound, ClipWire inbound, Func<byte[], CancellationToken, Task> send, Func<ClipBundle, CancellationToken, Task> apply, ClipLimits? limits = null)
{
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly object _gate = new();
    private uint _nextId;
    private Outgoing? _outgoing;
    private Incoming? _incoming;

    public ClipLimits Limits { get; } = limits ?? ClipLimits.Default;
    public ClipEchoGuard Guard { get; } = new();

    /// <summary>When false, every offer is refused with <see cref="UnavailableReason"/> (clipboard sync is off, or the machine has no rich clipboard).</summary>
    public bool AcceptIncoming { get; set; } = true;
    public string UnavailableReason { get; set; } = "Clipboard sync is off.";

    /// <summary>Asked before data above <see cref="ClipLimits.ConfirmAboveBytes"/> is accepted. With no handler such transfers are refused.</summary>
    public Func<ClipOfferInfo, Task<bool>>? ConfirmReceive { get; set; }

    /// <summary>Asked before this side starts sending data above <see cref="ClipLimits.ConfirmAboveBytes"/>. With no handler it is sent.</summary>
    public Func<ClipBundle, Task<bool>>? ConfirmSend { get; set; }

    public TimeSpan AnswerTimeout { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan ResultTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public event Action<string>? Problem;

    public bool Handles(byte kind) =>
        kind == inbound.Offer || kind == inbound.Chunk || kind == inbound.End || kind == inbound.Cancel || kind == outbound.Answer || kind == outbound.Result;

    // ---- sending ----

    public async Task<ClipSendOutcome> SendAsync(ClipBundle bundle, CancellationToken ct)
    {
        if (bundle.IsEmpty) return new ClipSendOutcome(ClipSendStatus.Failed, "Nothing to send.");
        if (Limits.Check(bundle) is { } tooBig) return new ClipSendOutcome(ClipSendStatus.Refused, tooBig);
        if (!Guard.ShouldSend(bundle)) return new ClipSendOutcome(ClipSendStatus.SkippedEcho);
        if (Limits.NeedsConfirmation(bundle.TotalBytes) && ConfirmSend is { } confirm && !await confirm(bundle).ConfigureAwait(false))
            return new ClipSendOutcome(ClipSendStatus.Declined, "Not sent.");

        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        Outgoing o;
        lock (_gate) _outgoing = o = new Outgoing(++_nextId);
        try
        {
            var items = bundle.Items.OrderBy(i => i.Kind).ToList();
            await send(ClipCodec.EncodeOffer(outbound.Offer, o.Id, items), ct).ConfigureAwait(false);
            var (accepted, why) = await o.Answer.Task.WaitAsync(AnswerTimeout, ct).ConfigureAwait(false);
            if (!accepted) return new ClipSendOutcome(ClipSendStatus.Refused, why);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var item in items)
            {
                hash.AppendData(item.Data);
                for (var at = 0; at < item.Data.Length; at += ClipCodec.ChunkBytes)
                    await send(ClipCodec.EncodeChunk(outbound.Chunk, o.Id, item.Data.AsSpan(at, Math.Min(ClipCodec.ChunkBytes, item.Data.Length - at))), ct).ConfigureAwait(false);
            }
            await send(ClipCodec.EncodeEnd(outbound.End, o.Id, hash.GetHashAndReset()), ct).ConfigureAwait(false);
            var (ok, message) = await o.Result.Task.WaitAsync(ResultTimeout, ct).ConfigureAwait(false);
            return new ClipSendOutcome(ok ? ClipSendStatus.Sent : ClipSendStatus.Failed, ok ? null : message);
        }
        catch (TimeoutException)
        {
            try { await send(ClipCodec.EncodeCancel(outbound.Cancel, o.Id), CancellationToken.None).ConfigureAwait(false); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
            return new ClipSendOutcome(ClipSendStatus.Failed, "The other side did not answer.");
        }
        finally
        {
            lock (_gate) if (ReferenceEquals(_outgoing, o)) _outgoing = null;
            _sendGate.Release();
        }
    }

    // ---- receiving ----

    public async Task HandleAsync(byte[] message, CancellationToken ct)
    {
        if (message.Length == 0) return;
        var kind = message[0];
        if (kind == outbound.Answer)
        {
            if (ClipCodec.TryDecodeReply(message, out var id, out var ok, out var reason)) FindOutgoing(id)?.Answer.TrySetResult((ok, reason));
        }
        else if (kind == outbound.Result)
        {
            if (ClipCodec.TryDecodeReply(message, out var id, out var ok, out var reason)) FindOutgoing(id)?.Result.TrySetResult((ok, reason));
        }
        else if (kind == inbound.Offer) await OnOfferAsync(message, ct).ConfigureAwait(false);
        else if (kind == inbound.Chunk) OnChunk(message);
        else if (kind == inbound.End) await OnEndAsync(message, ct).ConfigureAwait(false);
        else if (kind == inbound.Cancel && HostMessages.TryReadId(message, out var cancelled, out _))
        {
            lock (_gate) if (_incoming?.Id == cancelled) _incoming = null;
        }
    }

    private Outgoing? FindOutgoing(uint id)
    {
        lock (_gate) return _outgoing is { } o && o.Id == id ? o : null;
    }

    private async Task OnOfferAsync(byte[] message, CancellationToken ct)
    {
        if (!ClipCodec.TryDecodeOffer(message, out var info))
        {
            Problem?.Invoke("Bad clipboard offer.");
            return;
        }
        lock (_gate) _incoming = null; // a new offer replaces anything unfinished
        var refusal = !AcceptIncoming ? UnavailableReason : Limits.Check(info.Items);
        if (refusal is null && Limits.NeedsConfirmation(info.TotalBytes) && ConfirmReceive is null)
            refusal = "Large clipboard transfers need confirmation, and this side cannot ask.";
        if (refusal is not null)
        {
            await send(ClipCodec.EncodeReply(inbound.Answer, info.Id, false, refusal), ct).ConfigureAwait(false);
            return;
        }
        var incoming = new Incoming(info);
        lock (_gate) _incoming = incoming;
        if (!Limits.NeedsConfirmation(info.TotalBytes))
        {
            incoming.Accepted = true;
            await send(ClipCodec.EncodeReply(inbound.Answer, info.Id, true, ""), ct).ConfigureAwait(false);
            return;
        }
        // A person has to decide, which can take minutes: answer from a background task so the message loop keeps serving input.
        _ = Task.Run(async () =>
        {
            try
            {
                var yes = await ConfirmReceive!(info).ConfigureAwait(false);
                lock (_gate) { if (!ReferenceEquals(_incoming, incoming)) return; if (yes) incoming.Accepted = true; else _incoming = null; }
                await send(ClipCodec.EncodeReply(inbound.Answer, info.Id, yes, yes ? "" : "Declined."), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Problem?.Invoke("Clipboard confirmation failed: " + ex.Message); }
        }, CancellationToken.None);
    }

    private void OnChunk(byte[] message)
    {
        if (!HostMessages.TryReadId(message, out var id, out var data)) return;
        lock (_gate)
        {
            if (_incoming is not { Accepted: true } i || i.Id != id) return;
            if (i.Received + data.Length > i.Buffer.Length) { _incoming = null; Problem?.Invoke("Clipboard data exceeded what was announced."); return; }
            data.CopyTo(i.Buffer.AsSpan((int)i.Received));
            i.Received += data.Length;
        }
    }

    private async Task OnEndAsync(byte[] message, CancellationToken ct)
    {
        if (!HostMessages.TryReadId(message, out var id, out var sha)) return;
        Incoming? i;
        lock (_gate)
        {
            i = _incoming;
            if (i is not { Accepted: true } || i.Id != id) return;
            _incoming = null;
        }
        string? failure = null;
        if (i.Received != i.Buffer.Length || sha.Length != 32 || !SHA256.HashData(i.Buffer).AsSpan().SequenceEqual(sha)) failure = "The clipboard data was incomplete or damaged.";
        ClipBundle? bundle = null;
        if (failure is null)
        {
            var items = new List<ClipItem>();
            var at = 0;
            foreach (var (kind, size) in i.Info.Items)
            {
                items.Add(new ClipItem(kind, i.Buffer.AsSpan(at, (int)size).ToArray()));
                at += (int)size;
            }
            bundle = new ClipBundle(items);
            if (bundle.Get(ClipItemKind.Files) is { } files && !ClipFiles.TryDecode(files.Data, Limits, out _, out failure)) bundle = null;
        }
        if (bundle is not null && failure is null)
        {
            try
            {
                await apply(bundle, ct).ConfigureAwait(false);
                Guard.Applied(bundle);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException) { failure = ex.Message; }
        }
        await send(ClipCodec.EncodeReply(inbound.Result, id, failure is null, failure ?? ""), ct).ConfigureAwait(false);
    }

    private sealed class Outgoing(uint id)
    {
        public uint Id { get; } = id;
        public TaskCompletionSource<(bool Ok, string Message)> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<(bool Ok, string Message)> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Incoming(ClipOfferInfo info)
    {
        public ClipOfferInfo Info { get; } = info;
        public uint Id => Info.Id;
        public bool Accepted { get; set; }
        public byte[] Buffer { get; } = new byte[info.TotalBytes];
        public long Received { get; set; }
    }
}
