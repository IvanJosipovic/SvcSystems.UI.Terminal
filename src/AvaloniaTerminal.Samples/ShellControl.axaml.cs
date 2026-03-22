using Avalonia.Controls;
using Avalonia.Threading;
using System.Diagnostics;
using XtermSharp;

namespace AvaloniaTerminal.Samples;

public partial class ShellControl : UserControl
{
    private readonly TerminalControlModel _shellModel = new(new TerminalOptions
    {
        ReflowOnResize = false,
    });
    private (int cols, int rows)? _lastResize;

    private IShellSession? _session;

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
        var launch = ResolveShellLaunchConfiguration();

        try
        {
            _session = CreateShellSession(launch);
            _session.DataReceived += OnSessionDataReceived;
            _session.Exited += OnSessionExited;

            ApplyShellResize(Math.Max(_shellModel.Terminal.Cols, 1), Math.Max(_shellModel.Terminal.Rows, 1));
        }
        catch (Exception ex)
        {
            _shellModel.Feed($"Failed to start shell ({launch.DisplayName}).\r\n{ex.Message}\r\n");
        }
    }

    internal static IShellSession CreateShellSession(
        ShellLaunchConfiguration launch,
        bool? isWindowsOverride = null,
        Func<ShellLaunchConfiguration, IShellSession>? redirectedFactory = null,
        Func<ShellLaunchConfiguration, IShellSession>? conPtyFactory = null)
    {
        redirectedFactory ??= static configuration => new RedirectedShellSession(configuration);
        conPtyFactory ??= static configuration => new WindowsConPtyShellSession(configuration);

        var isWindows = isWindowsOverride ?? OperatingSystem.IsWindows();
        if (isWindows)
        {
            try
            {
                var conPtySession = conPtyFactory(launch);
                conPtySession.Start();
                return conPtySession;
            }
            catch (Exception ex) when (IsConPtyUnavailable(ex))
            {
            }
        }

        var redirectedSession = redirectedFactory(launch);
        redirectedSession.Start();
        return redirectedSession;
    }

    private void OnUserInput(byte[] input)
    {
        _session?.Send(input);
    }

    private void OnSessionDataReceived(byte[] data)
    {
        _ = Dispatcher.UIThread.InvokeAsync(() => _shellModel.Feed(data, data.Length), DispatcherPriority.Background);
    }

    private void OnSessionExited(int exitCode)
    {
        Dispatcher.UIThread.Post(() => _shellModel.Feed($"\r\n[Shell exited with code {exitCode}]\r\n"));
    }

    private void OnTerminalSizeChanged(int cols, int rows, double width, double height)
    {
        ApplyShellResize(cols, rows);
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopShell();
    }

    private void StopShell()
    {
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

    private static bool IsConPtyUnavailable(Exception ex)
    {
        return ex is PlatformNotSupportedException
            or EntryPointNotFoundException
            or DllNotFoundException
            or InvalidOperationException;
    }
}

internal sealed record ShellLaunchConfiguration(string FileName, IReadOnlyList<string> Arguments, string DisplayName);
