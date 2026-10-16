using Remotely.Client;
using Remotely.Core.Crypto;

namespace Remotely.App;

/// <summary>The viewer's one connection to the coordinator, opened on demand and reopened if it drops.</summary>
internal sealed class Connection(ViewerSettings settings, DeviceIdentity identity) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CoordinatorClient? _client;

    public ViewerSettings Settings { get; } = settings;
    public DeviceIdentity Identity { get; } = identity;

    private CoordinatorOptions Options => new()
    {
        ServerUri = new Uri(Settings.ServerUri),
        PinnedServerKey = Settings.ServerPin,
        AllowInsecureTransport = Settings.AllowInsecureTransport,
    };

    /// <summary>Returns a live, paired connection, reconnecting if needed. Throws <see cref="CoordinatorException"/> if the server cannot be used.</summary>
    public async Task<CoordinatorClient> EnsureAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is { IsConnected: true }) return _client;
            if (_client is not null) await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
            var client = await CoordinatorClient.ConnectAsync(Options, Identity, ct).ConfigureAwait(false);
            if (!client.IsPaired)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw new CoordinatorException("This device is not paired with the server yet. Open Settings and enter a pairing code.");
            }
            return _client = client;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Connects and pairs with a one-time code (does nothing if this device is already known to the server).</summary>
    public async Task PairAsync(string code, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is not null) await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
            var client = await CoordinatorClient.ConnectAsync(Options, Identity, ct).ConfigureAwait(false);
            try
            {
                if (!client.IsPaired) await client.PairAsync(code, Settings.DeviceName, canHost: false, ct).ConfigureAwait(false);
                _client = client;
            }
            catch
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync().ConfigureAwait(false);
    }
}
