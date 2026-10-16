using SvcSystems.UI.Terminal.Samples;
using Xunit;

namespace SvcSystems.UI.Terminal.Tests;

/// <summary>
/// Covers the ConPTY creation flags the sample requests and the fallback policy around them.
/// The grapheme-cluster width flag aligns ConPTY's cell-width model with the terminal engine;
/// the fallback keeps the sample starting on older Windows builds that reject the flag.
/// </summary>
public sealed class WindowsConPtyShellSessionTests
{
    // Values from the Windows Terminal conpty-static.h header.
    private const uint GlyphWidthGraphemes = 0x00000008;
    private const uint AmbiguousIsWide = 0x00000020;

    private const int EInvalidArg = unchecked((int)0x80070057);

    [Fact]
    public void PreferredPseudoConsoleFlags_RequestGraphemeClusterWidths()
    {
        Assert.Equal(GlyphWidthGraphemes, WindowsConPtyShellSession.PreferredPseudoConsoleFlags);
    }

    [Fact]
    public void PreferredPseudoConsoleFlags_LeaveAmbiguousWidthAtTheConsoleDefault()
    {
        // The engine reports ambiguous characters as narrow (ambiguous_width = 1), so the sample
        // must not ask ConPTY to treat them as wide.
        Assert.Equal(0u, WindowsConPtyShellSession.PreferredPseudoConsoleFlags & AmbiguousIsWide);
    }

    [Fact]
    public void CreatePseudoConsoleWithFallback_UsesPreferredFlagsWhenAccepted()
    {
        List<uint> attempts = [];
        WindowsConPtyShellSession.PseudoConsoleCreator create = (uint flags, out IntPtr handle) =>
        {
            attempts.Add(flags);
            handle = (IntPtr)0x1234;
            return 0;
        };

        int result = WindowsConPtyShellSession.CreatePseudoConsoleWithFallback(
            WindowsConPtyShellSession.PreferredPseudoConsoleFlags,
            create,
            out IntPtr pseudoConsoleHandle,
            out uint effectiveFlags);

        Assert.Equal(0, result);
        Assert.Equal(new uint[] { WindowsConPtyShellSession.PreferredPseudoConsoleFlags }, attempts);
        Assert.Equal((IntPtr)0x1234, pseudoConsoleHandle);
        Assert.Equal(WindowsConPtyShellSession.PreferredPseudoConsoleFlags, effectiveFlags);
    }

    [Fact]
    public void CreatePseudoConsoleWithFallback_RetriesWithLegacyFlagsWhenPreferredRejected()
    {
        List<uint> attempts = [];
        WindowsConPtyShellSession.PseudoConsoleCreator create = (uint flags, out IntPtr handle) =>
        {
            attempts.Add(flags);
            if (flags != 0)
            {
                handle = IntPtr.Zero;
                return EInvalidArg;
            }

            handle = (IntPtr)0x5678;
            return 0;
        };

        int result = WindowsConPtyShellSession.CreatePseudoConsoleWithFallback(
            WindowsConPtyShellSession.PreferredPseudoConsoleFlags,
            create,
            out IntPtr pseudoConsoleHandle,
            out uint effectiveFlags);

        Assert.Equal(0, result);
        Assert.Equal(new uint[] { WindowsConPtyShellSession.PreferredPseudoConsoleFlags, 0u }, attempts);
        Assert.Equal((IntPtr)0x5678, pseudoConsoleHandle);
        Assert.Equal(0u, effectiveFlags);
    }

    [Fact]
    public void CreatePseudoConsoleWithFallback_ReportsTheLegacyErrorWhenEveryAttemptFails()
    {
        int attempts = 0;
        WindowsConPtyShellSession.PseudoConsoleCreator create = (uint flags, out IntPtr handle) =>
        {
            attempts++;
            handle = IntPtr.Zero;
            return EInvalidArg;
        };

        int result = WindowsConPtyShellSession.CreatePseudoConsoleWithFallback(
            WindowsConPtyShellSession.PreferredPseudoConsoleFlags,
            create,
            out IntPtr pseudoConsoleHandle,
            out uint effectiveFlags);

        Assert.Equal(EInvalidArg, result);
        Assert.Equal(2, attempts);
        Assert.Equal(IntPtr.Zero, pseudoConsoleHandle);
        Assert.Equal(0u, effectiveFlags);
    }

    [Fact]
    public void CreatePseudoConsoleWithFallback_DoesNotRetryWhenOnlyLegacyFlagsWereRequested()
    {
        int attempts = 0;
        WindowsConPtyShellSession.PseudoConsoleCreator create = (uint flags, out IntPtr handle) =>
        {
            attempts++;
            handle = IntPtr.Zero;
            return EInvalidArg;
        };

        int result = WindowsConPtyShellSession.CreatePseudoConsoleWithFallback(
            0,
            create,
            out _,
            out uint effectiveFlags);

        Assert.Equal(EInvalidArg, result);
        Assert.Equal(1, attempts);
        Assert.Equal(0u, effectiveFlags);
    }
}
