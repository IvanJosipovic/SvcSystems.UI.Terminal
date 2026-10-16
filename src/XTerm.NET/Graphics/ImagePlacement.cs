namespace XTerm.Graphics;

/// <summary>
/// How a tile grid is laid over an image.
/// </summary>
public enum ImageScaling
{
    /// <summary>
    /// One cell shows one cell's worth of pixels, and the tiles at the right and bottom edges are
    /// clipped short. The image keeps its natural size.
    /// </summary>
    /// <remarks>
    /// What Sixel does, and what Kitty does when a placement names no cell box.
    /// </remarks>
    Natural,

    /// <summary>
    /// The source is divided proportionally across the cell box, so the image is stretched or
    /// squeezed to fill exactly the columns and rows asked for.
    /// </summary>
    /// <remarks>
    /// What Kitty's <c>c=</c> and <c>r=</c> keys mean.
    /// </remarks>
    Stretched
}

/// <summary>
/// What a client asked for when it placed an image: which part of it, at what size, and under whose
/// identity.
/// </summary>
/// <remarks>
/// <para>Cells reference a placement rather than an image, because Kitty transmits a picture once
/// and may then show it several times, cropped and scaled differently each time. The pixels stay
/// on the <see cref="TerminalImage"/> and are shared; only the view of them differs. A host that
/// caches a texture should therefore key it on <see cref="Image"/>, not on the placement.</para>
/// <para>Sixel makes a placement too, covering the whole image at its natural size. That keeps one
/// path through the renderer rather than two.</para>
/// </remarks>
public sealed class ImagePlacement
{
    /// <summary>The pixels this shows part of.</summary>
    public TerminalImage Image { get; }

    /// <summary>
    /// The client-assigned placement id, or 0 when the client named none.
    /// </summary>
    /// <remarks>
    /// Kitty uses it to delete one appearance of an image without disturbing the others. It is not
    /// an identity for the placement object -- two placements can legitimately share an id of 0 --
    /// so equality is by reference and this is only ever used for lookup.
    /// </remarks>
    public int Id { get; }

    /// <summary>Left edge of the part of the image being shown, in image pixels.</summary>
    public int SourceX { get; }

    /// <summary>Top edge of the part of the image being shown, in image pixels.</summary>
    public int SourceY { get; }

    /// <summary>Width of the part of the image being shown, in image pixels.</summary>
    public int SourceWidth { get; }

    /// <summary>Height of the part of the image being shown, in image pixels.</summary>
    public int SourceHeight { get; }

    /// <summary>How many columns of cells this occupies.</summary>
    public int Cols { get; }

    /// <summary>How many rows of cells this occupies.</summary>
    public int Rows { get; }

    /// <summary>Whether the tiles are natural size or stretched to the cell box.</summary>
    public ImageScaling Scaling { get; }

    /// <summary>
    /// The client's requested draw order, from Kitty's <c>z=</c> key.
    /// </summary>
    /// <remarks>
    /// <para>Decides what is drawn over what where two placements cover the same cell: the cell
    /// keeps both, stacked by this, and a host draws them from the bottom up. A negative z means
    /// something different in kind -- behind the <em>text</em> rather than behind another picture.
    /// </para>
    /// <para>A delete can also select by it, which the protocol's <c>d=z</c> and <c>d=q</c> targets
    /// require.</para>
    /// </remarks>
    public int ZIndex { get; }

    /// <summary>
    /// Pixels to shift the picture rightwards inside the first cell, from Kitty's <c>X=</c> key.
    /// </summary>
    /// <remarks>
    /// The shift happens INSIDE the cell box and does not enlarge it -- the protocol is explicit
    /// that an offset "is not added to the number of rows/columns" -- so what overflows the last
    /// cell is clipped. Clamped below a cell, which the protocol also requires; a larger value would
    /// push the whole picture out of the first cell and mean nothing.
    /// </remarks>
    public int OffsetX { get; }

    /// <summary>Pixels to shift the picture downwards inside the first cell, from <c>Y=</c>.</summary>
    public int OffsetY { get; }

    /// <summary>
    /// The whole image at its natural size, which is what Sixel produces.
    /// </summary>
    public static ImagePlacement Natural(TerminalImage image, int id = 0)
        => new(image, id, 0, 0, image.PixelWidth, image.PixelHeight,
               image.Cols, image.Rows, ImageScaling.Natural);

    public ImagePlacement(TerminalImage image, int id,
                          int sourceX, int sourceY, int sourceWidth, int sourceHeight,
                          int cols, int rows, ImageScaling scaling, int zIndex = 0,
                          int offsetX = 0, int offsetY = 0)
    {
        Image = image ?? throw new ArgumentNullException(nameof(image));

        if (sourceWidth <= 0 || sourceHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "A placement must show some pixels.");
        if (cols <= 0 || rows <= 0)
            throw new ArgumentOutOfRangeException(nameof(cols), "A placement must cover at least one cell.");

        // Clamped rather than rejected. The crop comes from another process, and a picture shown
        // slightly smaller than asked for is a better answer to a bad rectangle than no picture.
        SourceX = Math.Clamp(sourceX, 0, image.PixelWidth - 1);
        SourceY = Math.Clamp(sourceY, 0, image.PixelHeight - 1);
        SourceWidth = Math.Min(sourceWidth, image.PixelWidth - SourceX);
        SourceHeight = Math.Min(sourceHeight, image.PixelHeight - SourceY);

        Id = id;
        Cols = cols;
        Rows = rows;
        Scaling = scaling;
        ZIndex = zIndex;
        OffsetX = Math.Clamp(offsetX, 0, Math.Max(0, image.CellWidth - 1));
        OffsetY = Math.Clamp(offsetY, 0, Math.Max(0, image.CellHeight - 1));
    }

    /// <summary>
    /// Gets the source rectangle, in image pixels, for one tile of this placement.
    /// </summary>
    /// <remarks>
    /// <para>The two scalings are genuinely different arithmetic, not one formula with a special
    /// case. A 1160 pixel wide image over a 14 pixel cell needs 83 cells, and 83 times 14 is 1162 --
    /// so dividing the source proportionally across those 83 cells gives 13 pixel tiles and
    /// disagrees with the natural layout on every tile, not merely the last. Folding the two
    /// together would quietly resample every Sixel image by a couple of pixels.</para>
    /// <para>Under <see cref="ImageScaling.Natural"/> the edge tiles come back narrower or shorter
    /// than a full cell, and the caller scales the destination to match rather than stretching a
    /// part tile over a whole cell.</para>
    /// </remarks>
    /// <returns>False when the tile lies outside the placement.</returns>
    public bool TryGetTileSource(int tileCol, int tileRow, out int x, out int y, out int width, out int height)
        => TryGetTileLayout(tileCol, tileRow, out x, out y, out width, out height,
                            out _, out _, out _, out _);

    /// <summary>
    /// Everything a renderer needs for one tile: which pixels to take, and where in the cell to put
    /// them.
    /// </summary>
    /// <remarks>
    /// <para>One call rather than two because the destination is not derivable from the source size
    /// once a placement can be shifted within its first cell. With <c>X=3</c> the leading tile shows
    /// fewer pixels AND starts three pixels in, and only the placement knows which of those is
    /// happening.</para>
    /// <para>The model is uniform across both scalings and the offsets. The placement owns a box of
    /// <see cref="Cols"/> by <see cref="Rows"/> cells; inside that box the picture occupies a pixel
    /// span starting at <see cref="OffsetX"/>, <see cref="OffsetY"/> -- its own size when natural,
    /// the whole box when stretched. A tile is one cell of the box, so its content is the
    /// intersection of the cell with that span, mapped back through the span onto the source
    /// rectangle. Every previous special case falls out of that intersection.</para>
    /// </remarks>
    /// <param name="cellOffsetX">Where in the cell to start drawing, as a fraction of a cell.</param>
    /// <param name="cellsWide">How much of the cell to fill, as a fraction of a cell.</param>
    /// <returns>False when the tile lies outside the placement, or shows no pixels at all.</returns>
    public bool TryGetTileLayout(int tileCol, int tileRow,
                                 out int x, out int y, out int width, out int height,
                                 out double cellOffsetX, out double cellOffsetY,
                                 out double cellsWide, out double cellsHigh)
    {
        x = y = width = height = 0;
        cellOffsetX = cellOffsetY = 0;
        cellsWide = cellsHigh = 0;

        if (tileCol < 0 || tileRow < 0 || tileCol >= Cols || tileRow >= Rows)
            return false;

        var cellWidth = Image.CellWidth;
        var cellHeight = Image.CellHeight;
        if (cellWidth <= 0 || cellHeight <= 0)
            return false;

        var spanX = Scaling == ImageScaling.Natural ? SourceWidth : Cols * cellWidth;
        var spanY = Scaling == ImageScaling.Natural ? SourceHeight : Rows * cellHeight;

        if (!IntersectAxis(tileCol, cellWidth, OffsetX, spanX, SourceX, SourceWidth,
                           out x, out width, out cellOffsetX, out cellsWide))
            return false;

        return IntersectAxis(tileRow, cellHeight, OffsetY, spanY, SourceY, SourceHeight,
                             out y, out height, out cellOffsetY, out cellsHigh);
    }

    /// <summary>
    /// Intersects one cell with the picture's span along one axis, and maps the result back to
    /// source pixels.
    /// </summary>
    /// <remarks>
    /// Both edges are mapped independently from the box coordinate rather than one being derived as
    /// the other plus a width. That is what makes adjacent tiles meet exactly: the right edge of one
    /// and the left edge of the next are the same expression on the same input, so no rounding can
    /// put a seam or an overlap between them.
    /// </remarks>
    private static bool IntersectAxis(int tile, int cellSize, int offset, int span,
                                      int sourceStart, int sourceSize,
                                      out int from, out int size,
                                      out double cellOffset, out double cells)
    {
        from = 0;
        size = 0;
        cellOffset = 0;
        cells = 0;

        var boxLow = tile * cellSize;
        var boxHigh = boxLow + cellSize;

        // The picture occupies [offset, offset + span) of the box.
        var visibleLow = Math.Max(boxLow, offset);
        var visibleHigh = Math.Min(boxHigh, offset + span);
        if (visibleHigh <= visibleLow)
            return false;

        // Scaling num and den by the same positive amount leaves the floor unchanged, so with no
        // offset this reproduces the earlier arithmetic exactly -- for stretched tiles as well.
        var low = sourceStart + (int)((long)(visibleLow - offset) * sourceSize / span);
        var high = sourceStart + (int)((long)(visibleHigh - offset) * sourceSize / span);

        // A picture stretched UP -- fewer source pixels than the box has screen pixels -- can give a
        // cell less than one source pixel, and then the floors above meet: a 1x1 image over two
        // columns maps cell 0 to [0, 0). Rejecting that dropped the whole placement, because the
        // caller decides each row from its first cell. The cell is showing part of a single source
        // pixel, so it takes the one under its centre.
        if (high <= low)
        {
            var centre = sourceStart +
                         (int)((long)(visibleLow + visibleHigh - 2 * offset) * sourceSize / (2L * span));
            low = Math.Clamp(centre, sourceStart, sourceStart + sourceSize - 1);
            high = low + 1;
        }

        from = low;
        size = high - low;
        cellOffset = (visibleLow - boxLow) / (double)cellSize;
        cells = (visibleHigh - visibleLow) / (double)cellSize;

        return size > 0;
    }

    /// <summary>
    /// How much of a cell one tile covers, as a fraction from 0 to 1.
    /// </summary>
    /// <remarks>
    /// Kept because it is the documented way to size a destination and hosts already call it.
    /// <see cref="TryGetTileLayout"/> supersedes it: this cannot express a tile that starts partway
    /// into its cell, so a placement carrying <see cref="OffsetX"/> or <see cref="OffsetY"/> needs
    /// the newer call.
    /// </remarks>
    public void GetTileCoverage(int sourceWidth, int sourceHeight, out double cellsWide, out double cellsHigh)
    {
        if (Scaling == ImageScaling.Stretched)
        {
            cellsWide = 1.0;
            cellsHigh = 1.0;
            return;
        }

        cellsWide = Image.CellWidth > 0 ? sourceWidth / (double)Image.CellWidth : 1.0;
        cellsHigh = Image.CellHeight > 0 ? sourceHeight / (double)Image.CellHeight : 1.0;
    }
}
