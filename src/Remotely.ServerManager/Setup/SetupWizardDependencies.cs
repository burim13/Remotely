using Remotely.Core.NetworkSetup;

namespace Remotely.ServerManager.Setup;

/// <summary>
/// Everything <see cref="SetupWizardPage"/> needs from its host. Pass it as the navigation parameter (<c>frame.Navigate(typeof(SetupWizardPage), dependencies)</c>)
/// or call <see cref="SetupWizardPage.Initialize"/> on a page you created yourself.
/// </summary>
/// <param name="Services">Build with <see cref="SetupWizardDefaults.Create"/> and replace <see cref="SetupWizardServices.Elevated"/> and <see cref="SetupWizardServices.Admin"/> with the host's own.</param>
/// <param name="PickPfxFileAsync">Shows the file picker (it needs the window handle, which the page does not have) and returns the chosen .pfx path, or null if cancelled.</param>
/// <param name="Finished">Called when the user presses Done on the last step.</param>
public sealed record SetupWizardDependencies(SetupWizardServices Services, Func<Task<string?>> PickPfxFileAsync, Action? Finished = null);

public static class SetupWizardDefaults
{
    /// <summary>
    /// The real probes: system addresses, a TCP/TLS reachability probe, a port probe, UPnP and NAT-PMP mappers (inactive until the user switches automatic mapping on).
    /// The host must supply <paramref name="elevated"/> (it starts the elevated helper) and, once the server's admin API exists, <paramref name="admin"/> and <paramref name="outside"/>.
    /// </summary>
    public static SetupWizardServices Create(IElevatedActions elevated, Remotely.Core.Admin.IServerAdmin? admin = null, IOutsideCheck? outside = null)
    {
        var mappers = new IPortMapper[]
        {
            new UpnpPortMapper(new SsdpIgdLocator()),
            new NatPmpPortMapper(SystemLocalAddressSource.Gateways, new UdpNatPmpTransport()),
        };
        return new SetupWizardServices(new SystemLocalAddressSource(), new SystemPortProbe(), new TlsReachabilityProbe(), elevated, outside ?? new UnavailableOutsideCheck(),
            new PortMappingSession(mappers), admin, SystemLocalAddressSource.Gateways);
    }
}
