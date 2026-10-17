using Remotely.Core.Admin;
using Remotely.Core.ServerManager;
using Remotely.UI.Localization;

namespace Remotely.ServerManager;

/// <summary>
/// The one place that decides which <see cref="IServerAdmin"/> the Manager talks to. At integration, replace the right-hand side of the second line with the real
/// client from the server-admin work (<c>ServerAdminClient</c>); nothing else in the Manager changes.
/// </summary>
internal static class AdminFactory
{
    public static IServerAdmin Create(bool fake) =>
        fake ? new FakeServerAdmin() : new UnavailableServerAdmin(Loc.Get("Error_NoClient"));
}
