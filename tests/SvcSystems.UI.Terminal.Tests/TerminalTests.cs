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
    public void CellPixelDimensions_ArePassedThroughToXTermNet()
    {
        using var terminal = new Terminal(new TerminalOptions
        {
            CellWidthPixels = 7,
            CellHeightPixels = 15,
        });

        Assert.Equal(7, terminal.Engine.Options.CellWidthPixels);
        Assert.Equal(15, terminal.Engine.Options.CellHeightPixels);
        Assert.Equal(7, terminal.Options.CellWidthPixels);
        Assert.Equal(15, terminal.Options.CellHeightPixels);
    }

    [Fact]
    public void PixelGeometryQueries_ReportTheMeasuredCellAndGridDimensions()
    {
        using var terminal = new Terminal(new TerminalOptions
        {
            Cols = 91,
            Rows = 37,
            CellWidthPixels = 8,
            CellHeightPixels = 17,
        });
        List<string> replies = CaptureReplies(terminal);

        terminal.Feed("\u001b[14t\u001b[16t");

        Assert.Equal(
            ["\u001b[4;629;728t", "\u001b[6;17;8t"],
            replies);
    }

    [Fact]
    public void PixelGeometryQueries_TrackResizedGridAndUpdatedCellMetrics()
    {
        using var terminal = new Terminal(new TerminalOptions
        {
            Cols = 91,
            Rows = 37,
            CellWidthPixels = 8,
            CellHeightPixels = 17,
        });
        List<string> replies = CaptureReplies(terminal);

        terminal.Resize(100, 32);
        terminal.SetCellSizePixels(9, 18);
        terminal.Feed("\u001b[14t\u001b[16t");

        Assert.Equal(
            ["\u001b[4;576;900t", "\u001b[6;18;9t"],
            replies);
    }

    [Fact]
    public void CellMetricChanges_SendInBandResizeReportsWhenRequested()
    {
        using var terminal = new Terminal(new TerminalOptions
        {
            Cols = 91,
            Rows = 37,
            CellWidthPixels = 8,
            CellHeightPixels = 17,
        });
        List<string> replies = CaptureReplies(terminal);

        terminal.Feed("\u001b[?2048h");
        replies.Clear();
        terminal.SetCellSizePixels(9, 18);

        Assert.Equal(
            [$"\u001b[48;{terminal.Rows};{terminal.Cols};{terminal.Rows * 18};{terminal.Cols * 9}t"],
            replies);
    }

    [Fact]
    public void DeviceAttributes_AdvertiseSixelWhenTheEngineDecodesIt()
    {
        using var terminal = new Terminal();
        List<string> replies = CaptureReplies(terminal);

        terminal.Feed("\u001b[c");

        string reply = Assert.Single(replies);
        Assert.Contains(";4;", reply, StringComparison.Ordinal);
        Assert.True(terminal.Engine.Options.SixelEnabled);
    }

    [Fact]
    public void TerminalOptions_ControlTheProtocolsTheEngineCanAdvertise()
    {
        using var terminal = new Terminal(new TerminalOptions
        {
            SixelEnabled = false,
            KittyGraphicsEnabled = false,
            ITerm2ImagesEnabled = false,
            KittyNotificationsEnabled = false,
            PointerShapesEnabled = false,
        });

        Assert.False(terminal.Engine.Options.SixelEnabled);
        Assert.False(terminal.Engine.Options.KittyGraphicsEnabled);
        Assert.False(terminal.Engine.Options.ITerm2ImagesEnabled);
        Assert.False(terminal.Engine.Options.KittyNotificationsEnabled);
        Assert.False(terminal.Engine.Options.PointerShapesEnabled);
        Assert.False(terminal.Options.SixelEnabled);
        Assert.False(terminal.Options.KittyGraphicsEnabled);
        Assert.False(terminal.Options.ITerm2ImagesEnabled);
        Assert.False(terminal.Options.KittyNotificationsEnabled);
        Assert.False(terminal.Options.PointerShapesEnabled);
    }

    [Fact]
    public void XtVersionQuery_ReportsTheEmulatorIdentity()
    {
        using var terminal = new Terminal();
        List<string> replies = CaptureReplies(terminal);

        terminal.Feed("\u001b[>q");

        Assert.Equal(["\u001bP>|XTerm.NET(3.0.0)\u001b\\"], replies);
    }

    [Fact]
    public void UcsDetectGraphicsQueries_ReportEnabledProtocols()
    {
        using var terminal = new Terminal();
        List<string> replies = CaptureReplies(terminal);

        terminal.Feed("\u001b_Gi=31,s=1,v=1,a=q,t=d,f=24;AAAA\u001b\\");
        terminal.Feed("\u001b]1337;Capabilities\u0007");
        terminal.Feed("\u001b]99;i=blessed:p=?\u001b\\");
        terminal.Feed("\u001b]22;?__current__\u001b\\");

        Assert.Contains(replies, static reply => reply.StartsWith("\u001b_Gi=31;OK", StringComparison.Ordinal));
        Assert.Contains(replies, static reply => reply.StartsWith("\u001b]1337;Capabilities=", StringComparison.Ordinal));
        Assert.Contains(replies, static reply => reply.StartsWith("\u001b]99;", StringComparison.Ordinal));
        Assert.Contains(replies, static reply => reply.StartsWith("\u001b]22;", StringComparison.Ordinal));
    }

    [Fact]
    public void Iterm2Capabilities_ReflectClipboardPolicyAndOmitUnimplementedHyperlinks()
    {
        using var terminal = new Terminal();
        List<string> replies = CaptureReplies(terminal);

        terminal.Feed("\u001b]1337;Capabilities\u0007");

        string defaultCapabilities = Assert.Single(replies);
        Assert.DoesNotContain("Cw", defaultCapabilities, StringComparison.Ordinal);
        Assert.DoesNotContain("H", defaultCapabilities, StringComparison.Ordinal);

        using var clipboardEnabled = new Terminal(new TerminalOptions { ClipboardWriteEnabled = true });
        List<string> enabledReplies = CaptureReplies(clipboardEnabled);
        clipboardEnabled.Feed("\u001b]1337;Capabilities\u0007");

        Assert.Contains("Cw", Assert.Single(enabledReplies), StringComparison.Ordinal);
    }

    [Fact]
    public void ColorQueries_ReportTheConfiguredDefaultPaletteColors()
    {
        using var terminal = new Terminal();
        List<string> replies = CaptureReplies(terminal);

        terminal.Feed("\u001b]10;?\u0007\u001b]11;?\u0007");

        Assert.Equal(2, replies.Count);
        Assert.StartsWith("\u001b]10;rgb:", replies[0], StringComparison.Ordinal);
        Assert.StartsWith("\u001b]11;rgb:", replies[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2026)]
    [InlineData(2048)]
    [InlineData(5522)]
    public void DecPrivateModeQuery_RecognizesImplementedExtensionModes(int mode)
    {
        using var terminal = new Terminal();
        List<string> replies = CaptureReplies(terminal);

        terminal.Feed($"\u001b[?{mode}$p");

        Assert.Equal([$"\u001b[?{mode};2$y"], replies);
    }

    [Fact]
    public void DecrqssQuery_ReportsTheTruecolorSgrActuallyApplied()
    {
        using var terminal = new Terminal();
        List<string> replies = CaptureReplies(terminal);

        terminal.Feed("\u001b[48:2:1:2:3m\u001bP$qm\u001b\\");

        string reply = Assert.Single(replies);
        Assert.Contains("48;2;1;2;3", reply, StringComparison.Ordinal);
        Assert.EndsWith("m\u001b\\", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void KittyTextSizing_AdvancesByTheRequestedWidth()
    {
        using var terminal = new Terminal(new TerminalOptions { Cols = 10, Rows = 2 });

        terminal.Feed("\u001b]66;w=2; \u0007");

        Assert.Equal(2, terminal.Buffer.X);
    }

    [Fact]
    public void KittyTextSizing_WidthAndScaleProbesAdvanceTwoColumns()
    {
        using var terminal = new Terminal(new TerminalOptions { Cols = 10, Rows = 2 });
        List<string> replies = CaptureReplies(terminal);

        terminal.Feed("\u001b]66;w=2; \u001b[6n\u001b]66;s=2; \u001b[6n");

        Assert.Equal(["\u001b[1;3R", "\u001b[1;5R"], replies);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void KittyKeyboardSet_ReportsEachProgressiveEnhancementFlag(int flag)
    {
        using var terminal = new Terminal();
        List<string> replies = CaptureReplies(terminal);

        terminal.Feed($"\u001b[={flag};1u\u001b[?u");

        Assert.Equal([$"\u001b[?{flag}u"], replies);
    }

    [Theory]
    [InlineData(1004, "\u001b[?1004;2$y")]
    [InlineData(1006, "\u001b[?1006;2$y")]
    [InlineData(2004, "\u001b[?2004;2$y")]
    public void DecPrivateModeQuery_ReportsImplementedModesAsSupported(int mode, string expectedReply)
    {
        using var terminal = new Terminal();
        List<string> replies = CaptureReplies(terminal);

        terminal.Feed($"\u001b[?{mode}$p");

        Assert.Equal([expectedReply], replies);
    }

    [Fact]
    public void Feed_PreservesUtf8SequencesSplitAcrossProcessReads()
    {
        using var terminal = new Terminal(new TerminalOptions { Cols = 10, Rows = 2 });
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes("ક");

        foreach (byte value in utf8)
        {
            terminal.Feed([value]);
        }

        Assert.Equal("ક", terminal.Buffer.GetLine(0)!.TranslateToString(true));
        Assert.Equal(1, terminal.Buffer.X);
    }

    [Theory]
    [InlineData("👨‍👩‍👧‍👦")]
    [InlineData("🇯🇵")]
    [InlineData("👍🏽")]
    public void Feed_PreservesUnicodeGraphemesAcrossByteSizedReads(string grapheme)
    {
        using var terminal = new Terminal(new TerminalOptions { Cols = 10, Rows = 2 });
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(grapheme);

        foreach (byte value in utf8)
        {
            terminal.Feed([value]);
        }

        Assert.Equal(grapheme, terminal.Buffer.GetLine(0)!.TranslateToString(true));
        Assert.Equal(2, terminal.Buffer.X);
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
