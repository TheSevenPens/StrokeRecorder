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
        Clocks(found, take);
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


    /// <summary>
    /// Whether the pen went quiet before a stroke, or only appears to have.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gap this measures had a <c>Stillness</c> check above it, asking how fast the pen was
    /// travelling across each one. That check is gone: its 30 px/s line was fitted to the gaps
    /// themselves, and a speed over a span of time that did not pass is not a speed. Every
    /// trace before version five carried one clock -- the pen's -- and on that clock the
    /// airborne record stops for a tenth of a second or more before almost every landing. Three
    /// explanations for that were written into the notes and all three were withdrawn, and the
    /// reason they could be written at all is that a single clock cannot distinguish a device
    /// that stopped sending from a device that sent on time and stamped the packet late.
    /// </para>
    /// <para>
    /// Two clocks can. The recorder drains in batches, so readings that reached the application
    /// in one poll carry an identical arrival: if the last reading in the air and the first
    /// reading in contact came across together, no packet was ever late and the gap is in the
    /// pen's timestamp. There is no appeal to resolution in that -- it is two numbers being
    /// equal.
    /// </para>
    /// </remarks>
    private static void Clocks(List<Finding> found, Take take)
    {
        // A landing with the pen seen in the air immediately before it. Anything else has
        // nothing to compare: a stroke with no approach is a stroke this cannot speak about.
        var landings = take.Contacts
            .Where(contact => contact.Approach.Count > 0 && contact.Count > 0)
            .Select(contact => (Air: contact.Approach[^1], Ink: contact.Readings[0]))
            .Where(pair => pair.Air.Arrived > 0 && pair.Ink.Arrived > 0)
            .Select(pair => (
                Device: pair.Ink.At - pair.Air.At,
                Host: pair.Ink.Arrived - pair.Air.Arrived))
            .ToList();

        if (landings.Count == 0) return;

        // Before believing either clock, check the new one is a clock. A stamp that never
        // moves reports every gap as the device's fault, and a stamp that moves on every
        // reading reports none of them -- both are wrong in the direction of whatever was
        // asked, and neither would look wrong in the output. So the take has to show the
        // property the batching gives it: many readings, sharing arrivals, over more than one
        // poll. This is the packet-counter lesson written down again: a broken instrument
        // should say nothing rather than say something.
        var all = take.Readings.Concat(take.Aloft).Where(reading => reading.Arrived > 0).ToList();

        if (all.Count >= 50)
        {
            var arrivals = all.Select(reading => reading.Arrived).Distinct().Count();
            var elapsed = all.Max(reading => reading.Arrived) - all.Min(reading => reading.Arrived);

            if (arrivals <= 1 || elapsed < PollMicroseconds)
            {
                found.Add(new(Tone.Warn, "The arrival clock is not running, so the two clocks say nothing",
                    $"{all.Count} readings carry {arrivals} distinct arrival"
                    + $"{(arrivals == 1 ? "" : "s")} spanning {Ms(elapsed)}, which cannot happen "
                    + "if each poll stamps its own batch. Whatever is wrong is in the recorder, "
                    + "not the pen. Nothing below about the pen's clock is worth reading."));

                return;
            }

            if (arrivals == all.Count)
            {
                found.Add(new(Tone.Warn, "Every reading has its own arrival, which the recorder cannot produce",
                    $"All {all.Count} of them differ, but they are drained in batches and a "
                    + "batch is stamped once. The stamp is being taken per reading somewhere, "
                    + "so two readings arriving together no longer look like it -- which is the "
                    + "only thing this comparison relies on."));

                return;
            }
        }

        // Five times the 4 ms a packet interval actually runs at. Not tuned: the gaps this is
        // about are a hundred milliseconds and more, and anything between 20 ms and that is a
        // case nobody has recorded yet and would want to look at by hand anyway.
        const long Quiet = 20_000;

        var silent = landings.Where(landing => landing.Device > Quiet).ToList();

        if (silent.Count == 0)
        {
            found.Add(new(Tone.Good, "The pen reported continuously into every landing",
                $"On all {landings.Count} of them the pen's own clock runs straight from the "
                + "last reading in the air into the first in contact. Nothing here needs the "
                + "second clock to explain it."));

            return;
        }

        // The whole test. One poll delivered both packets, so whatever the pen's clock says
        // happened between them, no time passed in which this application could have been
        // waiting.
        var together = silent.Count(landing => landing.Host == 0);

        // Two polls. The batch boundary has to fall somewhere, and a landing that misses it by
        // one is not evidence of a stall.
        var prompt = silent.Count(landing => landing.Host <= 2 * PollMicroseconds);

        var worst = silent.OrderByDescending(landing => landing.Device).First();

        var span = $"The pen's clock loses {Ms(silent.Min(l => l.Device))} to "
            + $"{Ms(silent.Max(l => l.Device))} before a landing";

        if (together == silent.Count)
        {
            found.Add(new(Tone.Good,
                $"The pen never went quiet -- its clock did, on all {silent.Count} landings",
                span + ", and on every one of them the last reading in the air and the first "
                + "reading in contact arrived here in the same poll. No packet was late and "
                + "none is missing: the device stamped the contact packet with a time it did "
                + "not arrive at. Anything measured from gaps in 'at' -- how still the pen was, "
                + "how long it hovered, when it touched down -- is measuring the stamp."));
        }
        else if (prompt > silent.Count / 2)
        {
            found.Add(new(Tone.Warn,
                $"{prompt} of {silent.Count} gaps are in the pen's clock, not in the reporting",
                span + $". {together} of them arrived in a single poll and {prompt} within two, "
                + "which is the device stamping late rather than falling silent. The rest took "
                + $"real time to arrive -- the largest gap, {Ms(worst.Device)} on the pen's "
                + $"clock, took {Ms(worst.Host)} on this one. Two mechanisms, and this take "
                + "does not separate which landings had which."));
        }
        else
        {
            found.Add(new(Tone.Warn,
                $"The pen really did stop reporting before {silent.Count - prompt} landings",
                span + ", and this application waited out most of it: only "
                + $"{prompt} of {silent.Count} arrived within two polls. Packets were not "
                + "merely stamped late, they were not there to be drained. Whether they were "
                + "never sent or were dropped before delivery is a question for the driver's "
                + "own counters, not for these two clocks."));
        }
    }

    /// <summary>The recorder's poll, in microseconds. A gap smaller than one is not a gap.</summary>
    private const long PollMicroseconds = 16_000;

    private static string Ms(long microseconds) =>
        $"{microseconds / 1000.0:0.#} ms";

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
        // Two separate questions, and only one of them needs the session's counters.
        //
        //   did this window keep everything it was handed?   its own numbers answer it
        //   did the session hand over everything it got?     only the session can say
        //
        // They used to be one branch, and the first was inside the second: where the session
        // could not be asked, this reported "saw N readings and kept all of them" without
        // checking, and returned before the arithmetic that would have found otherwise.
        // Given 100 routed and two stored, it said exactly that. An instrument may say it
        // does not know; it may not say the data is whole when it has not looked.
        Kept(found, take);

        if (take.Counted is not { } c)
        {
            if (take.Routed > 0)
            {
                found.Add(new(Tone.Plain, "The session could not say what it was given",
                    $"This window was handed {take.Routed} readings. The counts beneath it are "
                    + "unavailable for this take -- the session could not be asked, or was "
                    + "replaced while the take was open -- so nothing here can speak for the "
                    + "layer below. What this window did with what it was handed is above."));
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

        if (take.Routed != c.Delivered)
        {
            found.Add(new(Tone.Warn,
                $"The session delivered {c.Delivered} points and this window saw {take.Routed}",
                "These should be equal. They are counted either side of the same handover, so "
                + "a difference is a fault in the recorder rather than anything about the pen."));
        }
    }

    /// <summary>
    /// Whether every reading this window was handed is in one of its columns.
    /// </summary>
    /// <remarks>
    /// Answerable from the take alone, so it is answered whatever the session could or could
    /// not say. Every reading handed over has exactly one place it should have ended up: in a
    /// stroke, in the airborne record, left out because the airborne record was not asked
    /// for, dropped for being off the pad, or arriving after the stop. A reading in none of
    /// them is one this window lost.
    /// </remarks>
    private static void Kept(List<Finding> found, Take take)
    {
        if (take.Routed == 0) return;

        var stored = take.Count + take.Aloft.Count + take.DroppedOffPad + take.AfterTheStop
                     + take.LeftOut;

        if (stored == take.Routed)
        {
            found.Add(new(Tone.Good,
                $"Every one of {take.Routed} readings this window was handed is accounted for",
                $"{take.Count} in strokes, {take.Aloft.Count} in the airborne record, "
                + $"{take.LeftOut} airborne and deliberately not kept, "
                + $"{take.DroppedOffPad} off the pad, {take.AfterTheStop} after the stop. "
                + "Counted from this take alone, so it holds whether or not the session below "
                + "could be asked what it was given."));

            return;
        }

        var missing = take.Routed - stored;

        found.Add(new(Tone.Warn,
            missing > 0
                ? $"{missing} readings reached this window and are in none of its columns"
                : $"{-missing} more readings are stored than were handed over",
            $"{take.Routed} were handed over; {take.Count} are in strokes, {take.Aloft.Count} "
            + $"in the airborne record, {take.LeftOut} were airborne and deliberately not "
            + $"kept, {take.DroppedOffPad} were off the pad and {take.AfterTheStop} arrived "
            + "after the stop. That is a fault in the recorder rather than anything about "
            + "the pen."));
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
        // On the host clock where there is one, and said to be otherwise where there is not.
        //
        // This used to read take.Milliseconds, which is the pen's own timestamp -- a packet
        // counter running at 0.673 of real time on the hardware measured here. A take lasting
        // three seconds reported two, and was then told it had missed a three-to-four second
        // brief it had in fact met.
        var (seconds, on) = Timing.Spanned(take.Readings);

        if (on == Clock.Pen)
        {
            found.Add(new(Tone.Plain,
                $"{seconds:F2} seconds on the pen's own clock, which is not a clock",
                "This recording carries no host timestamp, so how long it took cannot be said "
                + "in seconds. The pen's stamp advances once per packet delivered rather than "
                + "with time, so the number above is in packets-worth rather than in seconds "
                + "and is not comparable with a brief or with another take."));

            return;
        }

        if (on == Clock.None)
        {
            found.Add(new(Tone.Plain, "How long this took cannot be said",
                "Neither clock moved across this recording."));

            return;
        }

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
