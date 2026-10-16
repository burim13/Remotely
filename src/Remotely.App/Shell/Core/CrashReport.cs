using System.Text;

namespace Remotely.App.Shell;

/// <summary>The text of the "Something went wrong" dialog and the crash log: enough for a bug report. Exception messages can contain file paths (so a user name); the person sees the text before copying it.</summary>
public static class CrashReport
{
    public static string Format(Exception? ex, string appVersion, string osDescription, string architecture, DateTimeOffset when)
    {
        var sb = new StringBuilder();
        sb.Append("Remotely ").Append(appVersion).Append(" on ").Append(osDescription).Append(' ').Append(architecture).AppendLine();
        sb.Append(when.ToString("u")).AppendLine();
        sb.AppendLine();
        sb.Append(ex?.ToString() ?? "(no exception information)");
        return sb.ToString();
    }
}
