using StrokeRecorder;
using StrokeKit.Strokes;
using StrokeKit.Surfaces;
using WinPenKit;

namespace StrokeRecorder.Tests;

/// <summary>
/// That the recorder does not read the pen's packet counter as elapsed time.
/// </summary>
/// <remarks>
/// <para>
/// Reported on <c>#85</c>. The pen's timestamp advances a flat 4.166 ms per delivered packet
/// whatever the elapsed time, and on the hardware measured here runs at 0.673 of real time.
/// It looks exactly like a clock and is not one.
/// </para>
/// <para>
/// The correction factor is deliberately not encoded anywhere. It is a property of a device
/// that somebody measured, not a constant, and a clock model carrying it would be wrong on
/// the next tablet.
/// </para>
/// </remarks>
public class TimeSemantics
{
    /// <summary>
    /// Readings whose two clocks disagree, as the reviewer's probe had them.
    /// </summary>
    /// <param name="onHost">How long it really took, in seconds.</param>
    /// <param name="onPen">What the pen's counter would say.</param>
    private static List<Reading> Disagreeing(double onHost, double onPen, int count = 30)
    {
        var readings = new List<Reading>();

        for (var each = 0; each < count; each++)
        {
            var along = each / (double)(count - 1);

            readings.Add(new Reading(
                X: 20 + along * 200, Y: 100, Pressure: 600,
                At: (long)(along * onPen * 1e6),
                Height: 0, Status: 0, Lean: 0, Azimuth: 0, Twist: 0,
                Arrived: (long)(along * onHost * 1e6)));
        }

        return readings;
    }

    /// <summary>Readings delivered in batches, which is how a real drain hands them over.</summary>
    private static List<Reading> InBatches(int batches, int per, double secondsApart, double pxPerBatch)
    {
        var readings = new List<Reading>();
        var x = 0.0;

        for (var batch = 0; batch < batches; batch++)
        {
            // Every reading of a batch shares one arrival exactly. That is deliberate, and it
            // is why time within a batch is unobserved.
            var arrived = (long)(batch * secondsApart * 1e6);

            for (var each = 0; each < per; each++)
            {
                x += pxPerBatch / per;

                readings.Add(new Reading(
                    X: x, Y: 0, Pressure: 600, At: 0,
                    Height: 0, Status: 0, Lean: 0, Azimuth: 0, Twist: 0,
                    Arrived: arrived));
            }
        }

        return readings;
    }

    [Fact]
    public void A_duration_is_taken_from_the_host_clock_when_there_is_one()
    {
        var (seconds, on) = Timing.Spanned(Disagreeing(onHost: 3, onPen: 2));

        Assert.Equal(Clock.Host, on);
        Assert.Equal(3, seconds, 2);
    }

    [Fact]
    public void A_take_that_took_three_seconds_is_not_reported_as_two()
    {
        // The reviewer's case end to end: a slow-diagonal take lasting three seconds, which
        // the pen's counter calls two. It used to be told it had missed a three-to-four
        // second brief that it met.
        var take = Of("slow-diagonal", Disagreeing(onHost: 3, onPen: 2));

        var found = Findings.For(take);

        Assert.DoesNotContain(found, one => one.Title.Contains("2.00 seconds"));

        Assert.Contains(found, one =>
            one.Title.Contains("3.0") && one.Title.Contains("three or four seconds"));
    }

    [Fact]
    public void Without_a_host_clock_a_duration_is_not_offered_in_seconds()
    {
        var onlyPen = Disagreeing(onHost: 0, onPen: 2)
            .Select(reading => reading with { Arrived = 0 })
            .ToList();

        var (_, on) = Timing.Spanned(onlyPen);

        Assert.Equal(Clock.Pen, on);

        var found = Findings.For(Of("slow-diagonal", onlyPen));

        // It may say it does not know. It may not judge a brief in seconds against a counter.
        Assert.Contains(found, one => one.Title.Contains("not a clock"));

        Assert.DoesNotContain(found, one => one.Title.Contains("three or four seconds"));
    }

    [Fact]
    public void Speed_is_one_estimate_per_batch_and_does_not_drop_at_every_boundary()
    {
        // Four batches of five readings, 100 px and a tenth of a second apart: 1000 px/s
        // throughout, and a hand travelling evenly.
        var readings = InBatches(batches: 4, per: 5, secondsApart: 0.1, pxPerBatch: 100);

        var speeds = Timing.Speeds(readings);
        var measured = speeds.Where(one => one is not null).Select(one => one!.Value).ToList();

        Assert.NotEmpty(measured);

        // Every estimate is the same, because the hand did the same thing throughout. The
        // version this replaced divided one step by a whole batch's elapsed time at each
        // boundary, so it read a fifth of the truth there and carried a stale value between.
        Assert.All(measured, speed => Assert.Equal(1000, speed, 0));
    }

    [Fact]
    public void There_is_no_speed_without_a_host_clock()
    {
        var onlyPen = Disagreeing(onHost: 0, onPen: 2)
            .Select(reading => reading with { Arrived = 0 })
            .ToList();

        Assert.All(Timing.Speeds(onlyPen), one => Assert.Null(one));
    }

    [Fact]
    public void A_replay_follows_the_recorded_times_rather_than_a_fixed_step()
    {
        // A stroke that pauses: two batches close together, then a long gap, then two more.
        var readings = new List<Reading>
        {
            Reading(0, 0), Reading(10, 100_000), Reading(20, 2_000_000), Reading(30, 2_100_000),
        };

        var schedule = Timing.Schedule(readings, slower: 20);

        Assert.NotNull(schedule);

        // The gap survives: the third reading is due far later than the second, in proportion
        // to the pause the hand actually made.
        var first = schedule![1] - schedule[0];
        var pause = schedule[2] - schedule[1];

        Assert.True(pause > first * 10,
            $"the pause played as {pause:F0} ms against {first:F0}, so it was flattened");

        // And the whole thing is twenty times its recorded length.
        Assert.Equal(2.1 * 1000 * 20, schedule[^1], 0);
    }

    [Fact]
    public void Readings_that_arrived_together_are_due_together()
    {
        var readings = InBatches(batches: 2, per: 3, secondsApart: 0.1, pxPerBatch: 10);

        var schedule = Timing.Schedule(readings, slower: 20)!;

        Assert.Equal(schedule[0], schedule[1]);
        Assert.Equal(schedule[1], schedule[2]);
        Assert.NotEqual(schedule[2], schedule[3]);
    }

    private static Reading Reading(double x, long arrived) =>
        new(X: x, Y: 0, Pressure: 600, At: 0, Height: 0, Status: 0,
            Lean: 0, Azimuth: 0, Twist: 0, Arrived: arrived);

    private static Take Of(string gestureId, IReadOnlyList<Reading> readings)
    {
        var gesture = Gestures.All.FirstOrDefault(one => one.Id == gestureId) ?? Gestures.All[0];

        var take = new Take(gesture, InputApi.WintabDigitizer, 32767, new InkTransform(1, 1, 0, 0));

        var contact = take.Begin();

        foreach (var reading in readings) contact.Add(reading);

        return take;
    }

    /// <summary>A take as the live path builds one, where every reading is routed first.</summary>
    private static Take Routed(IReadOnlyList<Reading> readings)
    {
        var take = Of("slow-diagonal", readings);

        foreach (var reading in readings) take.Routing(reading);

        return take;
    }

    [Fact]
    public void A_takes_one_line_description_is_on_the_host_clock()
    {
        var said = Of("slow-diagonal", Disagreeing(onHost: 3, onPen: 2)).Describe();

        Assert.Contains("3000 ms", said);
        Assert.DoesNotContain("2000 ms", said);
    }

    [Fact]
    public void A_routed_take_lasts_on_the_host_clock()
    {
        // The live path, where readings reach the take through Routing rather than being
        // added to a contact by hand. Both have to answer the same way.
        var (seconds, on) = Routed(Disagreeing(onHost: 3, onPen: 2)).Lasted;

        Assert.Equal(Clock.Host, on);
        Assert.Equal(3, seconds, 2);
    }

    [Fact]
    public void A_rebased_first_arrival_is_a_time_and_not_a_missing_stamp()
    {
        // Every take's first arrival is zero once the format has rebased it. Reading that as
        // "unstamped" and starting from the second batch shortens every reopened take.
        var readings = Disagreeing(onHost: 3, onPen: 2);

        Assert.Equal(0, readings[0].Arrived);

        Assert.Equal(Clock.Host, Routed(readings).Lasted.On);
        Assert.Equal(3, Routed(readings).Lasted.Seconds, 2);
    }

    [Fact]
    public void Without_a_host_clock_a_description_says_which_clock_it_is_on()
    {
        var onlyThePen = Disagreeing(onHost: 3, onPen: 2)
            .Select(reading => reading with { Arrived = 0 })
            .ToList();

        var said = Of("slow-diagonal", onlyThePen).Describe();

        Assert.Contains("on the pen's counter", said);
    }

    [Fact]
    public void A_take_with_nothing_in_it_does_not_claim_a_duration()
    {
        var take = new Take(Gestures.All[0], InputApi.WintabDigitizer, 32767,
                            new InkTransform(1, 1, 0, 0));

        Assert.Equal(Clock.None, take.Lasted.On);
        Assert.Contains("an unrecorded length of time", take.Describe());
    }

    [Fact]
    public void A_take_read_back_from_a_file_is_as_long_as_it_is_not_as_old()
    {
        // Reopen restores recordedAt from the file and never sets StoppedAt, so Recording --
        // a wall clock from arming to stopping -- measures how long the file has existed.
        // Opening yesterday's take put 115,076 seconds on a readout that said "stopped".
        var take = new Take(Gestures.All[0], InputApi.WintabDigitizer, 32767,
                            new InkTransform(1, 1, 0, 0))
        {
            At = DateTimeOffset.Now.AddHours(-32),
        };

        var contact = take.Begin();

        foreach (var reading in Disagreeing(onHost: 3, onPen: 2)) contact.Add(reading);

        // Nothing was handed to it by a window, which is what says it was read and not
        // recorded. This is the signal the clock readout keys off.
        Assert.Equal(0, take.Routed);

        Assert.True(take.Recording / 1000 > 100_000, "the age of the file, which is not its length");

        Assert.Equal(3, take.Lasted.Seconds, 2);
    }

    [Fact]
    public void The_upright_finding_measures_its_share_on_the_host_clock()
    {
        // A lean of one degree: tilted, so the finding runs at all, and inside the three
        // degrees it calls too upright to aim, so the share is the whole stroke. The
        // milliseconds that share is of used to come from the pen's counter.
        var barelyLeaning = Disagreeing(onHost: 3, onPen: 2)
            .Select(reading => reading with { Lean = 1 })
            .ToList();

        var found = Findings.For(Of("slow-diagonal", barelyLeaning));

        var upright = found.FirstOrDefault(one => one.Title.Contains("of upright"));

        Assert.NotNull(upright);
        Assert.Contains("3000 ms", upright.Body);
        Assert.DoesNotContain("2000 ms", upright.Body);
    }
}
