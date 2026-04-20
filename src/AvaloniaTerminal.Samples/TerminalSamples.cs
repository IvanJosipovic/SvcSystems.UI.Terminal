using AvaloniaTerminal;

namespace AvaloniaTerminal.Samples;

public static class TerminalSamples
{
    public const int ScrollSampleLineCount = 500;
    public const string SelectionSampleText =
        "Avalonia Terminal selection sample\r\n" +
        "Double-click selects a word, triple-click selects a row.\r\n" +
        "Drag to select text and bind it into a context menu.";

    public static void LoadScrollSample(TerminalControlModel model, int lineCount = ScrollSampleLineCount)
    {
        ArgumentNullException.ThrowIfNull(model);
        model.Feed(CreateScrollSampleText(lineCount));
    }

    public static string CreateScrollSampleText(int lineCount = ScrollSampleLineCount)
    {
        return string.Join("\r\n", Enumerable.Range(1, lineCount).Select(static line =>
            $"Line {line:0000}  The quick brown fox jumps over the lazy dog."));
    }

    public static TerminalControlModel CreateSelectionSampleModel()
    {
        var model = new TerminalControlModel();
        LoadSelectionSample(model);
        return model;
    }

    public static void LoadSelectionSample(TerminalControlModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        model.Feed(SelectionSampleText);
    }
}
