using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using SvcSystems.UI.Terminal;
using SvcSystems.UI.Terminal.Samples;
using Xunit;

namespace SvcSystems.UI.Terminal.Tests;

/// <summary>
/// Diagnostic that isolates the transport from the terminal model. It runs the pinned WSL
/// <c>ucs-detect</c> twice against the same headless <see cref="TerminalControlModel"/>:
/// once over a direct POSIX PTY (exactly what <c>UcsDetectIntegrationTests</c> measures) and
/// once through Windows ConPTY (exactly what the desktop sample's local shell uses).
/// The report diff shows that the capabilities the control advertises are stripped by ConPTY
/// rather than missing from the terminal engine.
/// </summary>
/// <remarks>
/// Opt in with <c>UCS_DETECT_CONPTY_COMPARE=1</c>. Windows with the provisioned WSL Ubuntu
/// environment and the pinned <c>tests/requirements-ucs-detect.txt</c> dependencies is required;
/// the run is skipped otherwise so it never affects normal or CI runs.
/// </remarks>
public sealed class UcsDetectConPtyComparisonTests : AvaloniaTestBase
{
    private const string EnableVariable = "UCS_DETECT_CONPTY_COMPARE";
    private const string WslDistribution = "Ubuntu";
    private const int Columns = 131;
    private const int Rows = 28;

    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(30);
    private static readonly string[] TerminalFeaturesToCompare =
    [
        "sixel",
        "kitty_graphics",
        "kitty_clipboard_protocol",
        "kitty_query",
        "kitty_notifications",
        "kitty_keyboard",
        "kitty_pointer_shapes",
        "styled_underlines",
        "colored_underlines",
        "decrqcra",
        "decrqss",
        "decrqss_truecolor_probe",
        "osc52_clipboard",
        "iterm2_graphics",
        "iterm2_features",
        "text_sizing",
        "tofu",
        "color_scheme",
        "xtgettcap",
        "device_attributes",
        "modes",
    ];
    private static readonly string[] UnicodeCategoriesToCompare =
    [
        "unicode_wide_results",
        "narrow_results",
        "emoji_vs16_results",
        "emoji_vs15_results",
        "emoji_zwj_results",
        "ri_results",
        "sri_results",
        "sfz_results",
        "language_results",
    ];

    private readonly ITestOutputHelper _output;

    public UcsDetectConPtyComparisonTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task ConPtyTransportStripsCapabilitiesReportedByTheDirectPtyHarness()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("ConPTY is only available on Windows.");
        }

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnableVariable)))
        {
            Assert.Skip($"Set {EnableVariable}=1 to run the ConPTY versus direct-PTY comparison diagnostic.");
        }

        // The baseline is the exact measurement the conformance suite makes: ucs-detect attached
        // to the headless control through a direct POSIX PTY with no ConPTY in between.
        UcsDetectTerminalFixture direct = new();
        await direct.InitializeAsync();
        string conptyReportPath = Path.Combine(
            Path.GetDirectoryName(direct.ReportPath)!,
            $"conpty-compare-{DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)}.json");

        try
        {
            SortedDictionary<string, string> directSummary = Summarize(direct.Report);
            using JsonDocument conptyReport = await RunConPtyProbeAsync(conptyReportPath);
            SortedDictionary<string, string> conptySummary = Summarize(conptyReport);

            List<string> differences = [];
            foreach ((string feature, string directValue) in directSummary)
            {
                string conptyValue = conptySummary[feature];
                if (!string.Equals(directValue, conptyValue, StringComparison.Ordinal))
                {
                    differences.Add($"{feature}: direct={directValue}  conpty={conptyValue}");
                }
            }

            List<string> report = [$"Direct PTY report: {direct.ReportPath}", $"ConPTY report:     {conptyReportPath}", string.Empty];
            report.Add("feature                                   direct PTY            ConPTY");
            report.Add("--------------------------------------------------------------------------------");
            foreach (string feature in directSummary.Keys)
            {
                report.Add($"{feature,-40}  {directSummary[feature],-20}  {conptySummary[feature]}");
            }

            if (differences.Count > 0)
            {
                report.Add(string.Empty);
                report.Add("differences (transport-sensitive features):");
                report.AddRange(differences);
            }

            foreach (string line in report)
            {
                _output.WriteLine(line);
            }

            await File.WriteAllLinesAsync(
                conptyReportPath + ".compare.txt",
                report,
                TestContext.Current.CancellationToken);

            // The control itself must be fully capable; the conformance suite already asserts this,
            // but re-check the two strongest signals so the diff cannot be dismissed.
            Assert.True(
                string.Equals(directSummary["kitty_graphics"], "true", StringComparison.Ordinal),
                "The direct PTY baseline must report kitty graphics support.");
            Assert.Contains("supported=true", directSummary["xtgettcap"], StringComparison.Ordinal);

            // Proof: the same control, measured through ConPTY, loses capabilities it advertises
            // when attached directly. ConPTY is therefore the transport that strips them.
            bool stripsXtGetTcap = !string.Equals(
                directSummary["xtgettcap"], conptySummary["xtgettcap"], StringComparison.Ordinal);
            bool stripsKittyGraphics = !string.Equals(
                directSummary["kitty_graphics"], conptySummary["kitty_graphics"], StringComparison.Ordinal);
            Assert.True(
                stripsXtGetTcap || stripsKittyGraphics,
                "Expected ConPTY to strip at least one capability the control advertises on the " +
                $"direct PTY path, but the reports agreed.{Environment.NewLine}" +
                string.Join(Environment.NewLine, differences));
        }
        finally
        {
            await direct.DisposeAsync();
        }
    }

    private async Task<JsonDocument> RunConPtyProbeAsync(string windowsReportPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(windowsReportPath)!);
        string wslReportPath = ToWslPath(windowsReportPath);

        TerminalControlModel model = await RunInHeadlessSession(() =>
        {
            TerminalControlModel created = new(new TerminalOptions
            {
                Cols = Columns,
                Rows = Rows,
                CellWidthPixels = 21,
                CellHeightPixels = 52,
                ReflowOnResize = false,
                ClipboardReadEnabled = true,
            });
            TerminalControl control = new() { Model = created };
            control.ClipboardTextReaderOverride = () => Task.FromResult<string?>(string.Empty);
            control.ClipboardTextWriterOverride = _ => Task.CompletedTask;
            Window window = new() { Width = 800, Height = 600, Content = control };
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            created.Terminal.Resize(Columns, Rows);
            created.Terminal.SetCellSizePixels(21, 52);

            // Held for teardown on the headless UI thread.
            _conptyControl = control;
            _conptyWindow = window;
            return created;
        });

        // Mirrors the documented way to probe the sample with the Linux VT-query backend.
        string command = string.Join(
            " ",
            "ucs-detect",
            "--test-only all",
            "--all",
            "--terminal-full-probe",
            "--detect-all-dec-modes",
            "--probe-silently",
            "--limit-category-time 0",
            "--limit-errors 0",
            "--timeout-cps 3.0",
            "--timeout-query auto",
            $"--save-json {wslReportPath}",
            "--no-final-summary");

        WindowsConPtyShellSession session = new(
            new ShellLaunchConfiguration(
                "wsl.exe",
                ["--distribution", WslDistribution, "--exec", "sh", "-lc", command],
                "ucs-detect via WSL over ConPTY"),
            initialCols: Columns,
            initialRows: Rows);

        TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentQueue<Task> feeds = new();
        EventHandler<TerminalUserInputEventArgs> onUserInput = (_, args) => session.Send(args.Data.ToArray());
        session.DataReceived += data =>
        {
            byte[] chunk = data.ToArray();
            feeds.Enqueue(RunInHeadlessSession(() =>
            {
                model.Feed(chunk, chunk.Length);
                Dispatcher.UIThread.RunJobs();
            }));
        };
        session.Exited += code => exited.TrySetResult(code);
        model.UserInput += onUserInput;

        try
        {
            session.Start();
            int exitCode = await exited.Task.WaitAsync(RunTimeout, TestContext.Current.CancellationToken);
            await Task.WhenAll(feeds);
            Assert.True(exitCode == 0, $"ucs-detect over ConPTY exited with code {exitCode}.");
            Assert.True(
                File.Exists(windowsReportPath),
                $"ucs-detect over ConPTY did not write a report at {windowsReportPath}.");
            return JsonDocument.Parse(
                await File.ReadAllTextAsync(windowsReportPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            model.UserInput -= onUserInput;
            session.Dispose();
            await RunInHeadlessSession(() =>
            {
                _conptyWindow?.Close();
                _conptyWindow = null;
                _conptyControl?.Model = null;
                _conptyControl = null;
                model.Dispose();
            });
        }
    }

    private TerminalControl? _conptyControl;
    private Window? _conptyWindow;

    private static SortedDictionary<string, string> Summarize(JsonDocument report)
    {
        JsonElement terminal = report.RootElement.GetProperty("terminal_results");
        SortedDictionary<string, string> summary = new(StringComparer.Ordinal);
        foreach (string feature in TerminalFeaturesToCompare)
        {
            summary[feature] = terminal.TryGetProperty(feature, out JsonElement value)
                ? DescribeTerminalFeature(feature, value)
                : "<absent>";
        }

        JsonElement results = report.RootElement.GetProperty("test_results");
        foreach (string category in UnicodeCategoriesToCompare)
        {
            summary[category] = results.TryGetProperty(category, out JsonElement entries) &&
                entries.ValueKind == JsonValueKind.Object
                ? $"min pct_success={MinimumPctSuccess(entries)}"
                : "<absent>";
        }

        return summary;
    }

    private static string DescribeTerminalFeature(string feature, JsonElement value)
    {
        return feature switch
        {
            "xtgettcap" => $"supported={ReadBool(value, "supported")}, caps={CountObjectProperty(value, "capabilities")}",
            "iterm2_features" => $"supported={ReadBool(value, "supported")}, features={CountObjectProperty(value, "features")}",
            "device_attributes" => $"service_class={ReadNumber(value, "service_class")}, extensions={CountArrayProperty(value, "extensions")}",
            "kitty_keyboard" => $"supported_flags={ReadNumber(value, "supported_flags")}",
            "text_sizing" => $"width={ReadBool(value, "width")}, scale={ReadBool(value, "scale")}",
            "modes" => DescribeModes(value),
            _ => value.ValueKind switch
            {
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Object => $"supported={ReadBool(value, "supported")}",
                _ => value.ToString(),
            },
        };
    }

    private static string DescribeModes(JsonElement modes)
    {
        if (modes.ValueKind != JsonValueKind.Object)
        {
            return modes.ToString();
        }

        int total = 0;
        int supported = 0;
        foreach (JsonProperty mode in modes.EnumerateObject())
        {
            total++;
            if (mode.Value.ValueKind == JsonValueKind.Object &&
                mode.Value.TryGetProperty("supported", out JsonElement flag) &&
                flag.ValueKind == JsonValueKind.True)
            {
                supported++;
            }
        }

        return $"supported={supported}/{total}";
    }

    private static int MinimumPctSuccess(JsonElement entries)
    {
        double minimum = 100;
        bool any = false;
        foreach (JsonProperty entry in entries.EnumerateObject())
        {
            if (entry.Value.ValueKind == JsonValueKind.Object &&
                entry.Value.TryGetProperty("pct_success", out JsonElement pct) &&
                pct.ValueKind == JsonValueKind.Number)
            {
                minimum = Math.Min(minimum, pct.GetDouble());
                any = true;
            }
        }

        return any ? (int)Math.Round(minimum) : -1;
    }

    private static string ReadBool(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out JsonElement value))
        {
            return "<n/a>";
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.ToString(),
        };
    }

    private static string ReadNumber(JsonElement parent, string name)
    {
        return parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(name, out JsonElement value) &&
            value.ValueKind == JsonValueKind.Number
            ? value.ToString()
            : "<n/a>";
    }

    private static int CountObjectProperty(JsonElement parent, string name)
    {
        return parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(name, out JsonElement value) &&
            value.ValueKind == JsonValueKind.Object
            ? value.EnumerateObject().Count()
            : 0;
    }

    private static int CountArrayProperty(JsonElement parent, string name)
    {
        return parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(name, out JsonElement value) &&
            value.ValueKind == JsonValueKind.Array
            ? value.GetArrayLength()
            : 0;
    }

    private static string ToWslPath(string windowsPath)
    {
        string fullPath = Path.GetFullPath(windowsPath);
        if (fullPath.Length < 3 || fullPath[1] != ':' || fullPath[2] is not ('\\' or '/'))
        {
            throw new InvalidOperationException($"The report path cannot be mapped into WSL: {fullPath}");
        }

        string drive = char.ToLowerInvariant(fullPath[0]).ToString(CultureInfo.InvariantCulture);
        string relative = fullPath[3..].Replace('\\', '/');
        return $"/mnt/{drive}/{relative}";
    }
}
