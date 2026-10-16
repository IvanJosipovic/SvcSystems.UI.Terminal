using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace SvcSystems.UI.Terminal.Samples;

/// <summary>
/// Hosts a Linux shell inside a WSL distribution behind a real PTY that the guest allocates with
/// <c>script(1)</c>.
/// </summary>
/// <remarks>
/// Unlike <see cref="WindowsConPtyShellSession"/>, no ConPTY sits between the guest and the
/// terminal. The Avalonia control therefore answers the guest's VT queries directly (XTGETTCAP,
/// kitty, iTerm2, sixel, DECRQSS, …) and performs the width measurement itself, so a detector run
/// from this session measures the control rather than an intermediary console emulator.
/// <para>
/// Because the session's stdio are pipes, the guest PTY starts at <c>0 0</c>; the guest command
/// sets the initial size with <c>stty</c> and records its device path so later resizes can be
/// applied from outside with <c>stty -F</c>. The WSL distribution used is the default one.
/// </para>
/// </remarks>
internal sealed class WslShellSession : IShellSession
{
    internal const string DefaultTerminal = "xterm";
    internal const string DefaultColorTerm = "truecolor";

    private const string WslExecutable = "wsl.exe";
    private const string GuestScriptExecutable = "script";
    private const string GuestDeviceDirectory = "/tmp";
    private const string GuestDevicePrefix = "svc-terminal-pty-";
    private const string GuestDeviceExtension = ".tty";
    private const int DeviceResolutionAttempts = 20;
    private const int MaximumDeviceResolutionCycles = 2;
    private static readonly TimeSpan GuestProbeTimeout = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan DeviceResolutionDelay = TimeSpan.FromMilliseconds(150);

    private readonly ShellLaunchConfiguration _launch;
    private readonly string _wslExecutable;
    private readonly bool _isWindows;
    private readonly string _deviceFilePath =
        $"{GuestDeviceDirectory}/{GuestDevicePrefix}{Guid.NewGuid():N}{GuestDeviceExtension}";
    private readonly object _syncRoot = new();
    private readonly int _initialCols;
    private readonly int _initialRows;

    private Process? _process;
    private CancellationTokenSource? _lifetime;
    private string? _devicePath;
    private (int cols, int rows)? _pendingResize;
    private int _resizeWorkerActive;
    private int _deviceResolutionCycles;
    private bool _processExited;
    private bool _isDisposed;

    public WslShellSession(ShellLaunchConfiguration launch, int initialCols = 80, int initialRows = 25)
        : this(launch, WslExecutable, OperatingSystem.IsWindows(), initialCols, initialRows)
    {
    }

    internal WslShellSession(
        ShellLaunchConfiguration launch,
        string wslExecutable,
        bool isWindows,
        int initialCols = 80,
        int initialRows = 25)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentException.ThrowIfNullOrWhiteSpace(wslExecutable);
        _launch = launch;
        _wslExecutable = wslExecutable;
        _isWindows = isWindows;
        _initialCols = Math.Clamp(initialCols, 1, short.MaxValue);
        _initialRows = Math.Clamp(initialRows, 1, short.MaxValue);
    }

    public event Action<byte[]>? DataReceived;

    public event Action<int>? Exited;

    /// <summary>
    /// Asynchronously verifies that the default distro can actually allocate the PTY used by this
    /// session. Running the probe boots a cold WSL distro, so callers must not block the UI thread.
    /// </summary>
    internal static async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        ProcessStartInfo startInfo = new(WslExecutable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--exec");
        startInfo.ArgumentList.Add("sh");
        startInfo.ArgumentList.Add("-lc");
        startInfo.ArgumentList.Add(
            "command -v bash >/dev/null 2>&1 && command -v stty >/dev/null 2>&1 && " +
            "command -v script >/dev/null 2>&1 && script -qefc 'exit 0' /dev/null >/dev/null 2>&1");

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            using CancellationTokenSource timeout = new(GuestProbeTimeout);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeout.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                KillQuietly(process);
                return false;
            }

            await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is
            Win32Exception or InvalidOperationException or IOException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Builds the guest shell command that sets the size, records the PTY device, starts the shell,
    /// and removes the device file on exit.
    /// </summary>
    internal static string BuildGuestCommand(
        ShellLaunchConfiguration launch,
        string deviceFilePath,
        int cols,
        int rows,
        string terminal,
        string colorTerm)
    {
        string commandLine = string.Join(
            " ",
            new[] { QuotePosixArgument(launch.FileName) }.Concat(launch.Arguments.Select(QuotePosixArgument)));

        return string.Join(
            "; ",
            $"stty rows {rows} cols {cols} 2>/dev/null",
            $"tty > {QuotePosixArgument(deviceFilePath)} 2>/dev/null",
            $"export TERM={QuotePosixArgument(terminal)}",
            $"export COLORTERM={QuotePosixArgument(colorTerm)}",
            commandLine,
            "status=$?",
            $"rm -f {QuotePosixArgument(deviceFilePath)}",
            "exit $status");
    }

    /// <summary>
    /// Builds the <c>wsl.exe</c> arguments that run <paramref name="guestCommand"/> inside a
    /// guest-allocated PTY. The typescript target is <c>/dev/null</c>; the PTY traffic itself is
    /// this session's stdio.
    /// </summary>
    internal static IReadOnlyList<string> BuildWslArguments(string guestCommand)
    {
        return ["--exec", GuestScriptExecutable, "-qefc", guestCommand, "/dev/null"];
    }

    /// <summary>
    /// Quotes a value for a POSIX shell, leaving it untouched when it is already shell-safe.
    /// </summary>
    internal static string QuotePosixArgument(string value)
    {
        if (value.Length > 0 && value.All(static character =>
                char.IsLetterOrDigit(character) ||
                character is '_' or '-' or '.' or '/' or ':' or '@' or '%' or '+' or ',' or '='))
        {
            return value;
        }

        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    public void Start()
    {
        if (!_isWindows)
        {
            throw new PlatformNotSupportedException("The WSL shell session is only available on Windows.");
        }

        string guestCommand = BuildGuestCommand(
            _launch,
            _deviceFilePath,
            _initialCols,
            _initialRows,
            DefaultTerminal,
            DefaultColorTerm);

        ProcessStartInfo startInfo = new(_wslExecutable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in BuildWslArguments(guestCommand))
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment.Clear();
        foreach (KeyValuePair<string, string> entry in ShellControl.CreateShellEnvironment())
        {
            startInfo.Environment[entry.Key] = entry.Value;
        }

        Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
        lock (_syncRoot)
        {
            _processExited = false;
            _deviceResolutionCycles = 0;
        }

        process.Exited += OnProcessExited;
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Failed to start the WSL shell session.");
        }

        lock (_syncRoot)
        {
            _process = process;
        }
        _lifetime = new CancellationTokenSource();
        _ = PumpAsync(process.StandardOutput.BaseStream, _lifetime.Token);
        _ = PumpAsync(process.StandardError.BaseStream, _lifetime.Token);
    }

    public void Send(byte[] input)
    {
        try
        {
            Stream? stream = _process?.StandardInput.BaseStream;
            if (stream?.CanWrite != true)
            {
                return;
            }

            stream.Write(input, 0, input.Length);
            stream.Flush();
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Resize(int cols, int rows)
    {
        (int cols, int rows) normalized = (Math.Max(cols, 1), Math.Max(rows, 1));

        lock (_syncRoot)
        {
            if (_isDisposed || _processExited || _process is null)
            {
                return;
            }

            if (_devicePath is null && _resizeWorkerActive == 0 &&
                _deviceResolutionCycles >= MaximumDeviceResolutionCycles)
            {
                _deviceResolutionCycles = 0;
            }

            _pendingResize = normalized;
            if (Interlocked.CompareExchange(ref _resizeWorkerActive, 1, 0) != 0)
            {
                return;
            }
        }

        _ = Task.Run(RunResizeWorkerAsync);
    }

    public void Dispose()
    {
        Process? process;
        lock (_syncRoot)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            process = _process;
            _process = null;
        }

        if (process is not null)
        {
            process.Exited -= OnProcessExited;
        }

        _lifetime?.Cancel();
        _lifetime?.Dispose();
        _lifetime = null;

        try
        {
            process?.StandardInput.Close();
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        if (process is { HasExited: false })
        {
            KillQuietly(process);
        }

        process?.Dispose();
    }

    private async Task PumpAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4096];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                DataReceived?.Invoke(buffer.AsSpan(0, bytesRead).ToArray());
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        int exitCode = sender is Process process ? process.ExitCode : 0;
        lock (_syncRoot)
        {
            _processExited = true;
            _pendingResize = null;
        }

        Exited?.Invoke(exitCode);
    }

    private async Task RunResizeWorkerAsync()
    {
        try
        {
            string? device;
            lock (_syncRoot)
            {
                if (_isDisposed || _processExited || _process is null)
                {
                    return;
                }

                device = _devicePath;
                if (device is null)
                {
                    if (_deviceResolutionCycles >= MaximumDeviceResolutionCycles)
                    {
                        _pendingResize = null;
                        return;
                    }

                    _deviceResolutionCycles++;
                }
            }

            if (device is null)
            {
                device = await ResolveDevicePathAsync().ConfigureAwait(false);
                lock (_syncRoot)
                {
                    if (_isDisposed || _processExited)
                    {
                        _pendingResize = null;
                        return;
                    }

                    _devicePath = device;
                    if (device is null && _deviceResolutionCycles >= MaximumDeviceResolutionCycles)
                    {
                        _pendingResize = null;
                    }
                }
            }

            while (device is not null)
            {
                (int cols, int rows)? size;
                lock (_syncRoot)
                {
                    size = _pendingResize;
                    _pendingResize = null;
                    if (_isDisposed || _processExited)
                    {
                        return;
                    }
                }

                if (size is null)
                {
                    return;
                }

                await ApplyResizeAsync(device, size.Value).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is
            IOException or
            InvalidOperationException or
            Win32Exception or
            OperationCanceledException or
            ObjectDisposedException)
        {
        }
        finally
        {
            bool restart;
            lock (_syncRoot)
            {
                _resizeWorkerActive = 0;
                restart = ShouldRestartResizeWorker(
                    _isDisposed,
                    _processExited,
                    _pendingResize is not null,
                    _devicePath is not null,
                    _deviceResolutionCycles);
                if (restart)
                {
                    _resizeWorkerActive = 1;
                }
            }

            if (restart)
            {
                _ = Task.Run(RunResizeWorkerAsync);
            }
        }
    }

    private async Task<string?> ResolveDevicePathAsync()
    {
        for (int attempt = 0; attempt < DeviceResolutionAttempts; attempt++)
        {
            string? value = await ReadGuestFileAsync(_deviceFilePath).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }

            CancellationToken token = _lifetime?.Token ?? CancellationToken.None;
            if (token.IsCancellationRequested)
            {
                return null;
            }

            try
            {
                await Task.Delay(DeviceResolutionDelay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        return null;
    }

    private async Task<string?> ReadGuestFileAsync(string guestPath)
    {
        ProcessStartInfo startInfo = CreateWslStartInfo();
        startInfo.ArgumentList.Add("--exec");
        startInfo.ArgumentList.Add("cat");
        startInfo.ArgumentList.Add(guestPath);

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            string output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private async Task ApplyResizeAsync(string device, (int cols, int rows) size)
    {
        ProcessStartInfo startInfo = CreateWslStartInfo();
        startInfo.ArgumentList.Add("--exec");
        startInfo.ArgumentList.Add("stty");
        startInfo.ArgumentList.Add("-F");
        startInfo.ArgumentList.Add(device);
        startInfo.ArgumentList.Add("rows");
        startInfo.ArgumentList.Add(size.rows.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("cols");
        startInfo.ArgumentList.Add(size.cols.ToString(CultureInfo.InvariantCulture));

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return;
            }

            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
        }
    }

    private ProcessStartInfo CreateWslStartInfo()
    {
        return new ProcessStartInfo(_wslExecutable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
    }

    internal static bool ShouldRestartResizeWorker(
        bool isDisposed,
        bool processExited,
        bool hasPendingResize,
        bool hasResolvedDevice,
        int deviceResolutionCycles)
    {
        return !isDisposed && !processExited && hasPendingResize &&
            (hasResolvedDevice || deviceResolutionCycles < MaximumDeviceResolutionCycles);
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or
            Win32Exception or
            NotSupportedException)
        {
        }
    }
}
