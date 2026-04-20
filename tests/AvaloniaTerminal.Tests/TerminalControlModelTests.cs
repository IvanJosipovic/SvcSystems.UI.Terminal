using Avalonia.Media;
using AvaloniaTerminal;
using AvaloniaTerminal.Samples;
using Xunit;

namespace AvaloniaTerminal.Tests;

public sealed class TerminalControlModelTests : AvaloniaTestBase
{
    [Fact]
    public Task Constructor_InitializesTerminalStateAndBlankViewport()
    {
        return RunInHeadlessSession(() =>
        {
            var model = new TerminalControlModel();

            Assert.NotNull(model.Terminal);
            Assert.NotNull(model.SearchService);
            Assert.Equal(model.Terminal.Rows, model.ViewportRows.Count);
            Assert.Equal(model.Terminal.Cols, model.ViewportRows[0].Runs.Sum(static run => run.CellWidth));
            Assert.Equal(" ", GetCellText(model, 0, 0));
            Assert.False(model.CanScroll);
            Assert.Equal(0d, model.ScrollPosition);
            Assert.Equal(1f, model.ScrollThumbsize);
        });
    }

    [Fact]
    public Task Send_ForwardsUtf8PayloadToSubscribers()
    {
        return RunInHeadlessSession(() =>
        {
            var model = new TerminalControlModel();
            byte[]? bytes = null;

            model.UserInput += data => bytes = data;

            model.Send("abc");

            Assert.NotNull(bytes);
            Assert.Equal("abc"u8.ToArray(), bytes);
        });
    }

    [Fact]
    public Task Resize_IgnoresZeroSizedViewport()
    {
        return RunInHeadlessSession(() =>
        {
            var model = new TerminalControlModel();
            (int cols, int rows, double width, double height)? resize = null;
            model.SizeChanged += (cols, rows, width, height) => resize = (cols, rows, width, height);
            var originalCols = model.Terminal.Cols;
            var originalRows = model.Terminal.Rows;

            model.Resize(width: 0, height: 0, textWidth: 8, textHeight: 16);

            Assert.Null(resize);
            Assert.Equal(originalCols, model.Terminal.Cols);
            Assert.Equal(originalRows, model.Terminal.Rows);
        });
    }

    [Fact]
    public Task Resize_ClampsViewportToAtLeastOneCell()
    {
        return RunInHeadlessSession(() =>
        {
            var model = new TerminalControlModel();
            (int cols, int rows, double width, double height)? resize = null;
            model.SizeChanged += (cols, rows, width, height) => resize = (cols, rows, width, height);

            model.Resize(width: 1, height: 1, textWidth: 8, textHeight: 16);

            Assert.NotNull(resize);
            Assert.Equal(1d, resize.Value.width);
            Assert.Equal(1d, resize.Value.height);
            Assert.True(resize.Value.cols >= 1);
            Assert.True(resize.Value.rows >= 1);
            Assert.True(model.Terminal.Cols >= 1);
            Assert.True(model.Terminal.Rows >= 1);
        });
    }

    [Fact]
    public Task Feed_UpdatesTextStylingAndTriggersUiRefresh()
    {
        return RunInHeadlessSession(() =>
        {
            var model = new TerminalControlModel();
            var refreshCount = 0;
            model.UpdateUI = () => refreshCount++;

            model.Feed("\u001b[1;3;4;31;47mA");
            var cell = GetRun(model, 0, 0);

            Assert.Equal("A", cell.Text);
            Assert.Equal(FontWeight.Bold, cell.FontWeight);
            Assert.Equal(FontStyle.Italic, cell.FontStyle);
            Assert.Contains(cell.TextDecorations!, decoration => decoration.Location == TextDecorationLocation.Underline);
            Assert.Equal(1, cell.ForegroundColor);
            Assert.Equal(7, cell.BackgroundColor);
            Assert.Equal(1, refreshCount);
        });
    }

    [Fact]
    public Task Feed_DefaultStylingUsesWhiteOnBlack()
    {
        return RunInHeadlessSession(() =>
        {
            var model = new TerminalControlModel();

            model.Feed("A");

            var cell = GetRun(model, 0, 0);

            Assert.Equal(256, cell.ForegroundColor);
            Assert.Equal(257, cell.BackgroundColor);
        });
    }

    [Fact]
    public Task Feed_TitleSequenceUpdatesModelTitle()
    {
        return RunInHeadlessSession(() =>
        {
            var model = new TerminalControlModel();

            model.Feed("\u001b]0;Avalonia Terminal\u0007");

            Assert.Equal("Avalonia Terminal", model.Title);
        });
    }

    [Fact]
    public Task Feed_NonBmpRune_RendersAsRenderSafeText()
    {
        return RunInHeadlessSession(() =>
        {
            var model = new TerminalControlModel();

            model.Feed("😀");

            var text = GetCellText(model, 0, 0);
            Assert.False(string.IsNullOrEmpty(text));
            Assert.DoesNotContain(text, static ch => char.IsSurrogate(ch));
        });
    }

    [Fact]
    public Task Feed_BoxDrawingCharacter_UsesSingleCellWidthByDefault()
    {
        return RunInHeadlessSession(() =>
        {
            var model = new TerminalControlModel();

            model.Feed("│A");

            Assert.Equal("│", GetCellText(model, 0, 0));
            Assert.Equal("A", GetCellText(model, 1, 0));
            Assert.Equal(" ", GetCellText(model, 2, 0));
        });
    }

    [Fact]
    public Task CaretProperties_TrackCursorVisibilityAndPosition()
    {
        return RunInHeadlessSession(() =>
        {
            var model = new TerminalControlModel();

            model.Feed("abc");

            Assert.True(model.IsCaretVisible);
            Assert.Equal(3, model.CaretColumn);
            Assert.Equal(0, model.CaretRow);

            model.Feed("\u001b[?25l");
            Assert.False(model.IsCaretVisible);

            model.Feed("\u001b[?25h");
            Assert.True(model.IsCaretVisible);
        });
    }

    [Fact]
    public Task CaretRow_RemainsOnTheBottomViewportRowAfterScrollbackGrows()
    {
        return RunInHeadlessSession(() =>
        {
            var model = new TerminalControlModel(new TerminalOptions
            {
                Cols = 10,
                Rows = 4,
            });

            model.Feed("1\r\n2\r\n3\r\n4\r\n5\r\n6\r\n7\r\n8\r\n9\r\n10");

            Assert.True(model.ScrollOffset > 0);
            Assert.True(model.IsCaretVisible);
            Assert.Equal(model.Terminal.Rows - 1, model.CaretRow);
        });
    }

    [Fact]
    public Task SelectionProperties_ReflectEngineSelectionText()
    {
        return RunInHeadlessSession(() =>
        {
            var model = new TerminalControlModel();
            TerminalSamples.LoadSelectionSample(model);

            model.SetSoftSelectionStart(0, 0);
            model.StartSelectionFromSoftStart();
            model.DragExtendSelection(0, 8);

            Assert.True(model.HasSelection);
            Assert.Equal("Avalonia", model.SelectedText);

            model.ClearSelection();

            Assert.False(model.HasSelection);
            Assert.Equal(string.Empty, model.SelectedText);
        });
    }

    [Fact]
    public Task Search_SelectsMatchesAndNavigatesBetweenResults()
    {
        return RunInHeadlessSession(() =>
        {
            var model = new TerminalControlModel();
            model.Resize(width: 320, height: 120, textWidth: 8, textHeight: 16);
            model.Feed("alpha beta alpha gamma alpha");

            var count = model.Search("alpha");

            Assert.Equal(3, count);
            Assert.Equal(3, model.SearchResultCount);
            Assert.Equal("alpha", model.SelectedText);
            Assert.Equal(0, model.CurrentSearchResultIndex);

            var nextIndex = model.SelectNextSearchResult();
            Assert.Equal(1, nextIndex);

            var previousIndex = model.SelectPreviousSearchResult();
            Assert.Equal(0, previousIndex);
        });
    }

    [Fact]
    public Task Send_EnsuresCaretIsVisibleWhenScrolledAway()
    {
        return RunInHeadlessSession(() =>
        {
            var model = new TerminalControlModel();
            TerminalSamples.LoadScrollSample(model);

            model.ScrollToYDisp(0);
            Assert.Equal(0, model.ScrollOffset);

            model.Send("x");

            Assert.Equal(model.Terminal.Buffer.YBase, model.ScrollOffset);
            Assert.True(model.IsCaretVisible);
        });
    }

    [Fact]
    public Task ScrollProperties_ReflectScrollbackAndViewportMovement()
    {
        return RunInHeadlessSession(() =>
        {
            var model = new TerminalControlModel();
            model.Resize(width: 40, height: 30, textWidth: 10, textHeight: 10);

            model.Feed("1\r\n2\r\n3\r\n4\r\n5\r\n6");

            Assert.True(model.CanScroll);
            Assert.Equal(1d, model.ScrollPosition);
            Assert.InRange(model.ScrollThumbsize, 0.01f, 0.99f);

            model.Terminal.Engine.ScrollLines(-1);

            Assert.InRange(model.ScrollPosition, 0d, 0.99d);
        });
    }

    [Fact]
    public Task Scrolling_KeepsRenderedRunCoverageBoundedToViewport()
    {
        return RunInHeadlessSession(() =>
        {
            var model = new TerminalControlModel();
            model.Resize(width: 320, height: 120, textWidth: 8, textHeight: 16);
            TerminalSamples.LoadScrollSample(model);

            var viewportCellCount = model.Terminal.Cols * model.Terminal.Rows;

            Assert.Equal(viewportCellCount, GetViewportCoverage(model));

            model.ScrollToYDisp(model.MaxScrollback / 2);

            Assert.Equal(viewportCellCount, GetViewportCoverage(model));

            model.ScrollToYDisp(model.MaxScrollback);

            Assert.Equal(viewportCellCount, GetViewportCoverage(model));
        });
    }

    [Fact]
    public Task AlternateBuffer_DisablesScrollMetrics()
    {
        return RunInHeadlessSession(() =>
        {
            var model = new TerminalControlModel();
            model.Resize(width: 40, height: 30, textWidth: 10, textHeight: 10);
            model.Feed("1\r\n2\r\n3\r\n4\r\n5\r\n6");

            model.Terminal.SwitchToAltBuffer();

            Assert.False(model.CanScroll);
            Assert.Equal(0d, model.ScrollPosition);
            Assert.Equal(0f, model.ScrollThumbsize);
        });
    }

    [Fact]
    public Task Feed_CursorPrecedingLine_MovesUpRequestedNumberOfRows()
    {
        return RunInHeadlessSession(() =>
        {
            var model = new TerminalControlModel(new TerminalOptions
            {
                Cols = 10,
                Rows = 4,
            });

            model.Feed("1111\r\n2222\r\n3333\r\n4444");
            model.Feed("\u001b[1FZ");

            Assert.Equal("Z", GetCellText(model, 0, 2));
            Assert.Equal("4", GetCellText(model, 0, 3));
        });
    }

    [Fact]
    public Task Feed_AdjacentSameStyleAsciiCellsCollapseIntoSingleRun()
    {
        return RunInHeadlessSession(() =>
        {
            var model = new TerminalControlModel();

            model.Feed("ABC");

            var row = model.ViewportRows[0];
            var firstRun = row.Runs[0];
            Assert.Equal(0, firstRun.StartColumn);
            Assert.StartsWith("ABC", firstRun.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(row.Runs, run => run.StartColumn is 1 or 2);
        });
    }

    [Fact]
    public Task Feed_StyleChangeSplitsRuns()
    {
        return RunInHeadlessSession(() =>
        {
            var model = new TerminalControlModel();

            model.Feed("A\u001b[31mB");

            var row = model.ViewportRows[0];
            Assert.Contains(row.Runs, run => run.StartColumn == 0 && run.CellWidth == 1 && run.Text == "A");
            Assert.Contains(row.Runs, run => run.StartColumn == 1 && run.CellWidth == 1 && run.Text == "B");
        });
    }

    [Fact]
    public Task Feed_WideCharacterProducesSingleWidthTwoRun()
    {
        return RunInHeadlessSession(() =>
        {
            var model = new TerminalControlModel();

            model.Feed("好A");

            var row = model.ViewportRows[0];
            Assert.Contains(row.Runs, run => run.StartColumn == 0 && run.CellWidth == 2);
            Assert.Equal(" ", GetCellText(model, 1, 0));
            Assert.Equal("A", GetCellText(model, 2, 0));
        });
    }

    [Fact]
    public Task Feed_InverseDefaultColorsStayReadable()
    {
        return RunInHeadlessSession(() =>
        {
            var model = new TerminalControlModel();

            model.Feed("\u001b[7mA");

            var run = GetRun(model, 0, 0);
            Assert.Equal(257, run.ForegroundColor);
            Assert.Equal(256, run.BackgroundColor);
        });
    }

    [Fact]
    public Task Resize_WithReflowDisabled_PreservesBufferLineCount()
    {
        return RunInHeadlessSession(() =>
        {
            var model = new TerminalControlModel(new TerminalOptions
            {
                Cols = 10,
                Rows = 4,
                ReflowOnResize = false,
            });

            model.Feed("abcdefghij\r\nklmnopqrst\r\nuvwxyz");
            var before = model.Terminal.Buffer.Lines.Length;

            model.Resize(width: 160, height: 64, textWidth: 8, textHeight: 16);

            Assert.Equal(before, model.Terminal.Buffer.Lines.Length);
        });
    }

    private static int GetViewportCoverage(TerminalControlModel model)
    {
        return model.ViewportRows.Sum(static row => row.Runs.Sum(static run => run.CellWidth));
    }

    private static ViewportTextRun GetRun(TerminalControlModel model, int col, int row)
    {
        var viewportRow = model.ViewportRows[row];
        foreach (var run in viewportRow.Runs)
        {
            if (col >= run.StartColumn && col < run.StartColumn + run.CellWidth)
            {
                return run;
            }
        }

        throw new InvalidOperationException($"No run found at ({col}, {row}).");
    }

    private static string GetCellText(TerminalControlModel model, int col, int row)
    {
        var run = GetRun(model, col, row);
        var offset = col - run.StartColumn;
        if (run.CellWidth == 2 && offset == 1)
        {
            return " ";
        }

        if (offset < 0 || offset >= run.Text.Length)
        {
            return " ";
        }

        return run.Text[offset].ToString();
    }
}
