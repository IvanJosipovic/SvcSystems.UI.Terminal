using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace SvcSystems.UI.Terminal;

/// <summary>
/// Provides the default terminal color palette.
/// </summary>
public sealed partial class TerminalTheme : Styles
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TerminalTheme"/> class.
    /// </summary>
    public TerminalTheme()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
