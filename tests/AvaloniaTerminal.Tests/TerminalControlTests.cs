using Avalonia;
using Avalonia.Layout;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using AvaloniaTerminal;
using AvaloniaTerminal.Samples;
using System.Text;
using Xunit;

namespace AvaloniaTerminal.Tests;

public sealed class TerminalControlTests : AvaloniaTestBase
{
    [Fact]
    public Task PointerWheel_ScrollsTerminalViewportThroughModel()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out var scrollBar);
            PopulateScrollback(model);

            var before = model.ScrollOffset;

            control.SimulatePointerWheel(new Vector(0, 1));

            Assert.True(before > 0);
            Assert.Equal(before - 1, model.ScrollOffset);
            Assert.Equal(model.ScrollOffset, (int)scrollBar.Value);
            Assert.True(scrollBar.IsEnabled);
        });
    }

    [Fact]
    public Task PointerWheel_InMouseMode_SendsWheelUpEventToTerminal()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out _);
            model.Feed("\u001b[?1006h\u001b[?1000h");

            List<byte[]> sent = [];
            model.UserInput += bytes => sent.Add(bytes.ToArray());

            control.SimulatePointerWheel(new Vector(0, 1));

            Assert.NotEmpty(sent);
            Assert.Contains(sent, bytes => Encoding.UTF8.GetString(bytes).Contains("<64;", StringComparison.Ordinal));
            Assert.Contains(sent, bytes => Encoding.UTF8.GetString(bytes).EndsWith("M", StringComparison.Ordinal));
            Assert.Equal(0, model.ScrollOffset);
        });
    }

    [Fact]
    public Task PointerWheel_InMouseMode_SendsWheelDownEventToTerminal()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out _);
            model.Feed("\u001b[?1006h\u001b[?1000h");

            List<byte[]> sent = [];
            model.UserInput += bytes => sent.Add(bytes.ToArray());

            control.SimulatePointerWheel(new Vector(0, -1));

            Assert.NotEmpty(sent);
            Assert.Contains(sent, bytes => Encoding.UTF8.GetString(bytes).Contains("<65;", StringComparison.Ordinal));
            Assert.Contains(sent, bytes => Encoding.UTF8.GetString(bytes).EndsWith("M", StringComparison.Ordinal));
            Assert.Equal(0, model.ScrollOffset);
        });
    }

    [Fact]
    public Task Caret_TracksViewportPosition()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out _);

            model.Feed("abc");
            var firstRect = control.CaretRect;

            model.Feed("\r\nx");
            var secondRect = control.CaretRect;

            Assert.True(control.HasVisibleCaret);
            Assert.True(firstRect.Width > 0);
            Assert.True(firstRect.X > 0);
            Assert.True(secondRect.Y > firstRect.Y);
            Assert.True(secondRect.X < firstRect.X);
        });
    }

    [Fact]
    public Task Caret_HidesWhenViewportIsScrolledAwayFromCursor()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out _);
            PopulateScrollback(model);

            Assert.True(control.HasVisibleCaret);

            model.ScrollToYDisp(0);

            Assert.False(control.HasVisibleCaret);
        });
    }

    [Fact]
    public Task InvalidFontFamily_FallsBackToADrawableTypeface()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out _, out _);

            control.FontFamily = "__missing_font_family__";

            Assert.True(control.CanRenderTextForTests);
        });
    }

    [Fact]
    public Task TerminalControlPalette_UsesConfiguredDefaultPalette()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out _, out _);
            var brush = control.ResolveXtermColorForTests(15);

            var solid = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(brush);
            Assert.Equal(Avalonia.Media.Colors.White, solid.Color);
        });
    }

    [Fact]
    public Task ResourceStyle_DefaultsAlignFontCaretAndSelection()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out _, out _);

            Assert.Equal("Cascadia Mono", control.FontFamily);
            Assert.Equal(12, control.FontSize);
            Assert.Null(control.CaretBrush);
            Assert.Null(control.SelectionBrush);
        });
    }

    [Fact]
    public Task ApplicationResources_CanOverrideControlStyleDefaults()
    {
        return RunInHeadlessSession(() =>
        {
            var application = Avalonia.Application.Current ?? throw new InvalidOperationException("No application is running.");
            var hadFontFamily = application.Resources.TryGetValue("AvaloniaTerminalFontFamily", out var previousFontFamily);
            var hadFontSize = application.Resources.TryGetValue("AvaloniaTerminalFontSize", out var previousFontSize);
            var hadCaretBrush = application.Resources.TryGetValue("AvaloniaTerminalCaretBrush", out var previousCaretBrush);
            var hadSelectionBrush = application.Resources.TryGetValue("AvaloniaTerminalSelectionBrush", out var previousSelectionBrush);

            try
            {
                application.Resources["AvaloniaTerminalFontFamily"] = "Fira Code";
                application.Resources["AvaloniaTerminalFontSize"] = 14d;
                application.Resources["AvaloniaTerminalCaretBrush"] = Avalonia.Media.Brushes.Orange;
                application.Resources["AvaloniaTerminalSelectionBrush"] = Avalonia.Media.Brushes.CadetBlue;

                var control = CreateControl(out _, out _);

                Assert.Equal("Fira Code", control.FontFamily);
                Assert.Equal(14d, control.FontSize);
                Assert.Same(Avalonia.Media.Brushes.Orange, control.CaretBrushForTests);
                Assert.Same(Avalonia.Media.Brushes.CadetBlue, control.SelectionBrushForTests);
            }
            finally
            {
                RestoreResource(application, "AvaloniaTerminalFontFamily", hadFontFamily, previousFontFamily);
                RestoreResource(application, "AvaloniaTerminalFontSize", hadFontSize, previousFontSize);
                RestoreResource(application, "AvaloniaTerminalCaretBrush", hadCaretBrush, previousCaretBrush);
                RestoreResource(application, "AvaloniaTerminalSelectionBrush", hadSelectionBrush, previousSelectionBrush);
            }
        });
    }

    [Fact]
    public Task CaretBrush_UsesContrastingColorWhenCellForegroundMatchesBackground()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out _);
            model.Feed("\u001b[38;2;255;255;255;48;2;255;255;255mA\b");

            var brush = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(control.CaretBrushForTests);

            Assert.Equal(Avalonia.Media.Colors.Black, brush.Color);
        });
    }

    [Fact]
    public Task DragSelection_UpdatesSelectedTextAndControlBinding()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out _);
            TerminalSamples.LoadSelectionSample(model);

            control.HandleSelectionPressed(control.GetCellCenter(0, 0), KeyModifiers.None, clickCount: 1);
            control.HandleSelectionMoved(control.GetCellCenter(8, 0));
            control.HandleSelectionReleased(control.GetCellCenter(8, 0), KeyModifiers.None, clickCount: 1);

            Assert.True(model.HasSelection);
            Assert.Equal("Avalonia", model.SelectedText);
            Assert.True(control.HasSelection);
            Assert.Equal(model.SelectedText, control.SelectedText);
        });
    }

    [Fact]
    public Task DoubleClickSelection_SelectsWord()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out _);
            TerminalSamples.LoadSelectionSample(model);

            control.HandleSelectionPressed(control.GetCellCenter(10, 0), KeyModifiers.None, clickCount: 2);
            control.HandleSelectionReleased(control.GetCellCenter(10, 0), KeyModifiers.None, clickCount: 2);

            Assert.True(model.HasSelection);
            Assert.Equal("Terminal", model.SelectedText);
        });
    }

    [Fact]
    public Task OptionAsMetaKey_False_DoesNotPrefixEscape()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out _);
            model.OptionAsMetaKey = false;

            byte[]? sent = null;
            model.UserInput += bytes => sent = bytes;

            control.SimulateKeyUp(Key.A, KeyModifiers.Alt, "a");

            Assert.NotNull(sent);
            Assert.Equal("a"u8.ToArray(), sent);
        });
    }

    [Fact]
    public Task SearchHelpers_ForwardToModel()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out _);
            model.Feed("alpha beta alpha");

            var count = control.Search("alpha");

            Assert.Equal(2, count);
            Assert.Equal("alpha", control.CopySelection());

            var next = control.SelectNextSearchResult();
            var previous = control.SelectPreviousSearchResult();

            Assert.Equal(1, next);
            Assert.Equal(0, previous);
        });
    }

    [Fact]
    public Task MouseMode_PointerPress_DoesNotStartSelection_AndSendsMouseEvent()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out _);
            TerminalSamples.LoadSelectionSample(model);
            model.Feed("\u001b[?1006h\u001b[?1000h");

            var sent = new List<byte[]>();
            model.UserInput += bytes => sent.Add(bytes.ToArray());

            control.SimulatePointerPressed(control.GetCellCenter(2, 0), clickCount: 1);

            Assert.True(model.IsMouseModeActive);
            Assert.False(model.HasSelection);
            Assert.NotEmpty(sent);
            Assert.Contains(sent.Select(Encoding.UTF8.GetString), text => text.Contains("<0;3;1M", StringComparison.Ordinal));
        });
    }

    [Fact]
    public Task MouseMode_PointerRelease_SendsReleaseEvent()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out _);
            model.Feed("\u001b[?1006h\u001b[?1000h");

            var sent = new List<byte[]>();
            model.UserInput += bytes => sent.Add(bytes.ToArray());

            var point = control.GetCellCenter(1, 1);
            control.SimulatePointerPressed(point, clickCount: 1);
            control.SimulatePointerReleased(point);

            Assert.Contains(sent.Select(Encoding.UTF8.GetString), text => text.Contains("<0;2;2m", StringComparison.Ordinal));
        });
    }

    [Fact]
    public Task MouseMode_PointerMove_SendsMotionEvent()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out _);
            model.Feed("\u001b[?1006h\u001b[?1003h");

            var sent = new List<byte[]>();
            model.UserInput += bytes => sent.Add(bytes.ToArray());

            control.SimulatePointerPressed(control.GetCellCenter(0, 0), clickCount: 1);
            control.SimulatePointerMoved(control.GetCellCenter(3, 1), isLeftButtonPressed: true);

            Assert.Contains(sent.Select(Encoding.UTF8.GetString), text => text.Contains("<32;4;2M", StringComparison.Ordinal));
        });
    }

    [Fact]
    public Task SelectionAutoScroll_DraggingBelowViewport_ScrollsAndExtendsSelection()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out _);
            PopulateScrollback(model);
            var initialOffset = Math.Max(1, model.MaxScrollback / 2);
            model.ScrollToYDisp(initialOffset);

            control.SimulatePointerPressed(control.GetCellCenter(0, 1), clickCount: 1);
            control.SimulatePointerMoved(new Point(10, control.Bounds.Height + 48), isLeftButtonPressed: true);

            Assert.True(control.SelectionAutoScrollDeltaForTests > 0);

            control.ProcessSelectionAutoScrollForTests();

            Assert.True(model.ScrollOffset > initialOffset);
            Assert.True(model.HasSelection);
            Assert.False(string.IsNullOrEmpty(model.SelectedText));
        });
    }

    [Fact]
    public Task SelectionAutoScroll_DraggingAboveViewport_ScrollsAndExtendsSelection()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out _);
            PopulateScrollback(model);
            var initialOffset = Math.Max(1, model.MaxScrollback / 2);
            model.ScrollToYDisp(initialOffset);

            control.SimulatePointerPressed(control.GetCellCenter(0, 2), clickCount: 1);
            control.SimulatePointerMoved(new Point(10, -48), isLeftButtonPressed: true);

            Assert.True(control.SelectionAutoScrollDeltaForTests < 0);

            control.ProcessSelectionAutoScrollForTests();

            Assert.True(model.ScrollOffset < initialOffset);
            Assert.True(model.HasSelection);
            Assert.False(string.IsNullOrEmpty(model.SelectedText));
        });
    }

    [Fact]
    public Task SelectionAutoScroll_StopsOnPointerRelease()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out _);
            PopulateScrollback(model);
            model.ScrollToYDisp(Math.Max(1, model.MaxScrollback / 2));

            var outsidePoint = new Point(10, control.Bounds.Height + 48);
            control.SimulatePointerPressed(control.GetCellCenter(0, 1), clickCount: 1);
            control.SimulatePointerMoved(outsidePoint, isLeftButtonPressed: true);

            Assert.NotEqual(0, control.SelectionAutoScrollDeltaForTests);

            control.SimulatePointerReleased(outsidePoint);

            Assert.Equal(0, control.SelectionAutoScrollDeltaForTests);
        });
    }

    [Fact]
    public Task SelectionAutoScroll_DoesNotActivateWhenMouseModeIsEnabled()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out _);
            PopulateScrollback(model);
            model.Feed("\u001b[?1006h\u001b[?1000h");

            control.SimulatePointerPressed(control.GetCellCenter(0, 1), clickCount: 1);
            control.SimulatePointerMoved(new Point(10, control.Bounds.Height + 48), isLeftButtonPressed: true);

            Assert.True(model.IsMouseModeActive);
            Assert.Equal(0, control.SelectionAutoScrollDeltaForTests);
            Assert.False(model.HasSelection);
        });
    }

    [Fact]
    public Task PointerPosition_MapsToExpectedCellCoordinates()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out _, out _);

            Assert.True(control.TryGetCellFromPointForTests(control.GetCellCenter(4, 2), includeOutsideBounds: true, out var col, out var row));
            Assert.Equal(4, col);
            Assert.Equal(2, row);

            Assert.True(control.TryGetCellFromPointForTests(new Point(-20, -10), includeOutsideBounds: true, out col, out row));
            Assert.Equal(0, col);
            Assert.Equal(0, row);
        });
    }

    [Fact]
    public Task ContextRequested_RaisesWithSelectionStateAndPointerPosition()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out _);
            TerminalSamples.LoadSelectionSample(model);
            control.HandleSelectionPressed(control.GetCellCenter(0, 0), KeyModifiers.None, clickCount: 1);
            control.HandleSelectionMoved(control.GetCellCenter(8, 0));
            control.HandleSelectionReleased(control.GetCellCenter(8, 0), KeyModifiers.None, clickCount: 1);

            TerminalContextRequestedEventArgs? raised = null;
            control.ContextRequested += (_, args) => raised = args;

            var point = new Point(42, 18);
            control.SimulatePointerReleased(point, MouseButton.Right);

            Assert.NotNull(raised);
            Assert.True(raised.HasSelection);
            Assert.Equal("Avalonia", raised.SelectedText);
            Assert.Equal(point, raised.Position);
        });
    }

    [Fact]
    public Task ContextRequested_RaisesWithoutSelection()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out _, out _);

            TerminalContextRequestedEventArgs? raised = null;
            control.ContextRequested += (_, args) => raised = args;

            var point = new Point(12, 9);
            control.SimulatePointerReleased(point, MouseButton.Right);

            Assert.NotNull(raised);
            Assert.False(raised.HasSelection);
            Assert.Equal(string.Empty, raised.SelectedText);
            Assert.Equal(point, raised.Position);
        });
    }

    [Fact]
    public Task RightClickAction_CopyOrPaste_CopiesSelection()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out _);
            control.RightClickAction = RightClickAction.CopyOrPaste;

            string? clipboardText = null;
            control.ClipboardTextWriterOverride = text =>
            {
                clipboardText = text;
                return Task.CompletedTask;
            };

            TerminalSamples.LoadSelectionSample(model);
            control.HandleSelectionPressed(control.GetCellCenter(0, 0), KeyModifiers.None, clickCount: 1);
            control.HandleSelectionMoved(control.GetCellCenter(8, 0));
            control.HandleSelectionReleased(control.GetCellCenter(8, 0), KeyModifiers.None, clickCount: 1);

            control.SimulatePointerReleased(new Point(18, 12), MouseButton.Right);

            Assert.Equal("Avalonia", clipboardText);
        });
    }

    [Fact]
    public Task RightClickAction_CopyOrPaste_PastesWhenNothingSelected()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out _);
            control.RightClickAction = RightClickAction.CopyOrPaste;
            control.ClipboardTextReaderOverride = () => Task.FromResult<string?>("pwd");

            byte[]? sent = null;
            model.UserInput += bytes => sent = bytes;

            control.SimulatePointerReleased(new Point(18, 12), MouseButton.Right);

            Assert.NotNull(sent);
            Assert.Equal("pwd", Encoding.UTF8.GetString(sent!));
        });
    }

    [Fact]
    public Task RightClickAction_None_DoesNotRaiseContextRequest()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out _, out _);
            control.RightClickAction = RightClickAction.None;

            var wasRaised = false;
            control.ContextRequested += (_, _) => wasRaised = true;

            control.SimulatePointerReleased(new Point(18, 12), MouseButton.Right);

            Assert.False(wasRaised);
        });
    }

    [Fact]
    public Task CopySelectionAsync_WritesSelectedTextToClipboard()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out _);
            string? clipboardText = null;
            control.ClipboardTextWriterOverride = text =>
            {
                clipboardText = text;
                return Task.CompletedTask;
            };

            TerminalSamples.LoadSelectionSample(model);
            control.HandleSelectionPressed(control.GetCellCenter(0, 0), KeyModifiers.None, clickCount: 1);
            control.HandleSelectionMoved(control.GetCellCenter(8, 0));
            control.HandleSelectionReleased(control.GetCellCenter(8, 0), KeyModifiers.None, clickCount: 1);

            control.CopySelectionAsync().GetAwaiter().GetResult();

            Assert.Equal("Avalonia", clipboardText);
        });
    }

    [Fact]
    public Task CopySelectionAsync_NoSelection_DoesNothing()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out _, out _);
            var clipboardText = "existing";
            control.ClipboardTextWriterOverride = text =>
            {
                clipboardText = text;
                return Task.CompletedTask;
            };

            control.CopySelectionAsync().GetAwaiter().GetResult();

            Assert.Equal("existing", clipboardText);
        });
    }

    [Fact]
    public Task PasteFromClipboardAsync_ReadsClipboardAndSendsInput()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out _);
            control.ClipboardTextReaderOverride = () => Task.FromResult<string?>("echo hi");

            byte[]? sent = null;
            model.UserInput += bytes => sent = bytes;

            control.PasteFromClipboardAsync().GetAwaiter().GetResult();

            Assert.NotNull(sent);
            Assert.Equal("echo hi", Encoding.UTF8.GetString(sent!));
        });
    }

    [Fact]
    public Task PointerWheel_RepeatedScrollingAcrossScrollSample_DoesNotThrowAndStaysInBounds()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out var scrollBar);
            PopulateScrollback(model);

            var maxScrollback = model.MaxScrollback;

            for (var i = 0; i < maxScrollback + 10; i++)
            {
                control.SimulatePointerWheel(new Vector(0, 1));
            }

            Assert.Equal(0, model.ScrollOffset);
            Assert.Equal(0, (int)scrollBar.Value);

            for (var i = 0; i < maxScrollback + 10; i++)
            {
                control.SimulatePointerWheel(new Vector(0, -1));
            }

            Assert.Equal(maxScrollback, model.ScrollOffset);
            Assert.Equal(maxScrollback, (int)scrollBar.Value);
        });
    }

    [Fact]
    public Task ScrollBar_ValueChangeScrollsTerminalViewport()
    {
        return RunInHeadlessSession(() =>
        {
            _ = CreateControl(out var model, out var scrollBar);
            PopulateScrollback(model);

            var target = Math.Max(0, model.MaxScrollback / 2);

            scrollBar.Value = target;

            Assert.Equal(target, model.ScrollOffset);
            Assert.Equal(target, (int)scrollBar.Value);
            Assert.True(scrollBar.IsEnabled);
        });
    }

    [Fact]
    public Task ScrollBar_HidesInAlternateScreenAndAutoHidesOtherwise()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out var scrollBar);

            Assert.True(scrollBar.AllowAutoHide);
            Assert.Equal(ScrollBarVisibility.Auto, scrollBar.Visibility);

            model.Feed("\u001b[?1049h");

            Assert.Equal(ScrollBarVisibility.Hidden, scrollBar.Visibility);
            Assert.False(scrollBar.AllowAutoHide);
            Assert.True(control.ColumnDefinitions[1].Width.IsAbsolute);
            Assert.Equal(0, control.ColumnDefinitions[1].Width.Value);

            model.Feed("\u001b[?1049l");

            Assert.Equal(ScrollBarVisibility.Auto, scrollBar.Visibility);
            Assert.True(scrollBar.AllowAutoHide);
            Assert.True(control.ColumnDefinitions[1].Width.IsAuto);
        });
    }

    [Fact]
    public Task PageUp_ScrollsTerminalViewportByOnePage()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out var scrollBar);
            PopulateScrollback(model);

            var before = model.ScrollOffset;

            control.SimulateKeyUp(Key.PageUp);

            Assert.True(before > 0);
            Assert.Equal(Math.Max(0, before - model.Terminal.Rows), model.ScrollOffset);
            Assert.Equal(model.ScrollOffset, (int)scrollBar.Value);
        });
    }

    [Fact]
    public Task PageDown_ScrollsTerminalViewportByOnePage()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out var scrollBar);
            PopulateScrollback(model);

            control.SimulateKeyUp(Key.PageUp);
            var before = model.ScrollOffset;

            control.SimulateKeyUp(Key.PageDown);

            Assert.True(before >= 0);
            Assert.Equal(Math.Min(model.MaxScrollback, before + model.Terminal.Rows), model.ScrollOffset);
            Assert.Equal(model.ScrollOffset, (int)scrollBar.Value);
        });
    }

    [Fact]
    public Task SpecialKeys_UseEngineGeneratedSequencesInNormalMode()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out _);
            var sent = new List<byte[]>();
            model.UserInput += bytes => sent.Add(bytes.ToArray());

            control.SimulateKeyUp(Key.Up);
            control.SimulateKeyUp(Key.Delete);
            control.SimulateKeyUp(Key.OemBackTab);
            control.SimulateKeyUp(Key.F10);

            var payloads = sent.Select(Encoding.UTF8.GetString).ToArray();

            Assert.Contains("\u001b[A", payloads);
            Assert.Contains("\u001b[3~", payloads);
            Assert.Contains("\u001b[Z", payloads);
            Assert.Contains("\u001b[21~", payloads);
        });
    }

    [Fact]
    public Task ArrowKeys_UseEngineApplicationCursorSequencesWhenEnabled()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out _);
            var sent = new List<byte[]>();
            model.UserInput += bytes => sent.Add(bytes.ToArray());

            model.Feed("\u001b[?1h");
            control.SimulateKeyUp(Key.Up);
            control.SimulateKeyUp(Key.Home);

            var payloads = sent.Select(Encoding.UTF8.GetString).ToArray();

            Assert.Contains("\u001bOA", payloads);
            Assert.Contains("\u001b[H", payloads);
        });
    }

    [Fact]
    public Task PageKeys_UseEngineSequencesWhenApplicationCursorEnabled()
    {
        return RunInHeadlessSession(() =>
        {
            var control = CreateControl(out var model, out _);
            var sent = new List<byte[]>();
            model.UserInput += bytes => sent.Add(bytes.ToArray());

            model.Feed("\u001b[?1h");
            control.SimulateKeyUp(Key.PageUp);
            control.SimulateKeyUp(Key.PageDown);

            var payloads = sent.Select(Encoding.UTF8.GetString).ToArray();

            Assert.Contains("\u001b[5~", payloads);
            Assert.Contains("\u001b[6~", payloads);
        });
    }

    [Fact]
    public void ShellSample_NormalizesTerminalEnterForRedirectedInput()
    {
        var normalized = ShellControl.NormalizeStandardInput([0x0D]);

        Assert.Equal(Encoding.UTF8.GetBytes(Environment.NewLine), normalized);
    }

    [Fact]
    public void ShellSample_CreateShellSession_UsesRedirectedSessionOffWindows()
    {
        var redirected = new FakeShellSession();
        var session = ShellControl.CreateShellSession(
            new ShellLaunchConfiguration("sh", ["-i"], "sh"),
            isWindowsOverride: false,
            redirectedFactory: _ => redirected,
            conPtyFactory: _ => throw new InvalidOperationException("ConPTY should not be created."));

        Assert.Same(redirected, session);
        Assert.True(redirected.Started);
    }

    [Fact]
    public void ShellSample_CreateShellSession_UsesConPtyOnWindows()
    {
        var conPty = new FakeShellSession();
        var session = ShellControl.CreateShellSession(
            new ShellLaunchConfiguration("pwsh.exe", ["-NoLogo"], "pwsh.exe"),
            isWindowsOverride: true,
            redirectedFactory: _ => throw new InvalidOperationException("Redirected fallback should not be used."),
            conPtyFactory: _ => conPty);

        Assert.Same(conPty, session);
        Assert.True(conPty.Started);
    }

    [Fact]
    public void ShellSample_CreateShellSession_FallsBackWhenConPtyUnavailable()
    {
        var redirected = new FakeShellSession();
        var session = ShellControl.CreateShellSession(
            new ShellLaunchConfiguration("pwsh.exe", ["-NoLogo"], "pwsh.exe"),
            isWindowsOverride: true,
            redirectedFactory: _ => redirected,
            conPtyFactory: _ => new ThrowingShellSession(new PlatformNotSupportedException("ConPTY unavailable")));

        Assert.Same(redirected, session);
        Assert.True(redirected.Started);
    }

    [Fact]
    public void ShellSample_ShouldApplyShellResize_NormalizesAndDeduplicates()
    {
        var first = ShellControl.ShouldApplyShellResize(null, 0, -5, out var normalizedFirst);
        var second = ShellControl.ShouldApplyShellResize(normalizedFirst, 1, 1, out var normalizedSecond);
        var third = ShellControl.ShouldApplyShellResize(normalizedSecond, 2, 3, out var normalizedThird);

        Assert.True(first);
        Assert.Equal((1, 1), normalizedFirst);
        Assert.False(second);
        Assert.Equal((1, 1), normalizedSecond);
        Assert.True(third);
        Assert.Equal((2, 3), normalizedThird);
    }

    private static TestableTerminalControl CreateControl(
        out TerminalControlModel model,
        out ScrollBar scrollBar)
    {
        model = new TerminalControlModel();
        var control = new TestableTerminalControl
        {
            Width = 320,
            Height = 120,
            Model = model,
        };

        scrollBar = Assert.IsType<ScrollBar>(control.Children.OfType<ScrollBar>().Single());
        control.Measure(new Size(320, 120));
        control.Arrange(new Rect(0, 0, 320, 120));
        return control;
    }

    private static void PopulateScrollback(TerminalControlModel model)
    {
        TerminalSamples.LoadScrollSample(model);
        Assert.True(model.CanScroll);
        Assert.True(model.ScrollOffset > 0);
    }

    private static void RestoreResource(Avalonia.Application application, string key, bool hadPrevious, object? previous)
    {
        if (hadPrevious)
        {
            application.Resources[key] = previous;
        }
        else
        {
            application.Resources.Remove(key);
        }
    }

    private sealed class TestableTerminalControl : TerminalControl
    {
        public void SimulateKeyUp(Key key, KeyModifiers modifiers = KeyModifiers.None, string keySymbol = "")
        {
            var args = new KeyEventArgs
            {
                Key = key,
                KeyModifiers = modifiers,
                KeySymbol = keySymbol,
            };

            OnKeyUp(args);
        }

        public void SimulatePointerWheel(Vector delta)
        {
            var pointer = new Pointer(0, PointerType.Mouse, isPrimary: true);
            var args = new PointerWheelEventArgs(this, pointer, this, new Point(10, 10), 0, PointerPointProperties.None, KeyModifiers.None, delta);
            OnPointerWheelChanged(args);
        }

        public void SimulatePointerPressed(Point point, int clickCount, MouseButton button = MouseButton.Left, KeyModifiers modifiers = KeyModifiers.None)
        {
            var pointer = new Pointer(0, PointerType.Mouse, isPrimary: true);
            var (rawModifiers, updateKind) = button switch
            {
                MouseButton.Middle => (RawInputModifiers.MiddleMouseButton, PointerUpdateKind.MiddleButtonPressed),
                MouseButton.Right => (RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed),
                _ => (RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            };
            var properties = new PointerPointProperties(rawModifiers, updateKind);
            var args = new PointerPressedEventArgs(this, pointer, this, point, 0, properties, modifiers, clickCount);
            OnPointerPressed(args);
        }

        public void SimulatePointerMoved(Point point, bool isLeftButtonPressed, KeyModifiers modifiers = KeyModifiers.None)
        {
            var pointer = new Pointer(0, PointerType.Mouse, isPrimary: true);
            var rawModifiers = isLeftButtonPressed ? RawInputModifiers.LeftMouseButton : RawInputModifiers.None;
            var properties = new PointerPointProperties(rawModifiers, PointerUpdateKind.Other);
            var args = new PointerEventArgs(InputElement.PointerMovedEvent, this, pointer, this, point, 0, properties, modifiers);
            OnPointerMoved(args);
        }

        public void SimulatePointerReleased(Point point, MouseButton button = MouseButton.Left, KeyModifiers modifiers = KeyModifiers.None)
        {
            var pointer = new Pointer(0, PointerType.Mouse, isPrimary: true);
            var updateKind = button switch
            {
                MouseButton.Middle => PointerUpdateKind.MiddleButtonReleased,
                MouseButton.Right => PointerUpdateKind.RightButtonReleased,
                _ => PointerUpdateKind.LeftButtonReleased,
            };
            var properties = new PointerPointProperties(RawInputModifiers.None, updateKind);
            var args = new PointerReleasedEventArgs(this, pointer, this, point, 0, properties, modifiers, button);
            OnPointerReleased(args);
        }
    }

    private sealed class FakeShellSession : IShellSession
    {
        public bool Started { get; private set; }

        public event Action<byte[]>? DataReceived;

        public event Action<int>? Exited;

        public void Start()
        {
            Started = true;
        }

        public void Send(byte[] input)
        {
        }

        public void Resize(int cols, int rows)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingShellSession(Exception exception) : IShellSession
    {
        private readonly Exception _exception = exception;

        public event Action<byte[]>? DataReceived;

        public event Action<int>? Exited;

        public void Start()
        {
            throw _exception;
        }

        public void Send(byte[] input)
        {
        }

        public void Resize(int cols, int rows)
        {
        }

        public void Dispose()
        {
        }
    }

}
