namespace SvcSystems.UI.Terminal;

/// <summary>Describes a notification requested by a terminal application.</summary>
public sealed class TerminalNotificationEventArgs(
    string? identifier,
    string? title,
    string? body,
    int? urgency,
    string? icon) : EventArgs
{
    /// <summary>Gets the optional application-provided notification identifier.</summary>
    public string? Identifier { get; } = identifier;

    /// <summary>Gets the optional notification title.</summary>
    public string? Title { get; } = title;

    /// <summary>Gets the optional notification body.</summary>
    public string? Body { get; } = body;

    /// <summary>Gets the optional urgency level.</summary>
    public int? Urgency { get; } = urgency;

    /// <summary>Gets the optional icon reference.</summary>
    public string? Icon { get; } = icon;
}
