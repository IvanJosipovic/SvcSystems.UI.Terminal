namespace XTerm.Common;

/// <summary>
/// Cached single-codepoint strings.
///
/// The print path needs a <see cref="string"/> for each printed codepoint — the parser's Print event
/// carries one, the charset translator returns one, and <c>BufferCell.Content</c> stores one. Building
/// it with <c>char.ConvertFromUtf32</c> or <c>char.ToString()</c> allocates once per printed character,
/// which measured at ~119 bytes allocated per character on plain ASCII output and dominated emulator
/// throughput.
///
/// The strings are immutable and, for any given codepoint, always equal — so there is no reason to
/// build more than one. Handing out a cached instance keeps every existing signature and every
/// existing comparison working (<c>==</c> on string is value equality either way) while removing the
/// allocation entirely for the range that matters.
///
/// Two tiers:
///
///   U+0000..U+07FF is built eagerly — ASCII, Latin, Greek, Cyrillic, Hebrew, Arabic. Small enough
///   (2048 refs) that filling it up front is cheaper than branching on whether it is filled.
///
///   U+0800..U+FFFF is a lazily created, lazily populated table covering the rest of the BMP: CJK,
///   kana, hangul. A terminal showing Japanese was allocating for every glyph without it. The table
///   is only created when a codepoint in that range is actually printed, so ASCII-only sessions
///   never pay the 512 KB of references.
///
///   U+1F000..U+1FFFF is a third lazily created table. Measuring by content class showed every BMP
///   class allocating nothing while emoji, regional indicators and ZWJ sequences allocated 13.7–17.2
///   bytes per character — all of them above the BMP. That one plane holds essentially every emoji,
///   so 4096 more slots close the gap without indexing all of Unicode, which would need 1.1M.
///
/// Anything else above the BMP — CJK Extension B and friends — still allocates. Those are genuinely
/// rare in terminal output, and unlike emoji they are not concentrated in a single indexable plane.
/// </summary>
internal static class CodePointText
{
    /// <summary>Exclusive upper bound of the cached range. Chosen to cover the common scripts in one 16 KB table.</summary>
    private const int CacheSize = 0x0800;

    private static readonly string[] Cache = Build();

    /// <summary>
    /// U+0800..U+FFFF, created on first use. Null until a codepoint in the range is printed.
    ///
    /// Races here are benign and deliberately not locked against. Publishing the array uses
    /// CompareExchange so every thread ends up on one instance; filling a slot is a reference
    /// assignment, which is atomic, and two threads that race on the same codepoint compute equal
    /// strings, so whichever wins is indistinguishable. Locking a path this hot to prevent an
    /// occasional duplicate string would cost far more than the duplicate.
    /// </summary>
    private static string?[]? _bmp;

    private const int BmpStart = CacheSize;
    private const int BmpEnd = 0x10000;

    /// <summary>U+1F000..U+1FFFF — the emoji plane. Same lazy, benignly-racy scheme as <see cref="_bmp"/>.</summary>
    private static string?[]? _emoji;

    private const int EmojiStart = 0x1F000;
    private const int EmojiEnd = 0x20000;

    private static string[] Build()
    {
        var cache = new string[CacheSize];
        for (var i = 0; i < CacheSize; i++)
            cache[i] = char.ConvertFromUtf32(i);
        return cache;
    }

    private static string GetBmp(int codePoint)
    {
        var table = _bmp;
        if (table is null)
        {
            table = new string?[BmpEnd - BmpStart];
            table = Interlocked.CompareExchange(ref _bmp, table, null) ?? table;
        }

        var index = codePoint - BmpStart;
        return table[index] ??= char.ConvertFromUtf32(codePoint);
    }

    private static string GetEmoji(int codePoint)
    {
        var table = _emoji;
        if (table is null)
        {
            table = new string?[EmojiEnd - EmojiStart];
            table = Interlocked.CompareExchange(ref _emoji, table, null) ?? table;
        }

        var index = codePoint - EmojiStart;
        return table[index] ??= char.ConvertFromUtf32(codePoint);
    }

    /// <summary>
    /// The string for <paramref name="codePoint"/>, from the cache when it is in range.
    /// Behaviourally identical to <c>char.ConvertFromUtf32(codePoint)</c>.
    /// </summary>
    public static string Get(int codePoint)
    {
        if ((uint)codePoint < CacheSize)
            return Cache[codePoint];

        if ((uint)codePoint < BmpEnd)
            return GetBmp(codePoint);

        if (codePoint >= EmojiStart && codePoint < EmojiEnd)
            return GetEmoji(codePoint);

        return char.ConvertFromUtf32(codePoint);
    }

    /// <summary>The string for a single UTF-16 code unit. Identical to <c>c.ToString()</c>.</summary>
    public static string Get(char c) =>
        c < CacheSize ? Cache[c] : GetBmp(c);
}
