namespace SvcSystems.UI.Terminal;

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

    /// <summary>
    /// Gets or sets a value indicating whether the terminal answers the kitty keyboard protocol
    /// query and honours the flags an application sets. When <c>false</c> the terminal stays
    /// silent on the query, so applications keep to the legacy key encodings.
    /// </summary>
    public bool KittyKeyboardEnabled { get; set; } = true;
}
