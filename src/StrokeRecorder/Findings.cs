using StrokeFieldGuide.Strokes;
using WinPenKit;

namespace StrokeFieldGuide.Recorder;

/// <summary>How much a finding wants to be looked at.</summary>
public enum Tone
{
    /// <summary>A fact worth having, with nothing to decide.</summary>
    Plain,

    /// <summary>The recording is good for what it claims to be.</summary>
    Good,

    /// <summary>Usable, but not for what somebody is likely to assume.</summary>
    Warn,
}

/// <param name="Title">The conclusion. Read on its own, it should still say something.</param>
/// <param name="Body">Why, and what to do about it.</param>
public sealed record Finding(Tone Tone, string Title, string Body);

/// <summary>
/// What a take turned out to be, stated as conclusions.
/// </summary>
/// <remarks>
/// <para>
/// Conclusions and not numbers, which is the browser recorder's own recorded lesson about an
/// earlier version of this screen: it printed the figures and left the reader to decide what
/// they meant, and nobody noticed the two that mattered -- that the input had not been a pen,
/// and that every event carried exactly one sample.
/// </para>
/// <para>
/// So each of these says what it means first. A reader who reads only the titles should still
/// come away knowing whether the recording is worth keeping.
/// </para>
/// </remarks>
public static class Findings
{
    public static IReadOnlyList<Finding> For(Take take)
    {
        var found = new List<Finding>();

        Delivered(found, take);
        Stillness(found, take);
        Status(found, take);

        Series(found, take);
        Landing(found, take);

        Backend(found, take);
        Batching(found, take);
        Pressure(found, take);
        Cadence(found, take);
        Orientation(found, take);
        NearVertical(found, take);
        Pace(found, take);

        Named(found, take);

        return found;
    }

    /// <summary>
    /// How fast the pen was travelling across each gap in reporting, which is the claim.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured as a <b>speed</b> and not a distance, which is the second version of this
    /// check. The first asked whether the pen moved less than a pixel across a gap, on the
    /// strength of eighteen of twenty-one gaps that did -- a threshold chosen after seeing the
    /// data it described. The next take recorded broke it: six of eleven gaps moved 1.9 to 3.7
    /// px, all of them short, and a fixed distance is the wrong test for a quantity that scales
    /// with duration.
    /// </para>
    /// <para>
    /// As a speed it holds. Across every gap over 100 ms recorded so far the pen travels at a
    /// median of 1 px per second and never above 18, against a median of 261 while the device
    /// is reporting -- between twenty and a thousand times slower. Thirty px per second is the
    /// line drawn here, above the worst seen and far below anything the hand does while moving.
    /// </para>
    /// <para>
    /// It is a <b>necessary</b> condition and not a sufficient one: the pen is often equally
    /// still while reporting continues, so this can fail the claim and cannot confirm it.
    /// </para>
    /// </remarks>
    private static void Stillness(List<Finding> found, Take take)
    {
        const double Crawling = 30;

        var gaps = new List<(double Ms, double Moved, double Speed)>();

        foreach (var contact in take.Contacts)
        {
            if (contact.SinceLastSeen is not { } since || contact.LastAirborne is not { } last) continue;
            if (contact.Count == 0) continue;

            var landing = contact.Readings[0];
            var moved = Math.Sqrt(Math.Pow(landing.X - last.X, 2) + Math.Pow(landing.Y - last.Y, 2));

            gaps.Add((since / 1000.0, moved, moved / (since / 1_000_000.0)));
        }

        // Under 100 ms is one or two polls: the pen has had no time to be still or to move,
        // and a speed computed over it is mostly noise.
        var real = gaps.Where(gap => gap.Ms > 100).ToList();

        if (real.Count == 0)
        {
            if (gaps.Count > 0)
            {
                found.Add(new(Tone.Plain, "No gap here lasted longer than 100 ms",
                    $"{gaps.Count} gap{(gaps.Count == 1 ? "" : "s")}, the longest {gaps.Max(g => g.Ms):F0} ms. "
                    + "Too short to say anything about how fast the pen was moving."));
            }

            return;
        }

        var fast = real.Where(gap => gap.Speed >= Crawling).ToList();

        found.Add(new(fast.Count == 0 ? Tone.Good : Tone.Warn,
            fast.Count == 0
                ? $"The pen was crawling across all {real.Count} gap{(real.Count == 1 ? "" : "s")}"
                : $"{fast.Count} of {real.Count} gaps had the pen still moving",
            $"Gaps over 100 ms: {real.Min(g => g.Ms):F0} to {real.Max(g => g.Ms):F0} ms, the pen "
            + $"travelling {real.Min(g => g.Speed):F1} to {real.Max(g => g.Speed):F1} px per second "
            + $"across them. "
            + (fast.Count == 0
                ? $"Everything recorded so far stays under {Crawling:F0}, against a median of 261 "
                  + "while the device is reporting."
                : $"Above {Crawling:F0} px per second is faster than any gap measured so far, and "
                  + "is the observation worth keeping rather than repeating.")));
    }

    /// <summary>
    /// What the device's own status word did, where it reports one.
    /// </summary>
    /// <remarks>
    /// Reported rather than interpreted. Bit 1 on Wintab is a queue overflow, which nothing
    /// has ever read and which would look exactly like the device falling silent -- so if it
    /// ever sets, that is the answer to a question two days of recording has not settled. Bit
    /// 0 is documented as proximity and does not behave as the name suggests on this driver,
    /// so what it actually does is shown rather than assumed.
    /// </remarks>
    private static void Status(List<Finding> found, Take take)
    {
        var all = take.Readings.Concat(take.Aloft).ToList();

        if (all.Count == 0 || all.All(reading => reading.Status == 0)) return;

        var overflow = all.Count(reading => (reading.Status & 0x0002) != 0);

        if (overflow > 0)
        {
            found.Add(new(Tone.Warn, $"The device flagged a queue overflow on {overflow} readings",
                "Bit 1 of the status word. Readings were dropped before this application could "
                + "see them, which is the one thing that looks identical to the device going "
                + "quiet. Anything measured here about gaps in reporting is suspect."));
        }

        // Every distinct value, because the useful thing is which bits move and when, and a
        // summary that decided that in advance would answer only the question it assumed.
        var seen = all.Select(reading => reading.Status).Distinct().Order().ToList();

        found.Add(new(Tone.Plain,
            $"The status word took {seen.Count} distinct value{(seen.Count == 1 ? "" : "s")}",
            string.Join(", ", seen.Take(8).Select(v => $"0x{v:X4}"))
            + (seen.Count > 8 ? ", …" : "")
            + $". In contact it is {Bits(take.Readings)}; in the air {Bits(take.Aloft)}."));
    }

    private static string Bits(IEnumerable<Reading> readings)
    {
        var values = readings.Select(reading => reading.Status).Distinct().Take(4).ToList();

        return values.Count == 0 ? "not recorded" : string.Join(" and ", values.Select(v => $"0x{v:X4}"));
    }

    /// <summary>
    /// Whether the readings that never arrived were never sent, or were thrown away.
    /// </summary>
    /// <remarks>
    /// First, because when it says something is wrong nothing below it can be trusted. It
    /// answers the one question this window cannot answer about itself: the take reconciles
    /// internally whatever happens, and that says nothing about readings the session never
    /// handed over.
    /// </remarks>
    private static void Delivered(List<Finding> found, Take take)
    {
        if (take.Counted is not { } c)
        {
            // Absent rather than zero. The session could not be asked, or was replaced while
            // the take was open, and saying nothing is better than reporting a subtraction
            // across two different counters.
            if (take.Routed > 0)
            {
                found.Add(new(Tone.Plain, "The session could not say what it was given",
                    $"This window saw {take.Routed} readings and kept all of them. The counts "
                    + "beneath it are unavailable for this take, so nothing here can speak for "
                    + "the layer below."));
            }

            return;
        }

        var lost = c.FromDriver - c.Delivered;

        found.Add(new(lost == 0 ? Tone.Good : Tone.Warn,
            lost == 0
                ? $"The session passed on every one of {c.FromDriver} packets"
                : $"{lost} of {c.FromDriver} packets did not reach this window",
            lost == 0
                ? "Counted beneath the session's own filtering. So anything missing from this "
                  + "recording was never sent by the driver, rather than discarded on the way."
                : $"{c.OutsideRegion} were dropped for arriving outside the capture region"
                  + (lost == c.OutsideRegion
                      ? ". That accounts for all of them."
                      : $", which leaves {lost - c.OutsideRegion} unaccounted for.")));

        var stored = take.Count + take.Aloft.Count + take.DroppedOffPad + take.AfterTheStop;

        if (stored != take.Routed)
        {
            found.Add(new(Tone.Warn,
                $"{take.Routed - stored} readings reached this window and are in none of its columns",
                $"{take.Routed} were handed over; {take.Count} are in strokes, {take.Aloft.Count} "
                + $"in the airborne record, {take.DroppedOffPad} were off the pad and "
                + $"{take.AfterTheStop} arrived after the stop. The rest are unaccounted for, "
                + "which is a fault in the recorder rather than anything about the pen."));
        }

        if (take.Routed != c.Delivered)
        {
            found.Add(new(Tone.Warn,
                $"The session delivered {c.Delivered} points and this window saw {take.Routed}",
                "These should be equal. They are counted either side of the same handover, so "
                + "a difference is a fault in the recorder rather than anything about the pen."));
        }
    }

    /// <summary>
    /// How hard each stroke started, against how hard it went on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stated by the person holding the pen before it was measured: driving the pen at the
    /// tablet quickly makes it land hard, so the first few milliseconds of a fast stroke read
    /// much heavier than the rest, and it has consequences for what gets drawn. This reports
    /// the numbers and stops there. Whether the spike is the hand, the nib, or the sensor is
    /// not answerable from one take.
    /// </para>
    /// <para>
    /// Compared against the stroke's own median rather than against a fixed figure, because
    /// strokes are drawn at different weights on purpose and a landing is only heavy relative
    /// to the stroke it begins.
    /// </para>
    /// </remarks>
    private static void Landing(List<Finding> found, Take take)
    {
        var spikes = new List<double>();

        foreach (var contact in take.Contacts)
        {
            if (contact.Count < 12) continue;

            var start = contact.Readings[0].At;

            var opening = contact.Readings
                .Where(reading => reading.At - start <= 25_000)
                .Select(reading => (double)reading.Pressure)
                .ToList();

            var rest = contact.Readings
                .Where(reading => reading.At - start > 25_000)
                .Select(reading => (double)reading.Pressure)
                .Order()
                .ToList();

            if (opening.Count == 0 || rest.Count == 0) continue;

            var median = rest[rest.Count / 2];

            if (median <= 0) continue;

            spikes.Add(opening.Max() / median);
        }

        if (spikes.Count == 0) return;

        var worst = spikes.Max();

        found.Add(new(worst >= 1.2 ? Tone.Warn : Tone.Plain,
            $"The first 25 ms peaked at {worst * 100:F0}% of the stroke's own weight",
            spikes.Count == 1
                ? "One stroke measured. Against the median of everything after the first 25 "
                  + "milliseconds."
                : $"Across {spikes.Count} strokes, {spikes.Min() * 100:F0}% to "
                  + $"{worst * 100:F0}%. Against each stroke's own median after the first 25 "
                  + "milliseconds."));

        var withApproach = take.Contacts.Count(contact => contact.Approach.Count > 0);

        found.Add(new(Tone.Plain,
            withApproach == 0
                ? "No approach was captured"
                : $"The pen in the air is kept for {withApproach} of {take.Strokes} strokes",
            withApproach == 0
                ? "Nothing was hovering over the pad before these strokes began, so there is "
                  + "no record of how the pen arrived. A pen already resting on the tablet, "
                  + "or out of range until it landed, gives this."
                : "Up to a quarter of a second either side, at zero pressure, for position "
                  + "and angles only. It is there so the landing can be read against how the "
                  + "pen arrived rather than on its own."));
    }

    /// <summary>
    /// What the take holds, where it holds more than one stroke.
    /// </summary>
    /// <remarks>
    /// <para>
    /// First, because on a many-stroke take it is the finding that tells a reader how to read
    /// all the others: everything below this line pools every reading in the take, which is
    /// the right thing for a distribution and the wrong thing for anything sequential.
    /// </para>
    /// <para>
    /// The gaps are reported because they are the point of recording a series in one file. A
    /// pen that was off the tablet for two seconds and one that was off it for eighty
    /// milliseconds are not doing the same thing, and it is the sort of difference that is
    /// gone for good if the strokes are saved separately.
    /// </para>
    /// </remarks>
    private static void Series(List<Finding> found, Take take)
    {
        if (take.Strokes < 2) return;

        var lengths = take.Contacts.Select(contact => contact.Count).ToList();

        var gaps = new List<double>();

        for (var each = 1; each < take.Contacts.Count; each++)
        {
            var before = take.Contacts[each - 1].Readings;
            var after = take.Contacts[each].Readings;

            if (before.Count == 0 || after.Count == 0) continue;

            gaps.Add((after[0].At - before[^1].At) / 1000.0);
        }

        found.Add(new(Tone.Good,
            $"{take.Strokes} strokes in one take, {take.Count} readings",
            $"Shortest {lengths.Min()} readings, longest {lengths.Max()}. "
            + (gaps.Count == 0
                ? "No gap could be measured."
                : $"The pen was off the tablet between them for {gaps.Min():F0} to "
                  + $"{gaps.Max():F0} ms.")));

        // Only the strokes are drawn and only the strokes are kept, so a reader looking at
        // the numbers below should know they are pooled rather than sequential.
        found.Add(new(Tone.Plain, "The findings below pool every stroke",
            "Pressure, tilt and cadence are counted across the whole take. Anything about "
            + "speed or direction has to be read per stroke, because two readings either "
            + "side of a lift are not a movement."));
    }

    /// <summary>
    /// Whether anything says what made the recording.
    /// </summary>
    /// <remarks>
    /// Last, and a warning until it is answered, because it is the only finding on this
    /// screen about something a reader can still fix. The rest describe a stroke that has
    /// already been drawn.
    /// </remarks>
    private static void Named(List<Finding> found, Take take)
    {
        var missing = new[]
        {
            take.Tablet.Length == 0 ? "tablet" : null,
            take.Driver.Length == 0 ? "driver" : null,
        }.OfType<string>().ToList();

        found.Add(missing.Count == 0
            ? new(Tone.Good, $"{take.Tablet}, {take.Driver}",
                "Named, so this recording can answer the question most often asked of one, "
                + "which is what made it.")
            : new(Tone.Warn,
                $"The {string.Join(" and the ", missing)} "
                + (missing.Count == 1 ? "is" : "are") + " not named",
                "Name them on the next screen. Without them this recording cannot answer the "
                + "question most often asked of one, which is what made it."));
    }

    /// <summary>
    /// Which backend, and therefore what the numbers in this file are evidence about.
    /// </summary>
    /// <remarks>
    /// Every session here is a pen session, so the browser's "was this a pen" question does
    /// not translate directly. The one that does is which layer answered: a framework backend
    /// declares a fixed range rather than asking the device, so its pressures are evidence
    /// about the framework and not about the hardware.
    /// </remarks>
    private static void Backend(List<Finding> found, Take take)
    {
        var native = take.Api is InputApi.WintabDigitizer or InputApi.WintabSystem;

        found.Add(native
            ? new(Tone.Good, $"{take.Api}, full scale {take.FullScalePressure}",
                "A tablet-native backend, so the pressure and the angles are the hardware "
                + "talking and the full scale is the device's own.")
            : new(Tone.Warn, $"{take.Api}, full scale {take.FullScalePressure}",
                "Not a tablet-native backend. The full scale is the API's fixed range rather "
                + "than anything the device was asked, so these pressures are evidence about "
                + "the framework. Fine for testing the recorder; not evidence about a tablet."));
    }

    /// <summary>
    /// Whether readings arrived in batches, which decides what a naive reader would lose.
    /// </summary>
    /// <remarks>
    /// The analogue of the browser's coalescing finding. There the question is how many
    /// samples a single event carried; here it is how many readings a single poll drained,
    /// and the consequence is the same: a recorder that keeps one per wake-up throws the rest
    /// away and reports a rate that is its own frame rate wearing the tablet's name.
    /// </remarks>
    private static void Batching(List<Finding> found, Take take)
    {
        if (take.Polls == 0) return;

        var each = take.Count / (double)take.Polls;

        found.Add(each <= 1.05
            ? new(Tone.Warn, "One reading per poll",
                "Nothing was batched. Either the device reports no faster than this window "
                + "polls, or the readings are being delivered one at a time. Worth knowing "
                + "before this trace is used to say anything about report rate.")
            : new(Tone.Good,
                $"{Math.Round((1 - 1 / each) * 100)}% would be lost by keeping one reading per poll",
                $"{take.Count} readings arrived in {take.Polls} polls, {each:F1} at a time. "
                + "That fraction is what a recorder reading only one per wake-up throws away."));
    }

    /// <summary>
    /// Whether pressure was reported as often as position.
    /// </summary>
    /// <remarks>
    /// Found in the first real recording anybody made with this tool, and not by looking for
    /// it. Every pressure in that file appeared exactly twice: all 762 pairs of consecutive
    /// readings held the same value, and every run of an unchanged pressure had an even
    /// length. Position changed on almost every step over the same stroke. So the two
    /// channels ran at different rates -- about 240 a second and about 120 -- and the driver
    /// delivered each pressure twice rather than interpolating.
    /// <para>
    /// Worth a finding because nothing about a single reading shows it, and three things
    /// follow: a rate quoted from positions is twice the pressure rate, a width-from-pressure
    /// brush gets a new width every other stamp, and interpolating pressure is not smoothing
    /// away a measurement.
    /// </para>
    /// </remarks>
    private static void Cadence(List<Finding> found, Take take)
    {
        if (take.Count < 8) return;

        var pressures = take.Readings.Select(reading => reading.Pressure).ToList();

        var steps = pressures.Count - 1;
        var held = 0;

        for (var each = 0; each < steps; each++)
        {
            if (pressures[each] == pressures[each + 1]) held++;
        }

        // Half the steps holding is the signature. A stroke drawn at a steady force holds
        // far more than half, so the test is the pairing rather than the count: does every
        // run of an unchanged pressure have an even length.
        var odd = 0;
        var at = 0;

        while (at < pressures.Count)
        {
            var end = at;

            while (end + 1 < pressures.Count && pressures[end + 1] == pressures[at]) end++;

            if ((end - at + 1) % 2 == 1) odd++;

            at = end + 1;
        }

        if (odd > 0 || held < steps / 3) return;

        found.Add(new(Tone.Plain, "Pressure reported at half the rate of position",
            $"Every pressure in this take appears an even number of times, {held} of {steps} "
            + "steps holding the last one. The device reports a new pressure every other "
            + "reading. A rate quoted from positions is twice the pressure rate, and a brush "
            + "taking width from pressure gets a new width every other stamp."));
    }

    private static void Pressure(List<Finding> found, Take take)
    {
        var low = take.Readings.Min(reading => reading.Pressure);
        var high = take.Readings.Max(reading => reading.Pressure);

        if (low == high)
        {
            found.Add(new(Tone.Warn, $"Pressure never moved off {high}",
                "Either the hand held one force the whole way, or the device is not reporting "
                + "pressure at all. This trace cannot tell those apart, and neither can "
                + "anything that reads it."));

            return;
        }

        var used = (high - low) / (double)Math.Max(1, take.FullScalePressure);

        found.Add(new(Tone.Plain, $"Pressure ran {low} to {high}",
            $"{used * 100:F0}% of the device's {take.FullScalePressure}. "
            + (take.Gesture.Id == "pressure-ramp"
                ? "This gesture exists to find the usable range, so the interesting number is "
                  + "where it started rather than where it ended."
                : "The range the hand happened to cover, which this gesture does not ask "
                  + "anything of.")));
    }

    /// <summary>
    /// Whether the recording says anything about how the pen was held.
    /// </summary>
    /// <remarks>
    /// Here because the corpus carries no tilt on purpose, and the whole reason to record real
    /// strokes is to stop that being a gap. A take that reports nothing is still worth having
    /// and should not be mistaken for one that reports a pen held upright.
    /// </remarks>
    private static void Orientation(List<Finding> found, Take take)
    {
        var leaned = take.Readings.Any(reading => reading.Tilted);
        var rotated = take.Readings.Select(reading => reading.Twist).Distinct().Count() > 1;

        if (!leaned)
        {
            found.Add(new(Tone.Warn, "No tilt in this recording",
                "Every reading is upright. That is either a pen held straight up for the "
                + "whole stroke or a device reporting no tilt, and the file cannot say which. "
                + "Nothing reading it should treat it as a measurement of an upright pen."));

            return;
        }

        var lean = take.Readings.Max(reading => reading.Lean);

        found.Add(new(Tone.Good, $"Tilt up to {lean:F0} degrees off vertical",
            rotated
                ? "Lean and barrel rotation both vary, so this take carries the whole of how "
                  + "the pen was held."
                : "Lean varies; barrel rotation does not. Either the pen was not rolled or "
                  + "this one does not report rotation."));
    }

    /// <summary>
    /// The lean below which a direction is not worth having, in degrees.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Measured, after being derived wrongly.</b> This first stood at five, on the
    /// reasoning that the azimuth is worked out from a lean reported in whole degrees and is
    /// therefore uncertain by <c>atan(0.5 / L)</c>. Wintab reports the azimuth directly, so
    /// that derivation does not apply, and seven takes said so: the azimuth's step between
    /// consecutive readings has a median of <b>zero at every lean</b>, and its worst case only
    /// grows past a few degrees below a lean of three — 11 degrees at a lean of 4, 18 at 3,
    /// 27 at 2, 45 at 1.
    /// </para>
    /// <para>
    /// So three, and the shape of the thing is not a band of uncertainty but rare large
    /// jumps. The figure that matters is how much of a stroke is down there at all, which in
    /// those seven takes was none of six and a tenth of the seventh.
    /// </para>
    /// </remarks>
    private const double TooUprightToAim = 3;

    /// <summary>
    /// How much of the stroke was drawn too upright for the lean to have a direction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Here to answer a question rather than to report a number. A nib driven by the azimuth
    /// has nothing to point at while the pen is upright, and there are several ways to handle
    /// that — hold the last direction, freeze below a threshold, let the nib go round as it
    /// straightens, smooth the lean as a vector — all of which are work.
    /// </para>
    /// <para>
    /// None of it is worth doing if a drawing hand never goes near vertical while in contact.
    /// The first real stroke recorded here ran from 15 to 26 degrees end to end and never came
    /// close. One stroke is not an answer; this is how the next twenty give one.
    /// </para>
    /// </remarks>
    private static void NearVertical(List<Finding> found, Take take)
    {
        if (!take.Readings.Any(reading => reading.Tilted)) return;

        var upright = take.Readings.Count(reading => reading.Lean < TooUprightToAim);

        if (upright == 0)
        {
            found.Add(new(Tone.Good, $"Never within {TooUprightToAim:F0}° of upright",
                "So a nib driven by the direction of lean had something to point at for the "
                + "whole stroke. Six of the first seven takes recorded here were the same, "
                + "which is why this is a stated limit rather than a filter."));

            return;
        }

        var share = upright / (double)take.Readings.Count;
        var milliseconds = take.Milliseconds * share;

        found.Add(new(share > 0.05 ? Tone.Warn : Tone.Plain,
            $"{share * 100:F0}% of the stroke within {TooUprightToAim:F0}° of upright",
            $"About {milliseconds:F0} ms of it. Below {TooUprightToAim:F0}° the reported "
            + "azimuth starts making occasional large jumps -- 45 degrees at a lean of one, "
            + "measured -- and a nib driven from it jumps with them. Where in the stroke it "
            + "happened matters as much as how much: at the ends is where a taper is."));
    }

    /// <summary>The take against the brief it was drawn to, rather than against nothing.</summary>
    private static void Pace(List<Finding> found, Take take)
    {
        var seconds = take.Milliseconds / 1000.0;

        var (wanted, low, high) = take.Gesture.Id switch
        {
            "slow-diagonal" => ("three or four seconds", 2.5, 5.0),
            "fast-flick" => ("under half a second", 0.0, 0.5),
            "slow-arc" => ("about three seconds", 2.0, 4.5),
            "pressure-ramp" => ("no particular pace", 0.0, double.MaxValue),
            _ => ("no particular pace", 0.0, double.MaxValue),
        };

        if (take.Gesture.Id == "tap")
        {
            found.Add(take.Count <= 3
                ? new(Tone.Good, $"{take.Count} readings, {seconds:F2} seconds",
                    "A tap: a press and a release with nothing in between, which is what this "
                    + "gesture is for.")
                : new(Tone.Warn, $"{take.Count} readings over {seconds:F2} seconds",
                    "More than a tap. The pen moved, or stayed down long enough to report "
                    + "repeatedly, so this is not the endpoint case it was meant to show."));

            return;
        }

        var right = seconds >= low && seconds <= high;

        found.Add(right
            ? new(Tone.Good, $"{seconds:F2} seconds, which is {wanted}",
                "Drawn at the pace the gesture asked for, so it can be compared with others "
                + "of the same one.")
            : new(Tone.Warn, $"{seconds:F2} seconds, and this gesture wants {wanted}",
                "Usable, but not comparable with takes drawn to the brief. Worth another go "
                + "if the comparison is the point."));
    }
}
