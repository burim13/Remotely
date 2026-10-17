using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Remotely.UI.Controls;

/// <summary>
/// A code typed in groups, like <c>K7P4-4NX2-QMH9</c> (at most four groups). Typing moves on to the next group when one is full; Backspace in an empty group goes back;
/// pasting a whole code anywhere fills every group. Case, dashes and spaces are ignored, and characters outside <see cref="Alphabet"/> are dropped. The rules live in <see cref="TokenFormat"/>.
/// </summary>
public sealed partial class TokenBox : UserControl
{
    private const int MaxGroups = 4;

    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(nameof(Header), typeof(string), typeof(TokenBox), new PropertyMetadata("", (d, _) => ((TokenBox)d).ApplyLayout()));
    public static readonly DependencyProperty GroupCountProperty = DependencyProperty.Register(nameof(GroupCount), typeof(int), typeof(TokenBox), new PropertyMetadata(3, (d, _) => ((TokenBox)d).ApplyLayout()));
    public static readonly DependencyProperty GroupLengthProperty = DependencyProperty.Register(nameof(GroupLength), typeof(int), typeof(TokenBox), new PropertyMetadata(4, (d, _) => ((TokenBox)d).ApplyLayout()));
    public static readonly DependencyProperty AlphabetProperty = DependencyProperty.Register(nameof(Alphabet), typeof(string), typeof(TokenBox), new PropertyMetadata(""));
    public static readonly DependencyProperty ErrorMessageProperty = DependencyProperty.Register(nameof(ErrorMessage), typeof(string), typeof(TokenBox), new PropertyMetadata("", (d, _) => ((TokenBox)d).ShowError()));

    private readonly TextBox[] _boxes;
    private readonly TextBlock[] _separators;
    private bool _busy;

    public TokenBox()
    {
        InitializeComponent();
        _boxes = [G0, G1, G2, G3];
        _separators = [S0, S1, S2];
        for (var i = 0; i < _boxes.Length; i++)
        {
            var index = i;
            var box = _boxes[i];
            box.TextChanged += (_, _) => OnGroupChanged(index);
            box.KeyDown += (_, e) => OnGroupKeyDown(index, e);
            box.Paste += (_, e) => OnPaste(index, e);
            box.GotFocus += (_, _) => box.SelectAll();
        }
        ApplyLayout();
        ShowError();
    }

    /// <summary>Raised when the text changes (typing, paste or <see cref="Value"/>).</summary>
    public event EventHandler? ValueChanged;

    public string Header { get => (string)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    /// <summary>Number of groups, 1 to 4.</summary>
    public int GroupCount { get => (int)GetValue(GroupCountProperty); set => SetValue(GroupCountProperty, value); }
    public int GroupLength { get => (int)GetValue(GroupLengthProperty); set => SetValue(GroupLengthProperty, value); }
    /// <summary>The characters a code can contain (upper case). Empty means any letter or digit.</summary>
    public string Alphabet { get => (string)GetValue(AlphabetProperty); set => SetValue(AlphabetProperty, value); }
    /// <summary>When not empty the box is shown as invalid with this text under it.</summary>
    public string ErrorMessage { get => (string)GetValue(ErrorMessageProperty); set => SetValue(ErrorMessageProperty, value); }

    private int Count => Math.Clamp(GroupCount, 1, MaxGroups);
    private int Length => Math.Max(1, GroupLength);

    /// <summary>The characters without separators, for example <c>K7P44NX2QMH9</c>.</summary>
    public string Value
    {
        get => string.Concat(Groups());
        set { SetGroups(TokenFormat.Split(TokenFormat.Normalize(value, Alphabet), Length, Count)); }
    }

    /// <summary>The characters with dashes, for example <c>K7P4-4NX2-QMH9</c>.</summary>
    public string DisplayValue => TokenFormat.Display(Groups());

    public bool IsComplete => Groups().All(g => g.Length == Length);

    public void FocusFirst() => _boxes[0].Focus(FocusState.Programmatic);

    private string[] Groups() => _boxes.Take(Count).Select(b => b.Text ?? "").ToArray();

    private void SetGroups(string[] groups)
    {
        _busy = true;
        try { for (var i = 0; i < Count; i++) _boxes[i].Text = i < groups.Length ? groups[i] : ""; }
        finally { _busy = false; }
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyLayout()
    {
        if (HeaderText is null) return;
        HeaderText.Text = Header ?? "";
        HeaderText.Visibility = string.IsNullOrEmpty(Header) ? Visibility.Collapsed : Visibility.Visible;
        for (var i = 0; i < _boxes.Length; i++)
        {
            var visible = i < Count;
            _boxes[i].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            _boxes[i].MaxLength = Length;
            _boxes[i].Width = Length * 12 + 36;
            AutomationProperties.SetName(_boxes[i], $"{Header} {i + 1}/{Count}");
        }
        for (var i = 0; i < _separators.Length; i++)
        {
            _separators[i].Text = "-";
            _separators[i].Visibility = i < Count - 1 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ShowError()
    {
        if (ErrorText is null) return;
        ErrorText.Text = ErrorMessage ?? "";
        var invalid = !string.IsNullOrEmpty(ErrorMessage);
        ErrorRow.Visibility = invalid ? Visibility.Visible : Visibility.Collapsed;
        VisualStateManager.GoToState(this, invalid ? "Invalid" : "Valid", false);
    }

    private void OnGroupChanged(int index)
    {
        if (_busy) return;
        var box = _boxes[index];
        var typed = box.Text ?? "";
        var (groups, focus) = TokenFormat.Apply(Groups(), index, typed, Length, Alphabet);
        if (groups[index] != typed || focus != index || groups.Where((g, i) => i != index && g != _boxes[i].Text).Any())
        {
            _busy = true;
            try
            {
                for (var i = 0; i < Count; i++) _boxes[i].Text = groups[i];
                box.SelectionStart = groups[index].Length;
            }
            finally { _busy = false; }
            if (focus != index) _boxes[focus].Focus(FocusState.Keyboard);
        }
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnGroupKeyDown(int index, KeyRoutedEventArgs e)
    {
        var box = _boxes[index];
        if (e.Key == VirtualKey.Back && box.Text.Length == 0 && index > 0)
        {
            _boxes[index - 1].Focus(FocusState.Keyboard);
            _boxes[index - 1].SelectionStart = _boxes[index - 1].Text.Length;
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Left && box.SelectionStart == 0 && box.SelectionLength == 0 && index > 0)
        {
            _boxes[index - 1].Focus(FocusState.Keyboard);
            _boxes[index - 1].SelectionStart = _boxes[index - 1].Text.Length;
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Right && box.SelectionStart == box.Text.Length && index < Count - 1)
        {
            _boxes[index + 1].Focus(FocusState.Keyboard);
            _boxes[index + 1].SelectionStart = 0;
            e.Handled = true;
        }
    }

    private async void OnPaste(int index, TextControlPasteEventArgs e)
    {
        e.Handled = true;   // decided before the first await: the box must not paste the raw text itself
        string? text = null;
        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Text)) text = await content.GetTextAsync();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException) { }
        if (string.IsNullOrEmpty(text)) return;
        var normalized = TokenFormat.Normalize(text, Alphabet);
        if (normalized.Length == 0) return;
        // More than one group of text is a whole code: it replaces everything, whichever box it was pasted into.
        var start = normalized.Length > Length ? 0 : index;
        var (groups, focus) = TokenFormat.Apply(normalized.Length > Length ? new string[Count].Select(_ => "").ToArray() : Groups(), start, normalized, Length, Alphabet);
        SetGroups(groups);
        _boxes[Math.Min(focus, Count - 1)].Focus(FocusState.Keyboard);
    }
}
