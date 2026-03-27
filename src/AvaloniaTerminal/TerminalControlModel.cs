using Avalonia;
using Avalonia.Collections;
using Avalonia.Media;
using PropertyGenerator.Avalonia;
using System.Text;
using XTerm.Buffer;
using XTerm.Selection;

namespace AvaloniaTerminal;

public partial class TerminalControlModel : AvaloniaObject
{
    public TerminalControlModel(TerminalOptions? options = null)
    {
        // get the dimensions of terminal (cols and rows)
        Terminal = new Terminal(options);
        SearchService = new SearchService(Terminal);
        Terminal.TitleChanged += SetTerminalTitle;
        Terminal.Selection.SelectionChanged += HandleSelectionChanged;

        // trigger an update of the buffers
        FullBufferUpdate();
        HandleSelectionChanged();
        UpdateDisplay();
    }

    [GeneratedDirectProperty]
    public partial Terminal Terminal { get; set; }

    [GeneratedDirectProperty]
    public partial SearchService SearchService { get; set; }

    [GeneratedDirectProperty]
    public partial string Title { get; set; }

    [GeneratedDirectProperty]
    public partial Dictionary<(int x, int y), TextObject> ConsoleText { get; set; } = [];

    [GeneratedDirectProperty]
    public partial string SelectedText { get; set; } = string.Empty;

    [GeneratedDirectProperty]
    public partial bool HasSelection { get; set; }

    [GeneratedDirectProperty]
    public partial string LastSearchText { get; set; } = string.Empty;

    [GeneratedDirectProperty]
    public partial int SearchResultCount { get; set; }

    [GeneratedDirectProperty]
    public partial int CurrentSearchResultIndex { get; set; } = -1;

    /// <summary>
    /// Gets or sets a value indicating whether this <see cref="T:AvaloniaTerminal.TerminalControl"/> treats the "Alt/Option" key on the mac keyboard as a meta key,
    /// which has the effect of sending ESC+letter when Meta-letter is pressed.   Otherwise, it passes the keystroke that MacOS provides from the OS keyboard.
    /// </summary>
    /// <value><c>true</c> if option acts as a meta key; otherwise, <c>false</c>.</value>
    public bool OptionAsMetaKey { get; set; } = true;

    /// <summary>
    /// Gets a value indicating the relative position of the terminal scroller
    /// </summary>
    public double ScrollPosition
    {
        get
        {
            if (Terminal.IsAlternateBufferActive)
                return 0;

            // strictly speaking these ought not to be outside these bounds
            if (Terminal.Buffer.YDisp <= 0)
                return 0;

            var maxScrollback = Terminal.Buffer.YBase;
            if (maxScrollback <= 0)
                return 0;

            if (Terminal.Buffer.YDisp >= maxScrollback)
                return 1;

            return (double)Terminal.Buffer.YDisp / (double)maxScrollback;
        }
    }

    /// <summary>
    /// Gets a value indicating the scroll thumbsize
    /// </summary>
    public float ScrollThumbsize
    {
        get
        {
            if (Terminal.IsAlternateBufferActive)
                return 0;

            // the thumb size is the proportion of the visible content of the
            // entire content but don't make it too small
            return Math.Max((float)Terminal.Rows / (float)Terminal.Buffer.Lines.Length, 0.01f);
        }
    }

    /// <summary>
    /// Gets a value indicating whether or not the user can scroll the terminal contents
    /// </summary>
    public bool CanScroll
    {
        get
        {
            var shouldBeEnabled = !Terminal.IsAlternateBufferActive;
            shouldBeEnabled = shouldBeEnabled && Terminal.Buffer.YBase > 0;
            shouldBeEnabled = shouldBeEnabled && Terminal.Buffer.Lines.Length > Terminal.Rows;
            return shouldBeEnabled;
        }
    }

    /// <summary>
    /// Gets the current scrollback offset.
    /// </summary>
    public int ScrollOffset => Terminal.IsAlternateBufferActive ? 0 : Terminal.Buffer.YDisp;

    /// <summary>
    /// Gets the maximum scrollback offset.
    /// </summary>
    public int MaxScrollback => Math.Max(Terminal.Buffer.YBase, 0);

    /// <summary>
    /// Gets the caret column within the viewport.
    /// </summary>
    public int CaretColumn => Math.Clamp(Terminal.Buffer.X, 0, Math.Max(Terminal.Cols - 1, 0));

    /// <summary>
    /// Gets the caret row within the viewport.
    /// </summary>
    public int CaretRow
    {
        get
        {
            var row = Terminal.Buffer.Y - Terminal.Buffer.YDisp;
            return Math.Clamp(row, 0, Math.Max(Terminal.Rows - 1, 0));
        }
    }

    /// <summary>
    /// Gets a value indicating whether the caret is visible in the current viewport.
    /// </summary>
    public bool IsCaretVisible => !Terminal.CursorHidden && Terminal.Buffer.IsAtBottom;

    /// <summary>
    /// Gets a value indicating whether terminal mouse reporting is active.
    /// </summary>
    public bool IsMouseModeActive => Terminal.MouseMode != MouseMode.Off;

    /// <summary>
    ///  This event is raised when the terminal size (cols and rows, width, height) has change, due to a NSView frame changed.
    /// </summary>
    public event Action<int, int, double, double>? SizeChanged;

    /// <summary>
    /// Invoked to raise input on the control, which should probably be sent to the actual child process or remote connection
    /// </summary>
    public event Action<byte[]>? UserInput;

    public Action? UpdateUI;

    private void SetTerminalTitle(string title)
    {
        Title = title;
    }

    public void Send(string text)
    {
        Send(Encoding.UTF8.GetBytes(text));
    }

    public void Send(byte[] data)
    {
        EnsureCaretIsVisible();
        UserInput?.Invoke(data);
    }

    public void Resize(double width, double height, double textWidth, double textHeight)
    {
        if (width <= 0 || height <= 0 || textWidth <= 0 || textHeight <= 0)
        {
            return;
        }

        var cols = Math.Max((int)(width / textWidth), 1);
        var rows = Math.Max((int)(height / textHeight), 1);

        Terminal?.Resize(cols, rows);
        SearchService?.Invalidate();
        RemoveItemsDictionary();
        UpdateDisplay();

        SizeChanged?.Invoke(cols, rows, width, height);
    }

    /// <summary>
    /// Removes Items which are outside bounds Cols/Rows
    /// </summary>
    private void RemoveItemsDictionary()
    {
        var itemsToRemove = ConsoleText.Keys
            .Where(key => key.x >= Terminal.Cols || key.y >= Terminal.Rows)
            .ToList();

        foreach (var item in itemsToRemove)
        {
            ConsoleText.Remove(item);
        }
    }

    public void FullBufferUpdate()
    {
        RebuildViewport();
    }

    public void UpdateDisplay()
    {
        RebuildViewport();

        //UpdateCursorPosition();
        //UpdateScroller();
        UpdateUI?.Invoke();
    }

    public void Feed(string text)
    {
        Feed(Encoding.UTF8.GetBytes(text));
    }

    public void Feed(byte[] text, int length = -1)
    {
        SearchService?.Invalidate();
        var wasAtBottom = Terminal.Buffer.IsAtBottom;
        Terminal?.Feed(text, length);
        UpdateDisplay();

        if (wasAtBottom)
        {
            EnsureCaretIsVisible();
        }
    }

    /// <summary>
    /// Scrolls the terminal contents up by the given number of lines, up is negative and down is positive.
    /// </summary>
    public void ScrollLines(int lines)
    {
        Terminal.ScrollLines(lines);
        UpdateDisplay();
    }

    /// <summary>
    /// Scrolls the terminal contents so that the given row is at the top of the viewport.
    /// </summary>
    public void ScrollToYDisp(int ydisp)
    {
        ydisp = Math.Clamp(ydisp, 0, MaxScrollback);
        var linesToScroll = ydisp - Terminal.Buffer.YDisp;
        if (linesToScroll == 0)
        {
            return;
        }

        ScrollLines(linesToScroll);
    }

    /// <summary>
    /// Scrolls the terminal contents to the relative position in the buffer.
    /// </summary>
    public void ScrollToPosition(double position)
    {
        var newScrollPosition = (int)(MaxScrollback * position);
        ScrollToYDisp(newScrollPosition);
    }

    /// <summary>
    /// Scrolls the viewport so the live caret is visible.
    /// </summary>
    public void EnsureCaretIsVisible()
    {
        ScrollToYDisp(MaxScrollback);
    }

    /// <summary>
    /// Starts a selection drag from the given viewport row and column.
    /// </summary>
    public void StartSelection(int row, int col)
    {
        Terminal.Selection.StartSelection(col, row);
        HandleSelectionChanged();
    }

    /// <summary>
    /// Starts a selection drag from the previously stored soft selection start.
    /// </summary>
    public void StartSelectionFromSoftStart()
    {
        if (_softSelectionStart.HasValue)
        {
            Terminal.Selection.StartSelection(_softSelectionStart.Value.X, _softSelectionStart.Value.Y);
            HandleSelectionChanged();
        }
    }

    /// <summary>
    /// Records a soft selection start without activating selection.
    /// </summary>
    public void SetSoftSelectionStart(int row, int col)
    {
        _softSelectionStart = new BufferPoint(col, row);
        HandleSelectionChanged();
    }

    /// <summary>
    /// Extends the selection to the given viewport row and column.
    /// </summary>
    public void DragExtendSelection(int row, int col)
    {
        Terminal.Selection.UpdateSelection(NormalizeSelectionEnd(col), row);
        HandleSelectionChanged();
    }

    /// <summary>
    /// Extends the selection using shift-click semantics.
    /// </summary>
    public void ShiftExtendSelection(int row, int col)
    {
        if (!Terminal.Selection.HasSelection && _softSelectionStart.HasValue)
        {
            Terminal.Selection.StartSelection(_softSelectionStart.Value.X, _softSelectionStart.Value.Y);
        }
        else if (!Terminal.Selection.HasSelection)
        {
            Terminal.Selection.StartSelection(col, row);
        }
        else
        {
            Terminal.Selection.UpdateSelection(NormalizeSelectionEnd(col), row);
        }

        HandleSelectionChanged();
    }

    /// <summary>
    /// Selects the word or expression at the given viewport row and column.
    /// </summary>
    public void SelectWordOrExpression(int row, int col)
    {
        Terminal.Selection.StartSelection(col, row, SelectionMode.Word);
        HandleSelectionChanged();
    }

    /// <summary>
    /// Selects the full row at the given viewport row.
    /// </summary>
    public void SelectRow(int row)
    {
        Terminal.Selection.StartSelection(0, row, SelectionMode.Line);
        HandleSelectionChanged();
    }

    /// <summary>
    /// Selects the entire buffer.
    /// </summary>
    public void SelectAll()
    {
        Terminal.Selection.SelectAll();
        HandleSelectionChanged();
    }

    /// <summary>
    /// Clears the current selection.
    /// </summary>
    public void ClearSelection()
    {
        Terminal.Selection.ClearSelection();
        HandleSelectionChanged();
    }

    /// <summary>
    /// Searches the buffer, selects the first result, and returns the total number of matches.
    /// </summary>
    public int Search(string text)
    {
        var snapshot = SearchService.GetSnapshot();
        var result = snapshot.FindText(text);

        LastSearchText = text;
        SearchResultCount = result;
        CurrentSearchResultIndex = -1;

        if (result > 0)
        {
            SelectSearchResult(snapshot.FindNext(), snapshot);
        }
        else
        {
            ClearSelection();
        }

        return result;
    }

    /// <summary>
    /// Selects the next search result and returns its index, or -1 when no search results exist.
    /// </summary>
    public int SelectNextSearchResult()
    {
        var snapshot = SearchService.GetSnapshot();
        if (snapshot.LastSearchResults.Length == 0)
        {
            return -1;
        }

        SelectSearchResult(snapshot.FindNext(), snapshot);
        return CurrentSearchResultIndex;
    }

    /// <summary>
    /// Selects the previous search result and returns its index, or -1 when no search results exist.
    /// </summary>
    public int SelectPreviousSearchResult()
    {
        var snapshot = SearchService.GetSnapshot();
        if (snapshot.LastSearchResults.Length == 0)
        {
            return -1;
        }

        SelectSearchResult(snapshot.FindPrevious(), snapshot);
        return CurrentSearchResultIndex;
    }

    /// <summary>
    /// Scrolls the terminal contents up by one page.
    /// </summary>
    public void PageUp()
    {
        ScrollLines(Terminal.Rows * -1);
    }

    /// <summary>
    /// Scrolls the terminal contents down by one page.
    /// </summary>
    public void PageDown()
    {
        ScrollLines(Terminal.Rows);
    }

    /// <summary>
    /// Converts a pointer wheel delta into terminal scroll lines.
    /// </summary>
    public void HandlePointerWheel(Vector delta)
    {
        if (delta.Y == 0)
        {
            return;
        }

        var velocity = CalculateScrollVelocity(Math.Abs(delta.Y));
        ScrollLines(delta.Y > 0 ? velocity * -1 : velocity);
    }

    private int CalculateScrollVelocity(double delta)
    {
        if (delta > 9)
        {
            return Math.Max(Terminal.Rows, 20);
        }

        if (delta > 5)
        {
            return 10;
        }

        if (delta > 1)
        {
            return 3;
        }

        return 1;
    }

    private void HandleSelectionChanged()
    {
        var selectedText = Terminal.Selection.HasSelection ? Terminal.Selection.GetSelectionText() : string.Empty;
        SelectedText = selectedText;
        HasSelection = Terminal.Selection.HasSelection && !string.IsNullOrEmpty(selectedText);
        UpdateUI?.Invoke();
    }

    private void SelectSearchResult(SearchSnapshot.SearchResult? searchResult, SearchSnapshot snapshot)
    {
        ClearSelection();
        if (searchResult == null)
        {
            CurrentSearchResultIndex = -1;
            return;
        }

        Terminal.Selection.StartSelection(searchResult.Start.X, searchResult.Start.Y - Terminal.Buffer.YDisp);
        Terminal.Selection.UpdateSelection(NormalizeSelectionEnd(searchResult.End.X), searchResult.End.Y - Terminal.Buffer.YDisp);
        Terminal.Selection.EndSelection();
        HandleSelectionChanged();

        CurrentSearchResultIndex = snapshot.CurrentSearchResult;

        if ((searchResult.Start.Y < Terminal.Buffer.YDisp) || (searchResult.Start.Y >= Terminal.Buffer.YDisp + Terminal.Rows))
        {
            var newYDisp = Math.Max(searchResult.Start.Y - (Terminal.Rows / 2), 0);
            ScrollToYDisp(newYDisp);
        }
        else
        {
            UpdateUI?.Invoke();
        }
    }

    private static int NormalizeSelectionEnd(int col)
    {
        return Math.Max(col - 1, 0);
    }

    private void RebuildViewport()
    {
        var buffer = Terminal.Buffer;
        var viewportRows = Math.Max(Terminal.Rows, 0);
        var viewportCols = Math.Max(Terminal.Cols, 0);
        var visibleStart = Math.Clamp(buffer.YDisp, 0, Math.Max(buffer.Lines.Length - 1, 0));

        for (var row = 0; row < viewportRows; row++)
        {
            var bufferLine = visibleStart + row;
            BufferLine? line = bufferLine < buffer.Lines.Length ? buffer.GetLine(bufferLine) : null;

            for (var cell = 0; cell < viewportCols; cell++)
            {
                BufferCell cd = line is null ? BufferCell.Space : line[cell];

                if (!ConsoleText.TryGetValue((cell, row), out TextObject? text) || text == null)
                {
                    text = new TextObject();
                    ConsoleText[(cell, row)] = text;
                }

                text = SetStyling(text, cd);
                text.Text = ConvertCharDataToText(cd);
            }
        }

        RemoveItemsDictionary();
    }

    private BufferPoint? _softSelectionStart;

    private static string ConvertCharDataToText(BufferCell cd)
    {
        if (cd.CodePoint == 0 || cd.Width <= 0)
        {
            return " ";
        }

        if (cd.CodePoint > 0xFFFF || cd.Content.Length > 1)
        {
            return "\uFFFD";
        }

        try
        {
            return cd.Content.Length > 0 ? cd.Content : char.ConvertFromUtf32(cd.CodePoint);
        }
        catch (ArgumentOutOfRangeException)
        {
            return "\uFFFD";
        }
    }

    private static TextObject SetStyling(TextObject control, BufferCell cd)
    {
        var attribute = cd.Attributes;

        int fg = attribute.GetFgColor();
        int bg = attribute.GetBgColor();
        bool inverse = attribute.IsInverse();

        if (inverse)
        {
            (fg, bg) = (bg, fg);
        }

        control.FontWeight = attribute.IsBold() ? FontWeight.Bold : FontWeight.Normal;
        control.FontStyle = attribute.IsItalic() ? FontStyle.Italic : FontStyle.Normal;

        if (attribute.IsUnderline())
        {
            control.TextDecorations = TextDecorations.Underline;
        }
        else if (control.TextDecorations is not null)
        {
            var dec = control.TextDecorations.FirstOrDefault(x => x.Location == TextDecorationLocation.Underline);
            if (dec != null)
            {
                control.TextDecorations.Remove(dec);
            }
        }

        if (attribute.IsStrikethrough())
        {
            control.TextDecorations = TextDecorations.Strikethrough;
        }
        else if (control.TextDecorations is not null)
        {
            var dec = control.TextDecorations.FirstOrDefault(x => x.Location == TextDecorationLocation.Strikethrough);
            if (dec != null)
            {
                control.TextDecorations.Remove(dec);
            }
        }

        control.Foreground = ResolveBrush(fg, isForeground: true);
        control.Background = ResolveBrush(bg, isForeground: false);

        return control;
    }

    private static IBrush ResolveBrush(int color, bool isForeground)
    {
        if (color == 256)
        {
            return TerminalControl.ConvertXtermColor(isForeground ? 15 : 0);
        }

        if (color == 257)
        {
            return TerminalControl.ConvertXtermColor(isForeground ? 15 : 0);
        }

        if (color is >= 0 and <= 255)
        {
            return TerminalControl.ConvertXtermColor(color);
        }

        var red = (byte)((color >> 16) & 0xFF);
        var green = (byte)((color >> 8) & 0xFF);
        var blue = (byte)(color & 0xFF);
        return new SolidColorBrush(Color.FromRgb(red, green, blue));
    }
}

public partial class TextObject : AvaloniaObject
{
    [GeneratedStyledProperty]
    public partial IBrush Foreground { get; set; }

    [GeneratedStyledProperty]
    public partial IBrush Background { get; set; }

    [GeneratedStyledProperty]
    public partial string Text { get; set; }

    [GeneratedStyledProperty]
    public partial FontWeight FontWeight { get; set; }

    [GeneratedStyledProperty]
    public partial FontStyle FontStyle { get; set; }

    [GeneratedStyledProperty]
    public partial TextDecorationCollection? TextDecorations { get; set; }
}
