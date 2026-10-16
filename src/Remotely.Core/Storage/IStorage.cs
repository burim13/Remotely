namespace Remotely.Core.Storage;

/// <summary>
/// Atomic JSON storage with a schema version and migrations. CONTRACT: owned by the integrator; agent A14 implements it (<c>JsonSettingsStore</c>) and migrates
/// the existing <c>viewer.json</c> of versions 0.3.0 and 0.4.0. Writes are temp-file-then-move so a crash never leaves a half file.
/// </summary>
public interface ISettingsStore<T> where T : class, new()
{
    T Current { get; }
    event Action<T>? Changed;
    void Update(Action<T> change);
    void Reset();
}

/// <summary>Saved PCs. <see cref="Target"/> is a device id (server mode) or a host name / address (same network).</summary>
public sealed record AddressEntry(string Id, string Name, string Target, string? Group, string? Notes, bool Favorite, DateTimeOffset? LastUsedUtc);

public interface IAddressBook
{
    IReadOnlyList<AddressEntry> All { get; }
    event Action? Changed;
    AddressEntry Add(string name, string target, string? group = null);
    void Update(AddressEntry entry);
    void Remove(string id);
    void MarkUsed(string id);
    string Export();
    void Import(string json);
}
