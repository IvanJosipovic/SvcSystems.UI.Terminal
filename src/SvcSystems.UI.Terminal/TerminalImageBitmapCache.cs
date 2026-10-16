using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System.Buffers;
using System.Runtime.InteropServices;
using XTerm.Graphics;

namespace SvcSystems.UI.Terminal;

/// <summary>Caches Avalonia bitmaps for the Kitty images visible in the current frame.</summary>
internal sealed class TerminalImageBitmapCache
{
    private const int SnapshotAttempts = 3;
    private readonly Dictionary<TerminalImage, Entry> _entries = new(ReferenceEqualityComparer.Instance);
    private long _frame;

    internal long CachedPixelBytes { get; private set; }

    internal int UploadCount { get; private set; }

    internal void BeginFrame() => _frame++;

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The cache owns each successfully created bitmap and disposes it on replacement or eviction.")]
    internal Bitmap? GetOrCreate(TerminalImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        _entries.TryGetValue(image, out Entry? entry);
        int frameSerial = image.FrameSerial;
        if (entry is not null && entry.FrameSerial == frameSerial)
        {
            entry.LastSeenFrame = _frame;
            return entry.Bitmap;
        }

        if (!TryCreateBitmap(image, out Bitmap? bitmap, out int snapshotSerial))
        {
            if (entry is null)
            {
                _entries.Add(image, new Entry(snapshotSerial, null, _frame, 0));
            }
            else
            {
                RemoveBitmap(entry);
                entry.FrameSerial = snapshotSerial;
                entry.LastSeenFrame = _frame;
            }

            return null;
        }

        if (entry is not null)
        {
            Remove(image, entry);
        }

        long byteCount = checked((long)image.PixelWidth * image.PixelHeight * TerminalImage.BytesPerPixel);
        _entries.Add(image, new Entry(snapshotSerial, bitmap, _frame, byteCount));
        CachedPixelBytes += byteCount;
        UploadCount++;
        return bitmap;
    }

    internal void EndFrame()
    {
        List<TerminalImage>? stale = null;
        foreach (KeyValuePair<TerminalImage, Entry> pair in _entries)
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

        foreach (TerminalImage image in stale)
        {
            Remove(image, _entries[image]);
        }
    }

    internal void Clear()
    {
        foreach (Entry entry in _entries.Values)
        {
            entry.Bitmap?.Dispose();
        }

        _entries.Clear();
        CachedPixelBytes = 0;
    }

    private static bool TryCreateBitmap(TerminalImage image, out Bitmap? bitmap, out int frameSerial)
    {
        bitmap = null;
        frameSerial = 0;
        int stride = checked(image.PixelWidth * TerminalImage.BytesPerPixel);
        int byteCount = checked(stride * image.PixelHeight);

        for (int attempt = 0; attempt < SnapshotAttempts; attempt++)
        {
            int serialBeforeCopy = image.FrameSerial;
            ReadOnlyMemory<byte> pixels = image.CurrentPixels;
            if (pixels.Length < byteCount)
            {
                return false;
            }

            IntPtr native = Marshal.AllocHGlobal(byteCount);
            try
            {
                if (MemoryMarshal.TryGetArray(pixels, out ArraySegment<byte> segment) && segment.Array is { } source)
                {
                    Marshal.Copy(source, segment.Offset, native, byteCount);
                }
                else
                {
                    byte[] pooled = ArrayPool<byte>.Shared.Rent(byteCount);
                    try
                    {
                        pixels.Slice(0, byteCount).CopyTo(pooled);
                        Marshal.Copy(pooled, 0, native, byteCount);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(pooled);
                    }
                }

                if (serialBeforeCopy != image.FrameSerial)
                {
                    continue;
                }

                bitmap = new Bitmap(
                    PixelFormat.Bgra8888,
                    AlphaFormat.Unpremul,
                    native,
                    new PixelSize(image.PixelWidth, image.PixelHeight),
                    new Vector(96, 96),
                    stride);
                frameSerial = serialBeforeCopy;
                return true;
            }
            catch (Exception exception) when (exception is NotImplementedException or NotSupportedException)
            {
                frameSerial = image.FrameSerial;
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(native);
            }
        }

        frameSerial = image.FrameSerial;
        return false;
    }

    private void Remove(TerminalImage image, Entry entry)
    {
        _entries.Remove(image);
        RemoveBitmap(entry);
    }

    private void RemoveBitmap(Entry entry)
    {
        entry.Bitmap?.Dispose();
        entry.Bitmap = null;
        CachedPixelBytes -= entry.ByteCount;
        entry.ByteCount = 0;
    }

    private sealed class Entry(int frameSerial, Bitmap? bitmap, long lastSeenFrame, long byteCount)
    {
        internal int FrameSerial { get; set; } = frameSerial;

        internal Bitmap? Bitmap { get; set; } = bitmap;

        internal long LastSeenFrame { get; set; } = lastSeenFrame;

        internal long ByteCount { get; set; } = byteCount;
    }
}

/// <summary>Calculates the clipped bitmap source and cell-scaled destination for one Kitty row placement.</summary>
internal static class TerminalImageDrawPlan
{
    internal static bool TryCreate(
        TerminalImage image,
        XTerm.Graphics.LinePlacement placement,
        int rowIndex,
        double cellWidth,
        double cellHeight,
        double renderScaling,
        Rect viewport,
        out Rect source,
        out Rect destination)
    {
        ArgumentNullException.ThrowIfNull(image);
        source = default;
        destination = default;

        if (rowIndex < 0 || placement.Cols <= 0 || placement.SrcWidth <= 0 || placement.SrcHeight <= 0 ||
            cellWidth <= 0 || cellHeight <= 0 || image.CellWidth <= 0 || image.CellHeight <= 0)
        {
            return false;
        }

        double imageScaleX = cellWidth / image.CellWidth;
        double imageScaleY = cellHeight / image.CellHeight;
        double x = (placement.Column * cellWidth) + (placement.OffsetX * imageScaleX);
        double y = (rowIndex * cellHeight) + (placement.OffsetY * imageScaleY);
        double width = placement.PxPerCellX > 0
            ? placement.Cols * cellWidth
            : placement.SrcWidth * imageScaleX;
        double height = placement.PxPerCellY > 0
            ? cellHeight
            : placement.SrcHeight * imageScaleY;

        double right = TerminalRenderCoordinates.Snap(x + width, renderScaling);
        double bottom = TerminalRenderCoordinates.Snap(y + height, renderScaling);
        x = TerminalRenderCoordinates.Snap(x, renderScaling);
        y = TerminalRenderCoordinates.Snap(y, renderScaling);
        width = right - x;
        height = bottom - y;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        double clipLeft = Math.Max(x, viewport.X);
        double clipTop = Math.Max(y, viewport.Y);
        double clipRight = Math.Min(x + width, viewport.Right);
        double clipBottom = Math.Min(y + height, viewport.Bottom);
        if (clipRight <= clipLeft || clipBottom <= clipTop)
        {
            return false;
        }

        double sourceScaleX = placement.SrcWidth / width;
        double sourceScaleY = placement.SrcHeight / height;
        double sourceX = placement.SrcX + ((clipLeft - x) * sourceScaleX);
        double sourceY = placement.SrcY + ((clipTop - y) * sourceScaleY);
        double sourceWidth = (clipRight - clipLeft) * sourceScaleX;
        double sourceHeight = (clipBottom - clipTop) * sourceScaleY;

        source = new Rect(sourceX, sourceY, sourceWidth, sourceHeight);
        destination = new Rect(clipLeft, clipTop, clipRight - clipLeft, clipBottom - clipTop);
        return true;
    }
}
