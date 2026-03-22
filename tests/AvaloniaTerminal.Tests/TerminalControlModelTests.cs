using Avalonia.Media;
using AvaloniaTerminal.Samples;
using Xunit;

namespace AvaloniaTerminal.Tests;

public sealed class TerminalControlModelTests : AvaloniaTestBase
{
    [Fact]
    public Task Constructor_InitializesTerminalStateAndBlankViewport()
    {
        return RunInHeadlessSession(() =>
        {
            var model = new TerminalControlModel();

            Assert.NotNull(model.Terminal);
            Assert.NotNull(model.SelectionService);
            Assert.NotNull(model.SearchService);
            Assert.Equal(model.Terminal.Cols * model.Terminal.Rows, model.ConsoleText.Count);
            Assert.Equal(" ", model.ConsoleText[(0, 0)].Text);
            Assert.False(model.CanScroll);
            Assert.Equal(0d, model.ScrollPosition);
            Assert.Equal(1f, model.ScrollThumbsize);
        });
    }

    [Fact]
    public Task Send_ForwardsUtf8PayloadToSubscribers()
    {
        return RunInHeadlessSession(() =>
        {
            var model = new TerminalControlModel();
            byte[]? bytes = null;

            model.UserInput += data => bytes = data;

            model.Send("abc");

            Assert.NotNull(bytes);
            Assert.Equal("abc"u8.ToArray(), bytes);
        });
    }

    [Fact]
    public Task Resize_UsesFallbackSizeAndRemovesOutOfBoundsCells()
    {
        return RunInHeadlessSession(() =>
        {
            var model = new TerminalControlModel();
            (int cols, int rows, double width, double height)? resize = null;
            model.ConsoleText[(999, 999)] = new TextObject { Text = "x" };
            model.SizeChanged += (cols, rows, width, height) => resize = (cols, rows, width, height);

            model.Resize(width: 0, height: 0, textWidth: 8, textHeight: 16);

            Assert.Equal((80, 30, 640d, 480d), resize);
            Assert.Equal(80, model.Terminal.Cols);
            Assert.Equal(30, model.Terminal.Rows);
            Assert.DoesNotContain((999, 999), model.ConsoleText.Keys);
        });
    }

    [Fact]
    public Task Feed_UpdatesTextStylingAndTriggersUiRefresh()
    {
        return RunInHeadlessSession(() =>
        {
            var model = new TerminalControlModel();
            var refreshCount = 0;
            model.UpdateUI = () => refreshCount++;

            model.Feed("\u001b[1;3;4;31;47mA");
            var cell = model.ConsoleText[(0, 0)];

            Assert.Equal("A", cell.Text);
            Assert.Equal(FontWeight.Bold, cell.FontWeight);
            Assert.Equal(FontStyle.Italic, cell.FontStyle);
            Assert.Contains(cell.TextDecorations!, decoration => decoration.Location == TextDecorationLocation.Underline);
            Assert.Equal(Color.Parse("#800000"), AssertBrush(cell.Foreground).Color);
            Assert.Equal(Color.Parse("#C0C0C0"), AssertBrush(cell.Background).Color);
            Assert.Equal(1, refreshCount);
        });
    }

    [Fact]
    public Task Feed_TitleSequenceUpdatesModelTitle()
    {
        return RunInHeadlessSession(() =>
        {
            var model = new TerminalControlModel();

            model.Feed("\u001b]0;Avalonia Terminal\u0007");

            Assert.Equal("Avalonia Terminal", model.Title);
        });
    }

    [Fact]
    public Task ScrollProperties_ReflectScrollbackAndViewportMovement()
    {
        return RunInHeadlessSession(() =>
        {
            var model = new TerminalControlModel();
            model.Resize(width: 40, height: 30, textWidth: 10, textHeight: 10);

            model.Feed("1\r\n2\r\n3\r\n4\r\n5\r\n6");

            Assert.True(model.CanScroll);
            Assert.Equal(1d, model.ScrollPosition);
            Assert.InRange(model.ScrollThumbsize, 0.01f, 0.99f);

            model.Terminal.ScrollLines(-1);

            Assert.InRange(model.ScrollPosition, 0d, 0.99d);
        });
    }

    [Fact]
    public Task Scrolling_KeepsRenderedCellCacheBoundedToViewport()
    {
        return RunInHeadlessSession(() =>
        {
            var model = new TerminalControlModel();
            model.Resize(width: 320, height: 120, textWidth: 8, textHeight: 16);
            TerminalSamples.LoadScrollSample(model);

            var viewportCellCount = model.Terminal.Cols * model.Terminal.Rows;

            Assert.Equal(viewportCellCount, model.ConsoleText.Count);

            model.ScrollToYDisp(model.MaxScrollback / 2);

            Assert.Equal(viewportCellCount, model.ConsoleText.Count);

            model.ScrollToYDisp(model.MaxScrollback);

            Assert.Equal(viewportCellCount, model.ConsoleText.Count);
        });
    }

    [Fact]
    public Task AlternateBuffer_DisablesScrollMetrics()
    {
        return RunInHeadlessSession(() =>
        {
            var model = new TerminalControlModel();
            model.Resize(width: 40, height: 30, textWidth: 10, textHeight: 10);
            model.Feed("1\r\n2\r\n3\r\n4\r\n5\r\n6");

            model.Terminal.Buffers.ActivateAltBuffer(null);

            Assert.False(model.CanScroll);
            Assert.Equal(0d, model.ScrollPosition);
            Assert.Equal(0f, model.ScrollThumbsize);
        });
    }

    private static SolidColorBrush AssertBrush(IBrush brush)
    {
        return Assert.IsType<SolidColorBrush>(brush);
    }
}
