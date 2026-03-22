using AvaloniaTerminal;

namespace AvaloniaTerminal.Samples;

public static class TerminalSamples
{
    public const int ScrollSampleLineCount = 500;

    public static TerminalControlModel CreateScrollSampleModel(int lineCount = ScrollSampleLineCount)
    {
        var model = new TerminalControlModel();
        LoadScrollSample(model, lineCount);
        return model;
    }

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
}
