using SvcSystems.UI.Terminal;
using Xunit;

namespace SvcSystems.UI.Terminal.Tests;

public sealed class TerminalTests
{
    private const string PushDisambiguateThenQuery = "\u001b[>1u\u001b[?u";

    [Fact]
    public void Dispose_IgnoresLaterFeedsAndIsIdempotent()
    {
        var terminal = new Terminal();
        terminal.Feed("a");

        terminal.Dispose();
        terminal.Feed("b");
        terminal.Dispose();

        Assert.Equal("a", terminal.Buffer.GetLine(0)!.TranslateToString(true));
    }

    [Fact]
    public void KittyKeyboard_IsAdvertisedByDefault()
    {
        using var terminal = new Terminal();
        var replies = CaptureReplies(terminal);

        terminal.Feed(PushDisambiguateThenQuery);

        Assert.Equal(["\u001b[?1u"], replies);
        Assert.True(terminal.Engine.KittyKeyboardActive);
        Assert.True(terminal.Options.KittyKeyboardEnabled);
    }

    [Fact]
    public void KittyKeyboardEnabled_False_LeavesTheQueryUnansweredAndThePushIgnored()
    {
        using var terminal = new Terminal(new TerminalOptions { KittyKeyboardEnabled = false });
        var replies = CaptureReplies(terminal);

        terminal.Feed(PushDisambiguateThenQuery);

        Assert.Empty(replies);
        Assert.False(terminal.Engine.KittyKeyboardActive);
        Assert.False(terminal.Options.KittyKeyboardEnabled);
    }

    private static List<string> CaptureReplies(Terminal terminal)
    {
        List<string> replies = [];
        terminal.Engine.DataReceived += (_, e) => replies.Add(e.Data);
        return replies;
    }
}
