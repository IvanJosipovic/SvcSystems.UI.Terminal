namespace XTerm.Common;

/// <summary>
/// Progress state reported by OSC 9 ; 4 (the ConEmu convention, which Windows Terminal renders on
/// the taskbar).
/// </summary>
public enum ProgressState
{
    /// <summary>
    /// No progress indicator; any previous one is cleared. Progress value is not meaningful.
    /// </summary>
    None = 0,

    /// <summary>
    /// Normal progress, with a value from 0 to 100.
    /// </summary>
    Normal = 1,

    /// <summary>
    /// An error occurred; the indicator is shown in an error state.
    /// </summary>
    Error = 2,

    /// <summary>
    /// Work is ongoing but its extent is unknown. Progress value is not meaningful.
    /// </summary>
    Indeterminate = 3,

    /// <summary>
    /// Progress is paused or in a warning state.
    /// </summary>
    Warning = 4,
}
