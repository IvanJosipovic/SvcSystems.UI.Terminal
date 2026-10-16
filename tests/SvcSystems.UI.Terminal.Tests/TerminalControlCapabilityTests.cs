using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia;
using Avalonia.Threading;
using SvcSystems.UI.Terminal;
using System.Text;
using XTerm.Common;
using Xunit;

namespace SvcSystems.UI.Terminal.Tests;

public sealed class TerminalControlCapabilityTests : AvaloniaTestBase
{
    private const string Esc = "\u001b";
    private const string St = Esc + "\\";

    [Fact]
    public Task PointerShapeRequests_ChangeAndRestoreTheControlCursor()
    {
        return RunInHeadlessSession(() =>
        {
            using TerminalControlModel model = new();
            TerminalControl control = new()
            {
                Model = model,
                Cursor = new Cursor(StandardCursorType.Cross),
            };

            model.Feed($"{Esc}]22;pointer\u0007");
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("Hand", control.Cursor?.ToString());

            model.Feed($"{Esc}]22;\u0007");
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("Cross", control.Cursor?.ToString());
        });
    }

    [Fact]
    public Task KittyNotificationRequests_ArePublishedOnTheAvaloniaDispatcher()
    {
        return RunInHeadlessSession(() =>
        {
            using TerminalControlModel model = new();
            TerminalControl control = new() { Model = model };
            TerminalNotificationEventArgs? notification = null;
            bool receivedOnUiThread = false;
            control.NotificationRequested += (_, e) =>
            {
                notification = e;
                receivedOnUiThread = Dispatcher.UIThread.CheckAccess();
            };

            model.Feed($"{Esc}]99;i=build:p=title;Build complete{St}");
            Dispatcher.UIThread.RunJobs();

            Assert.NotNull(notification);
            Assert.Equal("build", notification.Identifier);
            Assert.Equal("Build complete", notification.Title);
            Assert.True(receivedOnUiThread);
        });
    }

    [Fact]
    public Task XtGetTcapReplies_AreForwardedToTheConnectedProcess()
    {
        return RunInHeadlessSession(() =>
        {
            using TerminalControlModel model = new();
            List<string> replies = [];
            model.UserInput += (_, e) => replies.Add(Encoding.UTF8.GetString(e.Data.Span));
            string[] encodedNames = new[] { "Ms", "RGB", "Su", "not-a-capability" }
                .Select(static name => Convert.ToHexString(Encoding.ASCII.GetBytes(name)))
                .ToArray();
            string names = string.Join(';', encodedNames);

            model.Feed($"{Esc}P+q{names}{St}");

            Assert.Equal(4, replies.Count);
            Assert.StartsWith($"{Esc}P1+r4D73=", replies[0], StringComparison.Ordinal);
            Assert.StartsWith($"{Esc}P1+r524742=", replies[1], StringComparison.Ordinal);
            Assert.StartsWith($"{Esc}P1+r5375=", replies[2], StringComparison.Ordinal);
            Assert.StartsWith($"{Esc}P0+", replies[3], StringComparison.Ordinal);
        });
    }

    [Fact]
    public Task KittyTextSizing_RendersTheEngineSizedRun()
    {
        return RunInHeadlessSession(() =>
        {
            using TerminalControlModel model = new();
            TerminalControl control = new() { Model = model, Width = 240, Height = 120 };
            control.Measure(new Size(240, 120));
            control.Arrange(new Rect(0, 0, 240, 120));

            model.Feed($"{Esc}]66;s=2;X{St}");

            ViewportTextRun sizedRun = Assert.Single(model.ViewportRows[0].Runs, static run => !run.Sizing.IsDefault);
            Assert.Equal("X", sizedRun.Text);
            Assert.Equal(2, sizedRun.Sizing.Scale);

            using RenderTargetBitmap bitmap = new(new PixelSize(240, 120));
            bitmap.Render(control);
        });
    }

    [Theory]
    [InlineData("4:1", UnderlineStyle.Single)]
    [InlineData("4:2", UnderlineStyle.Double)]
    [InlineData("4:3", UnderlineStyle.Curly)]
    [InlineData("4:4", UnderlineStyle.Dotted)]
    [InlineData("4:5", UnderlineStyle.Dashed)]
    public Task SgrUnderlineStylesAndColorReachTheRenderer(string sgr, UnderlineStyle expectedStyle)
    {
        return RunInHeadlessSession(() =>
        {
            using TerminalControlModel model = new();
            TerminalControl control = new() { Model = model, Width = 160, Height = 64 };
            control.Measure(new Size(160, 64));
            control.Arrange(new Rect(0, 0, 160, 64));

            model.Feed($"{Esc}[{sgr};58:2::255:0:0;53mA");
            ViewportTextRun run = Assert.Single(model.ViewportRows[0].Runs, static item => item.StartColumn == 0);

            Assert.Equal(expectedStyle, run.UnderlineStyle);
            Assert.Equal(0xFF0000, run.UnderlineColor);
            Assert.True(run.Overline);

            using RenderTargetBitmap bitmap = new(new PixelSize(160, 64));
            bitmap.Render(control);
            Assert.True(control.CanRenderTextForTests);
            bitmap.Save(@"C:\Users\ivanj\AppData\Local\Temp\opencode\underline-style.png", PngBitmapEncoderOptions.Default);
            Assert.True(BitmapPixelAssertions.ContainsRedPixel(bitmap), $"The {expectedStyle} underline/overline did not render with its requested color.");
        });
    }
}
