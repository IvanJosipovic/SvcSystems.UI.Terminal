using Avalonia;
using Avalonia.Collections;
using Avalonia.Media;
using PropertyGenerator.Avalonia;
using System.Text;
using XtermSharp;

namespace AvaloniaTerminal;

public partial class TerminalControlModel : AvaloniaObject, ITerminalDelegate
{
    public TerminalControlModel()
    {
        // get the dimensions of terminal (cols and rows)
        Terminal = new Terminal(this);
        SelectionService = new SelectionService(Terminal);
        SearchService = new SearchService(Terminal);
        SelectionService.SelectionChanged += HandleSelectionChanged;

        // trigger an update of the buffers
        FullBufferUpdate();
        HandleSelectionChanged();
        UpdateDisplay();
    }

    [GeneratedDirectProperty]
    public partial Terminal Terminal { get; set; }

    [GeneratedDirectProperty]
    public partial SelectionService SelectionService { get; set; }

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
            if (Terminal.Buffers.IsAlternateBuffer)
                return 0;

            // strictly speaking these ought not to be outside these bounds
            if (Terminal.Buffer.YDisp <= 0)
                return 0;

            var maxScrollback = Terminal.Buffer.Lines.Length - Terminal.Rows;
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
            if (Terminal.Buffers.IsAlternateBuffer)
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
            var shouldBeEnabled = !Terminal.Buffers.IsAlternateBuffer;
            shouldBeEnabled = shouldBeEnabled && Terminal.Buffer.HasScrollback;
            shouldBeEnabled = shouldBeEnabled && Terminal.Buffer.Lines.Length > Terminal.Rows;
            return shouldBeEnabled;
        }
    }

    /// <summary>
    /// Gets the current scrollback offset.
    /// </summary>
    public int ScrollOffset => Terminal.Buffers.IsAlternateBuffer ? 0 : Terminal.Buffer.YDisp;

    /// <summary>
    /// Gets the maximum scrollback offset.
    /// </summary>
    public int MaxScrollback => Math.Max(Terminal.Buffer.Lines.Length - Terminal.Rows, 0);

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
            var row = Terminal.Buffer.Y - Terminal.Buffer.YDisp + Terminal.Buffer.YBase;
            return Math.Clamp(row, 0, Math.Max(Terminal.Rows - 1, 0));
        }
    }

    /// <summary>
    /// Gets a value indicating whether the caret is visible in the current viewport.
    /// </summary>
    public bool IsCaretVisible => !Terminal.CursorHidden && Terminal.Buffer.IsCursorInViewport;

    /// <summary>
    ///  This event is raised when the terminal size (cols and rows, width, height) has change, due to a NSView frame changed.
    /// </summary>
    public event Action<int, int, double, double>? SizeChanged;

    /// <summary>
    /// Invoked to raise input on the control, which should probably be sent to the actual child process or remote connection
    /// </summary>
    public event Action<byte[]>? UserInput;

    public Action? UpdateUI;

    public void ShowCursor(Terminal source)
    {
        UpdateUI?.Invoke();
    }

    public void SetTerminalTitle(Terminal source, string title)
    {
        Title = title;
    }

    public void SetTerminalIconTitle(Terminal source, string title)
    {
    }

    void ITerminalDelegate.SizeChanged(Terminal source)
    {
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

    public string? WindowCommand(Terminal source, WindowManipulationCommand command, params int[] args)
    {
        return null;
    }

    public bool IsProcessTrusted()
    {
        return true;
    }

    public void Resize(double width, double height, double textWidth, double textHeight)
    {
        if (width == 0 || height == 0)
        {
            width = 640;
            height = 480;
        }

        var cols = (int)(width / textWidth);
        var rows = (int)(height / textHeight);


        Terminal?.Resize(cols, rows);
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
        Terminal.GetUpdateRange(out _, out _);
        Terminal.ClearUpdateRange();

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
        Terminal?.Feed(text, length);
        UpdateDisplay();
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
        ScrollToYDisp(Terminal.Buffer.YBase);
    }

    /// <summary>
    /// Starts a selection drag from the given viewport row and column.
    /// </summary>
    public void StartSelection(int row, int col)
    {
        SelectionService.StartSelection(row, col);
    }

    /// <summary>
    /// Starts a selection drag from the previously stored soft selection start.
    /// </summary>
    public void StartSelectionFromSoftStart()
    {
        SelectionService.StartSelection();
    }

    /// <summary>
    /// Records a soft selection start without activating selection.
    /// </summary>
    public void SetSoftSelectionStart(int row, int col)
    {
        SelectionService.SetSoftStart(row, col);
        HandleSelectionChanged();
    }

    /// <summary>
    /// Extends the selection to the given viewport row and column.
    /// </summary>
    public void DragExtendSelection(int row, int col)
    {
        SelectionService.DragExtend(row, col);
    }

    /// <summary>
    /// Extends the selection using shift-click semantics.
    /// </summary>
    public void ShiftExtendSelection(int row, int col)
    {
        SelectionService.ShiftExtend(row, col);
    }

    /// <summary>
    /// Selects the word or expression at the given viewport row and column.
    /// </summary>
    public void SelectWordOrExpression(int row, int col)
    {
        SelectionService.SelectWordOrExpression(col, row);
    }

    /// <summary>
    /// Selects the full row at the given viewport row.
    /// </summary>
    public void SelectRow(int row)
    {
        SelectionService.SelectRow(row);
    }

    /// <summary>
    /// Selects the entire buffer.
    /// </summary>
    public void SelectAll()
    {
        SelectionService.SelectAll();
    }

    /// <summary>
    /// Clears the current selection.
    /// </summary>
    public void ClearSelection()
    {
        SelectionService.SelectNone();
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
        var selectedText = SelectionService.Active ? SelectionService.GetSelectedText() : string.Empty;
        SelectedText = selectedText;
        HasSelection = SelectionService.Active && !string.IsNullOrEmpty(selectedText);
        UpdateUI?.Invoke();
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

            for (var cell = 0; cell < viewportCols; cell++)
            {
                var cd = bufferLine < buffer.Lines.Length
                    ? buffer.GetChar(cell, bufferLine)
                    : CharData.WhiteSpace;

                if (!ConsoleText.TryGetValue((cell, row), out TextObject? text) || text == null)
                {
                    text = new TextObject();
                    ConsoleText[(cell, row)] = text;
                }

                text = SetStyling(text, cd);
                text.Text = cd.Code == 0 ? " " : ((char)cd.Rune).ToString();
            }
        }

        RemoveItemsDictionary();
    }

    private static TextObject SetStyling(TextObject control, CharData cd)
    {
        var attribute = cd.Attribute;

        // ((int)flags << 18) | (fg << 9) | bg;
        int bg = attribute & 0x1ff;
        int fg = (attribute >> 9) & 0x1ff;
        var flags = (FLAGS)(attribute >> 18);

        if (flags.HasFlag(FLAGS.INVERSE))
        {
            var tmp = bg;
            bg = fg;
            fg = tmp;

            if (fg == Renderer.DefaultColor)
                fg = Renderer.InvertedDefaultColor;
            if (bg == Renderer.DefaultColor)
                bg = Renderer.InvertedDefaultColor;
        }

        if (flags.HasFlag(FLAGS.BOLD))
        {
            control.FontWeight = FontWeight.Bold;
        }
        else
        {
            control.FontWeight = FontWeight.Normal;
        }

        if (flags.HasFlag(FLAGS.ITALIC))
        {
            control.FontStyle = FontStyle.Italic;
        }
        else
        {
            control.FontStyle = FontStyle.Normal;
        }

        if (flags.HasFlag(FLAGS.UNDERLINE))
        {
            control.TextDecorations = TextDecorations.Underline;
        }
        else
        {
            var dec = control.TextDecorations?.FirstOrDefault(x => x.Location == TextDecorationLocation.Underline);
            if (dec != null)
            {
                control.TextDecorations.Remove(dec);
            }
        }

        if (flags.HasFlag(FLAGS.CrossedOut))
        {
            control.TextDecorations = TextDecorations.Strikethrough;
        }
        else
        {
            var dec = control.TextDecorations?.FirstOrDefault(x => x.Location == TextDecorationLocation.Strikethrough);
            if (dec != null)
            {
                control.TextDecorations.Remove(dec);
            }
        }

        if (fg <= 255)
        {
            control.Foreground = TerminalControl.ConvertXtermColor(fg);
        }
        else if (fg == 256) // DefaultColor
        {
            control.Foreground = TerminalControl.ConvertXtermColor(15);
        }
        else if (fg == 257) // InvertedDefaultColor
        {
            control.Foreground = TerminalControl.ConvertXtermColor(0);
        }

        if (bg <= 255)
        {
            control.Background = TerminalControl.ConvertXtermColor(bg);
        }
        else if (bg == 256) // DefaultColor
        {
            control.Background = TerminalControl.ConvertXtermColor(0);
        }
        else if (bg == 257) // InvertedDefaultColor
        {
            control.Background = TerminalControl.ConvertXtermColor(15);
        }

        return control;
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
