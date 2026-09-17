using StrokeRecorder;
using StrokeKit.Strokes;
using StrokeKit.Surfaces;
using WinPenKit;

namespace StrokeRecorder.Tests;

/// <summary>
/// What the recorder does with readings it is given.
/// </summary>
/// <remarks>
/// <para>
/// No tablet and no window. Hardware is needed to know what a driver supplies; it is not
/// needed to know what this application does with readings it has been supplied — which is
/// the part that was never checked, and where <c>#85</c> found four accounting faults by
/// reading.
/// </para>
/// <para>
/// <b>These describe the recorder that exists.</b> The controller was extracted without
/// changing policy, so where the behaviour below looks odd it is odd in the shipped tool and
/// is written down here rather than quietly corrected. <c>#66</c> is where policy changes,
/// and it should change it from a characterised starting point.
/// </para>
/// </remarks>
public class CaptureTests
{
    private const long Tick = 6_200;

    private static Capturing Ready(bool manyStrokes = true, bool keepAirborne = false)
    {
        var capturing = new Capturing(() => new InkTransform(1, 1, 0, 0))
        {
            KeepAirborne = keepAirborne,
        };

        var gesture = Gestures.All.First(one => one.ManyStrokes == manyStrokes);

        capturing.Choose(gesture, new Capturing.Device(InputApi.WintabDigitizer, 32767, "test"));

        return capturing;
    }

    private static Reading At(double x, uint pressure, long arrived) =>
        new(X: x, Y: 100, Pressure: pressure, At: arrived, Height: 0, Status: 0,
            Lean: 0, Azimuth: 0, Twist: 0, Arrived: arrived);

    /// <summary>A hovering reading that moved, so the hover buffer keeps it.</summary>
    private static Reading Above(double x, long arrived) => At(x, 0, arrived);

    private static Reading Down(double x, long arrived) => At(x, 600, arrived);

    // ---- landing and lifting ------------------------------------------------

    [Fact]
    public void Nothing_is_recorded_until_it_is_armed()
    {
        var capturing = Ready();

        var what = capturing.Took(Down(10, Tick), overThePad: true);

        Assert.Equal(Disposition.Unarmed, what.Of);
        Assert.Null(capturing.Take);
    }

    [Fact]
    public void The_first_contact_after_arming_starts_a_stroke()
    {
        var capturing = Ready();

        capturing.Arm(null);

        var what = capturing.Took(Down(10, Tick), overThePad: true);

        Assert.Equal(Disposition.Contact, what.Of);
        Assert.True(what.Drew);
        Assert.Equal(Capture.Drawing, capturing.State);
        Assert.Equal(1, capturing.Take!.Count);
    }

    [Fact]
    public void Lifting_ends_the_stroke_and_a_many_stroke_take_waits_for_the_next()
    {
        var capturing = Ready();

        capturing.Arm(null);
        capturing.Took(Down(10, Tick), true);
        capturing.Took(Down(20, Tick * 2), true);

        capturing.Took(Above(20, Tick * 3), true);

        Assert.Equal(Capture.Between, capturing.State);

        capturing.Took(Down(30, Tick * 4), true);

        Assert.Equal(Capture.Drawing, capturing.State);
        Assert.Equal(2, capturing.Take!.Strokes);
    }

    [Fact]
    public void Several_transitions_in_one_batch_are_each_handled()
    {
        // Readings drained together share an arrival stamp, so a lift and the landing after
        // it can both be in one batch. Nothing may be collapsed for sharing a timestamp.
        var capturing = Ready();

        capturing.Arm(null);

        foreach (var reading in new[]
                 {
                     Down(10, Tick), Above(15, Tick), Down(20, Tick), Above(25, Tick),
                 })
        {
            capturing.Took(reading, true);
        }

        Assert.Equal(2, capturing.Take!.Strokes);
    }

    // ---- off the pad --------------------------------------------------------

    [Fact]
    public void A_contact_off_the_pad_is_dropped_and_counted()
    {
        var capturing = Ready();

        capturing.Arm(null);
        capturing.Took(Down(10, Tick), true);

        var what = capturing.Took(Down(9000, Tick * 2), overThePad: false);

        Assert.Equal(Disposition.OffPad, what.Of);
        Assert.Equal(1, capturing.Take!.DroppedOffPad);
    }

    [Fact]
    public void Leaving_the_pad_mid_stroke_ends_the_stroke()
    {
        var capturing = Ready();

        capturing.Arm(null);
        capturing.Took(Down(10, Tick), true);
        capturing.Took(Down(9000, Tick * 2), false);

        Assert.Equal(Capture.Between, capturing.State);
        Assert.Equal("the pen left the pad", capturing.Take!.Contacts[0].EndedBy);
    }

    [Fact]
    public void Coming_back_onto_the_pad_starts_another_stroke()
    {
        var capturing = Ready();

        capturing.Arm(null);
        capturing.Took(Down(10, Tick), true);
        capturing.Took(Down(9000, Tick * 2), false);
        capturing.Took(Down(20, Tick * 3), true);

        Assert.Equal(Capture.Drawing, capturing.State);
        Assert.Equal(2, capturing.Take!.Strokes);
    }

    // ---- stopping -----------------------------------------------------------

    [Fact]
    public void Stopping_while_the_tip_is_down_says_so_on_the_stroke()
    {
        var capturing = Ready();

        capturing.Arm(null);
        capturing.Took(Down(10, Tick), true);

        capturing.Stop(null);

        Assert.Equal(Capture.Taken, capturing.State);
        Assert.Equal("the recording was stopped mid-stroke", capturing.Take!.Contacts[0].EndedBy);
    }

    [Fact]
    public void Drawing_after_a_many_stroke_take_is_stopped_is_counted_and_kept_out()
    {
        var capturing = Ready();

        capturing.Arm(null);
        capturing.Took(Down(10, Tick), true);
        capturing.Stop(null);

        var what = capturing.Took(Down(20, Tick * 5), true);

        Assert.Equal(Disposition.AfterTheStop, what.Of);
        Assert.Equal(1, capturing.Take!.AfterTheStop);
        Assert.Equal(1, capturing.Take.Strokes);
    }

    [Fact]
    public void Drawing_after_a_single_stroke_take_starts_it_again()
    {
        // Deliberate, and the opposite of the many-stroke rule: drawing another single stroke
        // is how somebody says the last one was not the one they wanted.
        var capturing = Ready(manyStrokes: false);

        capturing.Arm(null);
        capturing.Took(Down(10, Tick), true);
        capturing.Took(Above(10, Tick * 2), true);

        Assert.Equal(Capture.Taken, capturing.State);

        var first = capturing.Take;

        var what = capturing.Took(Down(50, Tick * 9), true);

        Assert.Equal(Disposition.Contact, what.Of);
        Assert.True(what.Wipe);
        Assert.NotSame(first, capturing.Take);
        Assert.Equal(1, capturing.Take!.Strokes);
    }

    // ---- the airborne record ------------------------------------------------

    [Fact]
    public void Airborne_readings_are_kept_when_asked_for()
    {
        var capturing = Ready(keepAirborne: true);

        capturing.Arm(null);

        var what = capturing.Took(Above(10, Tick), true);

        Assert.Equal(Disposition.RetainedAirborne, what.Of);
        Assert.Single(capturing.Take!.Aloft);
    }

    [Fact]
    public void Airborne_readings_are_excluded_when_not_asked_for_and_say_so()
    {
        // The accounting hole #85 found: these used to count as routed and land in no column,
        // with no category saying they were left out on purpose.
        var capturing = Ready(keepAirborne: false);

        capturing.Arm(null);

        var what = capturing.Took(Above(10, Tick), true);

        Assert.Equal(Disposition.ExcludedAirborne, what.Of);
        Assert.Empty(capturing.Take!.Aloft);
    }

    [Fact]
    public void Airborne_readings_before_a_take_exists_join_the_one_that_follows()
    {
        var capturing = Ready(manyStrokes: false, keepAirborne: true);

        capturing.Arm(null);

        capturing.Took(Above(10, Tick), true);
        capturing.Took(Above(11, Tick * 2), true);

        Assert.Equal(2, capturing.Aloft.Count);

        capturing.Took(Down(12, Tick * 3), true);

        Assert.Equal(2, capturing.Take!.Aloft.Count);
        Assert.Empty(capturing.Aloft);
    }

    // ---- the approach -------------------------------------------------------

    [Fact]
    public void A_landing_takes_the_hover_readings_immediately_before_it()
    {
        var capturing = Ready();

        capturing.Arm(null);

        capturing.Took(Above(10, Tick), true);
        capturing.Took(Above(11, Tick * 2), true);
        capturing.Took(Above(12, Tick * 3), true);

        capturing.Took(Down(13, Tick * 4), true);

        Assert.Equal(3, capturing.Take!.Contacts[0].Approach.Count);
    }

    [Fact]
    public void Hover_older_than_the_window_is_not_part_of_the_approach()
    {
        var capturing = Ready();

        capturing.Arm(null);

        capturing.Took(Above(10, 0), true);

        // Later than the quarter second the approach keeps, on the host clock.
        var late = Capturing.HoverKept + 50_000;

        capturing.Took(Above(11, late), true);
        capturing.Took(Down(12, late + Tick), true);

        Assert.Single(capturing.Take!.Contacts[0].Approach);
    }

    [Fact]
    public void One_strokes_approach_is_not_given_to_the_next()
    {
        var capturing = Ready();

        capturing.Arm(null);

        capturing.Took(Above(10, Tick), true);
        capturing.Took(Down(11, Tick * 2), true);
        capturing.Took(Above(12, Tick * 3), true);
        capturing.Took(Down(13, Tick * 4), true);

        Assert.Single(capturing.Take!.Contacts[0].Approach);

        // The second stroke gets what hovered before *it*, which is the one lift reading.
        Assert.Single(capturing.Take.Contacts[1].Approach);
    }

    // ---- accounting ---------------------------------------------------------

    [Fact]
    public void Every_reading_gets_exactly_one_disposition()
    {
        // The contract #85 asked for. Not that the numbers are right -- that the question is
        // answerable at all, which it was not when a reading could be routed and belong
        // nowhere.
        var capturing = Ready(keepAirborne: true);

        capturing.Arm(null);

        var readings = new[]
        {
            Above(10, Tick), Down(11, Tick * 2), Down(12, Tick * 3),
            Down(9000, Tick * 4), Above(13, Tick * 5), Down(14, Tick * 6),
        };

        var seen = new List<Disposition>();

        foreach (var reading in readings)
        {
            seen.Add(capturing.Took(reading, overThePad: reading.X < 1000).Of);
        }

        Assert.Equal(readings.Length, seen.Count);

        Assert.Contains(Disposition.Contact, seen);
        Assert.Contains(Disposition.RetainedAirborne, seen);
        Assert.Contains(Disposition.OffPad, seen);
    }

    // ---- what the window needs to draw it --------------------------------

    /// <summary>
    /// That a reading starting a stroke says so, so its ink is not joined to the last one.
    /// </summary>
    /// <remarks>
    /// Reported from the pad: "when the stroke first landed a line was drawn from some random
    /// point to the place the pen touched". The random point was the end of the previous
    /// stroke. The window draws from the last point it laid unless told to forget it, and the
    /// version of Record this replaced forgot it at every state transition -- which the
    /// extraction lost. The recorded data was never affected; only the ink on the pad.
    /// </remarks>
    [Fact]
    public void The_first_reading_of_every_stroke_says_it_began_one()
    {
        var capturing = Ready();

        capturing.Arm(null);

        Assert.True(capturing.Took(Down(10, Tick), true).Began, "the first landing");

        Assert.False(capturing.Took(Down(11, Tick * 2), true).Began, "the one after it");

        capturing.Took(Above(11, Tick * 3), true);

        Assert.True(capturing.Took(Down(40, Tick * 4), true).Began, "the second stroke");
    }

    [Fact]
    public void Coming_back_onto_the_pad_begins_a_stroke_too()
    {
        var capturing = Ready();

        capturing.Arm(null);
        capturing.Took(Down(10, Tick), true);
        capturing.Took(Down(9000, Tick * 2), false);

        Assert.True(capturing.Took(Down(20, Tick * 3), true).Began);
    }

    [Fact]
    public void A_single_stroke_take_drawn_again_begins_a_stroke()
    {
        var capturing = Ready(manyStrokes: false);

        capturing.Arm(null);
        capturing.Took(Down(10, Tick), true);
        capturing.Took(Above(10, Tick * 2), true);

        var what = capturing.Took(Down(50, Tick * 9), true);

        Assert.True(what.Began);
    }

    /// <summary>That a restarted take keeps the reading that restarted it.</summary>
    /// <remarks>
    /// Found while fixing the one above, by reading the version this replaced: it added the
    /// landing to the new take and the extraction did not, so the first reading of a redrawn
    /// single-stroke take was lost.
    /// </remarks>
    [Fact]
    public void A_restarted_take_holds_the_reading_that_restarted_it()
    {
        var capturing = Ready(manyStrokes: false);

        capturing.Arm(null);
        capturing.Took(Down(10, Tick), true);
        capturing.Took(Above(10, Tick * 2), true);

        capturing.Took(Down(50, Tick * 9), true);

        Assert.Equal(1, capturing.Take!.Count);
        Assert.Equal(50, capturing.Take.Readings[0].X);
    }
}
