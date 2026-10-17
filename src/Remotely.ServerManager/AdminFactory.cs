using Remotely.Core.Admin;
using Remotely.Core.ServerManager;
using Remotely.UI.Localization;

namespace Remotely.ServerManager;

/// <summary>
/// The one place that decides which <see cref="IServerAdmin"/> the Manager talks to: the real HTTP client (it reads the admin token from the server's data folder on every
/// call), or the in-memory fake behind <c>--fake</c> for screenshots and demos.
/// </summary>
internal static class AdminFactory
{
    public static IServerAdmin Create(bool fake) =>
        fake ? new FakeServerAdmin() : ServerAdminClient.ForDataDirectory(ManagerPaths.DataDir);
}
