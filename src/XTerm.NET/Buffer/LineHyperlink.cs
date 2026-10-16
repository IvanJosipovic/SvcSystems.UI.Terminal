namespace XTerm.Buffer;

/// <summary>
/// A span of columns on a line that an OSC 8 hyperlink covers.
/// </summary>
/// <remarks>
/// <para>The point of OSC 8 is a link whose DISPLAY TEXT is not the URL — "click here", a filename,
/// a commit subject. That is exactly what a regular expression over the visible text cannot find,
/// which is why URL detection and this are complementary features rather than the same one.</para>
///
/// <para>The URL is held here as a string rather than interned into a table the way cluster text is.
/// Interning is free only when the set is bounded, and cluster text is: a terminal sees a handful of
/// distinct emoji sequences in a session. URLs are not — <c>ls --hyperlink</c> over a large directory
/// emits one per file — so a table nothing is ever released from would be a leak that grows with
/// output. The line owns the string, and it goes when the line does.</para>
///
/// <para>That costs the cell nothing: the reference lives in a list on the LINE, next to the images
/// a line already holds strongly, and never in the cell array — which stays 24 bytes and
/// reference-free.</para>
/// </remarks>
public readonly struct LineHyperlink
{
    /// <summary>First column the link covers.</summary>
    public readonly int Column;

    /// <summary>How many columns it covers.</summary>
    public readonly int Cols;

    /// <summary>The URI to open.</summary>
    public readonly string Url;

    /// <summary>
    /// The <c>id=</c> parameter, or null when the client sent none.
    /// </summary>
    /// <remarks>
    /// What groups spans that are not contiguous into one logical link, so hovering a link that
    /// wrapped across two lines highlights both halves. Two spans with the same id are the same
    /// link; two with the same URL and no id are not necessarily.
    /// </remarks>
    public readonly string? Id;

    public LineHyperlink(int column, int cols, string url, string? id = null)
    {
        Column = column;
        Cols = cols;
        Url = url;
        Id = id;
    }

    /// <summary>One past the last column covered.</summary>
    public int EndColumn => Column + Cols;

    /// <summary>Whether this link covers <paramref name="column"/>.</summary>
    public bool Covers(int column) => column >= Column && column < EndColumn;

    /// <summary>Whether two spans are the same link, and so may be joined.</summary>
    internal bool SameLinkAs(string url, string? id)
        => string.Equals(Url, url, StringComparison.Ordinal)
           && string.Equals(Id, id, StringComparison.Ordinal);

    public override string ToString() => $"{Url}@{Column}+{Cols}";
}
