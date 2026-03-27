using System.Text;

namespace AvaloniaTerminal;

/// <summary>
/// Handles buffer search and search result navigation.
/// </summary>
public sealed class SearchService
{
    private readonly Terminal _terminal;
    private SearchSnapshot? _cache;

    public SearchService(Terminal terminal)
    {
        _terminal = terminal;
    }

    public SearchSnapshot GetSnapshot()
    {
        _cache ??= CreateSnapshot();
        return _cache;
    }

    public void Invalidate()
    {
        _cache = null;
    }

    private SearchSnapshot CreateSnapshot()
    {
        List<SearchSnapshot.SearchLine> lines = [];
        StringBuilder text = new();

        var buffer = _terminal.Buffer;
        for (int row = 0; row < buffer.Lines.Length; row++)
        {
            string lineText = buffer.Lines[row]?.TranslateToString(trimRight: false) ?? string.Empty;
            lines.Add(new SearchSnapshot.SearchLine(row, lineText, text.Length));
            text.Append(lineText);

            if (row < buffer.Lines.Length - 1)
            {
                text.Append('\n');
            }
        }

        return new SearchSnapshot(lines.ToArray(), text.ToString());
    }
}

public sealed class SearchSnapshot
{
    public sealed class SearchResult
    {
        public BufferPoint Start { get; set; }

        public BufferPoint End { get; set; }
    }

    public sealed record SearchLine(int BufferY, string Text, int StartIndex);

    private readonly SearchLine[] _lines;

    public SearchSnapshot(SearchLine[] lines, string text)
    {
        _lines = lines;
        Text = text;
    }

    public string Text { get; }

    public string LastSearch { get; private set; } = string.Empty;

    public SearchResult[] LastSearchResults { get; private set; } = [];

    public int CurrentSearchResult { get; set; } = -1;

    public int FindText(string txt)
    {
        LastSearch = txt;
        CurrentSearchResult = -1;

        if (string.IsNullOrEmpty(txt) || _lines.Length == 0)
        {
            LastSearchResults = [];
            return 0;
        }

        List<SearchResult> results = [];

        for (int i = 0; i < _lines.Length; i++)
        {
            SearchLine line = _lines[i];
            if (string.IsNullOrEmpty(line.Text))
            {
                continue;
            }

            int index = line.Text.IndexOf(txt, StringComparison.CurrentCultureIgnoreCase);
            while (index >= 0)
            {
                results.Add(new SearchResult
                {
                    Start = new BufferPoint(index, line.BufferY),
                    End = new BufferPoint(index + txt.Length, line.BufferY),
                });

                index = line.Text.IndexOf(txt, index + Math.Max(txt.Length, 1), StringComparison.CurrentCultureIgnoreCase);
            }
        }

        LastSearchResults = results.ToArray();
        return LastSearchResults.Length;
    }

    public SearchResult? FindNext()
    {
        if (LastSearchResults.Length == 0)
        {
            return null;
        }

        CurrentSearchResult++;
        if (CurrentSearchResult >= LastSearchResults.Length)
        {
            CurrentSearchResult = 0;
        }

        return LastSearchResults[CurrentSearchResult];
    }

    public SearchResult? FindPrevious()
    {
        if (LastSearchResults.Length == 0)
        {
            return null;
        }

        CurrentSearchResult--;
        if (CurrentSearchResult < 0)
        {
            CurrentSearchResult = LastSearchResults.Length - 1;
        }

        return LastSearchResults[CurrentSearchResult];
    }
}
