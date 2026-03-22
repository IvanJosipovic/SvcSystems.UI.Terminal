using System.Diagnostics;

namespace AvaloniaTerminal.Samples;

internal sealed class RedirectedShellSession(ShellLaunchConfiguration launch) : IShellSession
{
    private readonly ShellLaunchConfiguration _launch = launch;

    private Process? _process;

    private CancellationTokenSource? _pumpCancellation;

    public event Action<byte[]>? DataReceived;

    public event Action<int>? Exited;

    public void Start()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _launch.FileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = System.Text.Encoding.UTF8,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in _launch.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["TERM"] = "xterm-256color";
        startInfo.Environment["COLORTERM"] = "truecolor";

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

    public void Send(byte[] input)
    {
        try
        {
            var inputStream = _process?.StandardInput.BaseStream;
            if (inputStream?.CanWrite != true)
            {
                return;
            }

            var normalizedInput = ShellControl.NormalizeStandardInput(input);
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

    public void Resize(int cols, int rows)
    {
    }

    public void Dispose()
    {
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

    private async Task PumpOutputAsync(Stream stream, CancellationToken cancellationToken)
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
        Exited?.Invoke(_process?.ExitCode ?? 0);
    }
}
