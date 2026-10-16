using SvcTerminal = SvcSystems.UI.Terminal.Terminal;
using Xunit;

namespace SvcSystems.UI.Terminal.Tests;

public sealed class XTermEngineUnicodeRegressionTests
{
    [Fact]
    public void Unicode_18_wide_character_occupies_two_columns()
    {
        using var terminal = new SvcTerminal(new SvcSystems.UI.Terminal.TerminalOptions { Cols = 20, Rows = 3 });
        terminal.Feed("\U00018E00");

        Assert.Equal(2, terminal.Buffer.X);
        Assert.Equal(2, terminal.Buffer.Lines[0]![0].Width);
        Assert.Equal(0, terminal.Buffer.Lines[0]![1].Width);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Tai_Tham_khun_clusterUsesTheWcwidthGraphemeWidthWithoutInsertingText(int startColumn)
    {
        using var terminal = new SvcTerminal(new SvcSystems.UI.Terminal.TerminalOptions { Cols = 20, Rows = 3 });
        if (startColumn > 0)
        {
            terminal.Feed($"\u001b[1;{startColumn + 1}H");
        }

        terminal.Feed("\u1A63\u1A60\u1A20X");

        var line = terminal.Buffer.Lines[0]!;
        Assert.Equal("\u1A63\u1A60\u1A20", line[startColumn].Content);
        Assert.Equal(2, line[startColumn].Width);
        Assert.Equal("X", line[startColumn + 2].Content);
        Assert.Equal(startColumn + 3, terminal.Buffer.X);
    }

    [Theory]
    [InlineData("size=68;width=3;height=2", 3, 2)]
    [InlineData("width=50%;height=auto", 10, 5)]
    [InlineData("width=42px;height=104px", 2, 2)]
    public void Iterm2_inline_image_honorsCellPixelAndPercentageSizes(string imageOptions, int expectedCols, int expectedRows)
    {
        using var terminal = new SvcTerminal(new SvcSystems.UI.Terminal.TerminalOptions
        {
            Cols = 20,
            Rows = 20,
            CellWidthPixels = 21,
            CellHeightPixels = 52,
        });
        terminal.Feed("\u001b[1;1H");
        const string onePixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR42mNgAAIAAAUAAen63NgAAAAASUVORK5CYII=";
        terminal.Feed($"\u001b]1337;File=inline=1;{imageOptions}:{onePixelPng}\u0007");

        Assert.Null(terminal.Buffer.Lines[0]!.Sixel);
        var firstRowPlacement = Assert.Single(terminal.Buffer.Lines[0]!.Placements);
        Assert.Equal(expectedCols, firstRowPlacement.Cols);
        for (int row = 1; row < expectedRows; row++)
        {
            var nextRowPlacement = Assert.Single(terminal.Buffer.Lines[row]!.Placements);
            Assert.Equal(firstRowPlacement.ImageId, nextRowPlacement.ImageId);
        }

        Assert.Contains(terminal.Buffer.Lines[0]!.Images, image => image.Id == firstRowPlacement.ImageId);
        Assert.Equal(expectedCols, terminal.Buffer.X);
        Assert.Equal(expectedRows - 1, terminal.Buffer.Y);
    }

    [Fact]
    public void SixelCursorRightPrivateMode_IsEnabledAndDisabled()
    {
        using var terminal = new SvcTerminal();

        terminal.Feed("\u001b[?8452h");
        Assert.True(terminal.Engine.SixelCursorRight);

        terminal.Feed("\u001b[?8452l");
        Assert.False(terminal.Engine.SixelCursorRight);
    }
}
