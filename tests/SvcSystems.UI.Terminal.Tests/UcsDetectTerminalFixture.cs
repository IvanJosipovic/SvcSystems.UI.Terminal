using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Headless;
using SvcSystems.UI.Terminal;
using Xunit;

namespace SvcSystems.UI.Terminal.Tests;

public sealed class UcsDetectTerminalFixture : AvaloniaTestBase, IAsyncLifetime
{
    private const int StartFrame = 1;
    private const int TerminalOutputFrame = 1;
    private const int TerminalInputFrame = 2;
    private const int InputClosedFrame = 3;
    private const int CompletedFrame = 2;
    private const int ErrorFrame = 3;
    private const long MaximumControlFrameBytes = 1024 * 1024;
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromSeconds(15);
    private static readonly IReadOnlyDictionary<string, string> ExpectedVersions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ucs_detect"] = "2.3.8",
        ["blessed"] = "1.50.0",
        ["wcwidth"] = "0.9.2",
        ["prettytable"] = "3.18.0",
        ["PyYAML"] = "6.0.3",
        ["requests"] = "2.34.2",
        ["jinxed"] = "2.1.0",
        ["certifi"] = "2026.7.22",
        ["charset-normalizer"] = "3.5.2",
        ["idna"] = "3.20",
        ["urllib3"] = "2.8.0",
        ["colorama"] = "0.4.6",
    };

    private Process? _bridge;
    private bool _bridgeStarted;
    private TerminalControlModel? _model;
    private TerminalControl? _control;
    private Window? _window;
    private EventHandler<TerminalUserInputEventArgs>? _onUserInput;
    private Task<string>? _standardError;

    public JsonDocument Report { get; private set; } = null!;

    public string TerminalInput { get; private set; } = string.Empty;

    public string TerminalOutput { get; private set; } = string.Empty;

    public string ReportPath { get; private set; } = string.Empty;

    public string DependencyVersionsPath { get; private set; } = string.Empty;

    public string TranscriptPath { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        const string helperName = "ucs_detect_pty.py";
        string helperPath = Path.Combine(AppContext.BaseDirectory, helperName);
        if (!File.Exists(helperPath))
        {
            throw new InvalidOperationException(
                $"The ucs-detect helper was not copied to the test output: {helperPath}");
        }

        string artifactDirectory = Environment.GetEnvironmentVariable("UCS_DETECT_ARTIFACTS") ??
            Path.Combine(AppContext.BaseDirectory, "TestResults", "ucs-detect");
        Directory.CreateDirectory(artifactDirectory);
        string runId = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
        string artifactStem = Path.Combine(artifactDirectory, runId);
        ReportPath = artifactStem + ".json";
        DependencyVersionsPath = artifactStem + ".dependencies.json";
        TranscriptPath = artifactStem + ".pty.bin";

        await RunInHeadlessSession(() =>
        {
            _model = new TerminalControlModel(new TerminalOptions
            {
                Cols = 131,
                Rows = 28,
                CellWidthPixels = 21,
                CellHeightPixels = 52,
                ReflowOnResize = false,
                ClipboardReadEnabled = true,
                ClipboardWriteEnabled = true,
            });
            _control = new TerminalControl { Model = _model };
            _control.ClipboardTextReaderOverride = () => Task.FromResult<string?>(string.Empty);
            _control.ClipboardTextWriterOverride = _ => Task.CompletedTask;
            _window = new Window { Width = 800, Height = 600, Content = _control };
            _window.Show();
            _window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            // The Avalonia headless control measures its fallback font differently from the
            // dimensions requested for the PTY. Reapply the deterministic test geometry after
            // the control has been hosted and laid out.
            _model.Terminal.Resize(131, 28);
            _model.Terminal.SetCellSizePixels(21, 52);

            _onUserInput = (_, args) => SendTerminalInput(args.Data.Span);
            _model.UserInput += _onUserInput;
        });

        await RunPtyBridgeAsync(helperPath);
    }

    private async Task RunPtyBridgeAsync(string helperPath)
    {
        ProcessStartInfo startInfo = CreateBridgeStartInfo(helperPath);
        _bridge = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!_bridge.Start())
        {
            throw new InvalidOperationException("Failed to start the ucs-detect PTY bridge.");
        }

        _bridgeStarted = true;
        _standardError = _bridge.StandardError.ReadToEndAsync();
        await WriteFrameAsync(StartFrame, JsonSerializer.SerializeToUtf8Bytes(new
        {
            report_path = ToBridgePath(ReportPath),
            transcript_path = ToBridgePath(TranscriptPath),
            rows = 28,
            cols = 131,
            versions = ExpectedVersions,
        }));

        try
        {
            await RunAsync().WaitAsync(RunTimeout);
        }
        catch (TimeoutException)
        {
            await StopBridgeAsync();
            throw new TimeoutException(
                $"ucs-detect exceeded the {RunTimeout.TotalMinutes:0}-minute test timeout. " +
                $"PTY transcript: {TranscriptPath}. Bridge stderr: {await SafeReadStandardErrorAsync()}");
        }
        catch
        {
            await StopBridgeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Report?.Dispose();
        if (_bridge is not null)
        {
            if (_bridgeStarted && !_bridge.HasExited)
            {
                await StopBridgeAsync();
            }

            _bridge.Dispose();
            _bridge = null;
        }

        if (_model is not null)
        {
            await RunInHeadlessSession(() =>
            {
                _window?.Close();
                _window = null;

                if (_onUserInput is not null)
                {
                    _model.UserInput -= _onUserInput;
                }

                _control!.Model = null;
                _control = null;
                _model.Dispose();
                _model = null;
            });
        }
    }

    private void SendTerminalInput(ReadOnlySpan<byte> input)
    {
        SendFrame(TerminalInputFrame, input);
    }

    private static ProcessStartInfo CreateBridgeStartInfo(string helperPath)
    {
        ProcessStartInfo startInfo;
        if (OperatingSystem.IsWindows())
        {
            startInfo = new ProcessStartInfo("wsl.exe");
            startInfo.ArgumentList.Add("--distribution");
            startInfo.ArgumentList.Add("Ubuntu");
            startInfo.ArgumentList.Add("--exec");
            startInfo.ArgumentList.Add(Environment.GetEnvironmentVariable("UCS_DETECT_WSL_PYTHON") ?? "python3");
            startInfo.ArgumentList.Add(ToWslPath(helperPath));
        }
        else
        {
            startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("UCS_DETECT_PYTHON") ?? "python3");
            startInfo.ArgumentList.Add(helperPath);
        }

        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.CreateNoWindow = true;
        return startInfo;
    }

    private async Task RunAsync()
    {
        Process bridge = _bridge ?? throw new InvalidOperationException("The bridge was not started.");
        while (true)
        {
            int frameKind = bridge.StandardOutput.BaseStream.ReadByte();
            if (frameKind < 0)
            {
                await bridge.WaitForExitAsync().WaitAsync(ProcessExitTimeout);
                throw new InvalidOperationException(
                    $"ucs-detect PTY bridge exited without a completion frame (code {bridge.ExitCode}). " +
                    $"stderr: {await SafeReadStandardErrorAsync()}");
            }

            byte[] header = new byte[4];
            await bridge.StandardOutput.BaseStream.ReadExactlyAsync(header);
            int payloadLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header);
            if (payloadLength < 0 || payloadLength > MaximumControlFrameBytes)
            {
                throw new InvalidDataException($"Invalid ucs-detect PTY frame size: {payloadLength} bytes.");
            }

            byte[] payload = new byte[payloadLength];
            await bridge.StandardOutput.BaseStream.ReadExactlyAsync(payload);

            if (frameKind == TerminalOutputFrame)
            {
                await RunInHeadlessSession(() =>
                {
                    _model!.Feed(payload);
                    Dispatcher.UIThread.RunJobs();
                });
                continue;
            }

            using JsonDocument frame = JsonDocument.Parse(payload);
            if (frameKind == ErrorFrame)
            {
                throw new InvalidOperationException(
                    $"ucs-detect PTY bridge failed: {frame.RootElement.GetProperty("error").GetString()}" +
                    $"{Environment.NewLine}{frame.RootElement.GetProperty("traceback").GetString()}");
            }

            if (frameKind != CompletedFrame)
            {
                throw new InvalidDataException($"Unknown ucs-detect PTY frame kind: {frameKind}.");
            }

            JsonElement result = frame.RootElement;
            int exitCode = result.GetProperty("exit_code").GetInt32();
            JsonElement dependencies = result.GetProperty("dependencies");
            await File.WriteAllTextAsync(
                DependencyVersionsPath,
                JsonSerializer.Serialize(dependencies, new JsonSerializerOptions { WriteIndented = true }));
            AssertDependencyVersions(dependencies);

            if (!File.Exists(ReportPath))
            {
                throw new InvalidOperationException(
                    $"ucs-detect exited with code {exitCode} without creating a JSON report at {ReportPath}. " +
                    $"PTY transcript: {TranscriptPath}. stderr: {await SafeReadStandardErrorAsync()}");
            }

            string reportJson = await File.ReadAllTextAsync(ReportPath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(reportJson))
            {
                throw new InvalidOperationException($"ucs-detect created an empty JSON report at {ReportPath}.");
            }

            Report = JsonDocument.Parse(reportJson);
            TerminalInput = Encoding.UTF8.GetString(await ReadTranscriptDirectionAsync(TranscriptPath, (byte)'I'));
            TerminalOutput = Encoding.UTF8.GetString(await ReadTranscriptDirectionAsync(TranscriptPath, (byte)'O'));
            await bridge.WaitForExitAsync().WaitAsync(ProcessExitTimeout);
            Assert.True(
                exitCode == 0 && bridge.ExitCode == 0,
                $"ucs-detect exited with code {exitCode}; bridge exit code {bridge.ExitCode}. " +
                $"Transcript: {TranscriptPath}. stderr: {await SafeReadStandardErrorAsync()}");
            return;
        }
    }

    private static void AssertDependencyVersions(JsonElement actual)
    {
        List<string> mismatches = [];
        foreach ((string name, string expected) in ExpectedVersions)
        {
            string? installed = actual.TryGetProperty(name, out JsonElement value) ? value.GetString() : null;
            if (!StringComparer.Ordinal.Equals(expected, installed))
            {
                mismatches.Add($"{name}: expected {expected}, found {installed ?? "missing"}");
            }
        }

        Assert.True(
            mismatches.Count == 0,
            "The Python PTY environment is not provisioned with the pinned ucs-detect requirements. " +
            "Create a Python virtual environment, then install tests/requirements-ucs-detect.txt. " +
            string.Join("; ", mismatches));
    }

    private async Task WriteFrameAsync(int kind, ReadOnlyMemory<byte> payload)
    {
        Process bridge = _bridge ?? throw new InvalidOperationException("The bridge was not started.");
        byte[] frame = new byte[5 + payload.Length];
        frame[0] = checked((byte)kind);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1, 4), payload.Length);
        payload.Span.CopyTo(frame.AsSpan(5));
        await bridge.StandardInput.BaseStream.WriteAsync(frame);
        await bridge.StandardInput.BaseStream.FlushAsync();
    }

    private void SendFrame(int kind, ReadOnlySpan<byte> payload)
    {
        Process bridge = _bridge ?? throw new InvalidOperationException("The bridge was not started.");
        byte[] frame = new byte[5 + payload.Length];
        frame[0] = checked((byte)kind);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1, 4), payload.Length);
        payload.CopyTo(frame.AsSpan(5));
        lock (bridge)
        {
            bridge.StandardInput.BaseStream.Write(frame);
            bridge.StandardInput.BaseStream.Flush();
        }
    }

    private static async Task<byte[]> ReadTranscriptDirectionAsync(string path, byte direction)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        using MemoryStream result = new();
        await using FileStream stream = File.OpenRead(path);
        byte[] header = new byte[5];
        while (stream.Position < stream.Length)
        {
            await stream.ReadExactlyAsync(header);
            int recordDirection = header[0];
            int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
            if ((recordDirection != 'I' && recordDirection != 'O') || length < 0 || length > MaximumControlFrameBytes)
            {
                throw new InvalidDataException($"Invalid ucs-detect transcript record: direction={recordDirection}, length={length}.");
            }

            byte[] payload = new byte[length];
            await stream.ReadExactlyAsync(payload);
            if (recordDirection == direction)
            {
                await result.WriteAsync(payload);
            }
        }

        return result.ToArray();
    }

    private async Task StopBridgeAsync()
    {
        if (_bridge is null || _bridge.HasExited)
        {
            return;
        }

        try
        {
            await WriteFrameAsync(InputClosedFrame, ReadOnlyMemory<byte>.Empty);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
        }

        try
        {
            await _bridge.WaitForExitAsync().WaitAsync(ProcessExitTimeout);
        }
        catch (TimeoutException)
        {
            _bridge.Kill(entireProcessTree: true);
            await _bridge.WaitForExitAsync();
        }
    }

    private async Task<string> SafeReadStandardErrorAsync()
    {
        return _standardError is null ? string.Empty : await _standardError;
    }

    private static string ToBridgePath(string path) => OperatingSystem.IsWindows() ? ToWslPath(path) : path;

    private static string ToWslPath(string windowsPath)
    {
        string fullPath = Path.GetFullPath(windowsPath);
        if (fullPath.Length < 3 || fullPath[1] != ':' || fullPath[2] is not ('\\' or '/'))
        {
            throw new InvalidOperationException($"The test path cannot be mapped into WSL: {fullPath}");
        }

        string drive = char.ToLowerInvariant(fullPath[0]).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string relative = fullPath[3..].Replace('\\', '/');
        return $"/mnt/{drive}/{relative}";
    }

}
