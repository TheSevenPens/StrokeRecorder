using StrokeRecorder;

namespace StrokeRecorder.Tests;

/// <summary>
/// What the space bar and escape mean, over the key sequences a hand actually produces.
/// </summary>
/// <remarks>
/// <para>
/// <c>#72</c> was reported three times and fixed three times. What none of those attempts
/// had was a way to press the key twice without a hand, which is why the fault survived a
/// build that had already been "fixed".
/// </para>
/// <para>
/// <b>These check the decision, not the routing.</b> Whether the key reaches this decision
/// at all — focus, tunnelling, a canvas that wants the space bar for panning — is Avalonia's
/// and is not covered here. That is the remaining untested half and is worth saying rather
/// than leaving to be discovered.
/// </para>
/// </remarks>
public class KeyCommands
{
    /// <summary>A key held down: the events a keyboard sends, in order.</summary>
    /// <remarks>
    /// The whole of the fault. Holding a key produces repeated key-down events with no
    /// key-up between them, so a decision that reads only the current state sees a second
    /// press that nobody made.
    /// </remarks>
    private static IEnumerable<Command> Holding(int downs, bool manyStrokes, Capture from)
    {
        var capture = from;
        var held = false;

        for (var each = 0; each < downs; each++)
        {
            var wanted = Keys.Space(held, manyStrokes, capture);

            held = true;

            capture = After(wanted, capture);

            yield return wanted;
        }
    }

    /// <summary>What the recorder would be doing after carrying a command out.</summary>
    private static Capture After(Command wanted, Capture capture) => wanted switch
    {
        Command.Arm => Capture.Armed,
        Command.Stop => Capture.Idle,
        Command.Discard => Capture.Idle,
        _ => capture,
    };

    [Fact]
    public void Holding_space_on_a_many_stroke_take_stops_it_once_and_does_not_rearm()
    {
        // The reported fault, as a sequence: down, down, with no release. The second down
        // used to arm a new take over the one just stopped.
        var wanted = Holding(4, manyStrokes: true, Capture.Drawing).ToList();

        Assert.Equal(Command.Stop, wanted[0]);

        Assert.All(wanted.Skip(1), each => Assert.Equal(Command.Ignore, each));
    }

    [Fact]
    public void Holding_space_on_a_single_stroke_take_arms_once()
    {
        var wanted = Holding(4, manyStrokes: false, Capture.Idle).ToList();

        Assert.Equal(Command.Arm, wanted[0]);

        Assert.All(wanted.Skip(1), each => Assert.Equal(Command.Ignore, each));
    }

    [Fact]
    public void Pressing_and_releasing_twice_is_two_commands()
    {
        // The other half of the latch: it must not swallow a press somebody meant. Released
        // between presses, the second one counts however quickly it followed the first --
        // which is what the 400 ms guard this replaced could not promise.
        var first = Keys.Space(held: false, manyStrokes: true, Capture.Idle);

        Assert.Equal(Command.Arm, first);

        var second = Keys.Space(held: false, manyStrokes: true, Capture.Drawing);

        Assert.Equal(Command.Stop, second);
    }

    [Fact]
    public void Space_starts_a_recording_when_none_is_running()
    {
        // The reason it is a toggle rather than stop-only: it is the only way to start a
        // recording without reaching for the screen, which is the thing the key is for.
        Assert.Equal(Command.Arm, Keys.Space(false, manyStrokes: true, Capture.Idle));
        Assert.Equal(Command.Arm, Keys.Space(false, manyStrokes: false, Capture.Idle));
    }

    [Theory]
    [InlineData(Capture.Armed)]
    [InlineData(Capture.Drawing)]
    [InlineData(Capture.Between)]
    public void Space_stops_a_many_stroke_take_from_any_running_state(Capture capture)
    {
        Assert.Equal(Command.Stop, Keys.Space(false, manyStrokes: true, capture));
    }

    [Fact]
    public void Space_on_an_armed_single_stroke_take_throws_it_away_rather_than_stopping_it()
    {
        // A single-stroke take that is only armed has nothing in it. Stopping it would keep
        // an empty recording; discarding it is what the reader meant.
        Assert.Equal(Command.Discard, Keys.Space(false, manyStrokes: false, Capture.Armed));
    }

    [Fact]
    public void Escape_never_starts_anything()
    {
        // The property that separates it from space, over every state there is.
        foreach (var capture in Enum.GetValues<Capture>())
        {
            foreach (var many in new[] { true, false })
            {
                Assert.NotEqual(Command.Arm, Keys.Escape(many, capture));
            }
        }
    }

    [Fact]
    public void Escape_stops_a_running_many_stroke_take_and_is_otherwise_nothing()
    {
        Assert.Equal(Command.Stop, Keys.Escape(true, Capture.Drawing));
        Assert.Equal(Command.Stop, Keys.Escape(true, Capture.Armed));
        Assert.Equal(Command.Stop, Keys.Escape(true, Capture.Between));

        Assert.Equal(Command.Ignore, Keys.Escape(true, Capture.Idle));
        Assert.Equal(Command.Ignore, Keys.Escape(false, Capture.Drawing));
    }
}
