using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using SvcSystems.UI.Terminal;
using XTerm.Graphics;
using Xunit;

namespace SvcSystems.UI.Terminal.Tests;

public sealed class TerminalSixelTests : AvaloniaTestBase
{
    private const string Esc = "\u001b";
    private const string St = Esc + "\\";
    private const string TwoByFourCells = Esc + "P0;1;0q#0;2;100;0;0!4~-!4~" + St;

    [Fact]
    public Task Feed_SixelCreatesOneStripPerPaintedViewportRow()
    {
        return RunInHeadlessSession(() =>
        {
            using var model = CreateSixelModel();
            model.Feed(TwoByFourCells);

            Assert.Equal(4, model.ViewportRows.Take(4).Count(row => row.Sixel is not null));
            Assert.All(model.ViewportRows.Take(4), row => Assert.Equal(2, row.SixelColumns));
            Assert.All(model.ViewportRows.Skip(4), row => Assert.Null(row.Sixel));

            var strips = model.ViewportRows.Take(4).Select(row => row.Sixel).ToArray();
            Assert.Equal(4, strips.Distinct(ReferenceEqualityComparer.Instance).Count());
        });
    }

    [Fact]
    public Task Feed_KittyGraphicsCreatesAPlacedTerminalImage()
    {
        return RunInHeadlessSession(() =>
        {
            using var model = new TerminalControlModel(new TerminalOptions { Cols = 8, Rows = 4 });
            model.Feed(Esc + "_Ga=T,f=32,s=1,v=1;/wAA/w==" + St);

            var line = model.Terminal.Buffer.GetLine(model.Terminal.Buffer.YBase);

            Assert.NotNull(line);
            Assert.Contains(line.Placements, placement => placement.Kind == PlacementKind.Kitty);
            Assert.Single(line.Images);
            Assert.Equal(1, line.Images[0].PixelWidth);
            Assert.Equal(1, line.Images[0].PixelHeight);
        });
    }

    [Fact]
    public Task Feed_SixelKeepsTransparentPixelsAndDecodedColor()
    {
        return RunInHeadlessSession(() =>
        {
            using var model = CreateSixelModel();
            model.Feed(TwoByFourCells);
            var strip = Assert.IsType<SixelStrip>(model.ViewportRows[0].Sixel);
            byte[] pixels = new byte[strip.Stride * strip.CellHeight];

            strip.CopyPixelsTo(pixels, strip.Stride, strip.PaintedColumns);

            Assert.Equal(new byte[] { 0, 0, 255, 255 }, pixels.AsSpan(0, 4).ToArray());
            Assert.Equal(new byte[] { 0, 0, 0, 0 }, pixels.AsSpan(16, 4).ToArray());
        });
    }

    [Fact]
    public Task Feed_AdjacentSixelsMergeIntoTheSameLineStrip()
    {
        return RunInHeadlessSession(() =>
        {
            using var model = CreateSixelModel();
            model.Feed(TwoByFourCells);
            model.Feed(Esc + "[1;3H" + TwoByFourCells);

            Assert.Equal(4, model.ViewportRows[0].SixelColumns);
            Assert.Equal(4, model.ViewportRows[0].Sixel!.PaintedColumns);
            Assert.All(model.ViewportRows.Take(4), row => Assert.Equal(4, row.SixelColumns));
            Assert.Equal(4, model.ViewportRows.Take(4).Select(row => row.Sixel).Distinct(ReferenceEqualityComparer.Instance).Count());
        });
    }

    [Fact]
    public Task Feed_SixelPlacedAfterTextKeepsItsCursorColumnInTheLineStrip()
    {
        return RunInHeadlessSession(() =>
        {
            using var model = CreateSixelModel();
            model.Feed("ab" + Esc + "[1;3H" + TwoByFourCells);

            var row = model.ViewportRows[0];
            var strip = Assert.IsType<SixelStrip>(row.Sixel);

            Assert.Equal(4, row.SixelColumns);
            Assert.False(strip.HasPixelsAt(1));
            Assert.True(strip.HasPixelsAt(2));
            Assert.True(strip.HasPixelsAt(3));
            Assert.True(SixelDrawPlan.TryCreate(
                strip,
                row.SixelColumns,
                row.RowIndex,
                cellWidth: 8,
                cellHeight: 16,
                renderScaling: 1,
                viewport: new Rect(0, 0, 160, 128),
                out var source,
                out var destination));
            Assert.Equal(new Rect(0, 0, 8, 3), source);
            Assert.Equal(new Rect(0, 0, 32, 16), destination);
        });
    }

    [Fact]
    public Task SixelDrawPlanSnapsAdjacentRowsToTheDevicePixelGrid()
    {
        return RunInHeadlessSession(() =>
        {
            using var model = CreateSixelModel();
            model.Feed(TwoByFourCells);
            var firstRow = model.ViewportRows[0];
            var secondRow = model.ViewportRows[1];
            Assert.NotNull(firstRow.Sixel);
            Assert.NotNull(secondRow.Sixel);

            Assert.True(SixelDrawPlan.TryCreate(
                firstRow.Sixel!, firstRow.SixelColumns, firstRow.RowIndex,
                cellWidth: 7.2, cellHeight: 13, renderScaling: 1.25,
                viewport: new Rect(0, 0, 100, 100), out _, out Rect firstDestination));
            Assert.True(SixelDrawPlan.TryCreate(
                secondRow.Sixel!, secondRow.SixelColumns, secondRow.RowIndex,
                cellWidth: 7.2, cellHeight: 13, renderScaling: 1.25,
                viewport: new Rect(0, 0, 100, 100), out _, out Rect secondDestination));

            Assert.Equal(firstDestination.Bottom, secondDestination.Y);
            Assert.Equal(12.8, firstDestination.Height, 6);
            Assert.Equal(13.6, secondDestination.Height, 6);
        });
    }

    [Fact]
    public Task Feed_TextOverSixelClearsOnlyItsCellAndScreenEraseRemovesTheStrip()
    {
        return RunInHeadlessSession(() =>
        {
            using var model = CreateSixelModel();
            model.Feed(TwoByFourCells);
            model.Feed(Esc + "[1;1HX");

            var strip = Assert.IsType<SixelStrip>(model.ViewportRows[0].Sixel);
            Assert.False(strip.HasPixelsAt(0));
            Assert.True(strip.HasPixelsAt(1));

            model.Feed(Esc + "[2J");

            Assert.All(model.ViewportRows, row => Assert.Null(row.Sixel));
        });
    }

    [Fact]
    public Task Feed_EraseLineClearsOnlyTheSixelPixelsOnThatBufferLine()
    {
        return RunInHeadlessSession(() =>
        {
            using var model = CreateSixelModel();
            model.Feed(TwoByFourCells);
            model.Feed(Esc + "[1;1H" + Esc + "[2K");

            Assert.Null(model.ViewportRows[0].Sixel);
            Assert.All(model.ViewportRows.Skip(1).Take(3), row => Assert.NotNull(row.Sixel));
        });
    }

    [Fact]
    public Task SixelDrawPlanClipsTheImageSourceProportionallyToTheViewport()
    {
        return RunInHeadlessSession(() =>
        {
            using var model = CreateSixelModel();
            model.Feed(TwoByFourCells);
            var strip = Assert.IsType<SixelStrip>(model.ViewportRows[0].Sixel);

            Assert.True(SixelDrawPlan.TryCreate(
                strip,
                model.ViewportRows[0].SixelColumns,
                rowIndex: 0,
                cellWidth: 8,
                cellHeight: 16,
                renderScaling: 1,
                viewport: new Rect(0, 0, 8, 16),
                out var source,
                out var destination));

            Assert.Equal(new Rect(0, 0, 2, 3), source);
            Assert.Equal(new Rect(0, 0, 8, 16), destination);
        });
    }

    [Fact]
    public Task Feed_ResizingTheViewportDoesNotDestroyOffscreenSixelColumns()
    {
        return RunInHeadlessSession(() =>
        {
            using var model = CreateSixelModel();
            model.Feed(TwoByFourCells);

            model.Resize(width: 2, height: 24, textWidth: 2, textHeight: 3);
            Assert.Equal(2, model.ViewportRows[0].Sixel!.PaintedColumns);

            model.Resize(width: 40, height: 24, textWidth: 2, textHeight: 3);

            Assert.Equal(20, model.Terminal.Cols);
            Assert.Equal(2, model.ViewportRows[0].Sixel!.PaintedColumns);
            Assert.Equal(2, model.ViewportRows[0].SixelColumns);
        });
    }

    [Fact]
    public Task TerminalControl_PublishesMeasuredCellSizeToTheSixelDecoder()
    {
        return RunInHeadlessSession(() =>
        {
            using var model = CreateSixelModel();
            var control = new TerminalControl { Model = model };
            var measured = control.ConsoleTextSizeForTests;

            Assert.Equal(Math.Max((int)Math.Round(measured.Width), 1), model.Terminal.Options.CellWidthPixels);
            Assert.Equal(Math.Max((int)Math.Round(measured.Height), 1), model.Terminal.Options.CellHeightPixels);
        });
    }

    [Fact]
    public Task SixelBitmapCacheReusesAnUnchangedStripAndReuploadsAChangedVersion()
    {
        return RunInHeadlessSession(() =>
        {
            using var model = CreateSixelModel();
            model.Feed(TwoByFourCells);
            using var cache = new TestSixelBitmapCache();
            var strip = Assert.IsType<SixelStrip>(model.ViewportRows[0].Sixel);
            cache.Value.BeginFrame();

            var first = cache.Value.GetOrCreate(strip, model.ViewportRows[0].SixelColumns);
            var unchanged = cache.Value.GetOrCreate(strip, model.ViewportRows[0].SixelColumns);
            var firstVersion = strip.Version;

            Assert.Same(first, unchanged);
            Assert.Equal(1, cache.Value.UploadCount);
            Assert.Equal(2 * 2 * 3 * sizeof(uint), cache.Value.CachedPixelBytes);

            model.Feed(Esc + "[1;1H" + TwoByFourCells);
            var updatedStrip = Assert.IsType<SixelStrip>(model.ViewportRows[0].Sixel);
            Assert.Same(strip, updatedStrip);
            Assert.NotEqual(firstVersion, updatedStrip.Version);
            var updated = cache.Value.GetOrCreate(updatedStrip, model.ViewportRows[0].SixelColumns);

            Assert.NotSame(first, updated);
            Assert.Equal(2, cache.Value.UploadCount);
        });
    }

    [Fact]
    public Task SixelBitmapCacheReleasesStripsThatLeaveTheViewport()
    {
        return RunInHeadlessSession(() =>
        {
            using var model = CreateSixelModel();
            model.Feed(TwoByFourCells);
            using var cache = new TestSixelBitmapCache();
            cache.Value.BeginFrame();
            cache.Value.GetOrCreate(Assert.IsType<SixelStrip>(model.ViewportRows[0].Sixel), model.ViewportRows[0].SixelColumns);
            cache.Value.EndFrame();
            Assert.NotEqual(0, cache.Value.CachedPixelBytes);

            model.Feed(Esc + "[2J");
            cache.Value.BeginFrame();
            cache.Value.EndFrame();

            Assert.Equal(0, cache.Value.CachedPixelBytes);
        });
    }

    [Fact]
    public Task Render_DrawsSixelBitmapAndStillCompletesOnHeadlessDrawingContexts()
    {
        return RunInHeadlessSession(() =>
        {
            using var model = CreateSixelModel();
            var control = new TerminalControl { Model = model, Width = 240, Height = 120 };
            control.Measure(new Size(240, 120));
            control.Arrange(new Rect(0, 0, 240, 120));
            model.Feed(TwoByFourCells);

            using var bitmap = new RenderTargetBitmap(new PixelSize(240, 120));
            bitmap.Render(control);

            Assert.True(control.SixelBitmapUploadCountForTests > 0);
            Assert.True(control.SixelBitmapBytesForTests > 0);
            var pixel = BitmapPixelAssertions.GetBgraPixel(bitmap, 1, 1);
            Assert.Equal((byte)255, pixel.Red);
            Assert.Equal((byte)0, pixel.Green);
            Assert.Equal((byte)0, pixel.Blue);
            Assert.Equal((byte)255, pixel.Alpha);
        });
    }

    [Fact]
    public Task Render_HeadlessDrawingCompletesWithSixelsSelectionAndCaret()
    {
        return RunInHeadlessSession(() =>
        {
            using var model = CreateSixelModel();
            var control = new TerminalControl { Model = model, Width = 240, Height = 120 };
            control.Measure(new Size(240, 120));
            control.Arrange(new Rect(0, 0, 240, 120));
            model.Feed("X");
            model.Feed(Esc + "[1;1H" + TwoByFourCells);
            control.SelectionBrush = Avalonia.Media.Brushes.Blue;
            model.SelectAll();

            using var bitmap = new RenderTargetBitmap(new PixelSize(240, 120));
            bitmap.Render(control);

            Assert.True(model.HasSelection);
            Assert.True(control.SixelBitmapUploadCountForTests > 0);
            Assert.True(control.SixelBitmapBytesForTests > 0);
            Assert.True(control.CanRenderTextForTests);
        });
    }

    [Fact]
    public Task RedrawIntervalIsPerControlAndRejectsNegativeValues()
    {
        return RunInHeadlessSession(() =>
        {
            var first = new TerminalControl();
            var second = new TerminalControl();

            Assert.Equal(TimeSpan.FromSeconds(1d / 30), first.RedrawInterval);
            first.RedrawInterval = TimeSpan.FromMilliseconds(16);
            second.RedrawInterval = TimeSpan.FromMilliseconds(80);

            Assert.Equal(TimeSpan.FromMilliseconds(16), first.RedrawInterval);
            Assert.Equal(TimeSpan.FromMilliseconds(80), second.RedrawInterval);
            Assert.Throws<ArgumentException>(() => first.RedrawInterval = TimeSpan.FromMilliseconds(-1));
            Assert.Equal(TimeSpan.FromMilliseconds(16), first.RedrawInterval);
        });
    }

    [Fact]
    public Task RedrawIntervalZeroIsAccepted()
    {
        return RunInHeadlessSession(() =>
        {
            var control = new TerminalControl { RedrawInterval = TimeSpan.Zero };

            Assert.Equal(TimeSpan.Zero, control.RedrawInterval);
        });
    }

    [Fact]
    public Task RedrawGateCoalescesUpdatesForOneControl()
    {
        return RunInHeadlessSession((Func<Task>)(async () =>
        {
            var surface = new Control();
            var redrawGate = new ControlRedrawGate(surface, TimeSpan.FromSeconds(1));
            var updateCount = 0;
            redrawGate.Request();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            await Task.Run(() =>
            {
                for (var index = 0; index < 20; index++)
                {
                    redrawGate.Request(() => updateCount++);
                }
            });

            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, updateCount);
            Assert.True(redrawGate.HasPendingInvalidationForTests);
        }));
    }

    private static TerminalControlModel CreateSixelModel()
    {
        return new TerminalControlModel(new TerminalOptions
        {
            Cols = 20,
            Rows = 8,
            CellWidthPixels = 2,
            CellHeightPixels = 3,
        });
    }

    private sealed class TestSixelBitmapCache : IDisposable
    {
        internal SixelBitmapCache Value { get; } = new();

        public void Dispose() => Value.Clear();
    }
}
