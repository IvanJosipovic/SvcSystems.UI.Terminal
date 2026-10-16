using XTerm.Buffer;
using XTerm.Parser;

namespace XTerm;

/// <summary>
/// The column operations: DECIC and DECDC insert and delete columns at the cursor, DECBI and
/// DECFI index the cursor sideways and scroll the region a column when it has nowhere to go.
/// One file because they share one shape: a vertical slab of the scrolling region -- rows
/// between the top and bottom margins, columns between the left and right -- sliding sideways.
/// </summary>
public partial class InputHandler
{
    /// <summary>Whether the cursor sits inside the scrolling region on both axes.</summary>
    private bool CursorInsideRegion()
        => _buffer.Y >= _buffer.ScrollTop && _buffer.Y <= _buffer.ScrollBottom
        && _buffer.X >= _buffer.ScrollLeft && _buffer.X <= _buffer.ScrollRight;

    /// <summary>
    /// Slides the columns of the region's rows. <paramref name="at"/> is the first affected
    /// column; a positive <paramref name="by"/> opens blanks there pushing content toward the
    /// right margin, a negative one closes the gap pulling content leftward. What slides off
    /// either margin is gone; the vacated columns are blank with the erase attributes.
    /// </summary>
    private void SlideColumns(int at, int by)
    {
        var left = at;
        var right = _buffer.ScrollRight;
        var blank = new BufferCell(" ", 1, GetEraseAttributes());

        for (var row = _buffer.ScrollTop; row <= _buffer.ScrollBottom; row++)
        {
            var line = _buffer.Lines[_buffer.YBase + row];
            if (line is null)
                continue;

            if (by > 0)
            {
                for (var col = right; col >= left; col--)
                {
                    var cell = col - by >= left && col - by < line.Length ? line[col - by] : blank;
                    if (col < line.Length)
                        line.SetCell(col, ref cell);
                }
            }
            else
            {
                for (var col = left; col <= right; col++)
                {
                    var src = col - by;   // by is negative: src is to the right
                    var cell = src <= right && src < line.Length ? line[src] : blank;
                    if (col < line.Length)
                        line.SetCell(col, ref cell);
                }
            }
        }
    }

    /// <summary>DECIC (CSI Pn ' }) -- inserts blank columns at the cursor, inside the region only.</summary>
    private void InsertColumns(Params parameters)
    {
        if (!CursorInsideRegion())
            return;

        var count = Math.Max(parameters.GetParam(0, 1), 1);
        SlideColumns(_buffer.X, Math.Min(count, _buffer.ScrollRight - _buffer.X + 1));
    }

    /// <summary>DECDC (CSI Pn ' ~) -- deletes columns at the cursor, inside the region only.</summary>
    private void DeleteColumns(Params parameters)
    {
        if (!CursorInsideRegion())
            return;

        var count = Math.Max(parameters.GetParam(0, 1), 1);
        SlideColumns(_buffer.X, -Math.Min(count, _buffer.ScrollRight - _buffer.X + 1));
    }

    /// <summary>
    /// DECBI (ESC 6) -- back index: left one column, and AT the left margin the region slides
    /// right a column instead, cursor staying put. The scroll requires the cursor inside the
    /// region's rows; a cursor at the screen's own left edge with no margin to give has nowhere
    /// to go and nothing to slide.
    /// </summary>
    private void BackIndex()
    {
        if (_buffer.X == _buffer.ScrollLeft
            && _buffer.Y >= _buffer.ScrollTop && _buffer.Y <= _buffer.ScrollBottom)
        {
            SlideColumns(_buffer.ScrollLeft, 1);
            return;
        }

        if (_buffer.X > 0)
            _buffer.SetCursor(_buffer.X - 1, _buffer.Y);
    }

    /// <summary>DECFI (ESC 9) -- forward index, the mirror: at the right margin the region slides left.</summary>
    private void ForwardIndex()
    {
        // AT the right margin, from inside the pane, the region slides left -- and the phantom
        // column a full line leaves the cursor in counts as at-the-margin, not past it. A cursor
        // genuinely OUTSIDE the margins is DEC STD 070's other case: it simply moves, and may
        // step right past the pane it was never in.
        var atMargin = _buffer.X == _buffer.ScrollRight
                       || (_buffer.PendingWrap && _buffer.X == _buffer.ScrollRight + 1);
        if (atMargin && _buffer.X >= _buffer.ScrollLeft
            && _buffer.Y >= _buffer.ScrollTop && _buffer.Y <= _buffer.ScrollBottom)
        {
            SlideColumns(_buffer.ScrollLeft, -1);
            if (_buffer.X > _buffer.ScrollRight)
                _buffer.SetCursor(_buffer.ScrollRight, _buffer.Y);
            return;
        }

        if (_buffer.X < _terminal.Cols - 1)
            _buffer.SetCursor(_buffer.X + 1, _buffer.Y);
    }
}
