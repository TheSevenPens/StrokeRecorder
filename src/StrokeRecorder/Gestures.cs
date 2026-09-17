namespace StrokeFieldGuide.Recorder;

/// <summary>
/// The shape a guide is drawn as, in fractions of the box rather than pixels.
/// </summary>
/// <remarks>
/// Fractions because the box is whatever size the window is, and a guide given in pixels
/// would sit somewhere different on every machine -- which matters more than it sounds,
/// because two recordings of "the same" gesture are only comparable if the hand covered
/// comparable ground.
/// </remarks>
public abstract record Guide
{
    /// <summary>From one corner of the box towards another.</summary>
    public sealed record Line(double FromX, double FromY, double ToX, double ToY) : Guide;

    /// <summary>A shallow curve across the box.</summary>
    public sealed record Arc : Guide;

    /// <summary>A single target, for a gesture that does not travel.</summary>
    public sealed record Dot : Guide;
}

/// <summary>
/// One thing worth recording, and what it is worth recording for.
/// </summary>
/// <param name="Id">Goes in the file name, so it is kebab and stays stable.</param>
/// <param name="Label">What the reader picks it by.</param>
/// <param name="Detail">How to draw it. Pace and effort, because both change what is reported.</param>
/// <param name="Wants">
/// What the recording is evidence about. This is the field that makes the set worth having:
/// a trace nobody can say the purpose of is a trace nobody will use, and the purpose has to
/// be decided before the stroke rather than guessed at afterwards.
/// </param>
/// <param name="Intent">
/// Written into the recording as what the hand was asked to do, so the file carries its own
/// brief. A reader comparing two traces needs to know whether a difference is the device or
/// the instruction.
/// </param>
/// <param name="Thumb">SVG path data, drawn small above the label.</param>
/// <param name="Filled">
/// Whether the thumb is a shape or a line. The pressure ramp's is a closed wedge -- it shows
/// a stroke getting wider, which is a thing with an area -- and stroking its outline draws
/// the one shape in the set that does not mean what it looks like.
/// </param>
/// <param name="Shape">What to put on the strip to draw along, if anything.</param>
/// <param name="ManyStrokes">
/// Whether the take keeps going when the pen lifts.
/// <para>
/// False for everything that asks for one stroke, where the lift is the obvious and
/// buttonless end of the recording. True where the thing being recorded is a <b>series</b> --
/// the same stroke drawn faster and faster, or somebody simply drawing for a while -- and the
/// lifts in the middle are part of it rather than the end of it. Such a take is ended by
/// hand, because nothing else can tell the last lift from the others.
/// </para>
/// </param>
public sealed record Gesture(
    string Id,
    string Label,
    string Detail,
    string Wants,
    string Intent,
    string Thumb,
    Guide? Shape,
    bool Filled = false,
    bool ManyStrokes = false);

/// <summary>
/// The gestures this tool offers: six carried over from the browser recorder this one is
/// modelled on, and one that recording with the first six showed was missing.
/// </summary>
/// <remarks>
/// <para>
/// Offered as presets rather than left open because the useful recording is the one somebody
/// decided the purpose of first. Freeform is last and exists so that the list does not become
/// a fence: a trace the presets do not cover is still worth taking, and it asks for its own
/// description instead.
/// </para>
/// <para>
/// The pairing is the point of the first two. A slow diagonal and a fast one are the same
/// path and differ only in how long the hand took, so anything that differs between the two
/// recordings is about speed and cannot be about the shape.
/// </para>
/// </remarks>
public static class Gestures
{
    public static IReadOnlyList<Gesture> All =>
    [
        new("slow-diagonal", "Slow diagonal",
            "Trace the dashed line slowly, three or four seconds end to end. Against a ruler "
            + "if you have one.",
            "Quantisation and roughness. A slow stroke is where a pixel grid shows.",
            "One slow diagonal, about four seconds end to end, left to right and downward, "
            + "drawn as straight as possible.",
            "M12 14 L78 46",
            new Guide.Line(0.1, 0.22, 0.9, 0.8)),

        new("fast-flick", "Fast flick",
            "The same line, as fast as you can move. Half a second or less.",
            "The contrast with the slow one: quantisation should vanish at speed, and the "
            + "samples spread much further apart.",
            "The same diagonal drawn as fast as possible, under half a second, as a contrast "
            + "with the slow one.",
            "M12 14 L78 46 M62 30 L78 46 L60 48",
            new Guide.Line(0.1, 0.22, 0.9, 0.8)),

        new("slow-arc", "Slow arc",
            "Follow the curve steadily, about three seconds.",
            "Reconstruction. A curve is where curve fitting and filtering can be compared.",
            "One slow arc, about three seconds, following a smooth curve from left to right.",
            "M12 46 Q45 2 78 46",
            new Guide.Arc()),

        new("tap", "Tap",
            "Press on the dot and lift again, without moving.",
            "Endpoints. What a press and a release report when nothing moves.",
            "A single tap: press and lift with no movement.",
            "M32 30 A13 13 0 1 1 58 30 A13 13 0 1 1 32 30 M42 30 A3 3 0 1 1 48 30 A3 3 0 1 1 42 30",
            new Guide.Dot()),

        new("pressure-ramp", "Pressure ramp",
            "Along the line, starting as light as the pen will register and pressing harder "
            + "as you go.",
            "Pressure. Where a pen starts reporting, and what the top of its range looks like.",
            "One stroke along a straight line, pressure increasing from the lightest the pen "
            + "will register to the hardest, to show the usable range.",
            "M12 29 L78 22 L78 38 L12 31 Z",
            new Guide.Line(0.1, 0.5, 0.9, 0.5),
            Filled: true),

        new("multi-stroke", "Multi-stroke drawing",
            "Keeps recording across pen lifts. Draw the whole series, then press Stop. Say "
            + "what you were varying when you name it.",
            "What a hand does across a series rather than within one stroke: how a stroke "
            + "starts, how it ends, and what changes from one to the next when something is "
            + "being varied on purpose.",
            "A series of strokes recorded as one take, with the pen lifting between them.",
            "M10 40 C18 20 26 20 34 40 M40 38 C46 16 52 16 58 38 M64 36 C68 14 72 14 76 36",
            null,
            ManyStrokes: true),

        new("freeform", "Freeform",
            "No guide. Draw whatever the trace is for, and describe it yourself at the end.",
            "Anything the presets do not cover.",
            "",
            "M12 40 C26 8 34 52 46 30 S66 14 78 34",
            null),
    ];
}
