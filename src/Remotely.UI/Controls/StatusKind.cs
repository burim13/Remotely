namespace Remotely.UI.Controls;

/// <summary>How a status is shown. Colour is never the only signal: every kind also has an icon and the caller supplies the words.</summary>
public enum StatusKind
{
    Neutral,
    Connected,
    Info,
    Warning,
    Error,
}
