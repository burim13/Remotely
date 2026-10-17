using System.Text.Json;

namespace Remotely.Host.Unattended;

/// <summary>
/// Machine-wide choices for unattended access, in <c>%ProgramData%\Remotely\unattended.json</c> (SYSTEM and administrators only, like the rest of that folder). They change only through
/// an elevated <c>Remotely.Host.exe unattended ...</c> command; the service reads them again whenever it needs them. A missing or unreadable file means the safe defaults.
/// </summary>
public sealed class UnattendedSettings
{
    /// <summary>The service may listen for same-network tokens. Off until an administrator turns it on.</summary>
    public bool LanAllowed { get; set; }
    /// <summary>Ask the person at this PC before a PC that knows the token is let in. If nobody is there to answer, the PC is refused. On by default.</summary>
    public bool AskBeforeConnect { get; set; } = true;
    /// <summary>Ask Windows not to sleep (and to keep the display on) while someone is viewing this PC.</summary>
    public bool KeepAwake { get; set; } = true;
}

public sealed class UnattendedSettingsStore(string filePath)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string FilePath { get; } = filePath;

    public UnattendedSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            return JsonSerializer.Deserialize<UnattendedSettings>(File.ReadAllBytes(FilePath), Options) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    /// <summary>Temp file then move, so a crash never leaves half a file. Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when the disk refuses.</summary>
    public void Save(UnattendedSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
        var temp = FilePath + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(settings, Options));
        File.Move(temp, FilePath, overwrite: true);
    }

    public UnattendedSettings Update(Action<UnattendedSettings> change)
    {
        var settings = Load();
        change(settings);
        Save(settings);
        return settings;
    }
}

/// <summary>Reads the settings file at most every <see cref="Age"/>, for code that asks on every frame.</summary>
public sealed class CachedSettings(UnattendedSettingsStore store, Func<DateTimeOffset>? clock = null)
{
    private static readonly TimeSpan Age = TimeSpan.FromSeconds(2);
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly object _gate = new();
    private UnattendedSettings? _value;
    private DateTimeOffset _readAt;

    public UnattendedSettings Current
    {
        get
        {
            lock (_gate)
            {
                var now = _clock();
                if (_value is null || now - _readAt >= Age || now < _readAt)
                {
                    _value = store.Load();
                    _readAt = now;
                }
                return _value;
            }
        }
    }
}
