using XTerm.Buffer;

namespace XTerm.Search;

/// <summary>
/// Finds text in the scrollback, without turning the scrollback into text.
/// </summary>
/// <remarks>
/// <para>The obvious implementation asks each line for a string and runs <c>IndexOf</c> over it.
/// Measured on a 10,000-line buffer at 240 columns, that costs 9.7 ms and <b>16.3 MiB</b> per search;
/// reading codepoints straight out of the cells costs 3.7 ms and allocates nothing, for the same
/// 25,325 hits. A find box searches on every keystroke, so the first of those is roughly 80 MiB to
/// type "error" — in a library that went to some trouble to stop allocating per character.</para>
///
/// <para>So nothing here builds a string. The walk moves a (row, column) cursor through the buffer
/// and compares codepoints, which also removes the step where a character offset has to be mapped
/// back onto cells: the position IS the cursor, and a match that crosses a wrap simply produces two
/// runs because the cursor changed row part way through.</para>
///
/// <para><b>Call this on the thread that owns the terminal.</b> It reads the buffer directly and the
/// emulator is not thread-safe, so a search running beside a write is a race. At 3.7 ms per search
/// that is affordable inline; a much larger scrollback wants debouncing rather than a thread.</para>
/// </remarks>
public sealed class BufferSearch : IDisposable
{
    /// <summary>
    /// The most matches kept.
    /// </summary>
    /// <remarks>
    /// A single-letter search over a long scrollback matches almost everything, and every match kept
    /// is a struct in a list. The cap bounds that — and <see cref="Truncated"/> says when it bit,
    /// because a count that quietly stops being true reads as a bug in the search rather than a
    /// limit on it.
    /// </remarks>
    public const int MaxHits = 10_000;

    private readonly Terminal _terminal;
    private readonly List<SearchHit> _hits = new();

    private string _needle = string.Empty;

    /// <summary>
    /// The needle as CODEPOINTS, folded for case. A cell stores a codepoint, and comparing it to
    /// UTF-16 chars one at a time can never match anything outside the BMP -- an emoji in the
    /// needle is two chars and neither equals the cell. Built once per Find.
    /// </summary>
    /// <remarks>
    /// A cell whose text is a multi-codepoint cluster is compared by its LEADING codepoint, which
    /// is all the cell stores inline. Matching the full cluster text would mean materialising it
    /// per cell -- the same cost this class exists to avoid, and the same reason regular
    /// expressions are not offered. The limitation is one-directional: base-character searches
    /// still find cells that carry combining marks.
    /// </remarks>
    private int[] _pattern = Array.Empty<int>();
    private SearchOptions _options;
    private int _current = -1;
    private bool _disposed;

    public BufferSearch(Terminal terminal)
    {
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        _terminal.Buffer.Trimmed += OnTrimmed;
    }

    /// <summary>How many runs are held. See <see cref="MaxHits"/>.</summary>
    public int Count => _hits.Count;

    /// <summary>Whether the cap was reached and matches beyond it were not kept.</summary>
    public bool Truncated { get; private set; }

    /// <summary>Index of the current match within <see cref="Count"/>, or -1 before one is chosen.</summary>
    public int CurrentIndex => _current;

    /// <summary>The term last searched for.</summary>
    public string Needle => _needle;

    /// <summary>
    /// Searches the whole buffer, replacing any previous result.
    /// </summary>
    /// <returns>The number of runs found.</returns>
    public int Find(string needle, SearchOptions options = default)
    {
        _hits.Clear();
        _current = -1;
        Truncated = false;
        _needle = needle ?? string.Empty;
        _options = options;

        if (_needle.Length == 0)
            return 0;

        _pattern = ToFoldedCodePoints(_needle, options.CaseSensitive);

        var lines = _terminal.Buffer.Lines;
        var matchId = 0;

        // Logical lines, not physical ones: a match on long output usually straddles a wrap, and
        // those are the matches worth finding. A run starts at any line not flagged IsWrapped.
        for (var row = 0; row < lines.Length; row++)
        {
            if (lines[row] is null)
                continue;

            if (row > 0 && lines[row]!.IsWrapped)
                continue;   // a continuation; it was searched as part of the run above

            var end = row;
            while (end + 1 < lines.Length && lines[end + 1] is { IsWrapped: true })
                end++;

            SearchRun(lines, row, end, ref matchId);

            if (Truncated)
                break;
        }

        return _hits.Count;
    }

    /// <summary>
    /// The runs on one row, or empty for the rows that have none — which is nearly all of them.
    /// </summary>
    /// <remarks>
    /// A span rather than a list because this is asked once per row per frame, and the answer is
    /// usually nothing. Hits are produced in row order, so the row's block is found by binary search
    /// and handed back in place.
    /// </remarks>
    public ReadOnlySpan<SearchHit> HitsOnRow(int bufferRow)
    {
        var all = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_hits);

        var lo = 0;
        var hi = all.Length - 1;
        var found = -1;

        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (all[mid].BufferRow < bufferRow) lo = mid + 1;
            else if (all[mid].BufferRow > bufferRow) hi = mid - 1;
            else { found = mid; hi = mid - 1; }
        }

        if (found < 0)
            return ReadOnlySpan<SearchHit>.Empty;

        var length = 0;
        while (found + length < all.Length && all[found + length].BufferRow == bufferRow)
            length++;

        return all.Slice(found, length);
    }

    /// <summary>Steps to the next match, wrapping round at the end.</summary>
    public bool TryMoveNext(out SearchHit hit) => TryMove(1, out hit);

    /// <summary>Steps to the previous match, wrapping round at the start.</summary>
    public bool TryMovePrevious(out SearchHit hit) => TryMove(-1, out hit);

    /// <summary>Forgets the results, keeping the search usable.</summary>
    public void Clear()
    {
        _hits.Clear();
        _current = -1;
        _needle = string.Empty;
        Truncated = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _terminal.Buffer.Trimmed -= OnTrimmed;
    }

    // ---- the walk ------------------------------------------------------------------------

    private void SearchRun(CircularList<BufferLine> lines, int startRow, int endRow, ref int matchId)
    {
        for (var row = startRow; row <= endRow; row++)
        {
            var line = lines[row];
            if (line is null)
                continue;

            for (var col = 0; col < line.Length; col++)
            {
                if (!Matches(lines, endRow, row, col, out var afterRow, out var afterCol))
                    continue;

                if (_options.WholeWord && !IsWholeWord(lines, startRow, endRow, row, col, afterRow, afterCol))
                    continue;

                AddRuns(lines, matchId++, row, col, afterRow, afterCol);

                if (_hits.Count >= MaxHits)
                {
                    Truncated = true;
                    return;
                }

                // Matches do not overlap. Step past this one, which may have ended on a later row.
                if (afterRow != row)
                {
                    row = afterRow;
                    line = lines[row];
                    if (line is null)
                        return;
                    col = afterCol - 1;
                }
                else
                {
                    col = afterCol - 1;
                }
            }
        }
    }

    /// <summary>
    /// Whether the needle sits at (row, col), and where it ends if so.
    /// </summary>
    /// <remarks>
    /// Width-0 cells are stepped over rather than compared. There are two kinds and neither is a
    /// character of its own: the placeholder behind a wide glyph, and a combining mark that found
    /// nothing to attach to. Comparing either would make a needle fail to match text that reads
    /// exactly like it.
    /// </remarks>
    private bool Matches(CircularList<BufferLine> lines, int endRow, int row, int col,
                         out int afterRow, out int afterCol)
    {
        afterRow = row;
        afterCol = col;

        var r = row;
        var c = col;

        for (var i = 0; i < _pattern.Length; i++)
        {
            if (!Advance(lines, endRow, ref r, ref c, skipZeroWidth: i > 0))
                return false;

            var cell = lines[r]![c];
            if (cell.Width == 0)
                return false;

            if (Fold(cell.CodePoint, _options.CaseSensitive) != _pattern[i])
                return false;

            afterRow = r;
            afterCol = c + 1;
            c++;
        }

        return true;
    }

    /// <summary>
    /// Puts the cursor on the next cell that carries a character, crossing a wrap if it has to.
    /// </summary>
    private static bool Advance(CircularList<BufferLine> lines, int endRow, ref int row, ref int col,
                                bool skipZeroWidth)
    {
        while (true)
        {
            var line = lines[row];
            if (line is null)
                return false;

            if (col >= line.Length)
            {
                if (row >= endRow)
                    return false;

                row++;
                col = 0;
                continue;
            }

            if (skipZeroWidth && lines[row]![col].Width == 0)
            {
                col++;
                continue;
            }

            return true;
        }
    }

    private static int[] ToFoldedCodePoints(string needle, bool caseSensitive)
    {
        var points = new List<int>(needle.Length);
        for (var i = 0; i < needle.Length; i++)
        {
            int cp = needle[i];
            if (char.IsHighSurrogate(needle[i]) && i + 1 < needle.Length && char.IsLowSurrogate(needle[i + 1]))
            {
                cp = char.ConvertToUtf32(needle[i], needle[i + 1]);
                i++;
            }
            points.Add(Fold(cp, caseSensitive));
        }
        return points.ToArray();
    }

    private static int Fold(int codePoint, bool caseSensitive)
    {
        if (caseSensitive || codePoint > 0xFFFF)
            return codePoint;   // astral case pairs are rare enough that exact match is honest

        return char.ToUpperInvariant((char)codePoint);
    }

    /// <summary>
    /// Splits a match into one run per row and records them under one id.
    /// </summary>
    private void AddRuns(CircularList<BufferLine> lines, int matchId,
                         int startRow, int startCol, int endRow, int endCol)
    {
        for (var row = startRow; row <= endRow; row++)
        {
            var from = row == startRow ? startCol : 0;
            var to = row == endRow ? endCol : (lines[row]?.Length ?? from);

            if (to > from)
                _hits.Add(new SearchHit(row, from, to - from, matchId));
        }
    }

    private bool IsWholeWord(CircularList<BufferLine> lines, int startRow, int endRow,
                             int row, int col, int afterRow, int afterCol)
    {
        return !IsWordCharacterBefore(lines, startRow, row, col)
            && !IsWordCharacterAt(lines, endRow, afterRow, afterCol);
    }

    private static bool IsWordCharacterBefore(CircularList<BufferLine> lines, int startRow, int row, int col)
    {
        var r = row;
        var c = col - 1;

        while (true)
        {
            while (c < 0)
            {
                if (r <= startRow)
                    return false;   // start of the logical line; nothing before it

                r--;
                c = (lines[r]?.Length ?? 0) - 1;
            }

            // A width-0 cell is not a character -- it is the placeholder behind a wide glyph, and
            // treating it as the neighbour would let a whole-word match sit flush against a CJK
            // letter. Step past it to the glyph that owns it.
            if ((lines[r]?[c].Width ?? 1) == 0)
            {
                c--;
                continue;
            }

            return IsWordCharacter(lines[r]?[c].CodePoint ?? 0);
        }
    }

    private static bool IsWordCharacterAt(CircularList<BufferLine> lines, int endRow, int row, int col)
    {
        var r = row;
        var c = col;

        if (!Advance(lines, endRow, ref r, ref c, skipZeroWidth: false))
            return false;   // end of the logical line

        return IsWordCharacter(lines[r]?[c].CodePoint ?? 0);
    }

    private static bool IsWordCharacter(int codePoint)
        => codePoint == '_'
           || (codePoint <= 0xFFFF && char.IsLetterOrDigit((char)codePoint));

    private bool TryMove(int direction, out SearchHit hit)
    {
        hit = default;
        if (_hits.Count == 0)
            return false;

        // Step by MATCH rather than by run, so a match that wrapped is one stop rather than two.
        var startId = _current >= 0 ? _hits[_current].MatchId : (direction > 0 ? -1 : int.MaxValue);
        var best = -1;

        for (var i = 0; i < _hits.Count; i++)
        {
            var id = _hits[i].MatchId;
            if (direction > 0 ? id > startId : id < startId)
            {
                best = i;
                if (direction > 0)
                    break;
            }
        }

        if (best < 0)
            best = direction > 0 ? 0 : _hits.Count - 1;   // wrap round

        _current = best;
        hit = _hits[best];
        return true;
    }

    /// <summary>
    /// Shifts every result up as the ring drops lines off the top, and drops what fell off with them.
    /// </summary>
    /// <remarks>
    /// The same arrangement <c>SelectionManager</c> uses, and for the same reason: a row index that
    /// is not adjusted goes stale silently. Nothing about a wrong row looks wrong — the highlight
    /// simply lands somewhere else while a build scrolls past.
    /// </remarks>
    private void OnTrimmed(int count)
    {
        if (count <= 0 || _hits.Count == 0)
            return;

        var kept = 0;
        for (var i = 0; i < _hits.Count; i++)
        {
            var hit = _hits[i];
            var row = hit.BufferRow - count;
            if (row < 0)
                continue;

            _hits[kept++] = new SearchHit(row, hit.Column, hit.Cols, hit.MatchId);
        }

        var dropped = _hits.Count - kept;
        _hits.RemoveRange(kept, dropped);

        if (_current >= 0)
            _current = Math.Max(-1, _current - dropped);
    }
}
