using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Skia;
using Avalonia.Threading;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using SvcSystems.UI.Terminal;
using System.Text;
using XTerm.Graphics;

namespace SvcSystems.UI.Terminal.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0, iterationCount: 8, warmupCount: 5)]
public class KittyGraphicsBenchmarks
{
    private const string Esc = "\u001b";
    private const string St = Esc + "\\";
    private const int PlacementCount = 741;
    private const int ImageWidth = 10;
    private const int ImageHeight = 20;
    private const int Columns = 80;
    private const int Rows = 24;

    private RenderCase _uniqueImages = null!;
    private RenderCase _sharedImage = null!;

    [GlobalSetup]
    public void Setup()
    {
        AppBuilder.Configure<BenchmarkApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        _uniqueImages = CreateRenderCase(CreateUniqueImageWorkload());
        _sharedImage = CreateRenderCase(CreateSharedImageWorkload());

        Console.WriteLine($"Kitty unique image workload: {PlacementCount} images, "
            + $"decoded pixel bytes: {_uniqueImages.DecodedPixelBytes:N0}, "
            + $"visible bitmap bytes: {_uniqueImages.Control.TerminalImageBitmapBytesForTests:N0}.");
        Console.WriteLine($"Kitty shared placement workload: {PlacementCount} placements, "
            + $"decoded pixel bytes: {_sharedImage.DecodedPixelBytes:N0}, "
            + $"visible bitmap bytes: {_sharedImage.Control.TerminalImageBitmapBytesForTests:N0}.");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _uniqueImages?.Dispose();
        _sharedImage?.Dispose();
    }

    [Benchmark(Baseline = true)]
    public long RenderManyUniqueKittyImages()
    {
        _uniqueImages.Target.Render(_uniqueImages.Control);
        return _uniqueImages.Control.TerminalImageBitmapBytesForTests;
    }

    [Benchmark]
    public long RenderManyPlacementsOfOneKittyImage()
    {
        _sharedImage.Target.Render(_sharedImage.Control);
        return _sharedImage.Control.TerminalImageBitmapBytesForTests;
    }

    private static RenderCase CreateRenderCase(string workload)
    {
        TerminalControlModel model = new(new TerminalOptions
        {
            Cols = Columns,
            Rows = Rows,
            Scrollback = 0,
            CellWidthPixels = ImageWidth,
            CellHeightPixels = ImageHeight,
        });

        TerminalControl control = new() { Model = model };
        double width = control.ConsoleTextSizeForTests.Width * Columns;
        double height = control.ConsoleTextSizeForTests.Height * Rows;
        Window window = new() { Width = width, Height = height, Content = control };
        window.Show();
        window.UpdateLayout();
        control.Measure(new Size(width, height));
        control.Arrange(new Rect(0, 0, width, height));
        model.Feed(workload);
        Dispatcher.UIThread.RunJobs();

        RenderTargetBitmap target = new(new PixelSize(Math.Max((int)Math.Ceiling(width), 1), Math.Max((int)Math.Ceiling(height), 1)));
        target.Render(control);

        long decodedPixelBytes = 0;
        HashSet<TerminalImage> images = new(ReferenceEqualityComparer.Instance);
        foreach (ViewportRow row in model.ViewportRows)
        {
            foreach (ViewportImagePlacement placement in row.Images)
            {
                if (images.Add(placement.Image))
                {
                    decodedPixelBytes += placement.Image.ByteCount;
                }
            }
        }

        return new RenderCase(model, control, window, target, decodedPixelBytes);
    }

    private static string CreateUniqueImageWorkload()
    {
        byte[] pixels = CreateCellPixels();
        string encodedPixels = Convert.ToBase64String(pixels);
        StringBuilder workload = new(PlacementCount * (encodedPixels.Length + 48));

        for (int index = 0; index < PlacementCount; index++)
        {
            AppendCursor(workload, index);
            workload.Append(Esc).Append("_Ga=T,i=").Append(index + 1)
                .Append(",f=32,s=").Append(ImageWidth)
                .Append(",v=").Append(ImageHeight)
                .Append(",C=1,q=2;").Append(encodedPixels).Append(St);
        }

        return workload.ToString();
    }

    private static string CreateSharedImageWorkload()
    {
        byte[] pixels = CreateCellPixels();
        string encodedPixels = Convert.ToBase64String(pixels);
        StringBuilder workload = new(PlacementCount * 48);
        workload.Append(Esc).Append("_Ga=t,i=1,f=32,s=").Append(ImageWidth)
            .Append(",v=").Append(ImageHeight).Append(",q=2;").Append(encodedPixels).Append(St);

        for (int index = 0; index < PlacementCount; index++)
        {
            AppendCursor(workload, index);
            workload.Append(Esc).Append("_Ga=p,i=1,C=1,q=2").Append(St);
        }

        return workload.ToString();
    }

    private static void AppendCursor(StringBuilder workload, int index)
    {
        int row = (index / Columns) + 1;
        int column = (index % Columns) + 1;
        workload.Append(Esc).Append('[').Append(row).Append(';').Append(column).Append('H');
    }

    private static byte[] CreateCellPixels()
    {
        byte[] pixels = new byte[ImageWidth * ImageHeight * TerminalImage.BytesPerPixel];
        for (int pixel = 0; pixel < ImageWidth * ImageHeight; pixel++)
        {
            int offset = pixel * TerminalImage.BytesPerPixel;
            pixels[offset] = 32;
            pixels[offset + 1] = 160;
            pixels[offset + 2] = 224;
            pixels[offset + 3] = 255;
        }

        return pixels;
    }

    private sealed class RenderCase(
        TerminalControlModel model,
        TerminalControl control,
        Window window,
        RenderTargetBitmap target,
        long decodedPixelBytes) : IDisposable
    {
        internal TerminalControl Control { get; } = control;

        internal RenderTargetBitmap Target { get; } = target;

        internal long DecodedPixelBytes { get; } = decodedPixelBytes;

        public void Dispose()
        {
            Target.Dispose();
            window.Close();
            model.Dispose();
        }
    }

    private sealed class BenchmarkApp : Application
    {
    }
}
