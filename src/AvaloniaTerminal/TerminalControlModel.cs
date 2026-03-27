using Avalonia;
using Avalonia.Media;
using PropertyGenerator.Avalonia;
using System.Text;
using XTerm.Buffer;
using XTerm.Input;
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

    internal IReadOnlyList<ViewportRow> ViewportRows { get; private set; } = [];

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
    public bool IsCaretVisible => Terminal.Engine.CursorVisible && Terminal.Buffer.IsAtBottom;

    /// <summary>
    /// Gets a value indicating whether terminal mouse reporting is active.
    /// </summary>
    public bool IsMouseModeActive => Terminal.Engine.MouseTrackingMode != MouseTrackingMode.None;

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
        UpdateDisplay();

        SizeChanged?.Invoke(cols, rows, width, height);
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
        Terminal.Engine.ScrollLines(lines);
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
        List<ViewportRow> rows = new(viewportRows);

        for (var row = 0; row < viewportRows; row++)
        {
            var bufferLine = visibleStart + row;
            BufferLine? line = bufferLine < buffer.Lines.Length ? buffer.GetLine(bufferLine) : null;
            rows.Add(new ViewportRow(row, BuildRowRuns(line, viewportCols)));
        }

        ViewportRows = rows;
    }

    private BufferPoint? _softSelectionStart;

    private static IReadOnlyList<ViewportTextRun> BuildRowRuns(BufferLine? line, int viewportCols)
    {
        List<ViewportTextRun> runs = [];

        for (var cell = 0; cell < viewportCols;)
        {
            BufferCell bufferCell = line is null ? BufferCell.Space : line[cell];
            if (bufferCell.Width == 0)
            {
                cell++;
                continue;
            }

            var styleKey = CreateStyleKey(bufferCell);
            var startColumn = cell;
            var cellWidth = bufferCell.Width <= 0 ? 1 : bufferCell.Width;
            StringBuilder text = new();
            text.Append(ConvertCellToRenderableText(bufferCell));
            cell += cellWidth;

            if (bufferCell.Width == 1)
            {
                while (cell < viewportCols)
                {
                    BufferCell nextCell = line is null ? BufferCell.Space : line[cell];
                    if (nextCell.Width != 1 || CreateStyleKey(nextCell) != styleKey)
                    {
                        break;
                    }

                    text.Append(ConvertCellToRenderableText(nextCell));
                    cell++;
                    cellWidth++;
                }
            }

            runs.Add(new ViewportTextRun(
                startColumn,
                cellWidth,
                text.ToString(),
                ResolveBrush(styleKey.ForegroundColor, isForeground: true),
                ResolveBrush(styleKey.BackgroundColor, isForeground: false),
                styleKey.Bold ? FontWeight.Bold : FontWeight.Normal,
                styleKey.Italic ? FontStyle.Italic : FontStyle.Normal,
                CreateTextDecorations(styleKey)));
        }

        return runs;
    }

    private static string ConvertCellToRenderableText(BufferCell cell)
    {
        if (cell.CodePoint == 0 || cell.Width <= 0)
        {
            return " ";
        }

        if (cell.CodePoint > 0xFFFF || cell.Content.Length > 1)
        {
            return "\uFFFD";
        }

        try
        {
            return cell.Content.Length > 0 ? cell.Content : char.ConvertFromUtf32(cell.CodePoint);
        }
        catch (ArgumentOutOfRangeException)
        {
            return "\uFFFD";
        }
    }

    private static ViewportStyleKey CreateStyleKey(BufferCell cell)
    {
        var attribute = cell.Attributes;

        int fg = attribute.GetFgColor();
        int bg = attribute.GetBgColor();

        if (attribute.IsInverse())
        {
            (fg, bg) = (bg, fg);
        }

        return new ViewportStyleKey(
            fg,
            bg,
            attribute.IsBold(),
            attribute.IsItalic(),
            attribute.IsUnderline(),
            attribute.IsStrikethrough());
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

    private static TextDecorationCollection? CreateTextDecorations(ViewportStyleKey styleKey)
    {
        TextDecorationCollection? decorations = null;
        if (styleKey.Underline)
        {
            decorations = new TextDecorationCollection(TextDecorations.Underline);
        }

        if (styleKey.Strikethrough)
        {
            decorations ??= [];
            decorations.Add(new TextDecoration
            {
                Location = TextDecorationLocation.Strikethrough,
            });
        }

        return decorations;
    }
}

internal readonly record struct ViewportRow(int RowIndex, IReadOnlyList<ViewportTextRun> Runs);

internal readonly record struct ViewportTextRun(
    int StartColumn,
    int CellWidth,
    string Text,
    IBrush Foreground,
    IBrush Background,
    FontWeight FontWeight,
    FontStyle FontStyle,
    TextDecorationCollection? TextDecorations);

internal readonly record struct ViewportStyleKey(
    int ForegroundColor,
    int BackgroundColor,
    bool Bold,
    bool Italic,
    bool Underline,
    bool Strikethrough);
