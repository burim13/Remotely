using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Remotely.Core.Admin;
using Remotely.Core.ServerManager;
using Windows.Foundation;
using Shapes = Microsoft.UI.Xaml.Shapes;

namespace Remotely.ServerManager.Views;

public sealed partial class InvitePage : Page
{
    private readonly InfoBar _bar = new() { IsOpen = false, IsClosable = false };
    private readonly ComboBox _lifetime = new() { MinWidth = 180 };
    private readonly ComboBox _uses = new() { MinWidth = 140 };
    private readonly ProgressRing _busy = new() { IsActive = false, Width = 24, Height = 24 };
    private readonly Border _result;
    private readonly TextBlock _inviteText = Ui.Text("", "MonoTextStyle", 14);
    private readonly TextBlock _resultInfo = Ui.Secondary("");
    private readonly Border _qrHost = new() { Background = new SolidColorBrush(Colors.White), Padding = new Thickness(0), CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel _history = new() { Spacing = 8 };
    private InviteInfo? _shown;

    public InvitePage()
    {
        InitializeComponent();

        foreach (var lifetime in InviteRules.LifetimeChoices) _lifetime.Items.Add(new ComboBoxItem { Content = LifetimeLabel(lifetime), Tag = lifetime });
        _lifetime.SelectedIndex = 1;
        foreach (var uses in InviteRules.UseChoices) _uses.Items.Add(new ComboBoxItem { Content = uses == 1 ? Ui.T("Invite_OneUse") : Ui.T("Invite_NUses", uses), Tag = uses });
        _uses.SelectedIndex = 3;
        _lifetime.Header = Ui.T("Invite_Lifetime");
        _uses.Header = Ui.T("Invite_Uses");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_lifetime, Ui.T("Invite_Lifetime"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_uses, Ui.T("Invite_Uses"));

        var create = Ui.SmallButton(Ui.T("Invite_Create"), async (_, _) => await CreateAsync(), accent: true);
        var form = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Bottom };
        form.Children.Add(_lifetime);
        form.Children.Add(_uses);
        form.Children.Add(create);
        form.Children.Add(_busy);

        var formPanel = new StackPanel { Spacing = 10 };
        formPanel.Children.Add(Ui.Text(Ui.T("Invite_NewTitle"), "BodyStrongTextBlockStyle"));
        formPanel.Children.Add(Ui.Secondary(Ui.T("Invite_Help")));
        formPanel.Children.Add(form);

        var copy = new Button { Content = Ui.T("Invite_Copy"), Style = Ui.StyleOf("AccentButtonStyle"), Padding = new Thickness(28, 12, 28, 12), FontSize = 18, HorizontalAlignment = HorizontalAlignment.Left };
        copy.Click += (_, _) =>
        {
            if (_shown is null) return;
            Ui.CopyText(_shown.InviteText);
            Ui.Show(_bar, InfoBarSeverity.Success, Ui.T("Invite_Copied"));
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(copy, Ui.T("Invite_Copy"));

        var resultBody = new Grid { ColumnSpacing = 24 };
        resultBody.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        resultBody.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Spacing = 12 };
        left.Children.Add(Ui.Text(Ui.T("Invite_Ready"), "BodyStrongTextBlockStyle"));
        left.Children.Add(copy);
        left.Children.Add(_resultInfo);
        left.Children.Add(_inviteText);
        resultBody.Children.Add(left);
        var right = new StackPanel { Spacing = 6 };
        right.Children.Add(_qrHost);
        right.Children.Add(Ui.Secondary(Ui.T("Invite_QrHelp")));
        right.MaxWidth = 260;
        Grid.SetColumn(right, 1);
        resultBody.Children.Add(right);
        _result = Ui.Card(resultBody);
        _result.Visibility = Visibility.Collapsed;

        var historyPanel = new StackPanel { Spacing = 10 };
        historyPanel.Children.Add(Ui.Text(Ui.T("Invite_History"), "BodyStrongTextBlockStyle"));
        historyPanel.Children.Add(_history);

        Root.Children.Add(Ui.Text(Ui.T("Nav_Invite"), "TitleTextBlockStyle"));
        Root.Children.Add(_bar);
        Root.Children.Add(Ui.Card(formPanel));
        Root.Children.Add(_result);
        Root.Children.Add(Ui.Card(historyPanel));

        Loaded += async (_, _) => await LoadHistoryAsync();
    }

    private static string LifetimeLabel(TimeSpan lifetime) =>
        lifetime.TotalHours < 1 ? Ui.T("Invite_Minutes", (int)lifetime.TotalMinutes)
        : lifetime.TotalDays < 1 ? (lifetime.TotalHours == 1 ? Ui.T("Invite_OneHour") : Ui.T("Invite_Hours", (int)lifetime.TotalHours))
        : Ui.T("Invite_Days", (int)lifetime.TotalDays);

    private async Task CreateAsync()
    {
        var lifetime = (TimeSpan)((ComboBoxItem)_lifetime.SelectedItem).Tag;
        var uses = (int)((ComboBoxItem)_uses.SelectedItem).Tag;
        if (InviteRules.Validate(lifetime, uses) is { } problem)
        {
            Ui.Show(_bar, InfoBarSeverity.Error, Ui.T(problem));
            return;
        }
        _busy.IsActive = true;
        InviteInfo? created = null;
        var ok = await Ui.GuardAsync(async () => created = await App.Admin.CreateInviteAsync(lifetime, uses), _bar);
        _busy.IsActive = false;
        if (!ok || created is null) return;
        Show(created);
        await LoadHistoryAsync();
    }

    private void Show(InviteInfo invite)
    {
        _shown = invite;
        _inviteText.Text = invite.InviteText;
        _resultInfo.Text = Ui.T("Invite_Expires", Ui.When(invite.ExpiresUtc), invite.UsesLeft, invite.UsesAllowed);
        _qrHost.Child = BuildQr(invite.InviteText);
        _result.Visibility = Visibility.Visible;
    }

    /// <summary>The invite as a QR code, black on white with the four-module quiet zone the standard asks for, whatever the app theme.</summary>
    private static UIElement BuildQr(string text)
    {
        var qr = QrCode.EncodeText(text, QrEcc.Medium);
        const int quiet = 4;
        const double cell = 5;
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        for (var y = 0; y < qr.Size; y++)
            for (var x = 0; x < qr.Size; x++)
                if (qr.GetModule(x, y))
                    group.Children.Add(new RectangleGeometry { Rect = new Rect((x + quiet) * cell, (y + quiet) * cell, cell, cell) });
        var side = (qr.Size + 2 * quiet) * cell;
        var path = new Shapes.Path { Data = group, Fill = new SolidColorBrush(Colors.Black), Width = side, Height = side };
        var canvas = new Canvas { Width = side, Height = side };
        canvas.Children.Add(path);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(canvas, Ui.T("Invite_QrLabel"));
        return canvas;
    }

    private async Task LoadHistoryAsync()
    {
        IReadOnlyList<InviteInfo> invites = [];
        if (!await Ui.GuardAsync(async () => invites = await App.Admin.ListInvitesAsync(), _bar)) return;
        _history.Children.Clear();
        var now = DateTimeOffset.UtcNow;
        foreach (var invite in invites.OrderByDescending(i => i.ExpiresUtc))
        {
            var status = InviteRules.StatusOf(invite, now);
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var info = new StackPanel { Spacing = 2 };
            var statusText = status switch { InviteStatus.Active => Ui.T("Invite_Active"), InviteStatus.Expired => Ui.T("Invite_Expired"), _ => Ui.T("Invite_UsedUp") };
            info.Children.Add(Ui.Text($"{statusText}  ·  {Ui.T("Invite_Uses2", invite.UsesLeft, invite.UsesAllowed)}", semiBold: true));
            info.Children.Add(Ui.Secondary(Ui.T("Invite_ExpiresShort", Ui.When(invite.ExpiresUtc))));
            row.Children.Add(info);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var captured = invite;
            actions.Children.Add(Ui.SmallButton(Ui.T("Invite_Show"), (_, _) => Show(captured)));
            actions.Children.Add(Ui.SmallButton(Ui.T("Invite_CopyShort"), (_, _) => { Ui.CopyText(captured.InviteText); Ui.Show(_bar, InfoBarSeverity.Success, Ui.T("Invite_Copied")); }));
            if (status == InviteStatus.Active)
                actions.Children.Add(Ui.SmallButton(Ui.T("Invite_Revoke"), async (_, _) => await RevokeAsync(captured)));
            Grid.SetColumn(actions, 1);
            row.Children.Add(actions);
            _history.Children.Add(row);
        }
        if (invites.Count == 0) _history.Children.Add(Ui.Secondary(Ui.T("Invite_NoHistory")));
    }

    private async Task RevokeAsync(InviteInfo invite)
    {
        if (!await Ui.ConfirmAsync(XamlRoot, Ui.T("Invite_RevokeTitle"), Ui.T("Invite_RevokeBody"), Ui.T("Invite_Revoke"))) return;
        if (await Ui.GuardAsync(() => App.Admin.RevokeInviteAsync(invite.Id), _bar))
        {
            if (_shown?.Id == invite.Id) _result.Visibility = Visibility.Collapsed;
            await LoadHistoryAsync();
        }
    }
}
