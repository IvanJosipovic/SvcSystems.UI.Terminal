using System.Text;
using System.Diagnostics.CodeAnalysis;
using XTerm.Selection;
using EngineTerminal = XTerm.Terminal;
using EngineTerminalOptions = XTerm.Options.TerminalOptions;

namespace SvcSystems.UI.Terminal;

[SuppressMessage("Naming", "CA1724:Type names should not conflict with namespace names", Justification = "Public API keeps the Terminal type in the terminal namespace.")]
public sealed class Terminal
{
    private readonly EngineTerminal _terminal;
    private readonly TerminalOptions _options;

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
            ConvertEol = options.ConvertEol,
            TermName = options.TermName,
        };

        _terminal = new EngineTerminal(engineOptions);

        _terminal.TitleChanged += OnTitleChanged;
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
        TermName = _options.TermName,
        ConvertEol = _options.ConvertEol,
        ReflowOnResize = _options.ReflowOnResize,
    };

    public int Cols => _terminal.Cols;

    public int Rows => _terminal.Rows;

    public string Title => _terminal.Title;

    public void Feed(string text)
    {
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

        _terminal.Write(Encoding.UTF8.GetString(data, 0, actualLength));
    }

    public void Resize(int cols, int rows)
    {
        _terminal.Resize(Math.Max(cols, 1), Math.Max(rows, 1));
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
