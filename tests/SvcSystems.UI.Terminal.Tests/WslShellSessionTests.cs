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

    private sealed class FakeShellSession : IShellSession
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

        public bool Started { get; private set; }

        public void Start() => Started = true;

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
