using System.Drawing;
using System.Text.RegularExpressions;

namespace Remotely.UITests;

/// <summary>
/// Decides which parts of a screenshot are painted over in the copy that is compared with baselines: text that differs from run to run (addresses, ids, tokens, times, dates,
/// version), elements that animate or depend on timing, and the outermost pixels of the window (the desktop behind the window frame shows through there and differs between runs).
/// The screenshots published on ci-screenshots are never masked.
/// </summary>
internal static class Masking
{
    private const int Border = 3;

    private static readonly Regex DynamicText = new(
        @"\b\d{1,3}(\.\d{1,3}){3}\b|\b[0-9a-fA-F]{24,}\b|\b[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}\b|\b\d{1,2}[/.]\d{1,2}[/.]\d{2,4}\b|\b\d{1,2}:\d{2}(:\d{2})?\b|Version \d|Remotely \d+\.\d+|\bago\b|Changes in",
        RegexOptions.Compiled);

    /// <summary>Automation ids of elements whose picture depends on time or on the network: the token and its countdown ring, the search for nearby PCs, the address list.</summary>
    private static readonly HashSet<string> DynamicIds = ["TokenText", "ExpiryRing", "TokenInfo", "NearbyStatus", "NearbyList", "AddressText", "IdText", "Busy", "StatusText"];

    public static List<Rectangle> For(IEnumerable<UNode> nodes, Rectangle frame, int width, int height)
    {
        var list = nodes.Where(n => !n.Rect.IsEmpty && (DynamicIds.Contains(n.Id) || (n.Name.Length > 0 && DynamicText.IsMatch(n.Name))))
            .Select(n => Rectangle.Inflate(new Rectangle(n.Rect.X - frame.X, n.Rect.Y - frame.Y, n.Rect.Width, n.Rect.Height), 2, 2)).ToList();
        list.Add(new Rectangle(0, 0, width, Border));
        list.Add(new Rectangle(0, height - Border, width, Border));
        list.Add(new Rectangle(0, 0, Border, height));
        list.Add(new Rectangle(width - Border, 0, Border, height));
        return list;
    }
}
