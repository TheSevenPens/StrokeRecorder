using StrokeKit.Strokes;

namespace StrokeRecorder;

/// <summary>
/// How far each stroke of a take went, in millimetres, and how fast.
/// </summary>
/// <remarks>
/// <para>
/// Needs <see cref="ActiveArea"/>: the readings are pixels and only the scale says what a pixel
/// is. Each step is scaled <b>per axis</b> before the two components are combined, because a
/// mapping that stretches the tablet onto a desktop of another shape makes a pixel a different
/// distance across than down.
/// </para>
/// <para>
/// <b>Path and chord are both given because they answer different questions and the gap
/// between them is information.</b> The path is the sum of every step, and a noisy digitizer
/// over-counts it -- more at a higher report rate -- so it is an upper bound on what the hand
/// did and not the distance drawn. The chord is start to end, which jitter cannot lengthen, and
/// which undercounts any stroke that curves. A path far longer than its chord is a curve or a
/// noisy device, and this does not say which.
/// </para>
/// <para>
/// <b>Speed is on the host clock or not at all.</b> Over the whole stroke, first reading to
/// last, which sidesteps the fact that time within a drained batch is unobserved -- it needs
/// only the two ends. The pen's own timestamp is a packet counter, and a speed in millimetres
/// per counter-second is not a speed.
/// </para>
/// </remarks>
public static class Distances
{
    /// <param name="PathMm">Every step added up.</param>
    /// <param name="ChordMm">First reading to last, in a straight line.</param>
    /// <param name="Seconds">How long the stroke took on the host clock, or null without one.</param>
    public readonly record struct Stroke(double PathMm, double ChordMm, double? Seconds)
    {
        /// <summary>Average speed over the stroke, or null where there is no time to divide by.</summary>
        public double? MmPerSecond => Seconds is > 0 ? PathMm / Seconds : null;
    }

    /// <summary>One stroke's readings, measured on this tablet.</summary>
    public static Stroke Of(IReadOnlyList<Reading> readings, ActiveArea area)
    {
        var path = 0.0;

        for (var each = 1; each < readings.Count; each++)
        {
            path += area.Millimetres(
                readings[each].X - readings[each - 1].X,
                readings[each].Y - readings[each - 1].Y);
        }

        var chord = readings.Count < 2
            ? 0
            : area.Millimetres(readings[^1].X - readings[0].X, readings[^1].Y - readings[0].Y);

        var (seconds, on) = Timing.Spanned(readings);

        return new Stroke(path, chord, on == Clock.Host ? seconds : null);
    }

    /// <summary>Every stroke of a take that has more than one reading, in order.</summary>
    /// <remarks>
    /// A stroke of one reading is a tap with no length to speak of, and including it would pull
    /// the shortest figure to zero and say nothing about the hand.
    /// </remarks>
    public static IReadOnlyList<Stroke> Of(Take take, ActiveArea area) =>
        [.. take.Contacts
            .Where(contact => contact.Readings.Count > 1)
            .Select(contact => Of(contact.Readings, area))];
}
