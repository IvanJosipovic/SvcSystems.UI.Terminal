using Avalonia.Controls;
using Avalonia.Threading;
using System.Diagnostics;

namespace AvaloniaTerminal.Samples;

public partial class ShellControl : UserControl
{
    private readonly TerminalControlModel _shellModel = new();

    private Process? _process;

    private CancellationTokenSource? _pumpCancellation;

    public ShellControl()
    {
        InitializeComponent();

        ShellTerminalControl.Model = _shellModel;
        _shellModel.UserInput += OnUserInput;

        StartShell();
    }

    private void StartShell()
    {
        var launch = ResolveShellLaunchConfiguration();
        var startInfo = new ProcessStartInfo
        {
            FileName = launch.FileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = System.Text.Encoding.UTF8,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in launch.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["TERM"] = "xterm-256color";
        startInfo.Environment["COLORTERM"] = "truecolor";

        try
        {
            _process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true,
            };
            _process.Exited += OnProcessExited;
            _process.Start();

            _pumpCancellation = new CancellationTokenSource();
            _ = PumpOutputAsync(_process.StandardOutput.BaseStream, _pumpCancellation.Token);
            _ = PumpOutputAsync(_process.StandardError.BaseStream, _pumpCancellation.Token);
        }
        catch (Exception ex)
        {
            _shellModel.Feed($"Failed to start shell ({launch.DisplayName}).\r\n{ex.Message}\r\n");
        }
    }

    private async Task PumpOutputAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4096];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesRead = await stream.ReadAsync(buffer, cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                byte[] chunk = buffer.AsSpan(0, bytesRead).ToArray();
                await Dispatcher.UIThread.InvokeAsync(() => _shellModel.Feed(chunk, chunk.Length), DispatcherPriority.Background);
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

    private void OnUserInput(byte[] input)
    {
        try
        {
            var inputStream = _process?.StandardInput.BaseStream;
            if (inputStream?.CanWrite != true)
            {
                return;
            }

            var normalizedInput = NormalizeStandardInput(input);
            inputStream.Write(normalizedInput, 0, normalizedInput.Length);
            inputStream.Flush();
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_process is not null)
            {
                _shellModel.Feed($"\r\n[Shell exited with code {_process.ExitCode}]\r\n");
            }
        });
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopShell();
    }

    private void StopShell()
    {
        _shellModel.UserInput -= OnUserInput;

        if (_process is not null)
        {
            _process.Exited -= OnProcessExited;
        }

        _pumpCancellation?.Cancel();
        _pumpCancellation?.Dispose();
        _pumpCancellation = null;

        try
        {
            _process?.StandardInput.Close();
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
        }

        _process?.Dispose();
        _process = null;
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
}

internal sealed record ShellLaunchConfiguration(string FileName, IReadOnlyList<string> Arguments, string DisplayName);
