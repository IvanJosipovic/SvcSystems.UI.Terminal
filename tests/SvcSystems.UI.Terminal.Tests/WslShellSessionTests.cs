using System.ComponentModel;
using SvcSystems.UI.Terminal.Samples;
using Xunit;

namespace SvcSystems.UI.Terminal.Tests;

/// <summary>
/// Covers the WSL raw-PTY session: the guest command it builds, the <c>wsl.exe</c> arguments, POSIX
/// quoting, and the point-of-use selection in <see cref="ShellControl.CreateShellSession"/>.
/// </summary>
public sealed class WslShellSessionTests
{
    [Fact]
    public void BuildGuestCommand_SetsSizeRecordsDeviceAndStartsTheShell()
    {
        var launch = new ShellLaunchConfiguration("bash", ["-i"], "bash (WSL)");

        string command = WslShellSession.BuildGuestCommand(
            launch, "/tmp/svc-terminal-pty-abc.tty", 174, 42, "xterm", "truecolor");

        Assert.Equal(
            "stty rows 42 cols 174 2>/dev/null; " +
            "tty > /tmp/svc-terminal-pty-abc.tty 2>/dev/null; " +
            "export TERM=xterm; " +
            "export COLORTERM=truecolor; " +
            "bash -i; " +
            "status=$?; " +
            "rm -f /tmp/svc-terminal-pty-abc.tty; " +
            "exit $status",
            command);
    }

    [Fact]
    public void BuildGuestCommand_QuotesArgumentsWithShellMetacharacters()
    {
        var launch = new ShellLaunchConfiguration("my shell", ["a b", "it's"], "spaced");

        string command = WslShellSession.BuildGuestCommand(
            launch, "/tmp/x.tty", 80, 25, "xterm", "truecolor");

        Assert.Contains("'my shell' 'a b' 'it'\\''s';", command, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("bash", "bash")]
    [InlineData("a b", "'a b'")]
    [InlineData("it's", "'it'\\''s'")]
    [InlineData("", "''")]
    [InlineData("--flag", "--flag")]
    public void QuotePosixArgument_QuotesOnlyWhenNeeded(string value, string expected)
    {
        Assert.Equal(expected, WslShellSession.QuotePosixArgument(value));
    }

    [Fact]
    public void BuildWslArguments_RunScriptWithTheGuestCommand()
    {
        Assert.Equal(
            new[] { "--exec", "script", "-qefc", "echo hi", "/dev/null" },
            WslShellSession.BuildWslArguments("echo hi"));
    }

    [Fact]
    public void ConPtyDiagnostic_UsesTheSamePythonAndQuotesReportPath()
    {
        const string python = "/home/test user/.venvs/ucs/bin/python";
        const string report = "/mnt/c/work tree/results/ucs report.json";

        string command = UcsDetectConPtyComparisonTests.BuildConPtyProbeCommand(python, report);

        Assert.Contains("python_executable='/home/test user/.venvs/ucs/bin/python'", command, StringComparison.Ordinal);
        Assert.Contains("--save-json '/mnt/c/work tree/results/ucs report.json'", command, StringComparison.Ordinal);
        Assert.Contains("python_bin_directory", command, StringComparison.Ordinal);
        Assert.Contains("user_script_directory", command, StringComparison.Ordinal);
    }

    [Fact]
    public void ConPtyDiagnostic_UsesCurrentUcsDetectEnvironmentAndShellEscapesEveryArgument()
    {
        string command = UcsDetectConPtyComparisonTests.BuildConPtyProbeCommand(
            "python3",
            "/mnt/c/work tree's/report file.json");

        Assert.Contains("python_executable=python3", command, StringComparison.Ordinal);
        Assert.Contains("--save-json '/mnt/c/work tree'\\''s/report file.json'", command, StringComparison.Ordinal);
        Assert.Contains("exec ucs-detect", command, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateShellSession_SubscriptionReceivesOutputProducedImmediatelyOnStart()
    {
        FakeShellSession session = new(emitOutputOnStart: true);
        byte[]? received = null;

        IShellSession created = ShellControl.CreateShellSession(
            new ShellLaunchConfiguration("bash", ["-i"], "bash"),
            isWindowsOverride: false,
            redirectedFactory: _ => session,
            beforeStart: candidate => candidate.DataReceived += data => received = data);

        Assert.Same(session, created);
        Assert.Equal(new byte[] { 0x41 }, received);
    }

    [Theory]
    [InlineData(false, false, true, true, 2, true)]
    [InlineData(false, false, true, false, 0, true)]
    [InlineData(false, false, true, false, 1, true)]
    [InlineData(false, false, true, false, 2, false)]
    [InlineData(false, true, true, true, 0, false)]
    [InlineData(true, false, true, true, 0, false)]
    [InlineData(false, false, false, true, 0, false)]
    public void ShouldRestartResizeWorker_OnlyKeepsPendingWorkWhenSessionCanRetry(
        bool isDisposed,
        bool processExited,
        bool hasPendingResize,
        bool hasResolvedDevice,
        int resolutionCycles,
        bool expected)
    {
        Assert.Equal(
            expected,
            WslShellSession.ShouldRestartResizeWorker(
                isDisposed,
                processExited,
                hasPendingResize,
                hasResolvedDevice,
                resolutionCycles));
    }

    [Fact]
    public void CreateShellSession_PrefersWslWhenAvailable()
    {
        var wsl = new FakeShellSession();
        ShellLaunchConfiguration? wslLaunch = null;

        var session = ShellControl.CreateShellSession(
            new ShellLaunchConfiguration("pwsh.exe", ["-NoLogo"], "pwsh.exe"),
            isWindowsOverride: true,
            isWslAvailable: static () => true,
            wslFactory: configuration =>
            {
                wslLaunch = configuration;
                return wsl;
            },
            conPtyFactory: _ => throw new InvalidOperationException("ConPTY should not be used when WSL is available."),
            redirectedFactory: _ => throw new InvalidOperationException("Redirected fallback should not be used."));

        Assert.Same(wsl, session);
        Assert.True(wsl.Started);
        Assert.NotNull(wslLaunch);
        Assert.Equal("bash", wslLaunch!.FileName);
    }

    [Fact]
    public void CreateShellSession_FallsBackToConPtyWhenWslUnavailable()
    {
        var conPty = new FakeShellSession();

        var session = ShellControl.CreateShellSession(
            new ShellLaunchConfiguration("pwsh.exe", ["-NoLogo"], "pwsh.exe"),
            isWindowsOverride: true,
            isWslAvailable: static () => false,
            wslFactory: _ => throw new InvalidOperationException("WSL should not be used."),
            conPtyFactory: _ => conPty);

        Assert.Same(conPty, session);
        Assert.True(conPty.Started);
    }

    [Fact]
    public void CreateShellSession_FallsBackToConPtyWhenTheWslSessionFailsToStart()
    {
        var conPty = new FakeShellSession();

        var session = ShellControl.CreateShellSession(
            new ShellLaunchConfiguration("pwsh.exe", ["-NoLogo"], "pwsh.exe"),
            isWindowsOverride: true,
            isWslAvailable: static () => true,
            wslFactory: _ => new ThrowingShellSession(new Win32Exception("wsl.exe is not available")),
            conPtyFactory: _ => conPty);

        Assert.Same(conPty, session);
        Assert.True(conPty.Started);
    }

    [Fact]
    public void CreateShellSession_IgnoresWslOffWindows()
    {
        var redirected = new FakeShellSession();

        var session = ShellControl.CreateShellSession(
            new ShellLaunchConfiguration("bash", ["-i"], "bash"),
            isWindowsOverride: false,
            isWslAvailable: static () => true,
            wslFactory: _ => throw new InvalidOperationException("WSL must not be used off Windows."),
            conPtyFactory: _ => throw new InvalidOperationException("ConPTY must not be used off Windows."),
            redirectedFactory: _ => redirected);

        Assert.Same(redirected, session);
        Assert.True(redirected.Started);
    }

    private sealed class FakeShellSession(bool emitOutputOnStart = false) : IShellSession
    {
        public event Action<byte[]>? DataReceived;

        public event Action<int>? Exited
        {
            add { }
            remove { }
        }

        public bool Started { get; private set; }

        public void Start()
        {
            Started = true;
            if (emitOutputOnStart)
            {
                DataReceived?.Invoke([0x41]);
            }
        }

        public void Send(byte[] input)
        {
        }

        public void Resize(int cols, int rows)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingShellSession(Exception exception) : IShellSession
    {
        public event Action<byte[]>? DataReceived
        {
            add { }
            remove { }
        }

        public event Action<int>? Exited
        {
            add { }
            remove { }
        }

        public void Start() => throw exception;

        public void Send(byte[] input)
        {
        }

        public void Resize(int cols, int rows)
        {
        }

        public void Dispose()
        {
        }
    }
}
