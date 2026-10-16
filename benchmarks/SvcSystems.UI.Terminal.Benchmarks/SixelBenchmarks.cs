using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using SvcSystems.UI.Terminal;
using XTerm.Graphics;

namespace SvcSystems.UI.Terminal.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class SixelBenchmarks
{
    private const string Esc = "\u001b";
    private const string St = Esc + "\\";
    private const int ImagePixelWidth = 1600;
    private const int ImagePixelHeight = 120;
    private const int CellPixelWidth = 10;
    private const int CellPixelHeight = 20;
    private static readonly string SixelPayload = CreateSixelPayload();
    private static readonly string PlaceAtHome = Esc + "[1;1H";

    private TerminalControlModel _model = null!;
    private SixelBitmapCache _cache = null!;
    private SixelStrip _strip = null!;
    private byte[] _copyBuffer = null!;
    private int _stripColumns;

    [GlobalSetup]
    public void Setup()
    {
        AppBuilder.Configure<BenchmarkApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .SetupWithoutStarting();

        _model = new TerminalControlModel(new TerminalOptions
        {
            Cols = ImagePixelWidth / CellPixelWidth,
            Rows = 12,
            Scrollback = 0,
            CellWidthPixels = CellPixelWidth,
            CellHeightPixels = CellPixelHeight,
        });
        _model.Feed(SixelPayload);

        var row = _model.ViewportRows[0];
        _strip = row.Sixel ?? throw new InvalidOperationException("The Sixel workload did not produce a strip.");
        _stripColumns = row.SixelColumns;
        _copyBuffer = new byte[checked(_strip.Stride * _strip.CellHeight)];
        _cache = new SixelBitmapCache();
        _cache.BeginFrame();
        foreach (var viewportRow in _model.ViewportRows)
        {
            if (viewportRow.Sixel is { } strip)
            {
                _cache.GetOrCreate(strip, viewportRow.SixelColumns);
            }
        }
        _cache.EndFrame();

        Console.WriteLine($"Sixel workload: {ImagePixelWidth}x{ImagePixelHeight} pixels; "
            + $"decoded strips: {_model.ViewportRows.Count(static viewportRow => viewportRow.Sixel is not null)}; "
            + $"engine pixel bytes: {_model.ViewportRows.Where(static viewportRow => viewportRow.Sixel is not null).Sum(static viewportRow => viewportRow.Sixel!.ByteCount):N0}; "
            + $"visible bitmap cache bytes: {_cache.CachedPixelBytes:N0}.");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _cache.Clear();
        _model.Dispose();
    }

    [Benchmark(Baseline = true)]
    public int DecodeAndRefreshViewport()
    {
        _model.Feed(PlaceAtHome + SixelPayload);
        return _model.ViewportRows.Count;
    }

    [Benchmark]
    public long CopyDecodedStripPixels()
    {
        _strip.CopyPixelsTo(_copyBuffer, _strip.Stride, _stripColumns);
        return _strip.Version;
    }

    [Benchmark]
    public long CachedBitmapHit()
    {
        _cache.BeginFrame();
        foreach (var viewportRow in _model.ViewportRows)
        {
            if (viewportRow.Sixel is { } strip)
            {
                _cache.GetOrCreate(strip, viewportRow.SixelColumns);
            }
        }
        _cache.EndFrame();
        return _cache.CachedPixelBytes;
    }

    [Benchmark]
    public int DecodeAndUploadChangedStrip()
    {
        _cache.BeginFrame();
        _model.Feed(PlaceAtHome + SixelPayload);
        foreach (var viewportRow in _model.ViewportRows)
        {
            if (viewportRow.Sixel is { } strip)
            {
                _cache.GetOrCreate(strip, viewportRow.SixelColumns);
            }
        }
        _cache.EndFrame();
        return _cache.UploadCount;
    }

    private static string CreateSixelPayload()
    {
        var bands = new string[ImagePixelHeight / 6];
        for (var row = 0; row < bands.Length; row++)
        {
            bands[row] = row == 0 ? $"#{0};2;100;0;0!{ImagePixelWidth}~" : $"!{ImagePixelWidth}~";
        }

        return Esc + "P0;1;0q" + string.Join('-', bands) + St;
    }

    private sealed class BenchmarkApp : Application
    {
    }
}
