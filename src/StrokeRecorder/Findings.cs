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
    /// Not a taste. This tablet reports the lean as a whole number, so a lean of L is really
    /// L give or take a half, and the direction it implies is uncertain by about
    /// <c>atan(0.5 / L)</c> — 27 degrees at a lean of 1, 14 at 2, and under 6 by the time L
    /// reaches 5. Five is where the uncertainty falls below what anyone would notice in a nib.
    /// </remarks>
    private const double TooUprightToAim = 5;

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
                + "whole stroke, and the near-vertical case cost this recording nothing."));

            return;
        }

        var share = upright / (double)take.Readings.Count;
        var milliseconds = take.Milliseconds * share;

        found.Add(new(share > 0.05 ? Tone.Warn : Tone.Plain,
            $"{share * 100:F0}% of the stroke within {TooUprightToAim:F0}° of upright",
            $"About {milliseconds:F0} ms of it. The lean reports in whole degrees, so below "
            + $"{TooUprightToAim:F0}° the direction it implies is uncertain by more than a few "
            + "degrees and a nib driven from it has little to go on. Where in the stroke it "
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
