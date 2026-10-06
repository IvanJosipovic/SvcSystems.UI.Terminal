using Avalonia.Input;
using Xunit;

namespace SvcSystems.UI.Terminal.Tests;

public sealed class KeyEventTranslatorTests
{
    [Theory]
    [InlineData(Key.OemTilde, "~", PhysicalKey.Backquote, "Backquote")]
    [InlineData(Key.OemMinus, "_", PhysicalKey.Minus, "Minus")]
    [InlineData(Key.OemPlus, "+", PhysicalKey.Equal, "Equal")]
    [InlineData(Key.OemOpenBrackets, "{", PhysicalKey.BracketLeft, "BracketLeft")]
    [InlineData(Key.OemCloseBrackets, "}", PhysicalKey.BracketRight, "BracketRight")]
    [InlineData(Key.OemPipe, "|", PhysicalKey.Backslash, "Backslash")]
    [InlineData(Key.OemSemicolon, ":", PhysicalKey.Semicolon, "Semicolon")]
    [InlineData(Key.OemQuotes, "\"", PhysicalKey.Quote, "Quote")]
    [InlineData(Key.OemComma, "<", PhysicalKey.Comma, "Comma")]
    [InlineData(Key.OemPeriod, ">", PhysicalKey.Period, "Period")]
    [InlineData(Key.OemQuestion, "?", PhysicalKey.Slash, "Slash")]
    [InlineData(Key.OemBackslash, ">", PhysicalKey.IntlBackslash, "IntlBackslash")]
    [InlineData(Key.AbntC1, "_", PhysicalKey.IntlRo, "IntlRo")]
    [InlineData(Key.OemPipe, "|", PhysicalKey.IntlYen, "IntlYen")]
    public void ShiftedPunctuation_IsCodedByItsPosition(Key key, string keySymbol, PhysicalKey physicalKey, string expectedCode)
    {
        var e = new KeyEventArgs
        {
            Key = key,
            KeyModifiers = KeyModifiers.Shift,
            KeySymbol = keySymbol,
            PhysicalKey = physicalKey,
        };

        var keyEvent = KeyEventTranslator.Translate(e, optionAsMetaKey: false);

        Assert.Equal(keySymbol, keyEvent.Key);
        Assert.Equal(expectedCode, keyEvent.Code);
        Assert.True(keyEvent.ShiftKey);
    }
}
