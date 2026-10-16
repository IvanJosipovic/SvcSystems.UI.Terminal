using XTerm.Common;

namespace XTerm.Buffer;

/// <summary>
/// A shell-integration mark anchored to a position on a line: where a prompt began, where a command
/// started, what it exited with.
/// </summary>
/// <remarks>
/// <para>What turns OSC 133 from a notification into a feature. The mark events tell a host that
/// something happened; they cannot tell it <em>where</em>, and every use of shell integration —
/// jumping to the previous prompt, selecting a command's output, putting an exit status beside the
/// command that produced it — is a question about a position.</para>
///
/// <para>Anchored to the LINE rather than to a cell, for the same reason a picture is. A cell is a
/// struct, so a copy of one cannot say which line it came from; and the cell is 24 bytes and
/// reference-free, which the perf work depends on and which one more field would cost about 22% on
/// scroll-heavy output. The line already carries runs, already survives scroll and reflow by being
/// moved rather than copied, and already releases what it holds when it falls out of the
/// scrollback. A mark wants exactly those properties.</para>
/// </remarks>
public readonly struct LineMark
{
    /// <summary>The column the mark was emitted at.</summary>
    public readonly int Column;

    /// <summary>Which of the four OSC 133 marks this is.</summary>
    public readonly ShellIntegrationMark Kind;

    /// <summary>
    /// The exit status reported with a <see cref="ShellIntegrationMark.CommandFinished"/> mark.
    /// </summary>
    /// <remarks>
    /// Null where none was reported, which is not the same as zero: only D carries a status and it
    /// is optional even there — cmd.exe cannot read the previous command's status from its prompt
    /// and always sends a bare D. Defaulting to 0 would make "not reported" read as "succeeded".
    /// </remarks>
    public readonly int? ExitCode;

    public LineMark(int column, ShellIntegrationMark kind, int? exitCode = null)
    {
        Column = column;
        Kind = kind;
        ExitCode = exitCode;
    }

    public override string ToString()
        => ExitCode is { } code ? $"{Kind}@{Column} exit={code}" : $"{Kind}@{Column}";
}
