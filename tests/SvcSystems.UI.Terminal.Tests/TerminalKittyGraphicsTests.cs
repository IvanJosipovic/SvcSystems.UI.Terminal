using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using SvcSystems.UI.Terminal;
using XTerm.Graphics;
using Xunit;

namespace SvcSystems.UI.Terminal.Tests;

public sealed class TerminalKittyGraphicsTests : AvaloniaTestBase
{
    private const string Esc = "\u001b";
    private const string St = Esc + "\\";
    private const int CellWidth = 2;
    private const int CellHeight = 3;

    [Fact]
    public Task Feed_KittyImagePlacementsExposeOrderedViewportRows()
    {
        return RunInHeadlessSession(() =>
        {
            using TerminalControlModel model = CreateModel();
            byte[] pixels = CreatePixels(4, 6, 0, 0, 255);
            string payload = Convert.ToBase64String(pixels);

            model.Feed($"{Esc}_Ga=T,i=1,f=32,s=4,v=6,c=3,r=2,z=-1,C=1,q=2;{payload}{St}");
            model.Feed($"{Esc}_Ga=T,i=2,f=32,s=4,v=6,c=3,r=2,z=1,C=1,q=2;{payload}{St}");

            ViewportRow firstRow = model.ViewportRows[0];
            ViewportRow secondRow = model.ViewportRows[1];

            Assert.Equal(2, firstRow.Images.Count);
            Assert.Equal(-1, firstRow.Images[0].Placement.ZIndex);
            Assert.Equal(1, firstRow.Images[1].Placement.ZIndex);
            Assert.Equal(firstRow.Images[0].Placement.Serial, secondRow.Images[0].Placement.Serial);
            Assert.Equal(firstRow.Images[1].Placement.Serial, secondRow.Images[1].Placement.Serial);
            Assert.Same(firstRow.Images[0].Image, secondRow.Images[0].Image);
            Assert.All(model.ViewportRows.Skip(2), row => Assert.Empty(row.Images));
        });
    }

    [Fact]
    public Task Feed_ITerm2InlinePngIsPlacedThroughTheSixelContentPath()
    {
        return RunInHeadlessSession(() =>
        {
            using TerminalControlModel model = CreateModel();
            const string onePixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==";
            model.Feed($"{Esc}]1337;File=inline=1:{onePixelPng}{St}");

            Assert.NotNull(model.ViewportRows[0].Sixel);
            Assert.Equal(1, model.ViewportRows[0].SixelColumns);
            Assert.Empty(model.ViewportRows[0].Images);
        });
    }

    [Fact]
    public Task ImageDrawPlan_MapsCellScalingAndClipsWithoutSquashing()
    {
        return RunInHeadlessSession(() =>
        {
            using TerminalControlModel model = CreateModel();
            string payload = Convert.ToBase64String(CreatePixels(4, 6, 0, 0, 255));
            model.Feed($"{Esc}_Ga=T,i=1,f=32,s=4,v=6,c=3,r=2,C=1,q=2;{payload}{St}");

            ViewportImagePlacement firstRowImage = Assert.Single(model.ViewportRows[0].Images);
            Assert.True(TerminalImageDrawPlan.TryCreate(
                firstRowImage.Image,
                firstRowImage.Placement,
                rowIndex: 0,
                cellWidth: 10,
                cellHeight: 20,
                renderScaling: 1,
                viewport: new Rect(0, 0, 20, 40),
                out Rect source,
                out Rect destination));

            Assert.Equal(new Rect(0, 0, 20, 20), destination);
            Assert.Equal(0, source.X);
            Assert.Equal(0, source.Y);
            Assert.Equal(8d / 3, source.Width, 6);
            Assert.Equal(3, source.Height);

            ViewportImagePlacement nextRowImage = Assert.Single(model.ViewportRows[1].Images);
            Assert.True(TerminalImageDrawPlan.TryCreate(
                nextRowImage.Image,
                nextRowImage.Placement,
                rowIndex: 1,
                cellWidth: 10,
                cellHeight: 20,
                renderScaling: 1,
                viewport: new Rect(0, 0, 20, 40),
                out Rect nextSource,
                out Rect nextDestination));

            Assert.Equal(20, nextDestination.Y);
            Assert.Equal(3, nextSource.Y);
        });
    }

    [Fact]
    public Task ImageDrawPlanSnapsNeighboringRowsAtFractionalDisplayScaling()
    {
        return RunInHeadlessSession(() =>
        {
            using TerminalControlModel model = CreateModel();
            string payload = Convert.ToBase64String(CreatePixels(4, 6, 0, 0, 255));
            model.Feed($"{Esc}_Ga=T,i=1,f=32,s=4,v=6,c=3,r=2,C=1,q=2;{payload}{St}");

            ViewportImagePlacement first = Assert.Single(model.ViewportRows[0].Images);
            ViewportImagePlacement second = Assert.Single(model.ViewportRows[1].Images);
            Assert.True(TerminalImageDrawPlan.TryCreate(
                first.Image, first.Placement, rowIndex: 0,
                cellWidth: 7.2, cellHeight: 13, renderScaling: 1.25,
                viewport: new Rect(0, 0, 40, 40), out _, out Rect firstDestination));
            Assert.True(TerminalImageDrawPlan.TryCreate(
                second.Image, second.Placement, rowIndex: 1,
                cellWidth: 7.2, cellHeight: 13, renderScaling: 1.25,
                viewport: new Rect(0, 0, 40, 40), out _, out Rect secondDestination));

            Assert.Equal(firstDestination.Bottom, secondDestination.Y);
            Assert.Equal(12.8, firstDestination.Height, 6);
            Assert.Equal(13.6, secondDestination.Height, 6);
        });
    }

    [Fact]
    public Task ImageBitmapCache_ReusesCurrentFrameAndReleasesImagesOutsideViewport()
    {
        return RunInHeadlessSession(() =>
        {
            using TerminalControlModel model = CreateModel();
            using TestImageBitmapCache testCache = new();
            TerminalImageBitmapCache cache = testCache.Value;
            FeedTwoFrameAnimation(model);
            TerminalImage image = Assert.Single(model.ViewportRows[0].Images).Image;

            cache.BeginFrame();
            Bitmap? first = cache.GetOrCreate(image);
            Bitmap? unchanged = cache.GetOrCreate(image);
            cache.EndFrame();

            Assert.NotNull(first);
            Assert.Same(first, unchanged);
            Assert.Equal(1, cache.UploadCount);
            Assert.Equal(4 * 6 * sizeof(uint), cache.CachedPixelBytes);

            Assert.True(model.Terminal.Engine.AdvanceAnimations(TimeSpan.FromMilliseconds(60)));
            cache.BeginFrame();
            Bitmap? nextFrame = cache.GetOrCreate(image);
            cache.EndFrame();

            Assert.NotNull(nextFrame);
            Assert.NotSame(first, nextFrame);
            Assert.Equal(2, cache.UploadCount);
            Assert.Equal(255, image.CurrentPixels.Span[1]);

            cache.BeginFrame();
            cache.EndFrame();
            Assert.Equal(0, cache.CachedPixelBytes);
        });
    }

    [Fact]
    public Task Render_KittyPlacementBuildsBitmapCacheAndCompletesOnHeadlessSurface()
    {
        return RunInHeadlessSession(() =>
        {
            using TerminalControlModel model = CreateModel();
            TerminalControl control = new() { Model = model, Width = 160, Height = 80 };
            control.Measure(new Size(160, 80));
            control.Arrange(new Rect(0, 0, 160, 80));

            string payload = Convert.ToBase64String(CreatePixels(4, 6, 0, 0, 255));
            model.Feed($"{Esc}_Ga=T,f=32,s=4,v=6,C=1,q=2;{payload}{St}");
            model.Terminal.Engine.CursorVisible = false;

            using RenderTargetBitmap bitmap = new(new PixelSize(160, 80));
            bitmap.Render(control);

            Assert.True(control.TerminalImageBitmapUploadCountForTests > 0);
            Assert.True(control.CanRenderTextForTests);
            var pixel = BitmapPixelAssertions.GetBgraPixel(bitmap, 1, 1);
            Assert.Equal((byte)255, pixel.Red);
            Assert.Equal((byte)0, pixel.Green);
            Assert.Equal((byte)0, pixel.Blue);
            Assert.Equal((byte)255, pixel.Alpha);
        });
    }

    [Fact]
    public Task ImageAnimationTimer_RunsOnlyForAnAttachedAnimatedTerminal()
    {
        return RunInHeadlessSession(() =>
        {
            using TerminalControlModel model = CreateModel();
            model.Feed("plain text");
            TerminalControl control = new() { Model = model, Width = 160, Height = 80 };
            Window window = new() { Width = 160, Height = 80, Content = control };
            window.Show();
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            try
            {
                Assert.False(control.IsImageAnimationTimerRunningForTests);

                FeedTwoFrameAnimation(model);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();

                Assert.True(control.IsImageAnimationTimerRunningForTests);
            }
            finally
            {
                window.Close();
            }

            Assert.False(control.IsImageAnimationTimerRunningForTests);
        });
    }

    private static TerminalControlModel CreateModel()
    {
        return new TerminalControlModel(new TerminalOptions
        {
            Cols = 8,
            Rows = 4,
            CellWidthPixels = CellWidth,
            CellHeightPixels = CellHeight,
        });
    }

    private static void FeedTwoFrameAnimation(TerminalControlModel model)
    {
        string red = Convert.ToBase64String(CreatePixels(4, 6, 0, 0, 255));
        string green = Convert.ToBase64String(CreatePixels(4, 6, 0, 255, 0));
        model.Feed($"{Esc}_Ga=t,i=1,f=32,s=4,v=6,q=2;{red}{St}");
        model.Feed($"{Esc}_Ga=f,i=1,c=1,z=50,f=32,s=4,v=6,q=2;{green}{St}");
        model.Feed($"{Esc}_Ga=a,i=1,r=1,z=50,q=2{St}");
        model.Feed($"{Esc}_Ga=a,i=1,s=3,q=2{St}");
        model.Feed($"{Esc}[1;1H{Esc}_Ga=p,i=1,C=1,q=2{St}");
    }

    private static byte[] CreatePixels(int width, int height, byte blue, byte green, byte red)
    {
        byte[] bytes = new byte[width * height * 4];
        for (int index = 0; index < width * height; index++)
        {
            bytes[index * 4] = red;
            bytes[index * 4 + 1] = green;
            bytes[index * 4 + 2] = blue;
            bytes[index * 4 + 3] = 255;
        }

        return bytes;
    }

    private sealed class TestImageBitmapCache : IDisposable
    {
        internal TerminalImageBitmapCache Value { get; } = new();

        public void Dispose() => Value.Clear();
    }
}
