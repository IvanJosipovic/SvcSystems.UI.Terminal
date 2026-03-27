using System.Text;
using XTerm.Input;
using XTerm.Selection;
using EngineTerminal = XTerm.Terminal;
using EngineTerminalOptions = XTerm.Options.TerminalOptions;

namespace AvaloniaTerminal;

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

    public event Action<string>? TitleChanged;

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

    public bool ApplicationCursor => _terminal.ApplicationCursorKeys;

    public bool ApplicationKeypad => _terminal.ApplicationKeypad;

    public bool CursorHidden => !_terminal.CursorVisible;

    public bool CursorVisible
    {
        get => _terminal.CursorVisible;
        set => _terminal.CursorVisible = value;
    }

    public MouseMode MouseMode => _terminal.MouseTrackingMode switch
    {
        MouseTrackingMode.X10 => MouseMode.X10,
        MouseTrackingMode.VT200 => MouseMode.VT200,
        MouseTrackingMode.ButtonEvent => MouseMode.ButtonEventTracking,
        MouseTrackingMode.AnyEvent => MouseMode.AnyEvent,
        _ => MouseMode.Off,
    };

    public string Title => _terminal.Title;

    public void Feed(string text)
    {
        _terminal.Write(text);
    }

    public void Feed(byte[] data, int len = -1)
    {
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

    public void ScrollLines(int lines)
    {
        _terminal.ScrollLines(lines);
    }

    public string GenerateKeyInput(Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        return _terminal.GenerateKeyInput(key, modifiers);
    }

    public string GenerateCharInput(char c, KeyModifiers modifiers = KeyModifiers.None)
    {
        return _terminal.GenerateCharInput(c, modifiers);
    }

    public string GenerateMouseEvent(MouseButton button, int x, int y, MouseEventType eventType, KeyModifiers modifiers = KeyModifiers.None)
    {
        return _terminal.GenerateMouseEvent(button, x, y, eventType, modifiers);
    }

    public string GenerateFocusEvent(bool focused)
    {
        return _terminal.GenerateFocusEvent(focused);
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
        TitleChanged?.Invoke(e.Title);
    }

}
