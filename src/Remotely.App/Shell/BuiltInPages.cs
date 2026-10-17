namespace Remotely.App.Shell;

/// <summary>The pages that exist today. Other agents replace one by putting <c>[NavigationPage("home", ...)]</c> on their own page class (same tag wins); see <see cref="NavigationPageAttribute"/>.</summary>
internal static class BuiltInPages
{
    public static void Register()
    {
        NavigationCatalog.Register(new NavEntry("home", "Nav_Home", typeof(HomePage), 0xE10F, 10));
        NavigationCatalog.Register(new NavEntry("computers", "Nav_Computers", typeof(ComputersPage), 0xE8AF, 20));
        NavigationCatalog.Register(new NavEntry(NavigationCatalog.SettingsTag, "Nav_Settings", typeof(SettingsPage), 0xE713, 900));
    }
}
