using StrokeFieldGuide.Recorder;
using StrokeFieldGuide.Strokes;
using StrokeFieldGuide.Surfaces;
using WinPenKit;

namespace StrokeFieldGuide.Recorder.Tests;

/// <summary>
/// That the recorder does not claim a recording is whole when it has not looked.
/// </summary>
/// <remarks>
/// <para>
/// Reported on <c>#85</c>: where the session's counters were unavailable, the findings said
/// "this window saw N readings and <b>kept all of them</b>" and returned before the
/// arithmetic that would have found otherwise. The reviewer's probe supplied 100 routed
/// readings with two stored and got exactly that assurance.
/// </para>
/// <para>
/// This matters more than a wrong number on a screen. The whole reason anybody trusts a
/// recording from this tool is that it reconciles its counters and says so, and the question
/// behind the entire investigation was whether pen data was being lost. A false yes there is
/// worse than no answer.
/// </para>
/// </remarks>
public class Accounting
{
    private static Take Made(int readings, int routed)
    {
        var take = new Take(Gestures.All[0], InputApi.WintabDigitizer, 32767,
            new InkTransform(1, 1, 0, 0));

        var contact = take.Begin();

        for (var each = 0; each < readings; each++)
        {
            contact.Add(new Reading(
                X: 10 + each, Y: 20, Pressure: 500,
                At: each * 4166, Height: 0, Status: 0,
                Lean: 0, Azimuth: 0, Twist: 0, Arrived: each * 6200));
        }

        // Reopened is how a take's handover counters are set without a session, which is
        // also how a reopened recording restores what it counted on the day.
        take.Reopened(routed, 0, 0);

        return take;
    }

    private static IReadOnlyList<Finding> Of(Take take) => Findings.For(take);

    private static bool Says(IReadOnlyList<Finding> found, string what) =>
        found.Any(one => one.Title.Contains(what, StringComparison.OrdinalIgnoreCase)
                         || one.Body.Contains(what, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Readings_that_are_in_no_column_are_reported_even_with_no_session_counts()
    {
        // The reviewer's case: a great many handed over, almost none stored, and nothing
        // from the layer below to compare against.
        var found = Of(Made(readings: 2, routed: 100));

        Assert.False(Says(found, "kept all of them"),
            "the recorder said a recording was whole without checking");

        Assert.True(Says(found, "in none of its columns"),
            "98 readings went missing and nothing said so");
    }

    [Fact]
    public void A_take_whose_columns_add_up_is_said_to_add_up()
    {
        var found = Of(Made(readings: 8, routed: 8));

        Assert.True(Says(found, "accounted for"),
            "a recording that reconciles should say so");

        Assert.False(Says(found, "in none of its columns"));
    }

    [Fact]
    public void The_absence_of_session_counts_is_reported_as_an_absence()
    {
        var found = Of(Made(readings: 8, routed: 8));

        // It may say it does not know about the layer below. What it may not do is turn that
        // into an assurance about the layer it can see.
        Assert.True(Says(found, "could not say what it was given"),
            "the recorder should say when it cannot speak for the session");
    }

    /// <summary>
    /// That hover readings left out on purpose are counted, not reported as lost.
    /// </summary>
    /// <remarks>
    /// Found on a real recording made to check something else: 137 readings handed over, 18
    /// in strokes, 3 off the pad — and 116 reported as reaching the window and landing in no
    /// column. Every one was a hovering reading, left out because the airborne record was
    /// switched off, which is a thing the recorder chose to do rather than a thing that went
    /// wrong. <b>Left out is not lost</b>, and an instrument that cannot tell them apart is
    /// not much use for the question this one exists to answer.
    /// </remarks>
    [Fact]
    public void Airborne_readings_left_out_on_purpose_are_not_reported_as_missing()
    {
        var take = Made(readings: 18, routed: 137);

        for (var each = 0; each < 116; each++) take.OneLeftOut();

        for (var each = 0; each < 3; each++) take.DroppedOne();

        var found = Of(take);

        Assert.False(Says(found, "in none of its columns"),
            "readings left out on purpose were reported as missing");

        Assert.True(Says(found, "accounted for"));

        Assert.True(Says(found, "deliberately not kept"),
            "the column should say what it holds");
    }

    [Fact]
    public void More_stored_than_handed_over_is_also_reported()
    {
        // The other direction, which the original arithmetic would have rendered as a
        // negative count in a sentence about readings going missing.
        var found = Of(Made(readings: 10, routed: 4));

        Assert.True(Says(found, "more readings are stored than were handed over"),
            "a take holding more than it was given should say so");
    }
}
