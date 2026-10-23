using System.Windows.Input;
using Fotobox.Core;
using Xunit;

namespace Fotobox.Tests;

public sealed class KeyChordTests
{
    [Theory]
    [InlineData("Space", Key.Space, ModifierKeys.None)]
    [InlineData("ctrl+alt+a", Key.A, ModifierKeys.Control | ModifierKeys.Alt)]
    [InlineData("Shift+F5", Key.F5, ModifierKeys.Shift)]
    [InlineData("Enter", Key.Enter, ModifierKeys.None)]
    [InlineData("Return", Key.Enter, ModifierKeys.None)]
    [InlineData("PageDown", Key.PageDown, ModifierKeys.None)]
    [InlineData("Strg+B", Key.B, ModifierKeys.Control)]
    public void TryParse_Valid(string text, Key key, ModifierKeys modifiers)
    {
        Assert.True(KeyChord.TryParse(text, out var chord));
        Assert.Equal(new KeyChord(key, modifiers), chord);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Ctrl+A")]
    [InlineData("Win+A")]
    [InlineData("Foo")]
    [InlineData("7")]
    public void TryParse_Invalid(string? text) => Assert.False(KeyChord.TryParse(text, out _));

    [Theory]
    [InlineData("Alt+F4")]
    [InlineData("Alt+Tab")]
    [InlineData("Alt+Space")]
    [InlineData("Ctrl+Escape")]
    [InlineData("Ctrl+Alt+Delete")]
    [InlineData("LeftShift")]
    [InlineData("LWin")]
    public void IsAllowed_RejectsSystemCombinations(string text)
    {
        Assert.True(KeyChord.TryParse(text, out var chord));
        Assert.False(chord.IsAllowed);
    }

    [Fact]
    public void IsAllowed_RejectsWindowsModifier() =>
        Assert.False(new KeyChord(Key.A, ModifierKeys.Windows).IsAllowed);

    [Theory]
    [InlineData("Space")]
    [InlineData("Escape")]
    [InlineData("Ctrl+Alt+A")]
    [InlineData("F4")]
    [InlineData("Shift+Space")]
    public void IsAllowed_AcceptsNormalKeys(string text)
    {
        Assert.True(KeyChord.TryParse(text, out var chord));
        Assert.True(chord.IsAllowed);
    }

    [Fact]
    public void ToString_RoundTrips()
    {
        var chord = new KeyChord(Key.A, ModifierKeys.Shift | ModifierKeys.Control | ModifierKeys.Alt);
        Assert.Equal("Ctrl+Alt+Shift+A", chord.ToString());
        Assert.True(KeyChord.TryParse(chord.ToString(), out var back));
        Assert.Equal(chord, back);
    }

    [Fact]
    public void FromKeyEvent_UsesSystemKeyWhenAltPressed()
    {
        var chord = KeyChord.FromKeyEvent(Key.System, Key.A, ModifierKeys.Control | ModifierKeys.Alt);
        Assert.Equal(new KeyChord(Key.A, ModifierKeys.Control | ModifierKeys.Alt), chord);
    }

    [Theory]
    [InlineData("Space", "Leertaste")]
    [InlineData("Ctrl+Alt+A", "Strg+Alt+A")]
    [InlineData("D5", "5")]
    [InlineData("NumPad3", "Ziffernblock 3")]
    [InlineData("Shift+PageDown", "Umschalt+Bild ab")]
    [InlineData("Escape", "Esc")]
    public void DisplayName_IsGerman(string text, string expected)
    {
        Assert.True(KeyChord.TryParse(text, out var chord));
        Assert.Equal(expected, chord.DisplayName);
    }
}
