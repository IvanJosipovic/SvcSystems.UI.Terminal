using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using AvaloniaTerminal.Samples;
using System.Text;
using Xunit;

namespace AvaloniaTerminal.Tests;

public sealed class TerminalControlTests : AvaloniaTestBase
{
    [Fact]
    public Task PointerWheel_ScrollsTerminalViewportThroughModel()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out var scrollBar);
            PopulateScrollback(model);

            var before = model.ScrollOffset;

            control.SimulatePointerWheel(new Vector(0, 1));

            Assert.True(before > 0);
            Assert.Equal(before - 1, model.ScrollOffset);
            Assert.Equal(model.ScrollOffset, (int)scrollBar.Value);
            Assert.True(scrollBar.IsEnabled);
        });
    }

    [Fact]
    public Task Caret_TracksViewportPosition()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out _);

            model.Feed("abc");
            var firstRect = control.CaretRect;

            model.Feed("\r\nx");
            var secondRect = control.CaretRect;

            Assert.True(control.HasVisibleCaret);
            Assert.True(firstRect.Width > 0);
            Assert.True(firstRect.X > 0);
            Assert.True(secondRect.Y > firstRect.Y);
            Assert.True(secondRect.X < firstRect.X);
        });
    }

    [Fact]
    public Task Caret_HidesWhenViewportIsScrolledAwayFromCursor()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out _);
            PopulateScrollback(model);

            Assert.True(control.HasVisibleCaret);

            model.ScrollToYDisp(0);

            Assert.False(control.HasVisibleCaret);
        });
    }

    [Fact]
    public Task DragSelection_UpdatesSelectedTextAndControlBinding()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out _);
            TerminalSamples.LoadSelectionSample(model);

            control.HandleSelectionPressed(control.GetCellCenter(0, 0), KeyModifiers.None, clickCount: 1);
            control.HandleSelectionMoved(control.GetCellCenter(8, 0));
            control.HandleSelectionReleased(control.GetCellCenter(8, 0), KeyModifiers.None, clickCount: 1);

            Assert.True(model.HasSelection);
            Assert.Equal("Avalonia", model.SelectedText);
            Assert.True(control.HasSelection);
            Assert.Equal(model.SelectedText, control.SelectedText);
        });
    }

    [Fact]
    public Task DoubleClickSelection_SelectsWord()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out _);
            TerminalSamples.LoadSelectionSample(model);

            control.HandleSelectionPressed(control.GetCellCenter(10, 0), KeyModifiers.None, clickCount: 2);
            control.HandleSelectionReleased(control.GetCellCenter(10, 0), KeyModifiers.None, clickCount: 2);

            Assert.True(model.HasSelection);
            Assert.Equal("Terminal", model.SelectedText);
        });
    }

    [Fact]
    public Task OptionAsMetaKey_False_DoesNotPrefixEscape()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out _);
            model.OptionAsMetaKey = false;

            byte[]? sent = null;
            model.UserInput += bytes => sent = bytes;

            control.SimulateKeyUp(Key.A, KeyModifiers.Alt, "a");

            Assert.NotNull(sent);
            Assert.Equal("a"u8.ToArray(), sent);
        });
    }

    [Fact]
    public Task SearchHelpers_ForwardToModel()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out _);
            model.Feed("alpha beta alpha");

            var count = control.Search("alpha");

            Assert.Equal(2, count);
            Assert.Equal("alpha", control.CopySelection());

            var next = control.SelectNextSearchResult();
            var previous = control.SelectPreviousSearchResult();

            Assert.Equal(1, next);
            Assert.Equal(0, previous);
        });
    }

    [Fact]
    public Task PointerWheel_RepeatedScrollingAcrossScrollSample_DoesNotThrowAndStaysInBounds()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out var scrollBar);
            PopulateScrollback(model);

            var maxScrollback = model.MaxScrollback;

            for (var i = 0; i < maxScrollback + 10; i++)
            {
                control.SimulatePointerWheel(new Vector(0, 1));
            }

            Assert.Equal(0, model.ScrollOffset);
            Assert.Equal(0, (int)scrollBar.Value);

            for (var i = 0; i < maxScrollback + 10; i++)
            {
                control.SimulatePointerWheel(new Vector(0, -1));
            }

            Assert.Equal(maxScrollback, model.ScrollOffset);
            Assert.Equal(maxScrollback, (int)scrollBar.Value);
        });
    }

    [Fact]
    public Task ScrollBar_ValueChangeScrollsTerminalViewport()
    {
        return RunInHeadlessSession(() =>
        {
            _ = CreateControl(out var model, out var scrollBar);
            PopulateScrollback(model);

            var target = Math.Max(0, model.MaxScrollback / 2);

            scrollBar.Value = target;

            Assert.Equal(target, model.ScrollOffset);
            Assert.Equal(target, (int)scrollBar.Value);
            Assert.True(scrollBar.IsEnabled);
        });
    }

    [Fact]
    public Task PageUp_ScrollsTerminalViewportByOnePage()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out var scrollBar);
            PopulateScrollback(model);

            var before = model.ScrollOffset;

            control.SimulateKeyUp(Key.PageUp);

            Assert.True(before > 0);
            Assert.Equal(Math.Max(0, before - model.Terminal.Rows), model.ScrollOffset);
            Assert.Equal(model.ScrollOffset, (int)scrollBar.Value);
        });
    }

    [Fact]
    public Task PageDown_ScrollsTerminalViewportByOnePage()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out var scrollBar);
            PopulateScrollback(model);

            control.SimulateKeyUp(Key.PageUp);
            var before = model.ScrollOffset;

            control.SimulateKeyUp(Key.PageDown);

            Assert.True(before >= 0);
            Assert.Equal(Math.Min(model.MaxScrollback, before + model.Terminal.Rows), model.ScrollOffset);
            Assert.Equal(model.ScrollOffset, (int)scrollBar.Value);
        });
    }

    [Fact]
    public void ShellSample_NormalizesTerminalEnterForRedirectedInput()
    {
        var normalized = ShellControl.NormalizeStandardInput([0x0D]);

        Assert.Equal(Encoding.UTF8.GetBytes(Environment.NewLine), normalized);
    }

    private static TestableTerminalControl CreateControl(
        out TerminalControlModel model,
        out ScrollBar scrollBar)
    {
        model = new TerminalControlModel();
        var control = new TestableTerminalControl
        {
            Width = 320,
            Height = 120,
            Model = model,
        };

        scrollBar = Assert.IsType<ScrollBar>(control.Children.OfType<ScrollBar>().Single());
        control.Measure(new Size(320, 120));
        control.Arrange(new Rect(0, 0, 320, 120));
        return control;
    }

    private static void PopulateScrollback(TerminalControlModel model)
    {
        TerminalSamples.LoadScrollSample(model);
        Assert.True(model.CanScroll);
        Assert.True(model.ScrollOffset > 0);
    }

    private sealed class TestableTerminalControl : TerminalControl
    {
        public void SimulateKeyUp(Key key, KeyModifiers modifiers = KeyModifiers.None, string keySymbol = "")
        {
            var args = new KeyEventArgs
            {
                Key = key,
                KeyModifiers = modifiers,
                KeySymbol = keySymbol,
            };

            OnKeyUp(args);
        }

        public void SimulatePointerWheel(Vector delta)
        {
            var pointer = new Pointer(0, PointerType.Mouse, isPrimary: true);
            var args = new PointerWheelEventArgs(this, pointer, this, new Point(10, 10), 0, PointerPointProperties.None, KeyModifiers.None, delta);
            OnPointerWheelChanged(args);
        }
    }
}
