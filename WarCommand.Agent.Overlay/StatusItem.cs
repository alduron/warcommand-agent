namespace WarCommand.Agent.Overlay;

/// <summary>How loudly one status item reads. Decides its colour and its place in the strip.</summary>
public enum StatusSeverity
{
    /// <summary>Something is wrong and the board cannot be trusted until it clears.</summary>
    Fault,

    /// <summary>Worth knowing, and the board is still usable.</summary>
    Warn,

    /// <summary>A confirmation of something the viewer just did. Always transient.</summary>
    Note,
}

/// <summary>
/// One line on the status strip.
/// </summary>
/// <remarks>
/// Status used to be a single string written into the header's right cell, which is the cell the
/// JOIN CODE lives in: a status word covered the six digits somebody was about to read out loud,
/// and a second status silently replaced the first rather than appearing beside it. The strip is
/// its own surface for both reasons. Items stack, and the header goes back to holding only what it
/// is for.
/// <para>
/// It carries no colour. Severity picks a token in the strip's template, because every colour in
/// the overlay lives in <c>Theme/OverlayTokens.xaml</c> and nowhere else.
/// </para>
/// </remarks>
public sealed record StatusItem(string Text, StatusSeverity Severity)
{
    /// <summary>
    /// True for the leading item, which is the one that must not draw a divider before itself.
    /// </summary>
    /// <remarks>
    /// Carried on the item rather than handled by a separator pass, so the strip stays one
    /// ItemsControl over one list and there is no second collection to keep in step.
    /// </remarks>
    public bool IsFirst { get; init; }
}
