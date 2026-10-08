using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace SvcSystems.UI.Terminal;

/// <summary>
/// Provides the default terminal color palette.
/// </summary>
/// <remarks>
/// Add this style class directly to application styles. URI-based inclusion of
/// <c>Styles/Colors.axaml</c> remains supported for compatibility but is deprecated.
/// </remarks>
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
