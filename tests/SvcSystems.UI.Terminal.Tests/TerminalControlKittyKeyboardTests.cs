using Avalonia;
using Avalonia.Input;
using SvcSystems.UI.Terminal;
using SvcSystems.UI.Terminal.Samples;
using System.Text;
using Xunit;

namespace SvcSystems.UI.Terminal.Tests;

public sealed class TerminalControlKittyKeyboardTests : AvaloniaTestBase
{
    private const string Disambiguate = "\u001b[>1u";
    private const string DisambiguateAndEventTypes = "\u001b[>3u";
    private const string DisambiguateAndAlternateKeys = "\u001b[>5u";
    private const string AllKeysAsEscapeCodes = "\u001b[>9u";
    private const string AllKeysWithAssociatedText = "\u001b[>25u";
    private const string PopFlags = "\u001b[<u";
    private const string ApplicationCursorKeys = "\u001b[?1h";

    [Theory]
    [MemberData(nameof(PressCases))]
    public Task KeyPress_IsEncodedWithTheFlagsTheApplicationSet(
        string flags,
        Key key,
        KeyModifiers modifiers,
        string keySymbol,
        PhysicalKey physicalKey,
        string expected)
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out var sent);
            model.Feed(flags);

            var args = control.SimulateKeyDown(key, modifiers, keySymbol, physicalKey);

            Assert.Equal([expected], sent);
            Assert.True(args.Handled);
        });
    }

    [Theory]
    [InlineData(Key.A, KeyModifiers.None, "a", PhysicalKey.A)]
    [InlineData(Key.A, KeyModifiers.Shift, "A", PhysicalKey.A)]
    [InlineData(Key.D1, KeyModifiers.Shift, "!", PhysicalKey.Digit1)]
    [InlineData(Key.LeftShift, KeyModifiers.Shift, "", PhysicalKey.ShiftLeft)]
    [InlineData(Key.CapsLock, KeyModifiers.None, "", PhysicalKey.CapsLock)]
    public Task KeyPress_ThatProducesTextOrNothing_IsLeftToTextInput(
        Key key,
        KeyModifiers modifiers,
        string keySymbol,
        PhysicalKey physicalKey)
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out var sent);
            model.Feed(Disambiguate);

            var args = control.SimulateKeyDown(key, modifiers, keySymbol, physicalKey);

            Assert.Empty(sent);
            Assert.False(args.Handled);
        });
    }

    [Fact]
    public Task TextInput_StillSendsPrintableCharacters()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out var sent);
            model.Feed(Disambiguate);

            control.SimulateKeyDown(Key.A, KeyModifiers.None, "a", PhysicalKey.A);
            control.SimulateTextInput("a");

            Assert.Equal(["a"], sent);
        });
    }

    [Fact]
    public Task Space_WithoutModifiers_IsSentOnce()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out var sent);
            model.Feed(Disambiguate);

            var args = control.SimulateKeyDown(Key.Space, KeyModifiers.None, " ", PhysicalKey.Space);

            Assert.Equal([" "], sent);
            Assert.True(args.Handled);
        });
    }

    [Fact]
    public Task KeyRelease_IsReportedWhenTheApplicationAsksForEventTypes()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out var sent);
            model.Feed(DisambiguateAndEventTypes);

            control.SimulateKeyDown(Key.Escape, physicalKey: PhysicalKey.Escape);
            var released = control.SimulateKeyUp(Key.Escape, physicalKey: PhysicalKey.Escape);

            Assert.Equal(["\u001b[27u", "\u001b[27;1:3u"], sent);
            Assert.True(released.Handled);
        });
    }

    [Fact]
    public Task KeyHeldDown_IsReportedAsARepeat()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out var sent);
            model.Feed(DisambiguateAndEventTypes);

            control.SimulateKeyDown(Key.Escape, physicalKey: PhysicalKey.Escape);
            control.SimulateKeyDown(Key.Escape, physicalKey: PhysicalKey.Escape);
            control.SimulateKeyUp(Key.Escape, physicalKey: PhysicalKey.Escape);
            control.SimulateKeyDown(Key.Escape, physicalKey: PhysicalKey.Escape);

            Assert.Equal(["\u001b[27u", "\u001b[27;1:2u", "\u001b[27;1:3u", "\u001b[27u"], sent);
        });
    }

    [Fact]
    public Task KeyRelease_UsesTheTextTheKeyProducedWhenPressed()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out var sent);
            model.Feed(DisambiguateAndEventTypes);

            control.SimulateKeyDown(Key.OemSemicolon, KeyModifiers.None, ";", PhysicalKey.Semicolon);
            control.SimulateKeyUp(Key.OemSemicolon, physicalKey: PhysicalKey.Semicolon);

            Assert.Equal(["\u001b[59;1:3u"], sent);
        });
    }

    [Fact]
    public Task KeyRelease_OfEnter_IsNotReportedUnlessAllKeysAre()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out var sent);
            model.Feed(DisambiguateAndEventTypes);

            control.SimulateKeyDown(Key.Enter, physicalKey: PhysicalKey.Enter);
            var released = control.SimulateKeyUp(Key.Enter, physicalKey: PhysicalKey.Enter);

            Assert.Equal(["\r"], sent);
            Assert.False(released.Handled);
        });
    }

    [Fact]
    public Task KeyRelease_IsNotReportedWithoutEventTypes()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out var sent);
            model.Feed(Disambiguate);

            control.SimulateKeyDown(Key.Escape, physicalKey: PhysicalKey.Escape);
            var released = control.SimulateKeyUp(Key.Escape, physicalKey: PhysicalKey.Escape);

            Assert.Equal(["\u001b[27u"], sent);
            Assert.False(released.Handled);
        });
    }

    [Fact]
    public Task KeyRelease_WithoutAPressUnderTheProtocol_IsNotReported()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out var sent);

            control.SimulateKeyDown(Key.Escape, physicalKey: PhysicalKey.Escape);
            model.Feed(DisambiguateAndEventTypes);
            var released = control.SimulateKeyUp(Key.Escape, physicalKey: PhysicalKey.Escape);

            Assert.Equal(["\u001b"], sent);
            Assert.False(released.Handled);
        });
    }

    [Fact]
    public Task KeyRelease_OfAReportedKey_IsReportedWithMetaHeld()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out var sent);
            model.Feed(DisambiguateAndEventTypes);

            control.SimulateKeyDown(Key.C, KeyModifiers.Control, "c", PhysicalKey.C);
            control.SimulateKeyUp(Key.C, KeyModifiers.Control | KeyModifiers.Meta, "c", PhysicalKey.C);

            Assert.Equal(["\u001b[99;5u", "\u001b[99;13:3u"], sent);
        });
    }

    [Fact]
    public Task KeyReleasedWhileTheProtocolWasOff_IsPressedAgainNotRepeated()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out var sent);
            model.Feed(DisambiguateAndEventTypes);

            control.SimulateKeyDown(Key.Escape, physicalKey: PhysicalKey.Escape);
            model.Feed(PopFlags);
            control.SimulateKeyUp(Key.Escape, physicalKey: PhysicalKey.Escape);
            model.Feed(DisambiguateAndEventTypes);
            control.SimulateKeyDown(Key.Escape, physicalKey: PhysicalKey.Escape);

            Assert.Equal(["\u001b[27u", "\u001b[27u"], sent);
        });
    }

    [Fact]
    public Task LosingFocus_ReleasesTheKeysThatWereDown()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out var sent);
            model.Feed(DisambiguateAndEventTypes);

            control.SimulateKeyDown(Key.Escape, physicalKey: PhysicalKey.Escape);
            control.SimulateLostFocus();
            control.SimulateKeyDown(Key.Escape, physicalKey: PhysicalKey.Escape);

            Assert.Equal(["\u001b[27u", "\u001b[27;1:3u", "\u001b[27u"], sent);
        });
    }

    [Fact]
    public Task LosingFocus_SendsNothingWithoutEventTypes()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out var sent);
            model.Feed(Disambiguate);

            control.SimulateKeyDown(Key.Escape, physicalKey: PhysicalKey.Escape);
            control.SimulateLostFocus();

            Assert.Equal(["\u001b[27u"], sent);
        });
    }

    [Fact]
    public Task LegacyEncoding_IsUsedUntilTheApplicationSetsFlags()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out _, out var sent);

            control.SimulateKeyDown(Key.Escape, physicalKey: PhysicalKey.Escape);
            var released = control.SimulateKeyUp(Key.Escape, physicalKey: PhysicalKey.Escape);

            Assert.Equal(["\u001b"], sent);
            Assert.False(released.Handled);
        });
    }

    [Fact]
    public Task LegacyEncoding_ReturnsWhenTheApplicationPopsItsFlags()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out var sent);
            model.Feed(Disambiguate);
            model.Feed(PopFlags);

            control.SimulateKeyDown(Key.Escape, physicalKey: PhysicalKey.Escape);

            Assert.Equal(["\u001b"], sent);
        });
    }

    [Fact]
    public Task LegacyEncoding_IsKeptWhenTheProtocolIsDisabled()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out var sent, new TerminalOptions { KittyKeyboardEnabled = false });
            model.Feed(Disambiguate);

            control.SimulateKeyDown(Key.Escape, physicalKey: PhysicalKey.Escape);

            Assert.Equal(["\u001b"], sent);
        });
    }

    [Fact]
    public Task PageUp_StillScrollsTheViewport()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out var sent);
            TerminalSamples.LoadScrollSample(model);
            model.Feed(Disambiguate);
            var before = model.ScrollOffset;

            control.SimulateKeyDown(Key.PageUp, physicalKey: PhysicalKey.PageUp);

            Assert.True(before > 0);
            Assert.Equal(Math.Max(0, before - model.Terminal.Rows), model.ScrollOffset);
            Assert.Empty(sent);
        });
    }

    [Fact]
    public Task PageUp_IsSentWhenTheApplicationTakesTheCursorKeys()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out var sent);
            TerminalSamples.LoadScrollSample(model);
            model.Feed(Disambiguate + ApplicationCursorKeys);
            var before = model.ScrollOffset;

            control.SimulateKeyDown(Key.PageUp, physicalKey: PhysicalKey.PageUp);

            Assert.Equal(["\u001b[5~"], sent);
            Assert.Equal(before, model.ScrollOffset);
        });
    }

    [Fact]
    public Task KeyEvents_WithoutAModel_AreIgnored()
    {
        return RunInHeadlessSession(() =>
        {
            var control = new TestableTerminalControl();

            var pressed = control.SimulateKeyDown(Key.Escape, physicalKey: PhysicalKey.Escape);
            var released = control.SimulateKeyUp(Key.Escape, physicalKey: PhysicalKey.Escape);

            Assert.False(pressed.Handled);
            Assert.False(released.Handled);
        });
    }

    [Fact]
    public Task KeyPress_WithMeta_IsLeftToTheHost()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out var sent);
            model.Feed(AllKeysAsEscapeCodes);

            var args = control.SimulateKeyDown(Key.C, KeyModifiers.Meta, "c", PhysicalKey.C);

            Assert.Empty(sent);
            Assert.False(args.Handled);
        });
    }

    [Fact]
    public Task OptionAsMetaKey_False_SendsTheComposedCharacter()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out var sent);
            model.OptionAsMetaKey = false;
            model.Feed(Disambiguate);

            var args = control.SimulateKeyDown(Key.A, KeyModifiers.Alt, "å", PhysicalKey.A);

            Assert.Equal(["å"], sent);
            Assert.True(args.Handled);
        });
    }

    public static IEnumerable<object[]> PressCases()
    {
        yield return [Disambiguate, Key.Escape, KeyModifiers.None, string.Empty, PhysicalKey.Escape, "\u001b[27u"];
        yield return [Disambiguate, Key.Enter, KeyModifiers.None, "\r", PhysicalKey.Enter, "\r"];
        yield return [Disambiguate, Key.Enter, KeyModifiers.Shift, "\r", PhysicalKey.Enter, "\u001b[13;2u"];
        yield return [Disambiguate, Key.Enter, KeyModifiers.None, "\r", PhysicalKey.NumPadEnter, "\u001b[57414u"];
        yield return [Disambiguate, Key.Tab, KeyModifiers.None, "\t", PhysicalKey.Tab, "\t"];
        yield return [Disambiguate, Key.Tab, KeyModifiers.Shift, string.Empty, PhysicalKey.Tab, "\u001b[9;2u"];
        yield return [Disambiguate, Key.OemBackTab, KeyModifiers.None, string.Empty, PhysicalKey.Tab, "\u001b[9;2u"];
        yield return [Disambiguate, Key.Back, KeyModifiers.None, string.Empty, PhysicalKey.Backspace, "\u007f"];
        yield return [Disambiguate, Key.Back, KeyModifiers.Control, string.Empty, PhysicalKey.Backspace, "\u001b[127;5u"];
        yield return [Disambiguate, Key.C, KeyModifiers.Control, "c", PhysicalKey.C, "\u001b[99;5u"];
        yield return [Disambiguate, Key.C, KeyModifiers.Control, "\u0003", PhysicalKey.C, "\u001b[99;5u"];
        yield return [Disambiguate, Key.C, KeyModifiers.Control, string.Empty, PhysicalKey.None, "\u001b[99;5u"];
        yield return [Disambiguate, Key.C, KeyModifiers.Control | KeyModifiers.Shift, "C", PhysicalKey.C, "\u001b[99;6u"];
        yield return [Disambiguate, Key.A, KeyModifiers.Control | KeyModifiers.Shift, "A", PhysicalKey.Q, "\u001b[97;6u"];
        yield return [Disambiguate, Key.A, KeyModifiers.Alt, "a", PhysicalKey.A, "\u001b[97;3u"];
        yield return [Disambiguate, Key.A, KeyModifiers.Alt, "å", PhysicalKey.A, "\u001b[97;3u"];
        yield return [Disambiguate, Key.Space, KeyModifiers.Control, " ", PhysicalKey.Space, "\u001b[32;5u"];
        yield return [Disambiguate, Key.OemOpenBrackets, KeyModifiers.Control, "[", PhysicalKey.BracketLeft, "\u001b[91;5u"];
        yield return [Disambiguate, Key.Up, KeyModifiers.None, string.Empty, PhysicalKey.ArrowUp, "\u001b[A"];
        yield return [Disambiguate, Key.Up, KeyModifiers.Control, string.Empty, PhysicalKey.ArrowUp, "\u001b[1;5A"];
        yield return [Disambiguate, Key.Home, KeyModifiers.Shift, string.Empty, PhysicalKey.Home, "\u001b[1;2H"];
        yield return [Disambiguate, Key.Delete, KeyModifiers.None, string.Empty, PhysicalKey.Delete, "\u001b[3~"];
        yield return [Disambiguate, Key.F1, KeyModifiers.None, string.Empty, PhysicalKey.F1, "\u001bOP"];
        yield return [Disambiguate, Key.F1, KeyModifiers.Shift, string.Empty, PhysicalKey.F1, "\u001b[1;2P"];
        yield return [Disambiguate, Key.F5, KeyModifiers.None, string.Empty, PhysicalKey.F5, "\u001b[15~"];
        yield return [Disambiguate, Key.F12, KeyModifiers.Control, string.Empty, PhysicalKey.F12, "\u001b[24;5~"];
        yield return [Disambiguate, Key.F13, KeyModifiers.None, string.Empty, PhysicalKey.F13, "\u001b[57376u"];
        yield return [Disambiguate, Key.NumPad1, KeyModifiers.None, "1", PhysicalKey.NumPad1, "\u001b[57400u"];
        yield return [Disambiguate, Key.NumPad1, KeyModifiers.None, "1", PhysicalKey.None, "\u001b[57400u"];
        yield return [Disambiguate, Key.Add, KeyModifiers.None, "+", PhysicalKey.NumPadAdd, "\u001b[57413u"];

        yield return [Disambiguate, Key.Down, KeyModifiers.None, string.Empty, PhysicalKey.ArrowDown, "\u001b[B"];
        yield return [Disambiguate, Key.Right, KeyModifiers.None, string.Empty, PhysicalKey.ArrowRight, "\u001b[C"];
        yield return [Disambiguate, Key.Left, KeyModifiers.None, string.Empty, PhysicalKey.ArrowLeft, "\u001b[D"];
        yield return [Disambiguate, Key.End, KeyModifiers.None, string.Empty, PhysicalKey.End, "\u001b[F"];
        yield return [Disambiguate, Key.Insert, KeyModifiers.None, string.Empty, PhysicalKey.Insert, "\u001b[2~"];
        yield return [Disambiguate, Key.PageUp, KeyModifiers.Control, string.Empty, PhysicalKey.PageUp, "\u001b[5;5~"];
        yield return [Disambiguate, Key.PageDown, KeyModifiers.Control, string.Empty, PhysicalKey.PageDown, "\u001b[6;5~"];
        yield return [Disambiguate, Key.D1, KeyModifiers.Control, "1", PhysicalKey.Digit1, "\u001b[49;5u"];
        yield return [Disambiguate, Key.D1, KeyModifiers.Control | KeyModifiers.Shift, "!", PhysicalKey.None, "\u001b[49;6u"];

        yield return [Disambiguate, Key.Decimal, KeyModifiers.None, ".", PhysicalKey.NumPadDecimal, "\u001b[57409u"];
        yield return [Disambiguate, Key.Divide, KeyModifiers.None, "/", PhysicalKey.NumPadDivide, "\u001b[57410u"];
        yield return [Disambiguate, Key.Multiply, KeyModifiers.None, "*", PhysicalKey.NumPadMultiply, "\u001b[57411u"];
        yield return [Disambiguate, Key.Subtract, KeyModifiers.None, "-", PhysicalKey.NumPadSubtract, "\u001b[57412u"];
        yield return [Disambiguate, Key.OemPlus, KeyModifiers.None, "=", PhysicalKey.NumPadEqual, "\u001b[57415u"];
        yield return [Disambiguate, Key.Decimal, KeyModifiers.None, ".", PhysicalKey.None, "\u001b[57409u"];
        yield return [Disambiguate, Key.Divide, KeyModifiers.None, "/", PhysicalKey.None, "\u001b[57410u"];
        yield return [Disambiguate, Key.Multiply, KeyModifiers.None, "*", PhysicalKey.None, "\u001b[57411u"];
        yield return [Disambiguate, Key.Subtract, KeyModifiers.None, "-", PhysicalKey.None, "\u001b[57412u"];
        yield return [Disambiguate, Key.Add, KeyModifiers.None, "+", PhysicalKey.None, "\u001b[57413u"];

        yield return [DisambiguateAndAlternateKeys, Key.A, KeyModifiers.Alt | KeyModifiers.Shift, "A", PhysicalKey.A, "\u001b[97:65;4u"];

        yield return [AllKeysAsEscapeCodes, Key.A, KeyModifiers.None, "a", PhysicalKey.A, "\u001b[97u"];
        yield return [AllKeysAsEscapeCodes, Key.A, KeyModifiers.Shift, "A", PhysicalKey.A, "\u001b[97;2u"];
        yield return [AllKeysAsEscapeCodes, Key.D1, KeyModifiers.Shift, "!", PhysicalKey.Digit1, "\u001b[49;2u"];
        yield return [AllKeysAsEscapeCodes, Key.Enter, KeyModifiers.None, "\r", PhysicalKey.Enter, "\u001b[13u"];
        yield return [AllKeysAsEscapeCodes, Key.Space, KeyModifiers.None, " ", PhysicalKey.Space, "\u001b[32u"];
        yield return [AllKeysAsEscapeCodes, Key.LeftShift, KeyModifiers.Shift, string.Empty, PhysicalKey.ShiftLeft, "\u001b[57441;2u"];
        yield return [AllKeysAsEscapeCodes, Key.RightCtrl, KeyModifiers.Control, string.Empty, PhysicalKey.ControlRight, "\u001b[57448;5u"];
        yield return [AllKeysAsEscapeCodes, Key.LeftAlt, KeyModifiers.Alt, string.Empty, PhysicalKey.None, "\u001b[57443;3u"];
        yield return [AllKeysAsEscapeCodes, Key.CapsLock, KeyModifiers.None, string.Empty, PhysicalKey.CapsLock, "\u001b[57358u"];
        yield return [AllKeysAsEscapeCodes, Key.Scroll, KeyModifiers.None, string.Empty, PhysicalKey.ScrollLock, "\u001b[57359u"];
        yield return [AllKeysAsEscapeCodes, Key.NumLock, KeyModifiers.None, string.Empty, PhysicalKey.NumLock, "\u001b[57360u"];

        yield return [AllKeysAsEscapeCodes, Key.RightShift, KeyModifiers.Shift, string.Empty, PhysicalKey.ShiftRight, "\u001b[57447;2u"];
        yield return [AllKeysAsEscapeCodes, Key.LeftCtrl, KeyModifiers.Control, string.Empty, PhysicalKey.ControlLeft, "\u001b[57442;5u"];
        yield return [AllKeysAsEscapeCodes, Key.LeftAlt, KeyModifiers.Alt, string.Empty, PhysicalKey.AltLeft, "\u001b[57443;3u"];
        yield return [AllKeysAsEscapeCodes, Key.RightAlt, KeyModifiers.Alt, string.Empty, PhysicalKey.AltRight, "\u001b[57449;3u"];
        yield return [AllKeysAsEscapeCodes, Key.LWin, KeyModifiers.None, string.Empty, PhysicalKey.MetaLeft, "\u001b[57444u"];
        yield return [AllKeysAsEscapeCodes, Key.RWin, KeyModifiers.None, string.Empty, PhysicalKey.MetaRight, "\u001b[57450u"];

        yield return [AllKeysAsEscapeCodes, Key.LeftShift, KeyModifiers.Shift, string.Empty, PhysicalKey.None, "\u001b[57441;2u"];
        yield return [AllKeysAsEscapeCodes, Key.RightShift, KeyModifiers.Shift, string.Empty, PhysicalKey.None, "\u001b[57447;2u"];
        yield return [AllKeysAsEscapeCodes, Key.LeftCtrl, KeyModifiers.Control, string.Empty, PhysicalKey.None, "\u001b[57442;5u"];
        yield return [AllKeysAsEscapeCodes, Key.RightCtrl, KeyModifiers.Control, string.Empty, PhysicalKey.None, "\u001b[57448;5u"];
        yield return [AllKeysAsEscapeCodes, Key.RightAlt, KeyModifiers.Alt, string.Empty, PhysicalKey.None, "\u001b[57449;3u"];
        yield return [AllKeysAsEscapeCodes, Key.LWin, KeyModifiers.None, string.Empty, PhysicalKey.None, "\u001b[57444u"];
        yield return [AllKeysAsEscapeCodes, Key.RWin, KeyModifiers.None, string.Empty, PhysicalKey.None, "\u001b[57450u"];

        yield return [AllKeysWithAssociatedText, Key.A, KeyModifiers.None, "a", PhysicalKey.A, "\u001b[97;;97u"];
        yield return [AllKeysWithAssociatedText, Key.A, KeyModifiers.Shift, "A", PhysicalKey.A, "\u001b[97;2;65u"];
    }

    private static TestableTerminalControl CreateControl(
        out TerminalControlModel model,
        out List<string> sent,
        TerminalOptions? options = null)
    {
        model = new TerminalControlModel(options);
        var control = new TestableTerminalControl
        {
            Width = 320,
            Height = 120,
            Model = model,
        };

        control.Measure(new Size(320, 120));
        control.Arrange(new Rect(0, 0, 320, 120));

        List<string> payloads = [];
        model.UserInput += (_, e) => payloads.Add(Encoding.UTF8.GetString(e.Data.Span));
        sent = payloads;
        return control;
    }

    private sealed class TestableTerminalControl : TerminalControl
    {
        public KeyEventArgs SimulateKeyDown(
            Key key,
            KeyModifiers modifiers = KeyModifiers.None,
            string keySymbol = "",
            PhysicalKey physicalKey = PhysicalKey.None)
        {
            var args = CreateKeyEventArgs(key, modifiers, keySymbol, physicalKey);
            OnKeyDown(args);
            return args;
        }

        public KeyEventArgs SimulateKeyUp(
            Key key,
            KeyModifiers modifiers = KeyModifiers.None,
            string keySymbol = "",
            PhysicalKey physicalKey = PhysicalKey.None)
        {
            var args = CreateKeyEventArgs(key, modifiers, keySymbol, physicalKey);
            OnKeyUp(args);
            return args;
        }

        public void SimulateTextInput(string text)
        {
            OnTextInput(new TextInputEventArgs { Text = text });
        }

        public void SimulateLostFocus()
        {
            OnLostFocus(new FocusChangedEventArgs(LostFocusEvent));
        }

        private static KeyEventArgs CreateKeyEventArgs(Key key, KeyModifiers modifiers, string keySymbol, PhysicalKey physicalKey)
        {
            return new KeyEventArgs
            {
                Key = key,
                KeyModifiers = modifiers,
                KeySymbol = string.IsNullOrEmpty(keySymbol) ? null : keySymbol,
                PhysicalKey = physicalKey,
            };
        }
    }
}
