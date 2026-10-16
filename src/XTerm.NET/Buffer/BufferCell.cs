using System.Diagnostics;
using System.Text;
using XTerm.Common;
using XTerm.Graphics;

namespace XTerm.Buffer;

/// <summary>
/// Represents a single cell in the terminal buffer.
/// Each cell contains a character, width, and attributes.
///
/// The cell holds NO reference types, deliberately. It used to store its text as a string, which made
/// every cell of the buffer a GC reference: the collector had to trace the entire scrollback, and the
/// runtime emitted a write barrier for every cell written or filled. Measured on a 240-column line,
/// that cost 238 ns per fill against 75 ns without the reference, and 0.88 ns against 0.35 ns per
/// single cell assignment — the fill being what every scroll does, and the assignment what every
/// printed character does. Dropping it also took the struct from 32 bytes to 24.
///
/// <see cref="Content"/> is therefore derived rather than stored: from <see cref="CodePoint"/> for
/// the single-codepoint case, which is nearly all of them, and otherwise from
/// <see cref="ClusterId"/> via <see cref="ClusterTable"/>. It remains settable, and setting it still
/// round-trips, so callers cannot tell the difference.
/// </summary>
[DebuggerDisplay("'{Content}'  [{Width}, {Attributes}, {CodePoint}]")]
public struct BufferCell : IEquatable<BufferCell>
{
    public int Width = 0;
    public AttributeData Attributes = AttributeData.Default;
    public int CodePoint = 0;

    /// <summary>
    /// Identifies this cell's text when it spans more than one codepoint — a base character with
    /// combining marks, a ZWJ emoji sequence, a charset mapping that expands. <see cref="ClusterTable.None"/>
    /// for the ordinary case, where <see cref="CodePoint"/> alone says everything.
    /// </summary>
    public int ClusterId = ClusterTable.None;

    /// <summary>
    /// The text this cell displays.
    ///
    /// Derived, not stored. Setting it decomposes into <see cref="CodePoint"/> and, only when the
    /// text spans several codepoints, an interned <see cref="ClusterId"/>.
    /// </summary>
    public string Content
    {
        get
        {
            if (ClusterId != ClusterTable.None)
                return ClusterTable.Get(ClusterId);

            return CodePoint == 0 ? string.Empty : CodePointText.Get(CodePoint);
        }
        set
        {
            if (string.IsNullOrEmpty(value))
            {
                CodePoint = 0;
                ClusterId = ClusterTable.None;
                return;
            }

            var isPair = value.Length >= 2
                && char.IsHighSurrogate(value[0]) && char.IsLowSurrogate(value[1]);

            // The leading codepoint, which callers use for width and for combining-character tests.
            //
            // A LONE surrogate is not a scalar value, so ConvertToUtf32 throws on one — and it does
            // reach here, because the text comes from a hosted program that may emit any UTF-16 it
            // likes, and this used to be a plain string field that stored whatever it was given.
            // Recorded as U+FFFD, which is what it renders as anyway.
            CodePoint = isPair ? char.ConvertToUtf32(value[0], value[1])
                      : char.IsSurrogate(value[0]) ? 0xFFFD
                      : value[0];

            // One codepoint — one char, or a single surrogate pair — needs no cluster entry, since
            // the text is recoverable from the codepoint. A lone surrogate is not recoverable from
            // U+FFFD, so it interns like any other cluster and Content still returns what was set.
            var isSingle = (value.Length == 1 && !char.IsSurrogate(value[0]))
                || (value.Length == 2 && isPair);

            ClusterId = isSingle ? ClusterTable.None : ClusterTable.Intern(value);
        }
    }

    public static BufferCell Empty => new BufferCell();

    public static BufferCell Space => new BufferCell
    {
        Width = 1,
        Attributes = AttributeData.Default,
        CodePoint = 0x20
    };

    public BufferCell()
    {
        Attributes = AttributeData.Default;
    }

    public BufferCell(string content, int width, AttributeData attributes)
    {
        Width = width;
        Attributes = attributes;
        Content = content;
    }

    public BufferCell(int codePoint, int width, AttributeData attributes)
    {
        CodePoint = codePoint;
        Width = width;
        Attributes = attributes;
        ClusterId = ClusterTable.None;
    }

    public bool IsEmpty() => CodePoint == Empty.CodePoint;

    public bool IsSpace() => CodePoint == Space.CodePoint;

    public bool Equals(BufferCell other)
    {
        // No image term: a picture is a run on the line, so a cell beneath one is an ordinary space
        // and SHOULD compare equal to one — renderers coalesce adjacent cells by comparing them, and
        // merging cells under a picture is correct now that nothing about it is drawn from cells.
        //
        // And CodePoint plus ClusterId is exactly what Content is derived from, so comparing those
        // compares the text without materialising two strings to do it.
        return CodePoint == other.CodePoint &&
               ClusterId == other.ClusterId &&
               Width == other.Width &&
               Attributes.Equals(other.Attributes);
    }

    public override bool Equals(object? obj)
    {
        return obj is BufferCell other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(CodePoint, ClusterId, Width, Attributes);
    }

    public static bool operator ==(BufferCell left, BufferCell right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(BufferCell left, BufferCell right)
    {
        return !left.Equals(right);
    }
}
