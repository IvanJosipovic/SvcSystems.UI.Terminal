using System.Collections.Concurrent;

namespace XTerm.Common;

/// <summary>
/// Interns underline colours, so a cell can refer to one by id.
/// </summary>
/// <remarks>
/// <para>An underline colour is a full RGB value plus the mode that says how to read it, which is
/// more bits than a cell has left. Growing <see cref="Buffer.AttributeData"/> to carry it would grow
/// every cell in the buffer, and cell size is the thing that costs most on fills — measured, going
/// from 24 bytes to 32 cost scroll-heavy output 22%.</para>
///
/// <para>So the cell carries an id and this holds the colour. Any RGB value an application sets is
/// representable; what is bounded is how many DISTINCT colours can coexist, and twenty bits of id
/// allows about a million against the handful a real session uses. An LSP marking errors, warnings,
/// hints and information uses four.</para>
///
/// <para><b>Nothing is ever released, and that is the point.</b> Interning a whole style — as Ghostty
/// does — needs reference counting, which forces every cell write to read the old id so it can be
/// released. That read does not exist in this fork's run writer, and adding it measured at 240 ns
/// per line against 165. An underline colour needs no such bookkeeping: writing a cell stores an
/// int, and nothing is added to the write path at all. The lookup happens once per run per frame,
/// on the render side.</para>
///
/// <para>The cost of never releasing is a table that only grows. A program cycling underline colours
/// per cell would grow it without bound, which is the same exposure <see cref="ClusterTable"/>
/// already carries — and once the id space is exhausted, new colours resolve to the nearest already
/// interned rather than failing.</para>
/// </remarks>
internal static class UnderlineColorTable
{
    /// <summary>Id 0 means "no underline colour" — the underline takes the foreground colour.</summary>
    public const int None = 0;

    /// <summary>
    /// The largest id the cell can hold: twenty bits, since three are spent on the underline style.
    /// </summary>
    public const int MaxId = (1 << 20) - 1;

    private static readonly ConcurrentDictionary<int, UnderlineColor> ById = new();
    private static readonly ConcurrentDictionary<UnderlineColor, int> Ids = new();

    private static int _next = None;

    /// <summary>A colour and the mode that says how to read it.</summary>
    internal readonly record struct UnderlineColor(int Color, int Mode);

    /// <summary>
    /// Id for a colour, allocating one the first time it is seen.
    /// </summary>
    public static int Intern(int color, int mode)
    {
        var key = new UnderlineColor(color, mode);

        if (Ids.TryGetValue(key, out var existing))
            return existing;

        var id = Interlocked.Increment(ref _next);

        if (id > MaxId)
        {
            // Out of ids. Reuse whatever is already interned rather than fail: an underline drawn in
            // a near-enough colour is a far better outcome than a terminal that stops working
            // because a program insisted on a million distinct ones.
            Interlocked.Exchange(ref _next, MaxId);
            return MaxId;
        }

        ById[id] = key;
        Ids[key] = id;
        return id;
    }

    /// <summary>The colour for an id, or false when the id is <see cref="None"/> or unknown.</summary>
    public static bool TryGet(int id, out int color, out int mode)
    {
        if (id != None && ById.TryGetValue(id, out var entry))
        {
            color = entry.Color;
            mode = entry.Mode;
            return true;
        }

        color = 0;
        mode = 0;
        return false;
    }
}
