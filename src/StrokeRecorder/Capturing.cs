using StrokeFieldGuide.Strokes;
using StrokeFieldGuide.Surfaces;
using WinPenKit;

namespace StrokeFieldGuide.Recorder;

/// <summary>
/// What became of one reading the recorder was handed.
/// </summary>
/// <remarks>
/// <b>Exactly one per reading.</b> Asked for on <c>#85</c>, and the reason is that the
/// recorder's own accounting had no such rule: a hovering reading with the airborne record
/// switched off counted as routed and landed in no column, and a reading arriving before a
/// take existed counted against a take that had not been made. Approach and departure are
/// <em>views</em> of readings already counted somewhere else, not extra arrivals to add.
/// </remarks>
public enum Disposition
{
    /// <summary>The tip was down and it went into a stroke.</summary>
    Contact,

    /// <summary>Airborne, and kept because the airborne record was asked for.</summary>
    RetainedAirborne,

    /// <summary>Airborne, and deliberately not kept.</summary>
    ExcludedAirborne,

    /// <summary>Outside the part of the screen being recorded.</summary>
    OffPad,

    /// <summary>It arrived after the recording had been stopped.</summary>
    AfterTheStop,

    /// <summary>Nothing was armed, so there was nothing for it to belong to.</summary>
    Unarmed,
}

/// <summary>What the window should do about a reading, once the capture has decided.</summary>
/// <param name="Of">The one thing that became of it.</param>
/// <param name="Drew">Lay this point's ink on the pad.</param>
/// <param name="Began">
/// This reading started a stroke, so its ink must not be joined to whatever was drawn before
/// it. Reported from the pad as a line from nowhere to the landing: the window draws from the
/// last point it laid unless told to forget it, and the extraction lost the place that did.
/// </param>
/// <param name="Restage">The step's presentation has changed and wants rebuilding.</param>
/// <param name="Wipe">Clear the pad and draw the guide again.</param>
/// <param name="Forget">Reset the per-take readouts.</param>
public readonly record struct Captured(
    Disposition Of,
    bool Drew = false,
    bool Began = false,
    bool Restage = false,
    bool Wipe = false,
    bool Forget = false);

/// <summary>
/// The recording state machine, apart from the window that shows it.
/// </summary>
/// <remarks>
/// <para>
/// Extracted on <c>#85</c>'s recommendation. It had lived in a 166-line method inside a
/// 2,800-line code-behind, interleaved with laying ink, updating readouts and navigating the
/// wizard — so the rules about what happens to a reading could only be read by picking them
/// out of the things that happen to a window.
/// </para>
/// <para>
/// <b>The policy is unchanged on purpose.</b> This is a move, not a redesign: every branch
/// below does what the same branch did before it was moved, so that the tests written against
/// it describe the recorder that exists rather than one somebody meant. <c>#66</c> changes the
/// policy, and it should change it against a characterised starting point.
/// </para>
/// <para>
/// Nothing here touches a control, lays ink or changes step. It takes readings and commands
/// and answers what it did; the window does the rest.
/// </para>
/// </remarks>
public sealed class Capturing
{
    /// <summary>How long a hovering reading is kept as a possible approach.</summary>
    /// <remarks>
    /// A quarter of a second, on the host clock. Measured on the pen's own clock instead, a
    /// landing appears to arrive a tenth of a second after the last hover reading and the
    /// whole approach ages out of a window it never left — which is the fault the whole
    /// airborne investigation began with.
    /// </remarks>
    public const long HoverKept = 250_000;

    /// <summary>What the session says about itself, which a take records.</summary>
    public readonly record struct Device(InputApi Api, int MaxPressure, string Conventions);

    private readonly Func<InkTransform> _placement;

    private readonly List<Reading> _hover = [];
    private readonly List<Reading> _aloft = [];

    private Gesture? _gesture;
    private Device _device;

    public Capturing(Func<InkTransform> placement)
    {
        _placement = placement;
    }

    /// <summary>What the recorder is doing.</summary>
    public Capture State { get; private set; } = Capture.Idle;

    /// <summary>The take in hand, if there is one.</summary>
    public Take? Take { get; private set; }

    /// <summary>Whether airborne readings are being kept.</summary>
    public bool KeepAirborne { get; set; }

    /// <summary>The gesture being recorded, and the device it is being recorded through.</summary>
    public void Choose(Gesture gesture, Device device)
    {
        _gesture = gesture;
        _device = device;
    }

    /// <summary>Airborne readings kept before a take existed.</summary>
    public IReadOnlyList<Reading> Aloft => _aloft;

    /// <summary>
    /// Arms, so the next contact starts a take.
    /// </summary>
    /// <param name="counters">The session's counts now, to subtract from at the stop.</param>
    public Captured Arm((long, long, long)? counters)
    {
        Take = null;
        State = Capture.Armed;

        // A many-stroke take exists from the moment it is armed, because its airborne record
        // starts then rather than at the first contact.
        if (_gesture is { ManyStrokes: true })
        {
            Take = Fresh();
            Take.KeepAll(_aloft);

            ArmedAt = counters;
        }

        _hover.Clear();
        _aloft.Clear();

        return new(Disposition.Unarmed, Restage: true, Wipe: true, Forget: true);
    }

    /// <summary>The session counts when this take was armed, for the stop to subtract from.</summary>
    public (long, long, long)? ArmedAt { get; private set; }

    /// <summary>Stops the take, and freezes what the session counted while it ran.</summary>
    public Captured Stop((long, long, long)? counted)
    {
        if (Take is null || State is not (Capture.Armed or Capture.Drawing or Capture.Between))
        {
            return new(Disposition.Unarmed);
        }

        Take.Counted = counted;

        if (State == Capture.Drawing && Take.Current is { } drawing)
        {
            drawing.EndedBy = "the recording was stopped mid-stroke";
        }

        Take.StoppedAt = DateTimeOffset.Now;

        Take.EndedBy = Take.Strokes == 0
            ? "the recording was stopped before anything was drawn"
            : "the recording was stopped";

        State = Capture.Taken;

        return new(Disposition.Unarmed, Restage: true);
    }

    /// <summary>
    /// Takes over a recording read from a file, which is finished by definition.
    /// </summary>
    /// <remarks>
    /// The one way a take arrives without having been captured. It is Taken straight away:
    /// there is nothing to arm, nothing to draw, and the readings in it were made by somebody
    /// else's pen on some other day.
    /// </remarks>
    public void Opened(Take take)
    {
        Take = take;
        State = Capture.Taken;

        _hover.Clear();
        _aloft.Clear();
    }

    /// <summary>Throws the take away.</summary>
    public Captured Discard()
    {
        Take = null;
        State = Capture.Idle;

        _hover.Clear();
        _aloft.Clear();

        return new(Disposition.Unarmed, Restage: true, Wipe: true, Forget: true);
    }

    /// <summary>
    /// One reading, and what became of it.
    /// </summary>
    /// <param name="reading">What the pen said.</param>
    /// <param name="overThePad">Whether it was over the part of the screen being recorded.</param>
    public Captured Took(Reading reading, bool overThePad)
    {
        Take?.Routing(reading.At);

        var airborne = Disposition.ExcludedAirborne;

        if (!reading.InContact)
        {
            if (KeepAirborne)
            {
                if (Take is null) _aloft.Add(reading); else Take.Keep(reading);

                airborne = Disposition.RetainedAirborne;
            }
            else
            {
                // Counted, because it was handed over and nothing else has a column for it
                // yet. It may still be adopted into the next stroke's approach, which is why
                // Take reports this figure split rather than raw. Left out deliberately is a
                // disposition; it is not the same as missing.
                Take?.OneLeftOut();
            }

            if (Moved(reading)) Hovering(reading);
        }

        if (!overThePad)
        {
            var left = State == Capture.Drawing;

            if (left)
            {
                Take!.Current!.EndedBy = "the pen left the pad";

                if (Take.Gesture.ManyStrokes)
                {
                    State = Capture.Between;
                }
                else
                {
                    Take.EndedBy = "the pen left the pad";
                    State = Capture.Taken;
                }
            }

            if (reading.InContact)
            {
                Take?.DroppedOne();

                return new(Disposition.OffPad, Restage: left);
            }

            return new(airborne, Restage: left);
        }

        if (State == Capture.Armed && reading.InContact)
        {
            Take ??= Fresh();

            Take.KeepAll(_aloft);
            _aloft.Clear();

            var opening = Approaching(reading);

            Take.Begin().Approaching(opening.Readings, opening.SinceLastSeen, opening.Last);

            State = Capture.Drawing;

            Take.Add(reading);

            return new(Disposition.Contact, Drew: true, Began: true);
        }

        if (State == Capture.Drawing)
        {
            if (reading.InContact)
            {
                Take!.Add(reading);

                return new(Disposition.Contact, Drew: true);
            }

            State = Take!.Gesture.ManyStrokes ? Capture.Between : Capture.Taken;

            return new(airborne, Restage: true);
        }

        if (State == Capture.Between && reading.InContact)
        {
            var next = Take!.Begin();
            var coming = Approaching(reading);

            next.Approaching(coming.Readings, coming.SinceLastSeen, coming.Last);
            next.Add(reading);

            State = Capture.Drawing;

            return new(Disposition.Contact, Drew: true, Began: true);
        }

        if (State == Capture.Taken && reading.InContact)
        {
            // A many-stroke take is finished when it is stopped, and anything drawn after
            // that is counted and kept out. A single-stroke one starts again, because
            // drawing another is how somebody says the last was not the one they wanted.
            if (_gesture is { ManyStrokes: true })
            {
                Take!.OneAfterTheStop();

                return new(Disposition.AfterTheStop, Restage: true);
            }

            Restart(reading);

            // The landing that restarted it belongs to the take it restarted. The version
            // this replaced added it here; the extraction did not, so the first reading of a
            // redrawn single-stroke take was lost.
            Take!.Add(reading);

            return new(Disposition.Contact, Drew: true, Began: true, Wipe: true, Forget: true);
        }

        // Restage, because the version this replaced called Stage() at the bottom of every
        // path that was not an off-pad early return -- which is how the step's own summary
        // stayed current while somebody was drawing. Matched rather than improved on: this
        // extraction changes no policy, and how often a window redraws is policy.
        return new(reading.InContact ? Disposition.Unarmed : airborne, Restage: true);
    }

    /// <summary>A take of the chosen gesture, through the open device, at today's placement.</summary>
    private Take Fresh() => new(_gesture!, _device.Api, _device.MaxPressure, _placement())
    {
        Conventions = _device.Conventions,
    };

    /// <summary>Begins again, for a single-stroke gesture drawn a second time.</summary>
    private void Restart(Reading landing)
    {
        Take = Fresh();

        Take.KeepAll(_aloft);
        _aloft.Clear();

        var arriving = Approaching(landing);

        Take.Begin().Approaching(arriving.Readings, arriving.SinceLastSeen, arriving.Last);

        State = Capture.Drawing;
    }

    /// <summary>
    /// Whether this airborne reading says anything the one before it did not.
    /// </summary>
    /// <remarks>
    /// A pen resting in range repeats the same position, and keeping all of it makes the
    /// pauses larger in a file than the drawing. A reading carrying a lean or an azimuth is
    /// kept whatever it says, because those move when the position does not.
    /// </remarks>
    private bool Moved(Reading reading) =>
        reading.Lean != 0
        || reading.Azimuth != 0
        || _hover.Count == 0
        || _hover[^1].X != reading.X
        || _hover[^1].Y != reading.Y;

    /// <summary>Keeps a hovering reading, and gives it to a stroke that just ended.</summary>
    private void Hovering(Reading reading)
    {
        _hover.Add(reading);

        while (_hover.Count > 0 && reading.Arrived - _hover[0].Arrived > HoverKept)
        {
            _hover.RemoveAt(0);
        }

        if (State is not (Capture.Between or Capture.Taken)) return;

        if (Take?.Current is not { Count: > 0 } just) return;

        if (reading.Arrived - just.Readings[^1].Arrived <= HoverKept) just.Departing(reading);
    }

    /// <summary>The pen in the air immediately before a landing.</summary>
    /// <remarks>
    /// Aged on the host clock, and cleared afterwards so the next stroke cannot be handed
    /// this one's approach.
    /// </remarks>
    private (IReadOnlyList<Reading> Readings, long? SinceLastSeen, Reading? Last) Approaching(
        Reading landing)
    {
        var approach = _hover.Where(seen => landing.Arrived - seen.Arrived <= HoverKept).ToList();

        var since = _hover.Count > 0 ? landing.Arrived - _hover[^1].Arrived : (long?)null;
        var last = _hover.Count > 0 ? _hover[^1] : (Reading?)null;

        _hover.Clear();

        return (approach, since, last);
    }
}
