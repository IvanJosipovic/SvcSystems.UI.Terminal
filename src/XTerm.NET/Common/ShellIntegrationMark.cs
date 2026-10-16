namespace XTerm.Common;

/// <summary>
/// A shell integration mark, as reported by OSC 133 (FinalTerm/FTCS).
/// </summary>
/// <remarks>
/// These arrive only from a shell that has been configured to emit them, so their ABSENCE says
/// nothing at all: a shell with no integration installed looks identical to one sitting at a prompt.
/// That is why <see cref="XTerm.Terminal.ShellIntegrationState"/> is nullable rather than defaulting
/// to <see cref="PromptStart"/>.
/// </remarks>
public enum ShellIntegrationMark
{
    /// <summary>
    /// OSC 133 ; A - the start of a prompt.
    /// </summary>
    PromptStart,

    /// <summary>
    /// OSC 133 ; B - the start of the command line, i.e. the end of the prompt. The shell is
    /// waiting for input at this point.
    /// </summary>
    CommandStart,

    /// <summary>
    /// OSC 133 ; C - the start of command output. Something other than the shell holds the
    /// terminal from here until the matching <see cref="CommandFinished"/>.
    /// </summary>
    CommandExecuted,

    /// <summary>
    /// OSC 133 ; D - the end of a command, optionally carrying its exit code.
    /// </summary>
    CommandFinished,
}
