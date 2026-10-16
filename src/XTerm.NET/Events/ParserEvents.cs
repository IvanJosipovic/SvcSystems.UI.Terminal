using XTerm.Parser;

namespace XTerm.Events.Parser;

/// <summary>
/// Event arguments for print operations.
/// </summary>
public class PrintEventArgs : EventArgs
{
    /// <summary>
    /// The character(s) to print.
    /// </summary>
    public string Data { get; }

    public PrintEventArgs(string data)
    {
        Data = data;
    }
}

/// <summary>
/// Event arguments for control character execution.
/// </summary>
public class ExecuteEventArgs : EventArgs
{
    /// <summary>
    /// The control character code.
    /// </summary>
    public int Code { get; }

    public ExecuteEventArgs(int code)
    {
        Code = code;
    }
}

/// <summary>
/// Event arguments for CSI (Control Sequence Introducer) sequences.
/// </summary>
public class CsiEventArgs : EventArgs
{
    /// <summary>
    /// The CSI sequence identifier (final character and any collected intermediates).
    /// </summary>
    public string Identifier { get; }

    /// <summary>
    /// The parameters for the CSI sequence.
    /// </summary>
    public Params Parameters { get; }

    public CsiEventArgs(string identifier, Params parameters)
    {
        Identifier = identifier;
        Parameters = parameters;
    }
}

/// <summary>
/// Event arguments for ESC sequences.
/// </summary>
public class EscEventArgs : EventArgs
{
    /// <summary>
    /// The final character of the ESC sequence.
    /// </summary>
    public string FinalChar { get; }

    /// <summary>
    /// Any collected intermediate characters.
    /// </summary>
    public string Collected { get; }

    public EscEventArgs(string finalChar, string collected)
    {
        FinalChar = finalChar;
        Collected = collected;
    }
}

/// <summary>
/// Event arguments for OSC (Operating System Command) sequences.
/// </summary>
public class OscEventArgs : EventArgs
{
    /// <summary>
    /// The OSC command data.
    /// </summary>
    public string Data { get; }

    public OscEventArgs(string data)
    {
        Data = data;
    }
}

/// <summary>
/// Event arguments for DCS (Device Control String) sequences.
/// </summary>
public class DcsEventArgs : EventArgs
{
    /// <summary>
    /// The DCS command data.
    /// </summary>
    public string Data { get; }

    /// <summary>
    /// The parameters for the DCS sequence.
    /// </summary>
    public Params Parameters { get; }

    public DcsEventArgs(string data, Params parameters)
    {
        Data = data;
        Parameters = parameters;
    }
}

/// <summary>
/// Event arguments raised when a DCS sequence's final character has been seen and its payload is
/// about to begin.
/// </summary>
/// <remarks>
/// A DCS carries an open-ended payload -- a Sixel image can run to megabytes -- so the parser
/// streams it as hook/put/unhook rather than handing over one finished string. A handler decides
/// at hook time whether this is a sequence worth listening to, and only then does it pay for the
/// bytes.
/// </remarks>
public class DcsHookEventArgs : EventArgs
{
    /// <summary>
    /// Intermediate characters followed by the final character, e.g. "q" for DECSIXEL or "$q" for
    /// DECRQSS. Built the same way as the CSI identifier, so the two read alike.
    /// </summary>
    public string Identifier { get; }

    /// <summary>
    /// The numeric parameters that preceded the final character.
    /// </summary>
    public Params Parameters { get; }

    public DcsHookEventArgs(string identifier, Params parameters)
    {
        Identifier = identifier;
        Parameters = parameters;
    }
}

/// <summary>
/// Event arguments for a chunk of a DCS payload.
/// </summary>
public class DcsPutEventArgs : EventArgs
{
    /// <summary>
    /// A slice of the payload. Only valid for the duration of the event -- a handler that needs to
    /// keep it must copy it.
    /// </summary>
    public ReadOnlyMemory<char> Data { get; }

    public DcsPutEventArgs(ReadOnlyMemory<char> data)
    {
        Data = data;
    }
}

/// <summary>
/// Event arguments raised when an APC sequence begins, before any payload.
/// </summary>
/// <remarks>
/// APC has no parameter grammar in front of its payload -- everything after the introducer is
/// payload -- so unlike <see cref="DcsHookEventArgs"/> there is no identifier to report. What the
/// sequence means is decided by its first payload character: 'G' is Kitty graphics.
/// </remarks>
public class ApcHookEventArgs : EventArgs
{
    /// <summary>
    /// The character that introduced the sequence, which is always '_' for APC. Present so a
    /// handler reading these events on their own can tell what it is looking at.
    /// </summary>
    public char Introducer { get; }

    public ApcHookEventArgs(char introducer)
    {
        Introducer = introducer;
    }
}

/// <summary>
/// Event arguments for a chunk of an APC payload.
/// </summary>
public class ApcPutEventArgs : EventArgs
{
    /// <summary>
    /// A slice of the payload. Only valid for the duration of the event -- a handler that needs to
    /// keep it must copy it.
    /// </summary>
    public ReadOnlyMemory<char> Data { get; }

    public ApcPutEventArgs(ReadOnlyMemory<char> data)
    {
        Data = data;
    }
}

/// <summary>
/// Event arguments raised when an APC sequence ends.
/// </summary>
public class ApcUnhookEventArgs : EventArgs
{
    /// <summary>
    /// True when the sequence ended at a string terminator, false when it was abandoned. Half a
    /// Kitty transmission is not worth decoding, so handlers use this to tell the two apart.
    /// </summary>
    public bool TerminatedCleanly { get; }

    public ApcUnhookEventArgs(bool terminatedCleanly)
    {
        TerminatedCleanly = terminatedCleanly;
    }
}

/// <summary>
/// Event arguments raised when a DCS sequence ends.
/// </summary>
public class DcsUnhookEventArgs : EventArgs
{
    /// <summary>
    /// True when the sequence ended at a string terminator, false when it was abandoned -- a CAN,
    /// a SUB, or another escape sequence starting on top of it. A half-received image is not worth
    /// showing, so handlers use this to tell "finished" from "gave up".
    /// </summary>
    public bool TerminatedCleanly { get; }

    public DcsUnhookEventArgs(bool terminatedCleanly)
    {
        TerminatedCleanly = terminatedCleanly;
    }
}
