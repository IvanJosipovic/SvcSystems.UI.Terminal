using Avalonia.Input;
using EngineKeyEvent = XTerm.Options.KeyEvent;

namespace SvcSystems.UI.Terminal;

/// <summary>
/// Translates Avalonia key events into the key events the terminal engine encodes under the
/// kitty keyboard protocol. The engine models them on the DOM <c>KeyboardEvent</c>: a key is
/// named by what it produces (<c>Escape</c>, <c>a</c>) and coded by where it sits (<c>KeyA</c>).
/// </summary>
internal static class KeyEventTranslator
{
    private const int LetterCount = 26;

    private const int DigitCount = 10;

    private static readonly string[] LowercaseLetters = CreateNames(string.Empty, 'a', LetterCount);

    private static readonly string[] UppercaseLetters = CreateNames(string.Empty, 'A', LetterCount);

    private static readonly string[] Digits = CreateNames(string.Empty, '0', DigitCount);

    private static readonly string[] LetterCodes = CreateNames("Key", 'A', LetterCount);

    private static readonly string[] DigitCodes = CreateNames("Digit", '0', DigitCount);

    private static readonly string[] NumpadDigitCodes = CreateNames("Numpad", '0', DigitCount);

    private static readonly string[] FunctionKeyNames =
    [
        "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12",
        "F13", "F14", "F15", "F16", "F17", "F18", "F19", "F20", "F21", "F22", "F23", "F24",
    ];

    /// <summary>
    /// Translates a key event.
    /// </summary>
    /// <param name="e">The Avalonia key event.</param>
    /// <param name="optionAsMetaKey">
    /// Whether Alt held with a text key is a modifier. When <c>false</c> it is the key that
    /// composes a character, so the event carries that character and no Alt.
    /// </param>
    /// <returns>The key event for the engine.</returns>
    public static EngineKeyEvent Translate(KeyEventArgs e, bool optionAsMetaKey)
    {
        ArgumentNullException.ThrowIfNull(e);

        var modifiers = e.KeyModifiers;
        var keyName = GetKeyName(e.Key);
        var shift = modifiers.HasFlag(KeyModifiers.Shift) || e.Key == Key.OemBackTab;
        var control = modifiers.HasFlag(KeyModifiers.Control);
        var alt = modifiers.HasFlag(KeyModifiers.Alt) && (keyName is not null || optionAsMetaKey);

        return new EngineKeyEvent
        {
            Key = keyName ?? GetKeyText(e, shift, control || alt),
            Code = GetCode(e),
            ShiftKey = shift,
            CtrlKey = control,
            AltKey = alt,
            MetaKey = modifiers.HasFlag(KeyModifiers.Meta),
        };
    }

    private static string? GetKeyName(Key key)
    {
        return key switch
        {
            Key.Escape => "Escape",
            Key.Enter => "Enter",
            Key.Tab or Key.OemBackTab => "Tab",
            Key.Back => "Backspace",
            Key.Insert => "Insert",
            Key.Delete => "Delete",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Home => "Home",
            Key.End => "End",
            Key.Up => "ArrowUp",
            Key.Down => "ArrowDown",
            Key.Left => "ArrowLeft",
            Key.Right => "ArrowRight",
            >= Key.F1 and <= Key.F24 => FunctionKeyNames[key - Key.F1],
            Key.CapsLock => "CapsLock",
            Key.Scroll => "ScrollLock",
            Key.NumLock => "NumLock",
            Key.LeftShift or Key.RightShift => "Shift",
            Key.LeftCtrl or Key.RightCtrl => "Control",
            Key.LeftAlt or Key.RightAlt => "Alt",
            Key.LWin or Key.RWin => "Meta",
            _ => null,
        };
    }

    // Control and Alt change what a key produces (a control character, a composed letter), and
    // the protocol reports the key itself, so a modified letter or digit is named by its key.
    private static string GetKeyText(KeyEventArgs e, bool shift, bool modifierChangesSymbol)
    {
        var symbol = e.KeySymbol;
        var hasSymbol = symbol is { Length: 1 } && !char.IsControl(symbol[0]);

        if ((!hasSymbol || modifierChangesSymbol) && TryGetLetterOrDigit(e.Key, shift, out var text))
        {
            return text;
        }

        return hasSymbol ? symbol! : string.Empty;
    }

    private static bool TryGetLetterOrDigit(Key key, bool shift, out string text)
    {
        if (key is >= Key.A and <= Key.Z)
        {
            text = (shift ? UppercaseLetters : LowercaseLetters)[key - Key.A];
            return true;
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            text = Digits[key - Key.D0];
            return true;
        }

        text = string.Empty;
        return false;
    }

    // The engine takes a shifted letter's key code from its code. The layout's own letter is the
    // one the protocol reports, so a letter is coded by the key Avalonia mapped through the
    // layout and everything else by its position.
    private static string GetCode(KeyEventArgs e)
    {
        if (e.Key is >= Key.A and <= Key.Z)
        {
            return LetterCodes[e.Key - Key.A];
        }

        return e.PhysicalKey == PhysicalKey.None ? GetCode(e.Key) : GetCode(e.PhysicalKey);
    }

    private static string GetCode(PhysicalKey key)
    {
        return key switch
        {
            >= PhysicalKey.A and <= PhysicalKey.Z => LetterCodes[key - PhysicalKey.A],
            >= PhysicalKey.Digit0 and <= PhysicalKey.Digit9 => DigitCodes[key - PhysicalKey.Digit0],
            >= PhysicalKey.NumPad0 and <= PhysicalKey.NumPad9 => NumpadDigitCodes[key - PhysicalKey.NumPad0],
            PhysicalKey.Backquote => "Backquote",
            PhysicalKey.Minus => "Minus",
            PhysicalKey.Equal => "Equal",
            PhysicalKey.BracketLeft => "BracketLeft",
            PhysicalKey.BracketRight => "BracketRight",
            PhysicalKey.Backslash => "Backslash",
            PhysicalKey.Semicolon => "Semicolon",
            PhysicalKey.Quote => "Quote",
            PhysicalKey.Comma => "Comma",
            PhysicalKey.Period => "Period",
            PhysicalKey.Slash => "Slash",
            PhysicalKey.IntlBackslash => "IntlBackslash",
            PhysicalKey.IntlRo => "IntlRo",
            PhysicalKey.IntlYen => "IntlYen",
            PhysicalKey.NumPadAdd => "NumpadAdd",
            PhysicalKey.NumPadDecimal => "NumpadDecimal",
            PhysicalKey.NumPadDivide => "NumpadDivide",
            PhysicalKey.NumPadEnter => "NumpadEnter",
            PhysicalKey.NumPadEqual => "NumpadEqual",
            PhysicalKey.NumPadMultiply => "NumpadMultiply",
            PhysicalKey.NumPadSubtract => "NumpadSubtract",
            PhysicalKey.ShiftLeft => "ShiftLeft",
            PhysicalKey.ShiftRight => "ShiftRight",
            PhysicalKey.ControlLeft => "ControlLeft",
            PhysicalKey.ControlRight => "ControlRight",
            PhysicalKey.AltLeft => "AltLeft",
            PhysicalKey.AltRight => "AltRight",
            PhysicalKey.MetaLeft => "MetaLeft",
            PhysicalKey.MetaRight => "MetaRight",
            _ => string.Empty,
        };
    }

    private static string GetCode(Key key)
    {
        return key switch
        {
            >= Key.D0 and <= Key.D9 => DigitCodes[key - Key.D0],
            >= Key.NumPad0 and <= Key.NumPad9 => NumpadDigitCodes[key - Key.NumPad0],
            Key.Add => "NumpadAdd",
            Key.Decimal => "NumpadDecimal",
            Key.Divide => "NumpadDivide",
            Key.Multiply => "NumpadMultiply",
            Key.Subtract => "NumpadSubtract",
            Key.LeftShift => "ShiftLeft",
            Key.RightShift => "ShiftRight",
            Key.LeftCtrl => "ControlLeft",
            Key.RightCtrl => "ControlRight",
            Key.LeftAlt => "AltLeft",
            Key.RightAlt => "AltRight",
            Key.LWin => "MetaLeft",
            Key.RWin => "MetaRight",
            _ => string.Empty,
        };
    }

    private static string[] CreateNames(string prefix, char first, int count)
    {
        var names = new string[count];
        for (var i = 0; i < count; i++)
        {
            names[i] = prefix + (char)(first + i);
        }

        return names;
    }
}
