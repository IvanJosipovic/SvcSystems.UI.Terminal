using SvcSystems.UI.Terminal;
using Xunit;

namespace SvcSystems.UI.Terminal.Tests;

public sealed class TerminalTests
{
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
}
