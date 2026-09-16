using StrokeFieldGuide.Strokes;
using StrokeFieldGuide.Surfaces;
using WinPenKit;

namespace StrokeFieldGuide.Recorder;

/// <summary>Where a take has got to.</summary>
public enum Capture
{
    /// <summary>Nothing armed. Drawing on the pad does nothing.</summary>
    Idle,

    /// <summary>Armed and waiting. The next reading in contact starts the take.</summary>
    Armed,

    /// <summary>The tip is down and readings are being kept.</summary>
    Drawing,

    /// <summary>The tip lifted. There is a stroke to look at.</summary>
    Taken,
}

/// <summary>
/// One attempt at one gesture: the readings, and enough about the circumstances to read them
/// again later.
/// </summary>
/// <remarks>
/// <para>
/// <b>The circumstances are frozen when the take starts, not when it is saved.</b> This is
/// carried over from the browser recorder, where it was learned the expensive way: by save
/// time the drawing area is on a different screen of the wizard, its box measures zero, and a
/// trace came out looking complete while being unable to place a single position. The same
/// trap is here in a different shape -- the pad is not on screen during review, and the view
/// it was drawn through is recentred whenever the window is resized.
/// </para>
/// <para>
/// So the transform is taken once, at the moment the pen goes down, and every reading in the
/// take is placed through that one. A window dragged to another monitor mid-stroke then
/// produces a take that is wrong in a visible, explainable way rather than one that is
/// half in each frame of reference.
/// </para>
/// </remarks>
public sealed class Take(Gesture gesture, InputApi api, int fullScalePressure, InkTransform placed)
{
    private readonly List<Reading> _readings = [];

    public Gesture Gesture => gesture;

    public InputApi Api => api;

    /// <summary>The device's full scale, which the readings do not carry and cannot.</summary>
    public int FullScalePressure => fullScalePressure;

    /// <summary>The transform the whole take was placed through. Frozen at the first contact.</summary>
    public InkTransform Placed => placed;

    public DateTimeOffset At { get; } = DateTimeOffset.Now;

    public IReadOnlyList<Reading> Readings => _readings;

    public int Count => _readings.Count;

    /// <summary>
    /// How many polls contributed to this take.
    /// </summary>
    /// <remarks>
    /// Kept because readings and polls are two different clocks and the ratio is the only
    /// thing that says whether the device is reporting faster than this window wakes up. A
    /// recorder that never counted its own wake-ups could report a rate that was its frame
    /// rate wearing the tablet's name.
    /// </remarks>
    public int Polls { get; private set; }

    public void Polled() => Polls++;

    /// <summary>Why the take ended, for a reader who has only the file.</summary>
    public string EndedBy { get; set; } = "the pen lifted";

    /// <summary>
    /// What made it, named by the person who was there.
    /// </summary>
    /// <remarks>
    /// Not discoverable. WinPenKit can say which API answered and what its full scale is; it
    /// cannot say which tablet is on the desk or which driver build is installed, and both
    /// are what somebody comparing two recordings actually wants. So they are typed in, and
    /// a take with them empty says so rather than guessing.
    /// </remarks>
    public string Tablet { get; set; } = "";

    public string Driver { get; set; } = "";

    /// <summary>What the hand was asked to do. Starts as the gesture's, and can be edited.</summary>
    public string Intent { get; set; } = gesture.Intent;

    /// <summary>What the backend normalises and what it leaves alone.</summary>
    public string Conventions { get; set; } = "";

    public bool Named => Tablet.Length > 0 && Driver.Length > 0;

    public void Add(Reading reading) => _readings.Add(reading);

    /// <summary>
    /// How long the contact lasted, in milliseconds, from the pen's own clock.
    /// </summary>
    /// <remarks>
    /// The pen's clock and not the recorder's: a reading arrives when the poll happens to run,
    /// which is up to a frame after the pen reported it, and a duration measured off arrivals
    /// is a duration measured off this application's scheduler.
    /// </remarks>
    public double Milliseconds =>
        _readings.Count < 2 ? 0 : (_readings[^1].At - _readings[0].At) / 1000.0;

    /// <summary>The readings as a stroke, for anything that draws one.</summary>
    public Stroke? Stroke => _readings.Count == 0 ? null : new Stroke(_readings);

    /// <summary>
    /// What the recording is, in one line, for somebody who was not holding the pen.
    /// </summary>
    public string Describe() =>
        $"{Count} readings over {Milliseconds:F0} ms through {Api}, "
        + $"full scale {FullScalePressure}";
}
