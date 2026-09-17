using WinPenKit;

namespace StrokeRecorder;

/// <summary>
/// Which backend the list should be showing, after asking what can be opened.
/// </summary>
/// <remarks>
/// <para>
/// A rule rather than four lines inside the window, because it has to hold across a refresh
/// that happens while somebody is using the application: the driver's service can be
/// restarted mid-session, and asking again must not quietly move a reader onto a different
/// backend from the one they picked. A decision left inside a window is a decision nothing
/// checks.
/// </para>
/// <para>
/// The name is not <c>Backends</c>. That is a <c>Name=</c> on a ComboBox in the recorder's
/// XAML, which generates a field on the window, and a field beats a type.
/// </para>
/// </remarks>
public static class Choosing
{
    /// <summary>
    /// The index to select: what was chosen if it can still be opened, else the first that can.
    /// </summary>
    /// <param name="all">Every backend, in the order the list shows them.</param>
    /// <param name="available">Those that can be opened right now.</param>
    /// <param name="chosen">What was selected before this refresh, if anything.</param>
    /// <returns>
    /// An index into <paramref name="all"/>. Never negative: a backend that cannot be opened
    /// is still shown, saying so, and a list where nothing is available still has a selection.
    /// </returns>
    public static int Backend(
        IReadOnlyList<InputApi> all, IReadOnlyList<InputApi> available, InputApi? chosen)
    {
        if (all.Count == 0) return 0;

        // Held on to only where it can still be opened. A backend that has gone away is not a
        // choice any more, and leaving it selected would mean the next take opened nothing.
        if (chosen is { } api && available.Contains(api))
        {
            var kept = IndexOf(all, api);

            if (kept >= 0) return kept;
        }

        for (var each = 0; each < all.Count; each++)
        {
            if (available.Contains(all[each])) return each;
        }

        return 0;
    }

    private static int IndexOf(IReadOnlyList<InputApi> all, InputApi api)
    {
        for (var each = 0; each < all.Count; each++)
        {
            if (all[each] == api) return each;
        }

        return -1;
    }
}
