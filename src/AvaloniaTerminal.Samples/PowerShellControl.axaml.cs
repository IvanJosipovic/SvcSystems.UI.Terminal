using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using System.Diagnostics;

namespace AvaloniaTerminal.Samples;

public partial class PowerShellControl : UserControl
{
    private readonly TerminalControlModel _powerShellModel = new();

    private Process? _process;

    private CancellationTokenSource? _pumpCancellation;

    public PowerShellControl()
    {
        InitializeComponent();

        PowerShellTerminalControl.Model = _powerShellModel;
        _powerShellModel.UserInput += OnUserInput;

        StartPowerShellCore();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void StartPowerShellCore()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolvePowerShellExecutable(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = System.Text.Encoding.UTF8,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("-NoLogo");
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
            _powerShellModel.Feed($"Failed to start PowerShell Core ({startInfo.FileName}).\r\n{ex.Message}\r\n");
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
                await Dispatcher.UIThread.InvokeAsync(() => _powerShellModel.Feed(chunk, chunk.Length), DispatcherPriority.Background);
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

            inputStream.Write(input, 0, input.Length);
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
                _powerShellModel.Feed($"\r\n[PowerShell exited with code {_process.ExitCode}]\r\n");
            }
        });
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopPowerShellCore();
    }

    private void StopPowerShellCore()
    {
        _powerShellModel.UserInput -= OnUserInput;

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

    private static string ResolvePowerShellExecutable()
    {
        return OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";
    }
}
