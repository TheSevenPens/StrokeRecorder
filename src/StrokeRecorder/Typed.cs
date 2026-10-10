namespace StrokeRecorder;

/// <summary>
/// What a person typed into a box, and what is kept of it.
/// </summary>
/// <remarks>
/// A name is typed one character at a time, so a box cannot strip whitespace as it goes: the
/// space after "Wacom" is trailing whitespace until the "P" arrives, and removing it on the
/// keystroke makes "Wacom PTK-870" impossible to type. The box shows exactly what was typed. What
/// is kept (the take, the remembered answers, the file) is cleaned, and the box is tidied when
/// the person leaves it.
/// </remarks>
public static class Typed
{
    /// <summary>
    /// The text with leading and trailing whitespace removed. Spaces inside it are the person's own.
    /// </summary>
    public static string Clean(string? typed) => (typed ?? "").Trim();
}
