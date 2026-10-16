using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Remotely.UI.Controls;

namespace Remotely.App.Views.Gallery;

// Temporary diagnostics: one page per control, to find which one crashes at start-up.
public sealed class DiagHeaderPage : Page { public DiagHeaderPage() { Content = new PageHeader { Title = "t", Subtitle = "s", Glyph = "Monitor" }; } }
public sealed class DiagSectionPage : Page { public DiagSectionPage() { Content = new SectionTitle { Text = "t", Description = "d" }; } }
public sealed class DiagPillPage : Page { public DiagPillPage() { Content = new StatusPill { Text = "ok", Kind = StatusKind.Connected }; } }
public sealed class DiagBannerPage : Page { public DiagBannerPage() { Content = new InlineBanner { Title = "t", Message = "m", Kind = StatusKind.Error, DismissLabel = "x", IsDismissible = true }; } }
public sealed class DiagBannerLocPage : Page { public DiagBannerLocPage() { Content = new InlineBanner { Title = "t", IsDismissible = true }; } }
public sealed class DiagCardPage : Page { public DiagCardPage() { Content = new Card { Content = new TextBlock { Text = "card" } }; } }
public sealed class DiagTilePage : Page { public DiagTilePage() { Content = new DeviceTile { DeviceName = "n", Detail = "d", StatusText = "Online", StatusKind = StatusKind.Connected }; } }
public sealed class DiagTokenPage : Page { public DiagTokenPage() { Content = new TokenBox { Header = "h", Value = "K7P44NX2QMH9", ErrorMessage = "e" }; } }
public sealed class DiagEmptyPage : Page { public DiagEmptyPage() { Content = new EmptyState { Title = "t", Message = "m" }; } }
public sealed class DiagIconPage : Page { public DiagIconPage() { var p = new StackPanel(); foreach (var n in Remotely.UI.Icons.Names) p.Children.Add(new PorticoIcon { Glyph = n, Size = 24 }); Content = p; } }
