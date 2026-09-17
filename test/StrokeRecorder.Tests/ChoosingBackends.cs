using StrokeRecorder;
using WinPenKit;

namespace StrokeRecorder.Tests;

/// <summary>
/// What the backend list should be showing after it asks again what can be opened.
/// </summary>
/// <remarks>
/// Discovery used to run in the recorder's constructor, before there was a window, and the
/// answer was kept for the life of the process. Both halves of that are wrong for this
/// question: a Wintab context is granted against a window, and the driver's service can be
/// stopped and started while the application is up. So it is asked when the window opens and
/// whenever the Recheck button is pressed — which means the selection has to survive a
/// refresh, and that is what these are about.
/// </remarks>
public class ChoosingBackends
{
    private static readonly IReadOnlyList<InputApi> All =
    [
        InputApi.WintabDigitizer,
        InputApi.WintabSystem,
        InputApi.WmPointer,
        InputApi.AvaloniaPointer,
    ];

    /// <summary>Nothing picked yet: the first that can be opened.</summary>
    [Fact]
    public void TakesTheFirstAvailableWhenNothingIsChosen() =>
        Assert.Equal(2, Choosing.Backend(All, [InputApi.WmPointer, InputApi.AvaloniaPointer], null));

    /// <summary>
    /// A refresh does not move a reader off the backend they picked.
    /// </summary>
    /// <remarks>
    /// The point of the whole rule. Pressing Recheck to see whether Wintab has come back must
    /// not silently switch a recording onto a different API, because which API a recording
    /// came through is one of the things the recording is evidence about.
    /// </remarks>
    [Fact]
    public void KeepsTheChosenBackendWhenItIsStillAvailable() =>
        Assert.Equal(
            3,
            Choosing.Backend(All, All, InputApi.AvaloniaPointer));

    /// <summary>A backend that has gone away is not a choice any more.</summary>
    /// <remarks>
    /// Left selected, the next take would open nothing. The driver's service stopping is the
    /// ordinary way this happens.
    /// </remarks>
    [Fact]
    public void MovesOffAChosenBackendThatHasGoneAway() =>
        Assert.Equal(
            3,
            Choosing.Backend(All, [InputApi.AvaloniaPointer], InputApi.WintabDigitizer));

    /// <summary>A backend that comes back is available again, and can be chosen again.</summary>
    [Fact]
    public void FindsABackendThatHasComeBack() =>
        Assert.Equal(
            0,
            Choosing.Backend(All, All, InputApi.WintabDigitizer));

    /// <summary>
    /// With nothing available there is still a selection.
    /// </summary>
    /// <remarks>
    /// Every backend is listed either way, the unavailable ones saying so, so the list is
    /// never empty and a negative index would be a crash rather than an empty box.
    /// </remarks>
    [Fact]
    public void StillSelectsSomethingWhenNothingCanBeOpened() =>
        Assert.Equal(0, Choosing.Backend(All, [], InputApi.WintabDigitizer));

    [Fact]
    public void SelectsSomethingWhenThereIsNothingToSelect() =>
        Assert.Equal(0, Choosing.Backend([], [], null));

    /// <summary>
    /// A chosen backend that is not in the list at all is ignored.
    /// </summary>
    /// <remarks>
    /// It cannot happen from the window, which only ever selects out of the list. It is here
    /// because the rule returns an index and a rule that can return one out of range is worse
    /// than one that cannot.
    /// </remarks>
    [Fact]
    public void IgnoresAChosenBackendThatIsNotListed() =>
        Assert.Equal(
            0,
            Choosing.Backend(
                [InputApi.WintabDigitizer], [InputApi.WintabDigitizer, InputApi.WmPointer],
                InputApi.WmPointer));
}
