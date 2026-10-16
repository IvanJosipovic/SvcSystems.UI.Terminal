using System.Diagnostics.CodeAnalysis;
using XTerm.Common;
using XTerm.Selection;
using EngineTerminal = XTerm.Terminal;
using EngineTerminalOptions = XTerm.Options.TerminalOptions;

namespace SvcSystems.UI.Terminal;

[SuppressMessage("Naming", "CA1724:Type names should not conflict with namespace names", Justification = "Public API keeps the Terminal type in the terminal namespace.")]
public sealed class Terminal : IDisposable
{
    private readonly EngineTerminal _terminal;
    private readonly TerminalOptions _options;
    private bool _disposed;

    public Terminal(TerminalOptions? options = null)
    {
        options ??= new TerminalOptions();

        _options = options;

        EngineTerminalOptions engineOptions = new()
        {
            Cols = Math.Max(options.Cols, 2),
            Rows = Math.Max(options.Rows, 1),
            Scrollback = Math.Max(options.Scrollback, 0),
            TabStopWidth = Math.Max(options.TabStopWidth, 1),
            CellWidthPixels = Math.Max(options.CellWidthPixels, 1),
            CellHeightPixels = Math.Max(options.CellHeightPixels, 1),
            ConvertEol = options.ConvertEol,
            TermName = options.TermName,
            SixelEnabled = options.SixelEnabled,
            KittyGraphicsEnabled = options.KittyGraphicsEnabled,
            ITerm2ImagesEnabled = options.ITerm2ImagesEnabled,
            KittyNotificationsEnabled = options.KittyNotificationsEnabled,
            PointerShapesEnabled = options.PointerShapesEnabled,
            KittyKeyboardEnabled = options.KittyKeyboardEnabled,
            ClipboardReadEnabled = options.ClipboardReadEnabled,
            ClipboardWriteEnabled = options.ClipboardWriteEnabled,
            WindowOptions = new()
            {
                GetWinSizePixels = true,
                GetWinSizeChars = true,
                GetCellSizePixels = true,
            },
        };

        _terminal = new EngineTerminal(engineOptions);
        _terminal.TitleChanged += OnTitleChanged;
        _terminal.WindowInfoRequested += OnWindowInfoRequested;
    }

    public event EventHandler<TitleChangedEventArgs>? TitleChanged;

    public EngineTerminal Engine => _terminal;

    public XTerm.Buffer.TerminalBuffer Buffer => _terminal.Buffer;

    public SelectionManager Selection => _terminal.Selection;

    public bool IsAlternateBufferActive => _terminal.IsAlternateBufferActive;

    public TerminalOptions Options => new()
    {
        Cols = _terminal.Cols,
        Rows = _terminal.Rows,
        Scrollback = _options.Scrollback,
        TabStopWidth = _options.TabStopWidth,
        CellWidthPixels = _terminal.Options.CellWidthPixels,
        CellHeightPixels = _terminal.Options.CellHeightPixels,
        TermName = _options.TermName,
        ConvertEol = _options.ConvertEol,
        ReflowOnResize = _options.ReflowOnResize,
        SixelEnabled = _options.SixelEnabled,
        KittyGraphicsEnabled = _options.KittyGraphicsEnabled,
        ITerm2ImagesEnabled = _options.ITerm2ImagesEnabled,
        KittyNotificationsEnabled = _options.KittyNotificationsEnabled,
        PointerShapesEnabled = _options.PointerShapesEnabled,
        KittyKeyboardEnabled = _options.KittyKeyboardEnabled,
        ClipboardReadEnabled = _options.ClipboardReadEnabled,
        ClipboardWriteEnabled = _options.ClipboardWriteEnabled,
    };

    public int Cols => _terminal.Cols;

    public int Rows => _terminal.Rows;

    public string Title => _terminal.Title;

    public void Feed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _terminal.Write(text);
    }

    public void Feed(byte[] data, int len = -1)
    {
        ArgumentNullException.ThrowIfNull(data);

        int actualLength = len < 0 ? data.Length : Math.Min(len, data.Length);
        if (actualLength <= 0)
        {
            return;
        }

        _terminal.Write(data.AsSpan(0, actualLength));
    }

    public void Resize(int cols, int rows)
    {
        _terminal.Resize(Math.Max(cols, 1), Math.Max(rows, 1));
    }

    internal void SetCellSizePixels(int width, int height)
    {
        width = Math.Max(width, 1);
        height = Math.Max(height, 1);
        _options.CellWidthPixels = width;
        _options.CellHeightPixels = height;
        _terminal.Options.CellWidthPixels = width;
        _terminal.Options.CellHeightPixels = height;
        _terminal.NotifyTextAreaPixelsChanged();
    }

    public void SwitchToAltBuffer()
    {
        _terminal.SwitchToAltBuffer();
    }

    public void SwitchToNormalBuffer()
    {
        _terminal.SwitchToNormalBuffer();
    }

    private void OnTitleChanged(object? sender, XTerm.Events.TerminalEvents.TitleChangeEventArgs e)
    {
        TitleChanged?.Invoke(this, new TitleChangedEventArgs(e.Title));
    }

    private void OnWindowInfoRequested(object? sender, XTerm.Events.TerminalEvents.WindowInfoRequestedEventArgs e)
    {
        switch (e.Request)
        {
            case WindowInfoRequest.CellSizePixels:
                e.CellWidth = _terminal.Options.CellWidthPixels;
                e.CellHeight = _terminal.Options.CellHeightPixels;
                e.Handled = e.CellWidth > 0 && e.CellHeight > 0;
                break;

            case WindowInfoRequest.SizePixels:
                long width = (long)_terminal.Cols * _terminal.Options.CellWidthPixels;
                long height = (long)_terminal.Rows * _terminal.Options.CellHeightPixels;
                if (width is > 0 and <= int.MaxValue && height is > 0 and <= int.MaxValue)
                {
                    e.WidthPixels = (int)width;
                    e.HeightPixels = (int)height;
                    e.Handled = true;
                }

                break;
        }
    }

    /// <summary>
    /// Releases the underlying XTerm.NET terminal. Further calls are ignored.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _terminal.TitleChanged -= OnTitleChanged;
        _terminal.WindowInfoRequested -= OnWindowInfoRequested;
        _terminal.Dispose();
    }
}

/// <summary>
/// Provides the updated terminal title.
/// </summary>
public sealed class TitleChangedEventArgs(string title) : EventArgs
{
    /// <summary>
    /// Gets the new terminal title.
    /// </summary>
    public string Title { get; } = title;
}
