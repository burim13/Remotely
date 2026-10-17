using System.Reflection;

namespace Remotely.App.Shell;

/// <summary>
/// Puts a Page class into the navigation pane. An agent adds one attribute on its page; nothing in the shell has to change:
/// <code>[NavigationPage("share", "Nav_Share", Order = 30, Glyph = 0xE8AF)] public sealed partial class SharePage : Page</code>
/// <see cref="Tag"/> is the stable id (used by <c>IAppServices.Navigate</c>), <see cref="TitleKey"/> a key in Resources.resw, <see cref="Glyph"/> a
/// Segoe Fluent Icons code point. Lower <see cref="Order"/> sorts first; Ctrl+1 to Ctrl+9 open the first nine entries in that order. A page with the
/// same tag as a built-in one replaces it, and the tag "settings" replaces the Settings page (that entry is not listed in the pane).
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class NavigationPageAttribute(string tag, string titleKey) : Attribute
{
    public string Tag { get; } = tag;
    public string TitleKey { get; } = titleKey;
    public int Order { get; init; } = 100;
    public int Glyph { get; init; } = 0xE7C3;
}

public sealed record NavEntry(string Tag, string TitleKey, Type PageType, int Glyph, int Order);

public static class NavigationCatalog
{
    public const string SettingsTag = "settings";
    private static readonly List<NavEntry> Registered = [];

    /// <summary>Code registration (the built-in pages use this); an attribute with the same tag wins over it.</summary>
    public static void Register(NavEntry entry)
    {
        lock (Registered)
        {
            Registered.RemoveAll(e => e.Tag == entry.Tag);
            Registered.Add(entry);
        }
    }

    /// <summary>Everything registered or attributed in the assembly, including the Settings entry, in display order.</summary>
    public static IReadOnlyList<NavEntry> All(Assembly assembly) => Build(Snapshot(), SafeTypes(assembly));

    /// <summary>The pages for the navigation pane (without Settings), in display order.</summary>
    public static IReadOnlyList<NavEntry> Menu(IEnumerable<NavEntry> all) => all.Where(e => e.Tag != SettingsTag).ToList();

    /// <summary>Merges registered entries with attributed types: later wins per tag; tags must be non-empty; sorted by order, then tag.</summary>
    public static IReadOnlyList<NavEntry> Build(IEnumerable<NavEntry> registered, IEnumerable<Type> types)
    {
        var byTag = new Dictionary<string, NavEntry>(StringComparer.Ordinal);
        foreach (var e in registered)
            if (!string.IsNullOrWhiteSpace(e.Tag)) byTag[e.Tag] = e;
        foreach (var t in types)
        {
            if (t.GetCustomAttribute<NavigationPageAttribute>() is not { } a || string.IsNullOrWhiteSpace(a.Tag)) continue;
            byTag[a.Tag] = new NavEntry(a.Tag, a.TitleKey, t, a.Glyph, a.Order);
        }
        return byTag.Values.OrderBy(e => e.Order).ThenBy(e => e.Tag, StringComparer.Ordinal).ToList();
    }

    private static List<NavEntry> Snapshot()
    {
        lock (Registered) return [.. Registered];
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }
}
