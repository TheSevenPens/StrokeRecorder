namespace StrokeRecorder;

/// <summary>What a key press asks the recorder to do.</summary>
public enum Command
{
    /// <summary>Nothing. The key is not ours, or it is a repeat of one already acted on.</summary>
    Ignore,

    /// <summary>Arm, so the next contact starts a take.</summary>
    Arm,

    /// <summary>Stop the take that is running.</summary>
    Stop,

    /// <summary>Throw away the armed take without keeping it.</summary>
    Discard,
}

/// <summary>
/// Which command a key press is, decided apart from the window that receives it.
/// </summary>
/// <remarks>
/// <para>
/// Extracted because <c>#72</c> survived three attempts at fixing it inside a 2,882-line
/// code-behind, and because the fourth attempt should be one somebody can check. Everything
/// here is a decision about state; nothing in it touches a control, a session or a take.
/// </para>
/// <para>
/// <b>The fault it was extracted to fix.</b> Holding the space bar produces a stream of
/// key-down events with no key-up between them. The branch that could arm had no suppression
/// at all, so the first down stopped the take and the second armed a new one, replacing it.
/// That is the "sometimes it stops and rearms" reported from the pad three times, and it was
/// found by a reviewer reading the file rather than by anybody pressing the key.
/// </para>
/// <para>
/// <b>A latch, not a time guard.</b> The version before this had a 400 ms guard on one branch
/// and none on the other. A guard has to pick a number, and any number is both too long --
/// swallowing a second press somebody meant -- and too short, because the auto-repeat rate is
/// a system setting. <paramref name="held"/> asks the question that actually matters: has
/// this key been released since it was last acted on.
/// </para>
/// </remarks>
public static class Keys
{
    /// <summary>
    /// What the space bar means right now.
    /// </summary>
    /// <param name="held">Whether this press is a repeat of one not yet released.</param>
    /// <param name="manyStrokes">Whether the chosen gesture records several strokes.</param>
    /// <param name="capture">What the recorder is doing.</param>
    public static Command Space(bool held, bool manyStrokes, Capture capture)
    {
        if (held) return Command.Ignore;

        var running = capture is Capture.Armed or Capture.Drawing or Capture.Between;

        if (manyStrokes) return running ? Command.Stop : Command.Arm;

        // A single-stroke take that is merely armed has nothing in it yet, so the second
        // press throws it away rather than stopping something that never started.
        return capture is Capture.Armed ? Command.Discard : Command.Arm;
    }

    /// <summary>
    /// What escape means right now.
    /// </summary>
    /// <remarks>
    /// Stop and nothing else, ever, and only where there is something to stop. It is the key
    /// for when stopping is the only thing wanted, which is why it is not a toggle: somebody
    /// pressing it twice must not start a recording.
    /// </remarks>
    public static Command Escape(bool manyStrokes, Capture capture)
    {
        if (!manyStrokes) return Command.Ignore;

        return capture is Capture.Armed or Capture.Drawing or Capture.Between
            ? Command.Stop
            : Command.Ignore;
    }
}
