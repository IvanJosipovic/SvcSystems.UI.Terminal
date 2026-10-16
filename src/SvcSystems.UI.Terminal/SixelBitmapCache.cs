using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System.Buffers;
using System.Runtime.InteropServices;
using XTerm.Graphics;

namespace SvcSystems.UI.Terminal;

/// <summary>Caches immutable Avalonia bitmaps for the Sixel strips visible in the current frame.</summary>
internal sealed class SixelBitmapCache
{
    private const int SnapshotAttempts = 3;
    private readonly Dictionary<SixelStrip, Entry> _entries = new(ReferenceEqualityComparer.Instance);
    private long _frame;

    internal long CachedPixelBytes { get; private set; }

    internal int UploadCount { get; private set; }

    internal void BeginFrame()
    {
        _frame++;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The cache takes ownership of each successfully created bitmap and disposes it on replacement or eviction.")]
    internal Bitmap? GetOrCreate(SixelStrip strip, int columns)
    {
        ArgumentNullException.ThrowIfNull(strip);

        if (columns <= 0 || strip.Columns <= 0)
        {
            return null;
        }

        columns = Math.Min(columns, strip.Columns);
        _entries.TryGetValue(strip, out var entry);
        var version = strip.Version;
        if (entry is not null && entry.Version == version && entry.Columns == columns)
        {
            entry.LastSeenFrame = _frame;
            return entry.Bitmap;
        }

        if (!TryCreateBitmap(strip, columns, out var bitmap, out var snapshotVersion))
        {
            if (entry is not null && entry.Columns == columns)
            {
                entry.LastSeenFrame = _frame;
                return entry.Bitmap;
            }

            return null;
        }

        if (entry is not null)
        {
            Remove(strip, entry);
        }

        var byteCount = checked((long)strip.CellWidth * columns * strip.CellHeight * sizeof(uint));
        _entries.Add(strip, new Entry(snapshotVersion, columns, bitmap, _frame, byteCount));
        CachedPixelBytes += byteCount;
        UploadCount++;
        return bitmap;
    }

    internal void EndFrame()
    {
        List<SixelStrip>? stale = null;
        foreach (var pair in _entries)
        {
            if (pair.Value.LastSeenFrame != _frame)
            {
                stale ??= [];
                stale.Add(pair.Key);
            }
        }

        if (stale is null)
        {
            return;
        }

        foreach (var strip in stale)
        {
            Remove(strip, _entries[strip]);
        }
    }

    internal void Clear()
    {
        foreach (var entry in _entries.Values)
        {
            entry.Bitmap.Dispose();
        }

        _entries.Clear();
        CachedPixelBytes = 0;
    }

    private static bool TryCreateBitmap(SixelStrip strip, int columns, out Bitmap bitmap, out long version)
    {
        bitmap = null!;
        version = 0;
        var width = checked(strip.CellWidth * columns);
        var height = strip.CellHeight;
        var stride = checked(width * sizeof(uint));
        var byteCount = checked(stride * height);
        var pooled = ArrayPool<byte>.Shared.Rent(byteCount);

        try
        {
            for (var attempt = 0; attempt < SnapshotAttempts; attempt++)
            {
                var versionBeforeCopy = strip.Version;
                strip.CopyPixelsTo(pooled.AsSpan(0, byteCount), stride, columns);
                if (versionBeforeCopy != strip.Version)
                {
                    continue;
                }

                var native = Marshal.AllocHGlobal(byteCount);
                try
                {
                    for (var row = 0; row < height; row++)
                    {
                        Marshal.Copy(pooled, row * stride, IntPtr.Add(native, row * stride), stride);
                    }

                    bitmap = new Bitmap(
                        PixelFormat.Bgra8888,
                        AlphaFormat.Unpremul,
                        native,
                        new PixelSize(width, height),
                        new Vector(96, 96),
                        stride);
                    version = versionBeforeCopy;
                    return true;
                }
                finally
                {
                    Marshal.FreeHGlobal(native);
                }
            }

            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pooled);
        }
    }

    private void Remove(SixelStrip strip, Entry entry)
    {
        _entries.Remove(strip);
        entry.Bitmap.Dispose();
        CachedPixelBytes -= entry.ByteCount;
    }

    private sealed class Entry(long version, int columns, Bitmap bitmap, long lastSeenFrame, long byteCount)
    {
        internal long Version { get; } = version;

        internal int Columns { get; } = columns;

        internal Bitmap Bitmap { get; } = bitmap;

        internal long LastSeenFrame { get; set; } = lastSeenFrame;

        internal long ByteCount { get; } = byteCount;
    }
}

/// <summary>Calculates the bitmap portion and destination rectangle for one Sixel strip.</summary>
internal static class SixelDrawPlan
{
    internal static bool TryCreate(
        SixelStrip strip,
        int columns,
        int rowIndex,
        double cellWidth,
        double cellHeight,
        double renderScaling,
        Rect viewport,
        out Rect source,
        out Rect destination)
    {
        ArgumentNullException.ThrowIfNull(strip);
        source = default;
        destination = default;

        if (columns <= 0 || rowIndex < 0 || cellWidth <= 0 || cellHeight <= 0 ||
            strip.CellWidth <= 0 || strip.CellHeight <= 0)
        {
            return false;
        }

        columns = Math.Min(columns, strip.Columns);
        double x = TerminalRenderCoordinates.Snap(0, renderScaling);
        double y = TerminalRenderCoordinates.Snap(rowIndex * cellHeight, renderScaling);
        double right = TerminalRenderCoordinates.Snap(columns * cellWidth, renderScaling);
        double bottom = TerminalRenderCoordinates.Snap((rowIndex + 1) * cellHeight, renderScaling);
        if (right <= x || bottom <= y)
        {
            return false;
        }

        double clippedLeft = Math.Max(x, viewport.X);
        double clippedTop = Math.Max(y, viewport.Y);
        double clippedRight = Math.Min(right, viewport.Right);
        double clippedBottom = Math.Min(bottom, viewport.Bottom);
        if (clippedRight <= clippedLeft || clippedBottom <= clippedTop)
        {
            return false;
        }

        double sourceScaleX = (strip.CellWidth * columns) / (right - x);
        double sourceScaleY = strip.CellHeight / (bottom - y);
        double sourceX = (clippedLeft - x) * sourceScaleX;
        double sourceY = (clippedTop - y) * sourceScaleY;
        double sourceWidth = (clippedRight - clippedLeft) * sourceScaleX;
        double sourceHeight = (clippedBottom - clippedTop) * sourceScaleY;
        source = new Rect(sourceX, sourceY, sourceWidth, sourceHeight);
        destination = new Rect(clippedLeft, clippedTop, clippedRight - clippedLeft, clippedBottom - clippedTop);
        return true;
    }
}
