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

    /// <summary>Gets or sets the terminal cell width in device pixels used to lay out graphics.</summary>
    public int CellWidthPixels { get; set; } = 10;

    /// <summary>Gets or sets the terminal cell height in device pixels used to lay out graphics.</summary>
    public int CellHeightPixels { get; set; } = 20;

    public string TermName { get; set; } = "xterm";

    public bool ConvertEol { get; set; }

    public bool ReflowOnResize { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the terminal answers the kitty keyboard protocol
    /// query and honours the flags an application sets. When <c>false</c> the terminal stays
    /// silent on the query, so applications keep to the legacy key encodings.
    /// </summary>
    public bool KittyKeyboardEnabled { get; set; } = true;

    /// <summary>Gets or sets whether Sixel data is decoded, rendered, and advertised in device attributes.</summary>
    public bool SixelEnabled { get; set; } = true;

    /// <summary>Gets or sets whether Kitty graphics are decoded, rendered, and queried.</summary>
    public bool KittyGraphicsEnabled { get; set; } = true;

    /// <summary>Gets or sets whether iTerm2 inline images are decoded and rendered.</summary>
    public bool ITerm2ImagesEnabled { get; set; } = true;

    /// <summary>Gets or sets whether Kitty desktop notification requests are surfaced to the host.</summary>
    public bool KittyNotificationsEnabled { get; set; } = true;

    /// <summary>Gets or sets whether Kitty pointer shape requests are surfaced to the host.</summary>
    public bool PointerShapesEnabled { get; set; } = true;

    /// <summary>Gets or sets whether terminal applications can read the system clipboard through OSC 52 or Kitty clipboard requests.</summary>
    public bool ClipboardReadEnabled { get; set; }

    /// <summary>
    /// Gets or sets whether terminal applications can write the system clipboard through OSC 52 or Kitty clipboard requests.
    /// Disabled by default because terminal output may originate from an untrusted or remote process.
    /// </summary>
    public bool ClipboardWriteEnabled { get; set; }
}
