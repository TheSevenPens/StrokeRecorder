using StrokeKit.Strokes;

namespace StrokeFieldGuide.Recorder;

/// <summary>Which clock a set of readings can be measured on.</summary>
public enum Clock
{
    /// <summary>Neither. Nothing here can be dated or timed.</summary>
    None,

    /// <summary>
    /// Only the pen's own timestamp, which is a packet counter rather than a clock.
    /// </summary>
    /// <remarks>
    /// On the hardware measured here it advances a flat 4.166 ms per delivered packet
    /// whatever the elapsed time and runs at 0.673 of real time, resynchronising at each
    /// contact transition. A duration read off it is in counter-time, and the ratio to real
    /// time is a property of a device rather than a constant, so nothing here converts.
    /// </remarks>
    Pen,

    /// <summary>The host's monotonic clock, stamped when the batch was drained.</summary>
    Host,
}

/// <summary>
/// What can and cannot be said about when things happened.
/// </summary>
/// <remarks>
/// Named Timing rather than Clocks because Findings already has a method by that name, and a
/// method beats a type at a call site. The third such collision in this application: see
/// PenBackends, which could be neither Pen nor Backends.
/// </remarks>
/// <remarks>
/// <para>
/// Here rather than spread through the findings and the analyser, because the answers were
/// different in each and two of them were wrong. <c>Findings.Pace</c> judged a gesture's
/// duration against a brief in seconds using the pen's counter, so a take lasting three
/// seconds reported two and was told it had missed a three-to-four second brief. The
/// analyser's speed divided one step by an arrival difference, and arrival differences within
/// a batch are zero because the whole batch shares one stamp.
/// </para>
/// <para>
/// <b>The rule everything here follows.</b> The pen's timestamp is evidence about the device
/// and not a measurement of elapsed time. Where the host clock exists, use it. Where it does
/// not, say so rather than quietly answering on the other one.
/// </para>
/// </remarks>
public static class Timing
{
    /// <summary>Which clock these readings carry.</summary>
    public static Clock Available(IReadOnlyList<Reading> readings)
    {
        if (readings.Count < 2) return Clock.None;

        foreach (var reading in readings)
        {
            if (reading.Arrived != 0) return Clock.Host;
        }

        // A pen clock that never moves is not one either: some backends supply no timestamp
        // at all and every reading carries zero.
        return readings[^1].At != readings[0].At ? Clock.Pen : Clock.None;
    }

    /// <summary>
    /// How long these readings span, and on which clock.
    /// </summary>
    /// <remarks>
    /// The clock is returned with the number because they cannot be separated: seconds on the
    /// host clock are seconds, and "seconds" on the pen's are however long it took the device
    /// to send that many packets.
    /// </remarks>
    public static (double Seconds, Clock On) Spanned(IReadOnlyList<Reading> readings)
    {
        var clock = Available(readings);

        if (clock == Clock.None) return (0, Clock.None);

        var first = readings[0];
        var last = readings[^1];

        return clock == Clock.Host
            ? ((last.Arrived - first.Arrived) / 1e6, Clock.Host)
            : ((last.At - first.At) / 1e6, Clock.Pen);
    }

    /// <summary>
    /// Where each drained batch ends, as an index into the readings.
    /// </summary>
    /// <remarks>
    /// Readings handed over in one drain share an arrival stamp exactly, which is deliberate:
    /// they did arrive together, and giving each its own would invent a spread the delivery
    /// did not have. The consequence is that <b>time within a batch is unobserved</b>, and
    /// anything asking how fast the pen was moving between two readings of the same batch is
    /// asking a question the recording cannot answer.
    /// </remarks>
    public static IReadOnlyList<int> Batches(IReadOnlyList<Reading> readings)
    {
        var ends = new List<int>();

        for (var each = 1; each < readings.Count; each++)
        {
            if (readings[each].Arrived != readings[each - 1].Arrived) ends.Add(each - 1);
        }

        if (readings.Count > 0) ends.Add(readings.Count - 1);

        return ends;
    }

    /// <summary>
    /// How fast the pen was travelling, in pixels a second, or null where it cannot be said.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One estimate per batch, spread across the readings in it.</b> The observable
    /// quantity is the distance travelled between two drains divided by the time between
    /// them; a reading's own step is real but the time it took is not recorded. So every
    /// reading of a batch carries that batch's estimate, and the estimate is of the average
    /// over the batch rather than of the speed at any reading in it.
    /// </para>
    /// <para>
    /// The version this replaces divided one step by one arrival difference and, where that
    /// difference was zero, carried the previous reading's answer forward. Carrying a value
    /// forward does not measure anything, and at the next batch boundary the numerator was
    /// one step where the denominator covered the whole batch's travel — so the speed dropped
    /// by roughly the batch size at every boundary, which is an artefact of the drain rather
    /// than anything the hand did.
    /// </para>
    /// <para>
    /// <b>Null where there is no host clock</b>, rather than an answer on the packet counter.
    /// A speed in pixels per counter-second is not a speed.
    /// </para>
    /// <para>
    /// <b>What this still cannot remove.</b> A poll every 16 ms against a pen reporting every
    /// 6.2 ms catches two readings sometimes and three others, so consecutive batches cover
    /// different distances in similar times and the estimate saws up and down by roughly a
    /// third. That is the drain beating against the report rate and not the hand. It is
    /// visible in the channel, it is smaller than the drop-per-boundary it replaces, and the
    /// label says the number is a batch average so that a reader knows what granularity they
    /// are looking at. Removing it would mean a window longer than one batch, which is a
    /// different estimator and should be chosen deliberately rather than slipped in here.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<double?> Speeds(IReadOnlyList<Reading> readings)
    {
        var speeds = new double?[readings.Count];

        if (Available(readings) != Clock.Host) return speeds;

        var ends = Batches(readings);
        var from = 0;

        for (var batch = 1; batch < ends.Count; batch++)
        {
            var was = ends[batch - 1];
            var now = ends[batch];

            var seconds = (readings[now].Arrived - readings[was].Arrived) / 1e6;

            if (seconds <= 0)
            {
                from = was + 1;

                continue;
            }

            // Every step from the last batch's final reading to this one's, which is the
            // travel the elapsed time actually covers.
            var travelled = 0.0;

            for (var each = was + 1; each <= now; each++)
            {
                travelled += Math.Sqrt(
                    Math.Pow(readings[each].X - readings[each - 1].X, 2)
                    + Math.Pow(readings[each].Y - readings[each - 1].Y, 2));
            }

            var speed = travelled / seconds;

            for (var each = was + 1; each <= now; each++) speeds[each] = speed;

            from = now + 1;
        }

        // The first batch has no batch before it, so nothing timed it. The second batch's
        // estimate is the nearest thing there is, and saying so beats a zero that reads as a
        // pen standing still.
        var firstMeasured = speeds.FirstOrDefault(one => one is not null);

        for (var each = 0; each <= ends.FirstOrDefault(); each++) speeds[each] = firstMeasured;

        return speeds;
    }

    /// <summary>
    /// When each reading should be shown, in milliseconds from the start of a replay.
    /// </summary>
    /// <param name="slower">
    /// How much slower than the hand. At the rate a pen reports, a flick is over in sixty
    /// milliseconds and a replay at that speed shows what watching the hand showed, which is
    /// nothing.
    /// </param>
    /// <remarks>
    /// <para>
    /// Against the recorded times rather than a fixed step per reading. Stepping one reading
    /// every 120 ms is a way of looking through a recording, and a perfectly good one, but it
    /// is not a replay at a twentieth of the speed it was drawn: a pause where the hand
    /// stopped plays at the same rate as a flick.
    /// </para>
    /// <para>
    /// Readings sharing a timestamp are due together, which is the honest rendering of a
    /// batch: they arrived together and there is nothing recorded to spread them over.
    /// </para>
    /// <para>
    /// <b>Null where there is no host clock.</b> The caller then steps rather than replays,
    /// and should say which it is doing.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<double>? Schedule(IReadOnlyList<Reading> readings, double slower)
    {
        if (Available(readings) != Clock.Host) return null;

        var began = readings[0].Arrived;

        return [.. readings.Select(reading => (reading.Arrived - began) / 1000.0 * slower)];
    }
}
