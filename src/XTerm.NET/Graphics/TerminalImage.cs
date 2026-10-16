using System.Threading;

namespace XTerm.Graphics;

/// <summary>
/// A decoded image sitting in the terminal buffer, shared by every cell that shows part of it.
/// </summary>
/// <remarks>
/// <para>The image is stored once and referenced by each cell it covers, rather than sliced into
/// one bitmap per cell. The cells still own their piece of it -- each carries the tile coordinates
/// it displays -- so overwriting a single cell removes exactly that cell's worth of picture, which
/// is the behaviour that makes an image act like terminal content instead of an overlay. What the
/// sharing buys is one allocation per image rather than columns times rows of them, and a host
/// that can recognise a run of adjacent tiles and blit them in a single call.</para>
/// <para>Immutable, so it can be handed to a renderer on another thread without copying, and so a
/// host can cache a texture against it for as long as it lives. It dies when the last cell holding
/// it is overwritten or scrolled out of the buffer -- there is no eviction list to keep in step.</para>
/// <para>Deliberately framework-neutral: raw bytes and integers, no bitmap type. XTerm.NET has no
/// UI dependency and this is not the place to acquire one.</para>
/// </remarks>
public sealed class TerminalImage
{
    private static int _nextId;

    /// <summary>Bytes per pixel in <see cref="Pixels"/>.</summary>
    public const int BytesPerPixel = 4;

    private readonly byte[] _pixels;

    /// <summary>
    /// A process-wide identifier, handy for diagnostics and for keying a host-side cache. Identity
    /// comparison on the object itself works just as well and is what a host should normally use.
    /// </summary>
    public int Id { get; }

    /// <summary>Image width in pixels.</summary>
    public int PixelWidth { get; }

    /// <summary>Image height in pixels.</summary>
    public int PixelHeight { get; }

    /// <summary>Bytes per row of <see cref="Pixels"/>.</summary>
    public int Stride => PixelWidth * BytesPerPixel;

    /// <summary>
    /// The pixels, BGRA8888 with straight (unpremultiplied) alpha, top row first.
    /// </summary>
    /// <remarks>
    /// Alpha is not decoration: a Sixel drawn with background select 1 leaves its unset pixels
    /// transparent, and the cell's own background is meant to show through them.
    /// </remarks>
    public ReadOnlyMemory<byte> Pixels => _pixels;

    /// <summary>The cell width in pixels that the image was laid out against.</summary>
    public int CellWidth { get; }

    /// <summary>The cell height in pixels that the image was laid out against.</summary>
    public int CellHeight { get; }

    /// <summary>How many columns the image occupies. The rightmost may be partly covered.</summary>
    public int Cols { get; }

    /// <summary>How many rows the image occupies. The bottom one may be partly covered.</summary>
    public int Rows { get; }

    /// <summary>Size of the pixel buffer, for accounting against an image memory budget.</summary>
    /// <remarks>
    /// A long, and summed as one. An animation's frames are counted here as well as the root
    /// picture, and the two together can exceed what an int holds -- clamping the animation before
    /// adding the root, as this used to, wrapped the total negative at exactly the point the clamp
    /// existed to guard. Every consumer accumulates into a long already.
    /// </remarks>
    public long ByteCount => (long)_pixels.Length + (Animation?.ByteCount ?? 0);

    /// <summary>The backing array, so an animation can share the root frame without copying it.</summary>
    internal byte[] PixelArray => _pixels;

    /// <summary>
    /// The frames of this image, once a client has made it an animation, or null while it is a
    /// still picture.
    /// </summary>
    /// <remarks>
    /// An image starts as one frame and becomes an animation only when frames are added to it, so
    /// nothing is allocated for the overwhelmingly common case of a picture that never moves.
    /// </remarks>
    public ImageAnimation? Animation { get; private set; }

    /// <summary>Creates the animation on first use, or returns the one already there.</summary>
    internal ImageAnimation EnsureAnimation() => Animation ??= new ImageAnimation(this);

    /// <summary>
    /// The pixels a renderer should draw right now: the current animation frame, or the image
    /// itself when there is no animation.
    /// </summary>
    /// <remarks>
    /// A host should blit these and cache against <see cref="FrameSerial"/>, which changes whenever
    /// they do. <see cref="Pixels"/> stays the root frame and never changes, so it remains safe to
    /// hold and to hand to another thread.
    /// </remarks>
    public ReadOnlyMemory<byte> CurrentPixels => Animation?.CurrentPixels ?? _pixels;

    /// <summary>
    /// Changes whenever <see cref="CurrentPixels"/> does, so a cached texture can be spotted as
    /// stale without comparing the pixels.
    /// </summary>
    public int FrameSerial => Animation?.Serial ?? 0;

    public TerminalImage(byte[] pixels, int pixelWidth, int pixelHeight, int cellWidth, int cellHeight)
    {
        if (pixels is null)
            throw new ArgumentNullException(nameof(pixels));
        if (pixelWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelWidth));
        if (pixelHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelHeight));
        if (cellWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(cellWidth));
        if (cellHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(cellHeight));

        var required = (long)pixelWidth * pixelHeight * BytesPerPixel;
        if (pixels.LongLength < required)
            throw new ArgumentException(
                $"Pixel buffer holds {pixels.LongLength} bytes; {pixelWidth}x{pixelHeight} BGRA needs {required}.",
                nameof(pixels));

        _pixels = pixels;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        CellWidth = cellWidth;
        CellHeight = cellHeight;
        Cols = (pixelWidth + cellWidth - 1) / cellWidth;
        Rows = (pixelHeight + cellHeight - 1) / cellHeight;
        Id = Interlocked.Increment(ref _nextId);
    }

    /// <summary>
    /// Gets the source rectangle, in image pixels, for one tile.
    /// </summary>
    /// <remarks>
    /// Tiles on the right and bottom edges are clipped, so <paramref name="width"/> and
    /// <paramref name="height"/> can be smaller than <see cref="CellWidth"/> and
    /// <see cref="CellHeight"/>. A host must scale the destination to match rather than stretching
    /// a partial tile over a whole cell:
    /// <c>destWidth = currentCellWidth * width / image.CellWidth</c>.
    /// </remarks>
    /// <returns>False when the tile is outside the image.</returns>
    public bool TryGetTileSource(int tileCol, int tileRow, out int x, out int y, out int width, out int height)
    {
        x = y = width = height = 0;

        if (tileCol < 0 || tileRow < 0 || tileCol >= Cols || tileRow >= Rows)
            return false;

        x = tileCol * CellWidth;
        y = tileRow * CellHeight;
        width = Math.Min(CellWidth, PixelWidth - x);
        height = Math.Min(CellHeight, PixelHeight - y);
        return width > 0 && height > 0;
    }
}
