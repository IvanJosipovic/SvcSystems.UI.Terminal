using Avalonia.Controls;
using SvcSystems.UI.Terminal;
using Avalonia.Threading;
using System.Collections;
using System.Diagnostics;

namespace SvcSystems.UI.Terminal.Samples;

public partial class ShellControl : UserControl
{
    // The sample opts in to terminal-initiated clipboard reads so OSC 52 and Kitty clipboard
    // requests exercise the real host clipboard integration.
    private readonly TerminalControlModel _shellModel = new(new TerminalOptions
    {
        ReflowOnResize = false,
        ClipboardReadEnabled = true,
        ClipboardWriteEnabled = true,
    });
    private (int cols, int rows)? _lastResize;

    private IShellSession? _session;
    private bool _shellStopped;

    public ShellControl()
    {
        InitializeComponent();

        ShellTerminalControl.Model = _shellModel;
        _shellModel.UserInput += OnUserInput;
        _shellModel.SizeChanged += OnTerminalSizeChanged;

        StartShell();
    }

    private void StartShell()
    {
        _ = StartShellAsync();
    }

    private async Task StartShellAsync()
    {
        var launch = ResolveShellLaunchConfiguration();

        try
        {
            bool wslAvailable = OperatingSystem.IsWindows() && await WslShellSession.IsAvailableAsync();
            if (_shellStopped)
            {
                return;
            }

            _session = CreateShellSession(
                launch,
                isWslAvailable: () => wslAvailable);
            _session.DataReceived += OnSessionDataReceived;
            _session.Exited += OnSessionExited;

            ApplyShellResize(Math.Max(_shellModel.Terminal.Cols, 1), Math.Max(_shellModel.Terminal.Rows, 1));
        }
        catch (Exception ex)
        {
            if (!_shellStopped)
            {
                _shellModel.Feed($"Failed to start shell ({launch.DisplayName}).\r\n{ex.Message}\r\n");
            }
        }
    }

    internal static IShellSession CreateShellSession(
        ShellLaunchConfiguration launch,
        bool? isWindowsOverride = null,
        Func<ShellLaunchConfiguration, IShellSession>? redirectedFactory = null,
        Func<ShellLaunchConfiguration, IShellSession>? conPtyFactory = null,
        Func<ShellLaunchConfiguration, IShellSession>? wslFactory = null,
        Func<bool>? isWslAvailable = null)
    {
        redirectedFactory ??= static configuration => new RedirectedShellSession(configuration);
        conPtyFactory ??= static configuration => new WindowsConPtyShellSession(configuration);
        wslFactory ??= static configuration => new WslShellSession(configuration);
        // WSL probing boots the default distro and is asynchronous in StartShellAsync. This
        // synchronous factory only takes the WSL path when its caller has already verified it.
        isWslAvailable ??= static () => false;

        var isWindows = isWindowsOverride ?? OperatingSystem.IsWindows();

        // A WSL session allocates its own PTY in the guest and exchanges VT with the control
        // directly, so a detector run from this shell measures the control rather than ConPTY.
        if (isWindows && isWslAvailable())
        {
            try
            {
                var wslSession = wslFactory(new ShellLaunchConfiguration("bash", ["-i"], "bash (WSL)"));
                wslSession.Start();
                return wslSession;
            }
            catch (Exception ex) when (IsShellSessionUnavailable(ex))
            {
            }
        }

        if (isWindows)
        {
            try
            {
                var conPtySession = conPtyFactory(launch);
                conPtySession.Start();
                return conPtySession;
            }
            catch (Exception ex) when (IsShellSessionUnavailable(ex))
            {
            }
        }

        var redirectedSession = redirectedFactory(launch);
        redirectedSession.Start();
        return redirectedSession;
    }

    internal static Dictionary<string, string> CreateShellEnvironment(
        IEnumerable<KeyValuePair<string, string>>? inheritedEnvironment = null)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var environment = new Dictionary<string, string>(comparer);

        if (inheritedEnvironment is null)
        {
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                environment[(string)entry.Key] = (string?)entry.Value ?? string.Empty;
            }
        }
        else
        {
            foreach (var entry in inheritedEnvironment)
            {
                environment[entry.Key] = entry.Value;
            }
        }

        environment.Remove("TERM_PROGRAM");
        environment.Remove("TERM_PROGRAM_VERSION");
        environment.Remove("KITTY_WINDOW_ID");
        environment.Remove("KITTY_LISTEN_ON");
        environment.Remove("WEZTERM_PANE");
        environment.Remove("WEZTERM_UNIX_SOCKET");
        environment.Remove("VTE_VERSION");
        environment.Remove("KONSOLE_VERSION");
        environment.Remove("KONSOLE_DBUS_SESSION");
        environment.Remove("WT_SESSION");
        environment.Remove("WT_PROFILE_ID");
        environment.Remove("TMUX");
        environment.Remove("TMUX_PANE");
        environment.Remove("TERM_SESSION_ID");
        environment.Remove("LC_TERMINAL");
        environment.Remove("LC_TERMINAL_VERSION");
        environment.Remove("COLUMNS");
        environment.Remove("LINES");
        environment["TERM"] = "xterm";
        environment["COLORTERM"] = "truecolor";

        return environment;
    }

    private void OnUserInput(object? sender, TerminalUserInputEventArgs e)
    {
        _session?.Send(e.Data.ToArray());
    }

    private void OnSessionDataReceived(byte[] data)
    {
        _ = Dispatcher.UIThread.InvokeAsync(() => _shellModel.Feed(data, data.Length), DispatcherPriority.Background);
    }

    private void OnSessionExited(int exitCode)
    {
        Dispatcher.UIThread.Post(() => _shellModel.Feed($"\r\n[Shell exited with code {exitCode}]\r\n"));
    }

    private void OnTerminalSizeChanged(object? sender, TerminalSizeChangedEventArgs e)
    {
        ApplyShellResize(e.Cols, e.Rows);
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopShell();
    }

    private void StopShell()
    {
        _shellStopped = true;
        _shellModel.UserInput -= OnUserInput;
        _shellModel.SizeChanged -= OnTerminalSizeChanged;

        if (_session is not null)
        {
            _session.DataReceived -= OnSessionDataReceived;
            _session.Exited -= OnSessionExited;
            _session.Dispose();
        }

        _session = null;
        _lastResize = null;
    }

    internal static ShellLaunchConfiguration ResolveShellLaunchConfiguration(Func<string, bool>? executableExists = null)
    {
        executableExists ??= static command => FindExecutableInPath(command) is not null;

        if (OperatingSystem.IsWindows())
        {
            var powerShell = Environment.GetEnvironmentVariable("PATH") is not null && executableExists("pwsh.exe")
                ? new ShellLaunchConfiguration("pwsh.exe", ["-NoLogo"], "pwsh.exe")
                : null;

            if (powerShell is not null)
            {
                return powerShell;
            }

            var commandPrompt = Environment.GetEnvironmentVariable("ComSpec");
            if (!string.IsNullOrWhiteSpace(commandPrompt) && executableExists(commandPrompt))
            {
                return new ShellLaunchConfiguration(commandPrompt, [], commandPrompt);
            }

            if (executableExists("cmd.exe"))
            {
                return new ShellLaunchConfiguration("cmd.exe", [], "cmd.exe");
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            foreach (var candidate in new[]
            {
                new ShellLaunchConfiguration("zsh", ["-i"], "zsh"),
                new ShellLaunchConfiguration("bash", ["-i"], "bash"),
                new ShellLaunchConfiguration("sh", ["-i"], "sh"),
            })
            {
                if (executableExists(candidate.FileName))
                {
                    return candidate;
                }
            }
        }
        else
        {
            foreach (var candidate in new[]
            {
                new ShellLaunchConfiguration("bash", ["-i"], "bash"),
                new ShellLaunchConfiguration("ash", ["-i"], "ash"),
                new ShellLaunchConfiguration("sh", ["-i"], "sh"),
            })
            {
                if (executableExists(candidate.FileName))
                {
                    return candidate;
                }
            }
        }

        return new ShellLaunchConfiguration("echo", ["No Shell Found!"], "echo");
    }

    internal static byte[] NormalizeStandardInput(byte[] input)
    {
        if (input.Length == 0)
        {
            return input;
        }

        var normalized = new List<byte>(input.Length + 4);
        var newline = System.Text.Encoding.UTF8.GetBytes(Environment.NewLine);

        for (var i = 0; i < input.Length; i++)
        {
            var current = input[i];

            if (current == '\r')
            {
                normalized.AddRange(newline);

                if ((i + 1) < input.Length && input[i + 1] == '\n')
                {
                    i++;
                }

                continue;
            }

            normalized.Add(current);
        }

        return normalized.ToArray();
    }

    private void ApplyShellResize(int cols, int rows)
    {
        if (_session == null)
        {
            return;
        }

        if (!ShouldApplyShellResize(_lastResize, cols, rows, out var normalized))
        {
            return;
        }

        _lastResize = normalized;
        _session.Resize(normalized.cols, normalized.rows);
    }

    internal static bool ShouldApplyShellResize((int cols, int rows)? lastResize, int cols, int rows, out (int cols, int rows) normalized)
    {
        normalized = (Math.Max(cols, 1), Math.Max(rows, 1));
        return lastResize != normalized;
    }

    private static string? FindExecutableInPath(string command)
    {
        if (Path.IsPathRooted(command) || command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(command) ? command : null;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        IEnumerable<string> extensions = [string.Empty];
        if (OperatingSystem.IsWindows())
        {
            var pathExt = Environment.GetEnvironmentVariable("PATHEXT");
            if (!string.IsNullOrWhiteSpace(pathExt))
            {
                extensions = pathExt.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, command.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? command : command + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static bool IsShellSessionUnavailable(Exception ex)
    {
        return ex is PlatformNotSupportedException
            or EntryPointNotFoundException
            or DllNotFoundException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception;
    }
}

internal sealed record ShellLaunchConfiguration(string FileName, IReadOnlyList<string> Arguments, string DisplayName);
