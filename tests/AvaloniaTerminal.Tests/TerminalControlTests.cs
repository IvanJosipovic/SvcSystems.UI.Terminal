using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using AvaloniaTerminal.Samples;
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
