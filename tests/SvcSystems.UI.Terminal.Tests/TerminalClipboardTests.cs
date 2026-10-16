using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using SvcSystems.UI.Terminal;
using System.Text;
using Xunit;

namespace SvcSystems.UI.Terminal.Tests;

public sealed class TerminalClipboardTests : AvaloniaTestBase
{
    private const string Esc = "\u001b";
    private const string Bel = "\u0007";
    private const string St = Esc + "\\";

    [Fact]
    public Task Osc52Write_UpdatesTheHostClipboardThroughTheControl()
    {
        return RunInHeadlessSession(() =>
        {
            using TerminalControlModel model = new(new TerminalOptions { ClipboardWriteEnabled = true });
            TerminalControl control = new() { Model = model };
            string? writtenText = null;
            control.ClipboardTextWriterOverride = text =>
            {
                writtenText = text;
                return Task.CompletedTask;
            };

            model.Feed($"{Esc}]52;c;{ToBase64("written by the application")}{Bel}");
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("written by the application", writtenText);
        });
    }

    [Fact]
    public Task Osc52Write_IsDisabledByDefault()
    {
        return RunInHeadlessSession(() =>
        {
            using TerminalControlModel model = new();
            TerminalControl control = new() { Model = model };
            int writes = 0;
            control.ClipboardTextWriterOverride = _ =>
            {
                writes++;
                return Task.CompletedTask;
            };

            model.Feed($"{Esc}]52;c;{ToBase64("untrusted clipboard data")}{Bel}");
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0, writes);
            Assert.False(model.Terminal.Options.ClipboardWriteEnabled);
        });
    }

    [Fact]
    public Task Osc52Read_UsesTheHostClipboardOnlyWhenEnabled()
    {
        return RunInHeadlessSession(() =>
        {
            using TerminalControlModel model = new(new TerminalOptions { ClipboardReadEnabled = true });
            TerminalControl control = new() { Model = model };
            control.ClipboardTextReaderOverride = () => Task.FromResult<string?>("read from host");
            List<string> responses = [];
            model.UserInput += (_, e) => responses.Add(Encoding.UTF8.GetString(e.Data.Span));

            model.Feed($"{Esc}]52;c;?{Bel}");
            Dispatcher.UIThread.RunJobs();

            Assert.Equal($"{Esc}]52;c;{ToBase64("read from host")}{Bel}", string.Concat(responses));
        });
    }

    [Fact]
    public Task Osc52Read_IsSilentWhenClipboardReadIsNotEnabled()
    {
        return RunInHeadlessSession(() =>
        {
            using TerminalControlModel model = new(new TerminalOptions { ClipboardWriteEnabled = true });
            TerminalControl control = new() { Model = model };
            int readRequests = 0;
            control.ClipboardTextReaderOverride = () =>
            {
                readRequests++;
                return Task.FromResult<string?>("must remain private");
            };
            List<string> responses = [];
            model.UserInput += (_, e) => responses.Add(Encoding.UTF8.GetString(e.Data.Span));

            model.Feed($"{Esc}]52;c;?{Bel}");
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0, readRequests);
            Assert.Empty(responses);
        });
    }

    [Fact]
    public Task KittyClipboardRead_ReturnsTheRequestedTextMimeTypeWhenEnabled()
    {
        return RunInHeadlessSession(async () =>
        {
            using TerminalControlModel model = new(new TerminalOptions { ClipboardReadEnabled = true });
            TerminalControl control = new() { Model = model };
            control.ClipboardTextReaderOverride = () => Task.FromResult<string?>("clipboard text");
            StringBuilder response = new();
            TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            model.UserInput += (_, e) =>
            {
                response.Append(Encoding.UTF8.GetString(e.Data.Span));
                if (response.ToString().Contains("type=read:status=DONE", StringComparison.Ordinal))
                {
                    completed.TrySetResult();
                }
            };

            model.Feed($"{Esc}]5522;type=read:loc=clipboard;{ToBase64("text/plain")}{St}");
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Contains("type=read:status=OK", response.ToString(), StringComparison.Ordinal);
            Assert.Contains(
                $"type=read:status=DATA:mime={ToBase64("text/plain")};{ToBase64("clipboard text")}",
                response.ToString(),
                StringComparison.Ordinal);
            Assert.Contains("type=read:status=DONE", response.ToString(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public Task KittyClipboardWrite_SelectsTextPlainFromTheWholeMimeTransfer()
    {
        return RunInHeadlessSession(() =>
        {
            using TerminalControlModel model = new(new TerminalOptions { ClipboardWriteEnabled = true });
            TerminalControl control = new() { Model = model };
            string? writtenText = null;
            control.ClipboardTextWriterOverride = text =>
            {
                writtenText = text;
                return Task.CompletedTask;
            };

            model.Feed(BuildKittyWrite(("image/png", "image bytes"), ("text/plain", "plain text")));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("plain text", writtenText);
        });
    }

    [Fact]
    public Task KittyClipboardWrite_DoesNotMapPrimarySelectionOntoTheSystemClipboard()
    {
        return RunInHeadlessSession(() =>
        {
            using TerminalControlModel model = new(new TerminalOptions { ClipboardWriteEnabled = true });
            TerminalControl control = new() { Model = model };
            int writes = 0;
            control.ClipboardTextWriterOverride = _ =>
            {
                writes++;
                return Task.CompletedTask;
            };

            model.Feed($"{Esc}]52;p;{ToBase64("primary selection")}{Bel}");
            model.Feed(BuildKittyWrite(("text/plain", "primary selection"), location: "primary"));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0, writes);
        });
    }

    [Fact]
    public Task PasteFromClipboard_PreservesPlainTextAndHtmlForKittyMimePaste()
    {
        return RunInHeadlessSession(async () =>
        {
            using TerminalControlModel model = new();
            TerminalControl control = new() { Model = model, Width = 320, Height = 160 };
            Window window = new() { Width = 320, Height = 160, Content = control };
            List<string> sent = [];
            model.UserInput += (_, e) => sent.Add(Encoding.UTF8.GetString(e.Data.Span));

            try
            {
                window.Show();
                window.UpdateLayout();
                DataTransferItem item = new();
                item.Set(DataFormat.Text, "plain text");
                item.Set(DataFormat.CreateStringPlatformFormat("text/html"), "<b>rich text</b>");
                DataTransfer transfer = new();
                transfer.Add(item);
                await TopLevel.GetTopLevel(control)!.Clipboard!.SetDataAsync(transfer);

                model.Feed($"{Esc}[?5522h");
                await control.PasteFromClipboardAsync();
                Dispatcher.UIThread.RunJobs();

                string output = string.Concat(sent);
                string mimeList = ToBase64("text/plain text/html");
                Assert.Contains($"mime=Lg==:pw=", output, StringComparison.Ordinal);
                Assert.Contains(mimeList, output, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static string BuildKittyWrite((string MimeType, string Text) first, (string MimeType, string Text) second)
    {
        StringBuilder sequence = new();
        sequence.Append($"{Esc}]5522;type=write{St}");
        AppendKittyFormat(sequence, first);
        AppendKittyFormat(sequence, second);
        sequence.Append($"{Esc}]5522;type=wdata{St}");
        return sequence.ToString();
    }

    private static string BuildKittyWrite((string MimeType, string Text) format, string location)
    {
        StringBuilder sequence = new();
        sequence.Append($"{Esc}]5522;type=write:loc={location}{St}");
        AppendKittyFormat(sequence, format);
        sequence.Append($"{Esc}]5522;type=wdata{St}");
        return sequence.ToString();
    }

    private static void AppendKittyFormat(StringBuilder sequence, (string MimeType, string Text) format)
    {
        sequence.Append($"{Esc}]5522;type=wdata:mime={ToBase64(format.MimeType)};{ToBase64(format.Text)}{St}");
    }

    private static string ToBase64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
}
