namespace AvaloniaTerminal;

/// <summary>
/// Configuration options for the terminal engine wrapper.
/// </summary>
public sealed class TerminalOptions
{
    public int Cols { get; set; } = 80;

    public int Rows { get; set; } = 24;

    public int Scrollback { get; set; } = 1000;

    public int TabStopWidth { get; set; } = 8;

    public string TermName { get; set; } = "xterm";

    public bool ConvertEol { get; set; }

    public bool ReflowOnResize { get; set; } = true;
}
