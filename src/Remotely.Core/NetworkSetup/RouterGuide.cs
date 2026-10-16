using System.Globalization;
using System.Net;

namespace Remotely.Core.NetworkSetup;

/// <summary>A sentence of the instructions: a resource key plus the values to put into its {0}, {1} placeholders. The UI looks the key up in the active language.</summary>
public sealed record GuideStep(string Key, params string[] Args);

/// <summary>The values the user types into the router, and the steps around them. Everything here is derived from the chosen address and port, nothing is looked up online.</summary>
public sealed record RouterInstructions(string Protocol, int ExternalPort, int InternalPort, string InternalAddress, string? RouterAddress, string? PublicName,
    IReadOnlyList<GuideStep> Steps, IReadOnlyList<GuideStep> Notes);

public static class RouterGuide
{
    public static RouterInstructions Build(IPAddress localAddress, int port, string? publicName, IPAddress? routerAddress = null)
    {
        var p = port.ToString(CultureInfo.InvariantCulture);
        var local = localAddress.ToString();
        var router = routerAddress?.ToString();
        var steps = new List<GuideStep>
        {
            router is null ? new GuideStep("Setup_Router_Step_Open") : new GuideStep("Setup_Router_Step_OpenAt", router),
            new GuideStep("Setup_Router_Step_Find"),
            new GuideStep("Setup_Router_Step_Values", "TCP", p, local, p),
            new GuideStep("Setup_Router_Step_Reserve", local),
            new GuideStep("Setup_Router_Step_Save"),
        };
        var notes = new List<GuideStep> { new("Setup_Router_Note_Wan"), new("Setup_Router_Note_Ddns") };
        if (!string.IsNullOrWhiteSpace(publicName)) notes.Add(new GuideStep("Setup_Router_Note_Name", publicName.Trim()));
        return new RouterInstructions("TCP", port, port, local, router, string.IsNullOrWhiteSpace(publicName) ? null : publicName.Trim(), steps, notes);
    }
}

public enum WanVerdict
{
    /// <summary>The router's outside address is a public one. Forwarding can work; only the outside test proves it does.</summary>
    PublicAddress,
    /// <summary>The router's outside address is itself a private address: another router (modem, ISP equipment) is in front. Forwarding on this router alone cannot work.</summary>
    BehindAnotherRouter,
    /// <summary>The router's outside address is in 100.64.0.0/10: the provider shares one public address between customers. Forwarding cannot work.</summary>
    CarrierGradeNat,
    NotAnAddress,
}

public static class WanAssessment
{
    /// <summary>The user reads the "WAN" or "Internet" address off the router's status page and types it here. (UPnP can supply it automatically when the user opted in.)</summary>
    public static WanVerdict Assess(string? text)
    {
        if (!IPAddress.TryParse((text ?? "").Trim(), out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return WanVerdict.NotAnAddress;
        return Assess(ip);
    }

    public static WanVerdict Assess(IPAddress ip) => AddressClassifier.Classify(ip) switch
    {
        AddressKind.Public => WanVerdict.PublicAddress,
        AddressKind.CarrierGradeNat => WanVerdict.CarrierGradeNat,
        AddressKind.Private => WanVerdict.BehindAnotherRouter,
        _ => WanVerdict.NotAnAddress,
    };
}
