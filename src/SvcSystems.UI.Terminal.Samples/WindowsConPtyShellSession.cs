using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SvcSystems.UI.Terminal.Samples;

internal sealed class WindowsConPtyShellSession(
    ShellLaunchConfiguration launch,
    int initialCols = 80,
    int initialRows = 25) : IShellSession
{
    private readonly ShellLaunchConfiguration _launch = launch;
    private readonly object _syncRoot = new();
    private readonly int _initialCols = Math.Clamp(initialCols, 1, short.MaxValue);
    private readonly int _initialRows = Math.Clamp(initialRows, 1, short.MaxValue);

    private WindowsPseudoConsoleSafeHandle? _pseudoConsole;

    private SafeFileHandle? _inputWriteHandle;

    private SafeFileHandle? _outputReadHandle;

    private FileStream? _inputStream;

    private FileStream? _outputStream;

    private Process? _process;

    private CancellationTokenSource? _lifetimeCancellation;

    private Task? _outputPumpTask;

    private bool _isDisposed;

    private (int cols, int rows)? _lastResize;

    public event Action<byte[]>? DataReceived;

    public event Action<int>? Exited;

    public void Start()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("ConPTY is only available on Windows.");
        }

        if (!NativeMethods.CreatePipePair(out var inputReadHandle, out var inputWriteHandle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create ConPTY input pipe.");
        }

        if (!NativeMethods.CreatePipePair(out var outputReadHandle, out var outputWriteHandle))
        {
            inputReadHandle.Dispose();
            inputWriteHandle.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create ConPTY output pipe.");
        }

        try
        {
            NativeMethods.ClearHandleInheritance(inputWriteHandle);
            NativeMethods.ClearHandleInheritance(outputReadHandle);

            var result = CreatePseudoConsoleWithFallback(
                PreferredPseudoConsoleFlags,
                (uint flags, out IntPtr handle) => NativeMethods.CreatePseudoConsole(
                    new Coord((short)_initialCols, (short)_initialRows),
                    inputReadHandle.DangerousGetHandle(),
                    outputWriteHandle.DangerousGetHandle(),
                    flags,
                    out handle),
                out var pseudoConsoleHandle,
                out _);

            if (result != 0)
            {
                Marshal.ThrowExceptionForHR(result);
            }

            _pseudoConsole = new WindowsPseudoConsoleSafeHandle(pseudoConsoleHandle);
            _inputWriteHandle = inputWriteHandle;
            _outputReadHandle = outputReadHandle;

            inputReadHandle.Dispose();
            outputWriteHandle.Dispose();

            StartProcessAttachedToPseudoConsole(_pseudoConsole);

            _inputStream = new FileStream(_inputWriteHandle, FileAccess.Write, 4096, isAsync: false);
            _outputStream = new FileStream(_outputReadHandle, FileAccess.Read, 4096, isAsync: false);
            _lifetimeCancellation = new CancellationTokenSource();

            _outputPumpTask = Task.Run(() => PumpOutput(_outputStream, _lifetimeCancellation.Token));
            _ = WaitForExitAsync(_lifetimeCancellation.Token);
        }
        catch
        {
            inputReadHandle.Dispose();
            outputWriteHandle.Dispose();
            Dispose();
            throw;
        }
    }

    public void Send(byte[] input)
    {
        try
        {
            if (_inputStream?.CanWrite != true)
            {
                return;
            }

            _inputStream.Write(input, 0, input.Length);
            _inputStream.Flush();
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
        if (!TryBeginResize(cols, rows, out var pseudoConsoleHandle, out var normalized))
        {
            return;
        }

        try
        {
            var result = NativeMethods.ResizePseudoConsole(pseudoConsoleHandle, new Coord((short)normalized.cols, (short)normalized.rows));
            if (result != 0)
            {
                Marshal.ThrowExceptionForHR(result);
            }
        }
        catch (ExternalException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        WindowsPseudoConsoleSafeHandle? pseudoConsole;
        lock (_syncRoot)
        {
            _isDisposed = true;
            pseudoConsole = _pseudoConsole;
            _pseudoConsole = null;
        }

        _lifetimeCancellation?.Cancel();
        _lifetimeCancellation?.Dispose();
        _lifetimeCancellation = null;

        try
        {
            _inputStream?.Dispose();
        }
        catch (IOException)
        {
        }

        try
        {
            _outputStream?.Dispose();
        }
        catch (IOException)
        {
        }

        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
        }

        _process?.Dispose();
        _process = null;

        pseudoConsole?.Dispose();

        _inputWriteHandle?.Dispose();
        _inputWriteHandle = null;

        _outputReadHandle?.Dispose();
        _outputReadHandle = null;
    }

    private bool TryBeginResize(int cols, int rows, out IntPtr pseudoConsoleHandle, out (int cols, int rows) normalized)
    {
        pseudoConsoleHandle = IntPtr.Zero;
        normalized = (Math.Max(cols, 1), Math.Max(rows, 1));

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        lock (_syncRoot)
        {
            if (_isDisposed ||
                _process is { HasExited: true } ||
                _pseudoConsole is not { IsInvalid: false, IsClosed: false })
            {
                return false;
            }

            if (_lastResize == normalized)
            {
                return false;
            }

            _lastResize = normalized;
            pseudoConsoleHandle = _pseudoConsole.DangerousGetHandle();
            return true;
        }
    }

    private void StartProcessAttachedToPseudoConsole(WindowsPseudoConsoleSafeHandle pseudoConsole)
    {
        IntPtr attributeList = IntPtr.Zero;
        IntPtr commandLine = IntPtr.Zero;
        IntPtr environmentBlock = IntPtr.Zero;
        SafeFileHandle? processHandle = null;
        SafeFileHandle? threadHandle = null;

        try
        {
            var attributeListSize = IntPtr.Zero;
            NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);

            attributeList = Marshal.AllocHGlobal(attributeListSize);
            if (!NativeMethods.InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to initialize process attribute list.");
            }

            if (!NativeMethods.UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    NativeMethods.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    pseudoConsole.DangerousGetHandle(),
                    (IntPtr)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to attach pseudo console attribute.");
            }

            var startupInfo = new StartupInfoEx();
            startupInfo.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
            startupInfo.StartupInfo.dwFlags = NativeMethods.STARTF_USESTDHANDLES;
            startupInfo.lpAttributeList = attributeList;

            commandLine = Marshal.StringToHGlobalUni(BuildCommandLine(_launch));

            var environmentVariables = ShellControl.CreateShellEnvironment();
            environmentBlock = Marshal.StringToHGlobalUni(BuildEnvironmentBlock(environmentVariables));

            if (!NativeMethods.CreateProcess(
                    lpApplicationName: null,
                    lpCommandLine: commandLine,
                    lpProcessAttributes: IntPtr.Zero,
                    lpThreadAttributes: IntPtr.Zero,
                    bInheritHandles: false,
                    dwCreationFlags: NativeMethods.EXTENDED_STARTUPINFO_PRESENT | NativeMethods.CREATE_UNICODE_ENVIRONMENT,
                    lpEnvironment: environmentBlock,
                    lpCurrentDirectory: null,
                    lpStartupInfo: ref startupInfo,
                    lpProcessInformation: out var processInformation))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to start process attached to ConPTY.");
            }

            processHandle = new SafeFileHandle(processInformation.hProcess, ownsHandle: true);
            threadHandle = new SafeFileHandle(processInformation.hThread, ownsHandle: true);

            _process = Process.GetProcessById((int)processInformation.dwProcessId);
        }
        finally
        {
            threadHandle?.Dispose();
            processHandle?.Dispose();

            if (attributeList != IntPtr.Zero)
            {
                NativeMethods.DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }

            if (commandLine != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(commandLine);
            }

            if (environmentBlock != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(environmentBlock);
            }
        }
    }

    private void PumpOutput(Stream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4096];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var bytesRead = stream.Read(buffer, 0, buffer.Length);
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

    private async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        if (_process == null)
        {
            return;
        }

        try
        {
            await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            ClosePseudoConsole();
            if (_outputPumpTask is not null)
            {
                await _outputPumpTask.ConfigureAwait(false);
            }

            Exited?.Invoke(_process.ExitCode);
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void ClosePseudoConsole()
    {
        WindowsPseudoConsoleSafeHandle? pseudoConsole;
        lock (_syncRoot)
        {
            pseudoConsole = _pseudoConsole;
            _pseudoConsole = null;
        }

        pseudoConsole?.Dispose();
    }

    /// <summary>
    /// Flags requested when creating the pseudo console.
    /// </summary>
    /// <remarks>
    /// The grapheme-cluster width flag (<c>PSEUDOCONSOLE_GLYPH_WIDTH_GRAPHEMES</c>, from the Windows
    /// Terminal <c>conpty-static.h</c> header) makes ConPTY measure cell widths like the terminal
    /// engine instead of the legacy per-code-unit console widths, so wide, emoji, and combined
    /// glyphs keep the width the control expects when the sample's local shell is hosted in ConPTY.
    /// East Asian ambiguous width is left at the console default because the engine reports
    /// ambiguous characters as narrow (<c>ambiguous_width = 1</c>); <c>PSEUDOCONSOLE_AMBIGUOUS_IS_WIDE</c>
    /// is intentionally not set.
    /// </remarks>
    internal const uint PreferredPseudoConsoleFlags = NativeMethods.PSEUDOCONSOLE_GLYPH_WIDTH_GRAPHEMES;

    /// <summary>
    /// Creates a pseudo console with <paramref name="preferredFlags"/>, retrying with the legacy
    /// default flags (<c>0</c>) when the operating system rejects them.
    /// </summary>
    /// <param name="preferredFlags">The flags to request first.</param>
    /// <param name="create">The native creation call; extracted so the policy can be unit tested.</param>
    /// <param name="pseudoConsoleHandle">The handle from the successful attempt, or <see cref="IntPtr.Zero"/>.</param>
    /// <param name="effectiveFlags">The flags of the last attempt.</param>
    /// <returns>The <c>HRESULT</c> of the last attempt.</returns>
    /// <remarks>
    /// The glyph-width flags are only understood by newer Windows builds. Older builds reject the
    /// call (typically <c>E_INVALIDARG</c>), so the sample retries once without them to keep the
    /// shell starting everywhere. When a build simply ignores unknown bits, the first attempt
    /// succeeds and no retry happens.
    /// </remarks>
    internal static int CreatePseudoConsoleWithFallback(
        uint preferredFlags,
        PseudoConsoleCreator create,
        out IntPtr pseudoConsoleHandle,
        out uint effectiveFlags)
    {
        int result = create(preferredFlags, out pseudoConsoleHandle);
        effectiveFlags = preferredFlags;

        if (result >= 0 || preferredFlags == 0)
        {
            return result;
        }

        pseudoConsoleHandle = IntPtr.Zero;
        result = create(0, out pseudoConsoleHandle);
        effectiveFlags = 0;
        return result;
    }

    /// <summary>
    /// Issues a pseudo console creation attempt for the supplied flags.
    /// </summary>
    /// <param name="flags">The <c>CreatePseudoConsole</c> flags to attempt.</param>
    /// <param name="pseudoConsoleHandle">The created pseudo console handle on success.</param>
    /// <returns>The <c>HRESULT</c> of the attempt.</returns>
    internal delegate int PseudoConsoleCreator(uint flags, out IntPtr pseudoConsoleHandle);

    internal static string BuildCommandLine(ShellLaunchConfiguration launch)
    {
        return string.Join(" ", new[] { QuoteArgument(launch.FileName) }.Concat(launch.Arguments.Select(QuoteArgument)));
    }

    internal static string BuildEnvironmentBlock(IReadOnlyDictionary<string, string> environmentVariables)
    {
        var builder = new StringBuilder();
        foreach (var entry in environmentVariables.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(entry.Key)
                .Append('=')
                .Append(entry.Value)
                .Append('\0');
        }

        builder.Append('\0');
        return builder.ToString();
    }

    private static string QuoteArgument(string argument)
    {
        if (string.IsNullOrEmpty(argument))
        {
            return "\"\"";
        }

        if (!argument.Any(static ch => char.IsWhiteSpace(ch) || ch is '"'))
        {
            return argument;
        }

        var builder = new StringBuilder();
        builder.Append('"');

        var backslashCount = 0;
        foreach (var ch in argument)
        {
            if (ch == '\\')
            {
                backslashCount++;
                continue;
            }

            if (ch == '"')
            {
                builder.Append('\\', (backslashCount * 2) + 1);
                builder.Append('"');
                backslashCount = 0;
                continue;
            }

            if (backslashCount > 0)
            {
                builder.Append('\\', backslashCount);
                backslashCount = 0;
            }

            builder.Append(ch);
        }

        if (backslashCount > 0)
        {
            builder.Append('\\', backslashCount * 2);
        }

        builder.Append('"');
        return builder.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord(short x, short y)
    {
        public short X = x;

        public short Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int nLength;

        public IntPtr lpSecurityDescriptor;

        [MarshalAs(UnmanagedType.Bool)]
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;

        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;

        public IntPtr hThread;

        public uint dwProcessId;

        public uint dwThreadId;
    }

    private sealed class WindowsPseudoConsoleSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public WindowsPseudoConsoleSafeHandle()
            : base(ownsHandle: true)
        {
        }

        public WindowsPseudoConsoleSafeHandle(IntPtr preexistingHandle, bool ownsHandle = true)
            : base(ownsHandle)
        {
            SetHandle(preexistingHandle);
        }

        protected override bool ReleaseHandle()
        {
            NativeMethods.ClosePseudoConsole(handle);
            return true;
        }
    }

    private static class NativeMethods
    {
        internal const int HANDLE_FLAG_INHERIT = 0x00000001;
        internal const int EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        internal const int CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        internal const int STARTF_USESTDHANDLES = 0x00000100;

        // CreatePseudoConsole flags (see the Windows Terminal conpty-static.h header).
        internal const uint PSEUDOCONSOLE_GLYPH_WIDTH_GRAPHEMES = 0x00000008;

        internal static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = (IntPtr)0x00020016;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(
            out SafeFileHandle hReadPipe,
            out SafeFileHandle hWritePipe,
            ref SecurityAttributes lpPipeAttributes,
            int nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(SafeHandle hObject, int dwMask, int dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern int CreatePseudoConsole(
            Coord size,
            IntPtr hInput,
            IntPtr hOutput,
            uint dwFlags,
            out IntPtr phPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern int ResizePseudoConsole(IntPtr hPC, Coord size);

        [DllImport("kernel32.dll")]
        internal static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool InitializeProcThreadAttributeList(
            IntPtr lpAttributeList,
            int dwAttributeCount,
            int dwFlags,
            ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool UpdateProcThreadAttribute(
            IntPtr lpAttributeList,
            uint dwFlags,
            IntPtr attribute,
            IntPtr lpValue,
            IntPtr cbSize,
            IntPtr lpPreviousValue,
            IntPtr lpReturnSize);

        [DllImport("kernel32.dll")]
        internal static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool CreateProcess(
            string? lpApplicationName,
            IntPtr lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
            int dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            ref StartupInfoEx lpStartupInfo,
            out ProcessInformation lpProcessInformation);

        internal static bool CreatePipePair(out SafeFileHandle readPipe, out SafeFileHandle writePipe)
        {
            var attributes = new SecurityAttributes
            {
                nLength = Marshal.SizeOf<SecurityAttributes>(),
                bInheritHandle = true,
            };

            return CreatePipe(out readPipe, out writePipe, ref attributes, 0);
        }

        internal static void ClearHandleInheritance(SafeHandle handle)
        {
            if (!SetHandleInformation(handle, HANDLE_FLAG_INHERIT, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to clear handle inheritance.");
            }
        }
    }
}
