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
}
