using Remotely.Core.Lan;
using Remotely.UI.Controls;

namespace Remotely.Tests;

/// <summary>The TokenBox text rules (Remotely.UI/Controls/TokenFormat.cs, compiled into this project), checked against the real LanToken rules.</summary>
public class TokenFormatTests
{
    [Theory]
    [InlineData("k7p4-4nx2-qmh9", "K7P44NX2QMH9")]
    [InlineData("  K7P4 4NX2  QMH9 ", "K7P44NX2QMH9")]
    [InlineData("K7P4_4NX2/QMH9\r\n", "K7P44NX2QMH9")]
    public void Paste_is_normalised_to_the_same_value_LanToken_reads(string pasted, string expected)
    {
        var normalized = TokenFormat.Normalize(pasted, LanToken.Alphabet);
        Assert.Equal(expected, normalized);
        Assert.True(LanToken.TryParse(pasted, out var token, out _));
        Assert.Equal(token!.Id + token.Secret, normalized);
    }

    [Fact]
    public void Characters_outside_the_alphabet_are_dropped()
    {
        Assert.Equal("K7P4", TokenFormat.Normalize("K0I7O1P4", LanToken.Alphabet));
        Assert.Equal("K0I7", TokenFormat.Normalize("k0i7"));   // no alphabet: any letter or digit
    }

    [Fact]
    public void Null_and_empty_give_empty()
    {
        Assert.Equal("", TokenFormat.Normalize(null));
        Assert.Equal("", TokenFormat.Normalize(""));
        Assert.Equal(new[] { "", "", "" }, TokenFormat.Split("", 4, 3));
    }

    [Fact]
    public void Split_and_display_round_trip()
    {
        var groups = TokenFormat.Split("K7P44NX2QMH9", 4, 3);
        Assert.Equal(new[] { "K7P4", "4NX2", "QMH9" }, groups);
        Assert.Equal("K7P4-4NX2-QMH9", TokenFormat.Display(groups));
        Assert.Equal("K7P4-4N", TokenFormat.Display(TokenFormat.Split("K7P44N", 4, 3)));
        Assert.Equal("", TokenFormat.Display(TokenFormat.Split("", 4, 3)));
    }

    [Fact]
    public void A_full_group_moves_the_caret_to_the_next_group()
    {
        var (groups, focus) = TokenFormat.Apply(new[] { "", "", "" }, 0, "K7P4", 4);
        Assert.Equal("K7P4", groups[0]);
        Assert.Equal(1, focus);
        var (_, last) = TokenFormat.Apply(new[] { "K7P4", "4NX2", "" }, 2, "QMH9", 4);
        Assert.Equal(2, last);   // nothing after the last group
    }

    [Fact]
    public void A_partial_group_keeps_the_caret()
    {
        var (groups, focus) = TokenFormat.Apply(new[] { "", "", "" }, 0, "k7", 4);
        Assert.Equal("K7", groups[0]);
        Assert.Equal(0, focus);
    }

    [Fact]
    public void Pasting_a_whole_token_into_the_first_group_fills_every_group()
    {
        var (groups, focus) = TokenFormat.Apply(new[] { "", "", "" }, 0, "K7P4-4NX2-QMH9", 4, LanToken.Alphabet);
        Assert.Equal(new[] { "K7P4", "4NX2", "QMH9" }, groups);
        Assert.Equal(2, focus);
    }

    [Fact]
    public void Overflow_past_the_last_group_is_dropped()
    {
        var (groups, _) = TokenFormat.Apply(new[] { "", "", "" }, 0, "K7P44NX2QMH9ZZZZ", 4);
        Assert.Equal(new[] { "K7P4", "4NX2", "QMH9" }, groups);
    }

    [Fact]
    public void Deleting_text_empties_the_group()
    {
        var (groups, focus) = TokenFormat.Apply(new[] { "K7P4", "4NX2", "" }, 1, "", 4);
        Assert.Equal(new[] { "K7P4", "", "" }, groups);
        Assert.Equal(1, focus);
    }
}
