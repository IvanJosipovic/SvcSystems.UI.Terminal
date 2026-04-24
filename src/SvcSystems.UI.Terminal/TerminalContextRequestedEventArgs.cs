using Avalonia;

namespace SvcSystems.UI.Terminal;

public sealed class TerminalContextRequestedEventArgs(Point position, string selectedText, bool hasSelection) : EventArgs
{
    public Point Position { get; } = position;

    public string SelectedText { get; } = selectedText;

    public bool HasSelection { get; } = hasSelection;
}
