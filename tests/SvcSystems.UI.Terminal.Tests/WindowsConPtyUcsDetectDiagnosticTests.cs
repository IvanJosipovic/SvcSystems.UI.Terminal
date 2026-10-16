using System.Collections.Concurrent;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using SvcSystems.UI.Terminal;
using SvcSystems.UI.Terminal.Samples;
using Xunit;

namespace SvcSystems.UI.Terminal.Tests;

public sealed class WindowsConPtyUcsDetectDiagnosticTests : AvaloniaTestBase
{
    private readonly ITestOutputHelper _output;

    public WindowsConPtyUcsDetectDiagnosticTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task CaptureNativeUcsDetectThroughWindowsConPty()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("This diagnostic requires Windows ConPTY.");
        }

        string? executablePath = Environment.GetEnvironmentVariable("UCS_DETECT_WINDOWS_EXE");
        string? reportPath = Environment.GetEnvironmentVariable("UCS_DETECT_WINDOWS_REPORT");
        if (string.IsNullOrWhiteSpace(executablePath) || string.IsNullOrWhiteSpace(reportPath))
        {
            Assert.Skip("Set UCS_DETECT_WINDOWS_EXE and UCS_DETECT_WINDOWS_REPORT to run this diagnostic.");
        }

        string executable = executablePath;
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        ConcurrentQueue<byte[]> detectorOutput = new();
        ConcurrentQueue<byte[]> terminalReplies = new();

        TerminalControl? control = null;
        Window? window = null;
        TerminalControlModel model = await RunInHeadlessSession(() =>
        {
            TerminalControlModel createdModel = new(new TerminalOptions
            {
                Cols = 120,
                Rows = 30,
                CellWidthPixels = 10,
                CellHeightPixels = 20,
                ReflowOnResize = false,
                ClipboardReadEnabled = true,
            });
            control = new TerminalControl { Model = createdModel };
            window = new Window { Width = 800, Height = 600, Content = control };
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            createdModel.Terminal.Resize(120, 30);
            createdModel.Terminal.SetCellSizePixels(10, 20);
            return createdModel;
        });

        WindowsConPtyShellSession session = new(
            new ShellLaunchConfiguration(
                executable,
                [
                    "--all",
                    "--terminal-full-probe",
                    "--detect-all-dec-modes",
                    "--timeout-cps", "3.0",
                    "--timeout-query", "auto",
                    "--save-json", reportPath,
                    "--no-final-summary",
                ],
                "ucs-detect"),
            initialCols: 120,
            initialRows: 30);

        TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentQueue<Task> feeds = new();
        EventHandler<TerminalUserInputEventArgs> onUserInput = (_, args) =>
        {
            byte[] reply = args.Data.ToArray();
            terminalReplies.Enqueue(reply);
            session.Send(reply);
        };
        session.DataReceived += data =>
        {
            detectorOutput.Enqueue(data.ToArray());
            feeds.Enqueue(RunInHeadlessSession(() =>
            {
                model.Feed(data, data.Length);
                Dispatcher.UIThread.RunJobs();
            }));
        };
        session.Exited += code => exited.TrySetResult(code);
        model.UserInput += onUserInput;

        try
        {
            session.Start();
            int exitCode = await exited.Task.WaitAsync(TimeSpan.FromMinutes(10), TestContext.Current.CancellationToken);
            await Task.WhenAll(feeds);
            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(reportPath), $"Detector report not found: {reportPath}");
            await File.WriteAllBytesAsync(
                reportPath + ".out.bin",
                detectorOutput.SelectMany(static chunk => chunk).ToArray(),
                TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(
                reportPath + ".in.bin",
                terminalReplies.SelectMany(static chunk => chunk).ToArray(),
                TestContext.Current.CancellationToken);

            using JsonDocument report = JsonDocument.Parse(
                await File.ReadAllTextAsync(reportPath, TestContext.Current.CancellationToken));
            JsonElement terminal = report.RootElement.GetProperty("terminal_results");
            _output.WriteLine($"Report: {reportPath}");
            _output.WriteLine($"Detector output bytes: {detectorOutput.Sum(static chunk => chunk.Length)}");
            _output.WriteLine($"Terminal reply bytes: {terminalReplies.Sum(static chunk => chunk.Length)}");
            _output.WriteLine($"Terminal type: {terminal.GetProperty("ttype")}");
            foreach (string name in new[]
            {
                "sixel", "osc52_clipboard", "foreground_color_rgb", "background_color_rgb",
                "modes", "iterm2_features", "kitty_graphics", "xtgettcap", "software_name",
            })
            {
                if (terminal.TryGetProperty(name, out JsonElement value))
                {
                    _output.WriteLine($"{name}: {value}");
                }
            }
        }
        finally
        {
            model.UserInput -= onUserInput;
            session.Dispose();
            await RunInHeadlessSession(() =>
            {
                window!.Close();
                control!.Model = null;
                model.Dispose();
            });
        }
    }
}
