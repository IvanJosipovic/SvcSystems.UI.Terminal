using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Controls;
using Xunit;

namespace AvaloniaTerminal.Tests;

public class TerminalControlTests
{
    private class TestTerminalControl : TerminalControl
    {
        public ScrollBar ScrollBar => VerticalScrollBar;
        public void RaisePointerWheel(PointerWheelEventArgs e) => base.OnPointerWheelChanged(e);
    }

    // Verifies that pointer wheel events adjust the terminal's scroll position
    [AvaloniaFact]
    public void PointerWheelScroll_ChangesBufferOffset()
    {
        var model = new TerminalControlModel();
        var control = new TestTerminalControl { Model = model };

        // Populate terminal with enough lines to enable scrolling
        for (var i = 0; i < model.Terminal.Rows + 10; i++)
        {
            model.Feed($"line {i}\n");
        }

        Assert.Equal(0, model.Terminal.Buffer.YDisp);

        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        var properties = new PointerPointProperties();

        // Simulate wheel down (negative Y delta) to scroll up one line
        var scrollUp = new PointerWheelEventArgs(control, pointer, control, new Point(), 0, properties, KeyModifiers.None, new Vector(0, -1));
        control.RaisePointerWheel(scrollUp);
        Assert.Equal(1, model.Terminal.Buffer.YDisp);

        // Simulate wheel up to scroll back down
        var scrollDown = new PointerWheelEventArgs(control, pointer, control, new Point(), 0, properties, KeyModifiers.None, new Vector(0, 1));
        control.RaisePointerWheel(scrollDown);
        Assert.Equal(0, model.Terminal.Buffer.YDisp);
    }

    // Verifies that adjusting the scroll bar updates the buffer offset
    [AvaloniaFact]
    public void ScrollBarValue_ChangesBufferOffset()
    {
        var model = new TerminalControlModel();
        var control = new TestTerminalControl { Model = model };
        control.Measure(new Size(100,100));
        control.Arrange(new Rect(0,0,100,100));

        for (var i = 0; i < model.Terminal.Rows + 50; i++)
        {
            model.Feed($"line {i}\n");
        }

        Assert.Equal(0, model.Terminal.Buffer.YDisp);

        var sb = control.ScrollBar;
        Assert.True(sb.IsVisible);
        sb.Value = sb.Maximum;

        Assert.Equal((int)sb.Maximum, model.Terminal.Buffer.YDisp);
    }
}

