using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Reflection;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Media;
using WinPenKit.Diagnostics;
using SkiaSharp;
using StrokeKit.Brushes;
using Nib = StrokeKit.Brushes.Brush;
using StrokeKit.Avalonia;
using StrokeKit.Strokes;
using StrokeKit.Surfaces;
using StrokeKit.Views;
using WinPenKit;

namespace StrokeRecorder;

/// <summary>
/// Step one: prove the pen is reporting before anybody records anything with it.
/// </summary>
/// <remarks>
/// <para>
/// The browser recorder this is modelled on opens the same way, and for the same reason: the
/// commonest wasted recording is one made with a mouse, and the second commonest is one made
/// through a backend that was not the one the notes say. Both are visible here in a few
/// seconds and invisible afterwards.
/// </para>
/// <para>
/// It is also the smallest thing that puts the whole stack in one window -- a WinPenKit
/// session, this guide's stroke model, its brush engine, its surface, and the control that
/// presents one -- which is the other reason to build it first.
/// </para>
/// </remarks>
[SuppressMessage("Usage", "CA1001:Types that own disposable fields should be disposable",
    Justification =
        "A window releases what it owns when it closes, which is what the Closed handler " +
        "does. IDisposable would say a caller should dispose the window; nothing does that, " +
        "so it would be a second release path nobody calls.")]
public partial class MainWindow : Window
{
    /// <summary>
    /// The sizes offered, rather than a free slider.
    /// </summary>
    /// <remarks>
    /// Because the useful ones are far apart. A wobble of a tenth of the range is a fraction
    /// of a pixel on a ten-wide nib and plainly visible on a three-hundred-wide one, so what
    /// a reader wants is to jump between orders of magnitude rather than to tune a number.
    /// </remarks>
    private static readonly double[] Sizes = [5, 10, 25, 50, 100, 200, 300, 400];

    private Remembered _remembered = Remembered.Read();

    /// <summary>
    /// The file this take was read back from, or null when it was recorded here.
    /// </summary>
    /// <remarks>
    /// Kept so the analysis step can say which take it is showing. Set in one place and
    /// cleared in one place: a banner announcing an opened file over a take just recorded is
    /// worse than no banner, and setting the TextBlock directly from both paths is how that
    /// happens.
    /// </remarks>
    private string? _opened;

    /// <summary>
    /// Which stroke of the take is picked out, or null for none.
    /// </summary>
    /// <remarks>
    /// One at a time and nothing by default. A take of forty-two strokes is a wall of
    /// identical rows and a drawing they all went into; picking one is how a reader asks
    /// "which of these is that", and the answer has to be in the drawing rather than in the
    /// table, because the table is what they are already looking at.
    /// </remarks>
    private int? _picked;

    private double _diameter = 26;

    /// <summary>
    /// Whether the nib ignores the lean and stays a circle.
    /// </summary>
    /// <remarks>
    /// Asked for while testing pressure at a nib four hundred wide, where the shape was the
    /// problem: a chisel that long renders a stroke whose weight changes with the hand's
    /// angle, and that movement sits on top of the one being looked for. A circle has one
    /// number and shows it.
    /// </remarks>
    private bool _round;

    private readonly PressureTrace _trace = new(0, 92);
    private readonly PressureTrace _takeTrace = new(230, 74);

    private readonly Gauges _gauges = new();

    /// <summary>
    /// The same dials on the recording step, small enough to sit in the row of readouts.
    /// </summary>
    /// <remarks>
    /// The step that needs them most had none. A reader can check a pen against the big ones
    /// and then go and record with nothing to watch, which is the wrong way round: the probe
    /// is where a hand is free, and the take is where it matters whether the barrel is where
    /// it was meant to be. Smaller here, because what they take they take off the pad.
    /// </remarks>
    private readonly Gauges _takeGauges = new(26);
    private readonly PenPad _strip;
    private readonly PenPad _pad;
    private readonly PenPad _replay;
    /// <summary>
    /// The pen, opened and polled. Shared with the lab, and that is the point.
    /// </summary>
    /// <remarks>
    /// This window used to own a <c>DispatcherTimer</c>, an <c>IPenSession</c> and a drain of
    /// its own, borrowing two static helpers from the stream the lab used. Which is not
    /// sharing: the two had already disagreed about when a batch is stamped, and the one that
    /// was wrong is the one whose output gets published.
    /// </remarks>
    private readonly PenStream _pen = new();

    private readonly Readout _api = new("api");
    /// <summary>
    /// The largest pressure the open backend will ever report.
    /// </summary>
    /// <remarks>
    /// Labelled with the backend that said so, because only one of them is answering from the
    /// hardware. Wintab queries the device and gets this tablet's real 32767; WM_POINTER,
    /// WinUI, Avalonia and WinForms all return a hard-coded 1024, which is the API's
    /// normalisation range and says nothing about the pen. A reader comparing two backends
    /// needs to know which kind of number they are looking at, and the two look identical.
    /// </remarks>
    private readonly Readout _range = new("max pressure level");

    /// <summary>
    /// How big the tablet is, as the driver states it.
    /// </summary>
    /// <remarks>
    /// Wintab only. The pointer backends say "not reported" rather than a blank, because a blank
    /// reads as a reading that has not arrived yet and this one never will.
    /// </remarks>
    private readonly Readout _area = new("active area");
    private readonly Readout _tiltX = new("tilt x");
    private readonly Readout _tiltY = new("tilt y");
    /// <summary>
    /// What the pen is pressing right now, as a number.
    /// </summary>
    /// <remarks>
    /// The bar to the right of the dials shows the same reading and is the better thing for
    /// watching it move, which is why this was taken out with the other three the dials
    /// already carried. It is back because a bar answers "is it changing" and a figure answers
    /// "what is it", and on a pre-flight beside the maximum it can reach, the second question
    /// is the one being asked.
    /// </remarks>
    private readonly Readout _pressure = new("pressure");
    /// <summary>Readings a second, counted over a rolling one-second window of arrivals.</summary>
    private readonly Readout _rate = new("rate");

    /// <summary>
    /// How many readings were waiting in the session's queue when it was last emptied.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Literally a queue depth, not a figure of speech: WinPenKit holds a
    /// <c>ConcurrentQueue&lt;PenPoint&gt;</c>, the driver enqueues one reading per packet, and
    /// this window drains the whole thing every 16 ms. Each item is a complete reading -- the
    /// position, the pressure, the angles and the timestamp -- so the depth counts readings and
    /// not packets or bytes.
    /// </para>
    /// <para>
    /// <b>Zero is not the healthy value here</b>, which is where the instinct from a mail queue
    /// misleads. The queue exists because the pen reports at about 162 a second and this window
    /// looks 60 times a second, so a small standing depth is the buffer doing its job. Expect
    /// <c>rate × poll interval</c>: 161.6 × 17.2 ms is 2.8, and three takes measured 2.72, 2.97
    /// and 3.18 with a median of 3 in all three.
    /// </para>
    /// <para>
    /// So: 0 means nothing is arriving, 2 or 3 is keeping up, and anything sustained above
    /// about five means the polls are landing late. The queue is unbounded, so a spike means
    /// <em>late</em> and never <em>lost</em> -- which is the reason to show it at all, because
    /// it is what tells a stuttering application apart from a pen that has stopped, and those
    /// look identical everywhere else in this project. The range beside it carries that: the
    /// worst poll of a take is the thing worth catching and the current one is not.
    /// </para>
    /// </remarks>
    private readonly Readout _batch = new("queue depth");


    /// <summary>Whether the window exists yet, which a session needs and a constructor has not.</summary>
    private bool _shown;

    /// <summary>Which step is on screen, counting from one as the rail does.</summary>
    private int _step = 1;

    private Gesture? _gesture;

    /// <summary>
    /// The recording state machine, which used to be spread through this file.
    /// </summary>
    /// <remarks>
    /// Extracted on #85. What it decides is testable without a window or a tablet; what this
    /// window does about it -- ink, readouts, which step is shown -- stays here.
    /// </remarks>
    private readonly Capturing _capturing;

    private Capture _capture => _capturing.State;

    /// <summary>Where the take was written, once it has been. Null until then.</summary>
    private string? _saved;

    /// <summary>
    /// The last name this window suggested, so a name the reader typed can be told from one
    /// it wrote itself.
    /// </summary>
    /// <remarks>
    /// Compared rather than flagged. The first version raised a flag around the assignment
    /// and lowered it afterwards, which assumed the change notification arrived inside the
    /// assignment. It does not, so the flag stayed raised and the suggestion froze at
    /// whatever it was when the screen first opened -- typing the tablet's name then left the
    /// file named after no tablet at all.
    /// </remarks>
    private string _suggested = "";
    private Take? _take => _capturing.Take;

    /// <summary>
    /// Which session those counts came from, so the subtraction can refuse to span two.
    /// </summary>
    /// <remarks>
    /// The counts are cumulative on the session object. If that object is replaced while a
    /// take is open -- a reopened Wintab context, a backend change -- the new one starts at
    /// zero and subtracting the old arming figures from it produces a number that is not
    /// wrong in a detectable direction: it is simply meaningless. Twice it came out saying
    /// the recorder had seen more readings than the session handed it, which is impossible,
    /// and both times the take was one of hovering with no strokes in it.
    /// </remarks>
    private IPenSession? _countedFrom;

    /// <summary>When the space bar was last acted on, to tell a held key from two presses.</summary>
    /// <summary>
    /// Whether the space bar is being held down right now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A latch, not a time guard.</b> Holding a key produces a stream of key-down events
    /// with no key-up between them, and the branch below that can arm had no suppression at
    /// all: the first down stopped the take and the second armed a new one, which is the
    /// "sometimes it stops and rearms" reported from the pad three times.
    /// </para>
    /// <para>
    /// A time guard was tried and is the wrong instrument. It has to pick a number, and any
    /// number is both too long -- swallowing a real second press somebody meant -- and too
    /// short, because auto-repeat rates are a system setting. A latch asks the question that
    /// actually matters: has this key been released since it was last acted on.
    /// </para>
    /// <para>
    /// Cleared on release and on the window losing focus, because a key released while the
    /// window is not in front never arrives, and a latch that stayed raised would leave the
    /// space bar dead until it was pressed and released again.
    /// </para>
    /// </remarks>
    private bool _spaceHeld;

    /// <summary>
    /// How much of the pen in the air to keep either side of a stroke, in microseconds.
    /// </summary>
    /// <remarks>
    /// A quarter of a second, which at the 240 readings a second measured here is about sixty
    /// readings at each end. Bounded by time rather than by a count, because the count is a
    /// property of the device's report rate and the question is about the hand: what happened
    /// in the last moment before the pen landed is the same question whatever rate it was
    /// sampled at.
    /// <para>
    /// There can be many seconds between two strokes and almost none of it is interesting.
    /// Keeping all of it would make the pauses larger in the file than the drawing.
    /// </para>
    /// </remarks>
    /// <summary>
    /// How much of the pen in the air to keep either side of a stroke, in microseconds
    /// <b>of the host's clock</b>.
    /// </summary>
    /// <remarks>
    /// Measured on <see cref="Reading.Arrived"/> and never on <see cref="Reading.At"/>. The
    /// pen's own timestamp advances a flat 4.166 ms per packet whatever the elapsed time and
    /// jumps forward at each landing by the drift it accumulated since the last one, so ageing
    /// a hover reading with it asks how old a reading is in units that are not time. A reading
    /// genuinely 20 ms old measured 2 seconds old, and was discarded.
    /// <para>
    /// That is what emptied the approaches this recorder was built to capture. Across the four
    /// takes that carry both clocks the arithmetic is exact: every stroke either kept its
    /// approach or had one the pen's clock aged past this window, 93 strokes with nothing else
    /// involved -- and on the host's clock not one of them exceeds it. The worst case was the
    /// take recorded specifically to watch a landing, whose five strokes all lost their
    /// approach to gaps the pen's clock reported as 762 to 2106 ms and which really were 0 to
    /// 31.
    /// </para>
    /// </remarks>
    private const long HoverKept = 250_000;

    private readonly Readout _took = new("readings");
    private readonly Readout _lasted = new("milliseconds");

    private PenPoint _last;
    private bool _haveLast;

    private readonly Queue<long> _arrivals = new();
    private int _seen;

    public MainWindow()
    {
        InitializeComponent();

        _strip = new PenPad(1200, 420);
        this.FindControl<Panel>("StripHost")!.Children.Insert(0, _strip);

        this.FindControl<Panel>("GaugeHost")!.Children.Add(_gauges);
        this.FindControl<Panel>("TraceHost")!.Children.Add(_trace);

        _diameter = _remembered.Diameter;

        foreach (var name in new[] { "Round", "TakeRound" })
        {
            var box = this.FindControl<CheckBox>(name)!;

            box.IsCheckedChanged += (_, _) =>
            {
                _round = box.IsChecked == true;

                foreach (var other in new[] { "Round", "TakeRound" })
                {
                    this.FindControl<CheckBox>(other)!.IsChecked = _round;
                }
            };
        }

        // One tablet and one driver, edited from either step. The save step asks because that
        // is where a file gets its name; the probe step asks because that is the step about
        // the tablet, and a reader wanting to check which one this is should not have to walk
        // to the end of the wizard to see.
        foreach (var name in new[] { "ProbeTablet", "ProbeDriver", "Tablet", "Driver" })
        {
            var box = this.FindControl<TextBox>(name)!;

            box.TextChanged += (_, _) => Named(name.Contains("Tablet"), box.Text ?? "");
        }

        this.FindControl<TextBox>("ProbeTablet")!.Text = _remembered.Tablet;
        this.FindControl<TextBox>("ProbeDriver")!.Text = _remembered.Driver;

        foreach (var name in new[] { "Size", "TakeSize" })
        {
            var sizes = this.FindControl<ComboBox>(name)!;

            sizes.ItemsSource = Sizes.Select(size => $"{size:F0} px").ToList();
            // Nearest offered, so a remembered size that is no longer on the list -- or a
            // default that never was -- picks something sensible rather than the smallest.
            sizes.SelectedIndex = Array.IndexOf(Sizes,
                Sizes.OrderBy(size => Math.Abs(size - _diameter)).First());
            sizes.SelectionChanged += (_, _) =>
            {
                if (sizes.SelectedIndex < 0) return;

                _diameter = Sizes[sizes.SelectedIndex];

                // Both choosers show the one size, because there is one brush.
                foreach (var other in new[] { "Size", "TakeSize" })
                {
                    this.FindControl<ComboBox>(other)!.SelectedIndex = sizes.SelectedIndex;
                }

                _remembered = _remembered with { Diameter = _diameter };
                _remembered.Write();
            };
        }

        // Step three's own pad. Two of them rather than one moved between panels, because
        // they hold different things for different lengths of time: the strip is scribbled
        // on and wiped, and the pad carries one take and the guide it was drawn against.
        _replay = new PenPad(1200, 700);
        this.FindControl<Panel>("ReviewHost")!.Children.Add(_replay);

        _pad = new PenPad(1200, 700);

        // Made here because it needs the pad: a take freezes the transform it was drawn
        // through, and the pad is what knows it.
        _capturing = new Capturing(() => _pad.ForPen());
        _pad.Grew += (_, _) => Regrown();

        // The same treatment for the review pad, and for the same reason. Without it a stroke
        // that landed past the pad's constructed size is gone before anybody sees the step.
        _replay.Grew += (_, _) => { if (_step == 4) Replayed(); };
        this.FindControl<Panel>("PadHost")!.Children.Add(_pad);

        foreach (var readout in All)
        {
            this.FindControl<StackPanel>("Readouts")!.Children.Add(readout.AsRow().Visual);
        }

        // Discovery itself happens when the window is up, not here. See Discover.
        this.FindControl<ComboBox>("Backends")!.SelectionChanged += (_, _) =>
        {
            if (!_discovering) Chose();
        };

        this.FindControl<Button>("Recheck")!.Click += (_, _) => Discover();

        this.FindControl<Button>("Clear")!.Click += (_, _) => Wipe();

        BuildGestures();

        // First in the row, so the dials sit beside the figures rather than under them.
        this.FindControl<WrapPanel>("TakeReadouts")!.Children.Add(_takeGauges);
        this.FindControl<WrapPanel>("TakeReadouts")!.Children.Add(_takeTrace);

        // Rows in their own column, for the reason the pre-flight's are rows: three figures
        // beside a set of dials is a table, and as cards they wrapped onto a second line and
        // left the first one short.
        foreach (var readout in Taken)
        {
            this.FindControl<StackPanel>("TakeFigures")!.Children.Add(readout.AsRow().Visual);
        }

        this.FindControl<Button>("OpenTake")!.Click += async (_, _) => await Reread();
        this.FindControl<Button>("OpenTakeTwo")!.Click += async (_, _) => await Reread();

        this.FindControl<Button>("Save")!.Click += (_, _) => Keep();
        this.FindControl<Button>("ShowFolder")!.Click += (_, _) => OpenFolder();

        this.FindControl<TextBox>("FileName")!.TextChanged += (_, _) => Foot();

        foreach (var box in new[] { "RecordingName", "Tablet", "PenModel", "Driver", "Firmware", "Username", "Intent", "Notes" })
        {
            this.FindControl<TextBox>(box)!.TextChanged += (_, _) => Named();
        }

        this.FindControl<Button>("Arm")!.Click += (_, _) =>
        {
            if (_capture is Capture.Armed or Capture.Drawing or Capture.Between
                && _take?.Gesture.ManyStrokes == true)
            {
                StopTake();
            }
            else ArmTake();
        };
        this.FindControl<Button>("Again")!.Click += (_, _) => Discard();

        this.FindControl<Button>("AgainSame")!.Click += (_, _) => RecordAnother(3);
        this.FindControl<Button>("AgainOther")!.Click += (_, _) => RecordAnother(2);

        this.FindControl<Button>("Back")!.Click += (_, _) => GoTo(_step - 1);
        this.FindControl<Button>("Next")!.Click += (_, _) => GoTo(_step + 1);

        // The build, in the title bar. Codex's review pointed out that the running recorder
        // reported a commit older than the fix being discussed, and neither of us could say
        // whether that was a stale process or a stamp that lags the source. Both are cheap to
        // rule out once the window says what it is.
        Title = $"Record a stroke — Stroke Field Guide — {BuildStamp()}";

        GoTo(1);

        // Tunnelled, so the canvas cannot take the space bar first.
        AddHandler(KeyDownEvent, Pressed, RoutingStrategies.Tunnel);

        // And the key *up*, which is the whole bug. A focused Button in Avalonia has
        // ClickMode.Release, so it calls OnClick on KeyUp -- and handling KeyDown does not
        // suppress the separate KeyUp event. So space went: down, this window stops the take,
        // Stage turns that same button back into Arm, up, the button activates, ArmTake runs.
        // Stop and immediately restart, and only when a button happened to hold focus, which
        // is exactly how it was reported: flaky.
        //
        // Three fixes were attempted against the key-down path -- a repeat debounce, a
        // dedicated stop key, then refusing to arm from space at all -- and none of them could
        // have worked, because the restart was never coming from the key-down path.
        AddHandler(KeyUpEvent, Released, RoutingStrategies.Tunnel);

        // Wintab needs the window that owns the context to be the active one, and says so by
        // going silent rather than by failing: WTEnable and WTOverlap put the context back on
        // top of the overlap order when focus returns. Without this the recorder can come back
        // from an alt-tab looking exactly like a pen that has stopped reporting.
        //
        // Every sample in WinPenKit does this in one line and this window never did. It has not
        // cost a take yet only because nobody has left the window mid-recording -- which is a
        // thing a person doing a long series of strokes will eventually do.
        Activated += (_, _) => _pen.Session?.OnActivated();

        // A key released while this window is not in front never arrives here, so the latch
        // would stay raised and the space bar would be dead until it was pressed and released
        // again. The same reason the lab clears its hand-panning flag on deactivation.
        Deactivated += (_, _) => _spaceHeld = false;

        _pen.Drained += (_, batch) =>
        {
            Took(batch);

            // Whether or not that brought any readings. A wall clock that only moved when the
            // pen reported would be the same misleading thing wearing a different number.
            if (_step == 3 && _take is { Gesture.ManyStrokes: true }) Tick();
        };

        Chose();

        // The gesture picked for the reader rather than waiting to be. Every other entry names a
        // thing to draw, so choosing one is a decision about what the session is for; the first
        // asks nothing and keeps recording until you stop, which is the closest thing to picking
        // a pen up. Nothing stops a reader picking another -- this only decides what is already
        // selected when they arrive, so the step can be passed through rather than answered.
        //
        // Here and not in BuildGestures, which runs halfway up this constructor: Chose(Gesture)
        // ends in Refresh(), and Refresh touches controls wired below that point.
        //
        // Taken off the card rather than from Gestures.All. That property is expression-bodied
        // and builds a fresh list on every read, so Gestures.All[0] is a different object from
        // the one BuildGestures hung on the card -- and Chose marks the picked card with
        // ReferenceEquals, which then matched nothing. The pane said "Multi-stroke drawing"
        // and not one card looked chosen, which is worse than choosing nothing.
        if (this.FindControl<WrapPanel>("GestureList")!.Children.FirstOrDefault()
            is ToggleButton first && first.Tag is Gesture opening)
        {
            Chose(opening);
        }

        // Not from the constructor. A WM_POINTER session subclasses the window handle and a
        // Wintab one wants a window in the foreground, and at this point there is no window
        // -- TryGetPlatformHandle answers null and the session starts against nothing.
        Opened += (_, _) => { _shown = true; Discover(); Open(); };

        Closed += (_, _) => Shutdown();
    }

    /// <summary>
    /// Every readout, in the order they are shown.
    /// </summary>
    /// <remarks>
    /// Azimuth and altitude sit beside tilt x and y because they are the same fact twice: a
    /// Wintab device reports the pair of angles and the tilt is worked out from them. Showing
    /// only the derived form hides which of the two a disagreement is in, and showing only the
    /// reported form asks the reader to do trigonometry to compare backends -- the pointer
    /// backends report tilt and leave the angles at nothing.
    /// </remarks>
    /// <summary>The steps that exist, in order. Named here so the rail and the panels agree.</summary>
    /// <remarks>
    /// Five are on the rail from the start, deliberately: a reader can see what recording one
    /// stroke is going to involve before committing to the first step of it. Three of them do
    /// not exist yet, which is why <see cref="Built"/> is separate -- the rail shows the shape
    /// of the whole thing and the foot refuses to walk into a room with no floor.
    /// </remarks>
    private const int Built = 5;

    private void GoTo(int step)
    {
        if (step < 1 || step > Built) return;

        _step = step;

        this.FindControl<Grid>("StepOne")!.IsVisible = step == 1;
        this.FindControl<Grid>("StepTwo")!.IsVisible = step == 2;
        this.FindControl<Grid>("StepThree")!.IsVisible = step == 3;
        this.FindControl<Grid>("StepFour")!.IsVisible = step == 4;
        this.FindControl<Grid>("StepFive")!.IsVisible = step == 5;

        for (var each = 1; each <= 5; each++)
        {
            var box = this.FindControl<Border>($"Step{each}")!;

            box.Classes.Set("here", each == step);
            box.Classes.Set("done", each < step);
        }

        this.FindControl<Button>("Back")!.IsEnabled = step > 1;

        // Only where there is a finished take to move on from. On the earlier steps there is
        // nothing to record again and the buttons would be two more things to read past.
        foreach (var name in new[] { "AgainSame", "AgainOther" })
        {
            this.FindControl<Button>(name)!.IsVisible = step is 4 or 5;
        }

        // Arriving with a take already in hand leaves it alone: walking back to look at the
        // gesture and returning should not throw away the stroke that was just drawn.
        // Auto-armed for the single-stroke gestures, where arming is friction nobody wanted.
        // Not for a many-stroke one: there the arming is the start of the recording and
        // starting it on somebody's behalf, before they are holding the pen, begins a
        // recording of the room.
        if (step == 3 && _take is null && _gesture is not { ManyStrokes: true }) ArmTake();
        else if (step == 3) Stage();
        else if (step == 4) Review();
        else if (step == 5) ToSave();
        else Refresh();
    }

    /// <summary>Whether this step has been answered well enough to leave it.</summary>
    /// <remarks>
    /// The step after this one also has to exist. Without that the last built step offers a
    /// live Next that does nothing when pressed, which is worse than a dead one: a disabled
    /// button says there is nowhere to go and an inert one says the window is broken.
    /// </remarks>
    private bool CanLeave(int step) => step < Built && Answered(step);

    private bool Answered(int step) => step switch
    {
        // Nothing. Step one used to require a point before it would let anybody past, on
        // the grounds that the difference between a backend that answers and one that merely
        // opened is the whole of what it is for. True, and it does not follow that the step
        // should refuse: a reader using the same tablet for the twentieth time knows it
        // answers, and making them prove it again is a toll rather than a check.
        //
        // The foot still says whether anything has been reported, so the fact is there for
        // anybody who wants it and in the way of nobody who does not.
        1 => true,
        2 => _gesture is not null,

        // A take with something in it. One reading counts, because a tap is one reading and
        // is a real recording -- refusing it would refuse the gesture that exists to show
        // what a press and a release report when nothing moves.
        3 => _take is { Holds: true } && _capture == Capture.Taken,

        // Nothing to answer. Review is for reading, and a reader who disagrees with what it
        // says goes back and draws again rather than arguing with this screen.
        4 => true,

        _ => false,
    };

    // A control's Name in the XAML becomes a field on this class, so nothing here may share
    // a name with one. Four builds so far -- Arm, Folder, Preview, Again -- and the error
    // names the generated file rather than the XAML, so it reads as a compiler fault rather
    // than a clash with a button three hundred lines away in another file. Writing this
    // comment did not stop the fourth: check the XAML's names before adding a member.

    /// <summary>Where takes are kept.</summary>
    /// <remarks>
    /// Somewhere the reader can find without being told, rather than beside the executable.
    /// A folder of recordings is the output of this tool and belongs where output goes.
    /// </remarks>
    private static string TakesFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "StrokeFieldGuide", "takes");

    /// <summary>
    /// Brings the foot up to date with whatever just changed.
    /// </summary>
    /// <remarks>
    /// Wanted because the foot is written from state that is settled later than the step is:
    /// the wizard starts on step one from the constructor, and the session it reports on does
    /// not exist until the window opens. Left unrefreshed, the foot said no session was open
    /// while one was, which is the kind of wrong that makes a reader distrust the rest of the
    /// window.
    /// </remarks>
    private void Refresh()
    {
        this.FindControl<Button>("Next")!.IsEnabled = CanLeave(_step);

        Foot();
    }

    private void Foot()
    {
        this.FindControl<TextBlock>("FootNote")!.Text = _step switch
        {
            1 when _pen.Session is null => "No session open.",
            1 when _seen == 0 => "Draw on the strip once, and the pen will have proved itself.",
            1 => $"{_seen} points reported through {_pen.Session!.Api}. Ready.",

            2 when _gesture is null => "Pick what you are going to draw.",
            2 => $"{_gesture!.Label}. Next is where you draw it.",

            3 when _capture == Capture.Armed && _take?.Gesture.ManyStrokes == true =>
                "Recording. Nothing has been drawn yet; the clock is running anyway.",
            3 when _capture == Capture.Armed => "Armed. Put the tip down and the take starts.",
            3 when _capture == Capture.Idle && _gesture is { ManyStrokes: true } =>
                "Press Arm, or the space bar, to start recording. The clock runs from then.",
            3 when _take is null => $"{_gesture?.Label}. Arm, then draw.",
            3 when _capture is Capture.Drawing or Capture.Between
                && _take?.Gesture.ManyStrokes == true =>
                $"{SoFar(_take)} so far. Escape stops it, and so does the Stop button.",
            3 when _capture == Capture.Drawing => "Recording. Lift the pen to finish.",
            3 when _capture == Capture.Taken && _gesture is { ManyStrokes: true } =>
                $"Stopped. {_take!.Describe()}. Space or Arm records another; Next keeps this one.",
            3 when _capture == Capture.Taken =>
                $"{_take!.Describe()}. Next reads it back to you.",
            3 => "Armed. Waiting for the tip.",

            4 when _take is null => "Nothing to review.",
            4 => $"{_take!.Describe()}. Next names it and keeps it; recording again throws "
                 + "this one away.",

            5 when _saved is not null => $"Saved to {_saved}",
            5 when _take is { Named: false } => "Name the tablet and the driver, then save.",
            5 => "Ready to save.",

            _ => "",
        };
    }

    private void BuildGestures()
    {
        var list = this.FindControl<WrapPanel>("GestureList")!;

        foreach (var gesture in Gestures.All)
        {
            var card = new ToggleButton
            {
                Classes = { "gesture" },
                Content = Card(gesture),
                Tag = gesture,
            };

            card.Click += (_, _) => Chose(gesture);

            list.Children.Add(card);
        }
    }

    /// <summary>One gesture's card: the shape it draws, its name, and what it is for.</summary>
    private static Control Card(Gesture gesture) => new StackPanel
    {
        Spacing = 8,
        Children =
        {
            new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse(gesture.Thumb),
                Stroke = gesture.Filled ? null : new SolidColorBrush(Color.Parse("#9AA3B0")),
                Fill = gesture.Filled ? new SolidColorBrush(Color.Parse("#9AA3B0")) : null,
                StrokeThickness = 2.5,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
                Height = 56,
                HorizontalAlignment = HorizontalAlignment.Left,
            },
            // Coloured explicitly, like everything else in the card. A ToggleButton turns its
            // foreground white when checked, so a label inheriting it disappeared the moment
            // the gesture was picked -- on the one card the reader was looking at.
            new TextBlock
            {
                Text = gesture.Label,
                FontWeight = FontWeight.SemiBold,
                FontSize = 15,
                Foreground = new SolidColorBrush(Color.Parse("#2B2B28")),
            },

            // No description. Seven cards each carrying two or three lines of instruction made
            // a wall to read before anything could be picked, and the detail is already on the
            // left, for the one gesture the reader has actually chosen. The shape and the name
            // are what a card has to carry to be picked from.
        },
    };

    private void Chose(Gesture gesture)
    {
        _gesture = gesture;

        // One at a time. A ToggleButton left to itself stays down alongside the next one,
        // and two gestures showing as picked is worse than none.
        foreach (var child in this.FindControl<WrapPanel>("GestureList")!.Children)
        {
            if (child is ToggleButton button) button.IsChecked = ReferenceEquals(button.Tag, gesture);
        }

        this.FindControl<TextBlock>("Picked")!.Text = gesture.Label;
        this.FindControl<TextBlock>("Wants")!.Text = gesture.Wants;

        Refresh();
    }

    /// <summary>
    /// The figures beside the dials while a take runs.
    /// </summary>
    /// <remarks>
    /// No pressure. The trace next to them is a second-by-second picture of exactly that
    /// reading, with its own caption saying the number and the share of full scale, and a
    /// figure repeating it added nothing the eye was not already getting from the line.
    /// </remarks>
    private Readout[] Taken => [_took, _lasted];

    /// <summary>The nib this window draws with, and shows a cursor for.</summary>
    /// <remarks>
    /// <para>
    /// One place, because a cursor that is not the brush is worse than no cursor: it says the
    /// mark will be one thing and the mark is another, and a reader trusts the picture over
    /// the ink for as long as it takes to notice.
    /// </para>
    /// <para>
    /// A chisel held to the lean rather than a circle, because a circle has nothing to show
    /// about how the pen is held and this window exists to show that. It also means the ink
    /// exercises the control the guide has just grown, on a real hand, which no fixture can.
    /// </para>
    /// </remarks>
    /// <summary>The take's own ink.</summary>
    private static readonly SKColor Ink = SKColors.Black.WithAlpha(0xD0);

    /// <summary>
    /// The colour a picked stroke is drawn in.
    /// </summary>
    /// <remarks>
    /// Warm and dark enough to read against the paper at full opacity, and far enough from
    /// black to be unmistakable where it crosses the rest of the take -- which on a
    /// cross-hatching take it does constantly. Opaque, because it is drawn over the ink and a
    /// translucent mark would come out as a muddied black rather than as a colour.
    /// </remarks>
    private static readonly SKColor Highlight = new(0xB4, 0x54, 0x1E);

    private Nib Brush(int fullScale, SKColor? colour = null) => new(
        _diameter, colour ?? Ink, 0.25, Buildup.PerStamp,
        new Width(Math.Min(0.5, _diameter / 20), _diameter, (uint)Math.Max(1, fullScale)),
        SpacedBy.Diameters,
        Nib: _round ? null : new StrokeKit.Brushes.Nib(0.3, 0, Held.ToTheLean));

    // ── step three ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Sends a reported point wherever the reader currently is.
    /// </summary>
    /// <remarks>
    /// One session serves every step, so the points arrive whatever is on screen. Routing
    /// here rather than opening a second session keeps the reader on the backend they proved
    /// in step one -- reopening for the take would mean the thing that was checked and the
    /// thing that recorded were two different sessions.
    /// </remarks>
    private void Route(PenPoint point, Reading reading, IPenSession session)
    {
        if (_step == 1)
        {
            Lay(_strip, point, session.MaxPressure);

            return;
        }

        if (_step == 3) Record(point, reading, session);
    }

    /// <summary>
    /// One reading, given to the capture, and whatever it asks for afterwards.
    /// </summary>
    /// <remarks>
    /// The state machine this used to carry is <see cref="Capturing"/>. What is left is the
    /// half that needs a window: ink on the pad, the readouts, and which step is shown.
    /// </remarks>
    private void Record(PenPoint point, Reading reading, IPenSession session)
    {
        var what = _capturing.Took(reading, _pad.Covers(reading.X, reading.Y));

        if (what.Forget)
        {
            foreach (var readout in Taken) readout.Forget();

            _takeGauges.Forget();
        }

        if (what.Wipe)
        {
            _opened = null;

            _pad.Clear();
            DrawGuide();
        }

        // A stroke that is starting must not be joined to the one before it. Lay draws from
        // the last point it was given unless this is cleared, and the version of Record this
        // replaced cleared it at every state transition -- which was lost in the move, so the
        // first reading of each stroke drew a line from wherever the previous stroke ended.
        // Reported from the pad as a line from nowhere to the landing.
        if (what.Began || !what.Drew) _haveLast = false;

        if (what.Drew)
        {
            Lay(_pad, point, session.MaxPressure, _take!.Placed);

            _took.Saw(_take.Count);

            // The gauge is labelled in milliseconds, so it is given milliseconds. Where there
            // is no host clock it is left alone rather than shown the pen's counter: a gauge
            // has no room to say which clock it is on, and one that cannot say must not guess.
            var (lasted, on) = _take.Lasted;

            if (on == StrokeRecorder.Clock.Host) _lasted.Saw(lasted * 1000);

            Tick();
        }

        if (what.Restage) Stage();
    }

    /// <summary>
    /// Ends a many-stroke take by hand, which is the only thing that can end one.
    /// </summary>
    /// <remarks>
    /// The stroke being drawn is closed too, if the pen is still down when this is pressed.
    /// Somebody who stops mid-stroke has told us the take is over; throwing away the readings
    /// up to that moment would be a second decision they did not make.
    /// </remarks>
    /// <summary>What the session counted, where the backend can say.</summary>
    /// <remarks>
    /// A type test rather than an assumption: only the Wintab sessions implement it, and a
    /// backend that cannot say should answer nothing rather than zero.
    /// </remarks>
    private static (long, long, long)? Counts(IPenSession? session) =>
        session is IPacketCounts c ? (c.PacketsFromDriver, c.PacketsOutsideCaptureRegion, c.PointsDelivered) : null;

    /// <summary>Stops the take, with what the session counted while it ran.</summary>
    /// <remarks>
    /// The counters are read here rather than inside the capture because the session belongs
    /// to this window, and a difference taken across two different sessions is not a
    /// difference. Same session, and not gone backwards, or nothing is claimed.
    /// </remarks>
    private void StopTake()
    {
        var counted =
            ReferenceEquals(_pen.Session, _countedFrom)
            && Counts(_pen.Session) is { } now
            && _capturing.ArmedAt is { } then
            && now.Item1 >= then.Item1 && now.Item2 >= then.Item2 && now.Item3 >= then.Item3
                ? (now.Item1 - then.Item1, now.Item2 - then.Item2, now.Item3 - then.Item3)
                : ((long, long, long)?)null;

        Apply(_capturing.Stop(counted));
    }

    /// <summary>Arms, and puts the window into the state that shows it.</summary>
    private void ArmTake()
    {
        _opened = null;

        _capturing.KeepAirborne = Keeping;

        if (_gesture is { } gesture && _pen.Session is { IsRunning: true } armed)
        {
            _capturing.Choose(gesture, Speaking(armed));

            _countedFrom = armed;
        }

        Apply(_capturing.Arm(Counts(_pen.Session)));
    }

    /// <summary>Whether the readout has been given a size, as opposed to "not reported".</summary>
    private bool _areaShown;

    /// <summary>Shows the tablet's size, and says whether the session had one to show.</summary>
    private bool ShowArea(IPenSession session)
    {
        if (session.PhysicalArea is { } size)
        {
            _area.Set(ActiveArea.From(size).Describe());

            return true;
        }

        _area.Set("not reported");

        return false;
    }

    /// <summary>What a session says about itself, in the capture's terms.</summary>
    private static Capturing.Device Speaking(IPenSession session) =>
        new(
            session.Api,
            session.MaxPressure,
            session.Conventions.ToString() ?? "",
            session.PhysicalArea is { } area ? ActiveArea.From(area) : null);

    /// <summary>Does whatever the capture asked the window for.</summary>
    private void Apply(Captured what)
    {
        if (what.Forget)
        {
            foreach (var readout in Taken) readout.Forget();

            _takeGauges.Forget();
        }

        if (what.Wipe)
        {
            _pad.Clear();
            DrawGuide();
        }

        if (what.Restage) Stage();
    }

    /// <summary>
    /// Throws the take away and goes back to record another.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Thrown away rather than kept, because both buttons mean the same thing -- this one is
    /// finished with -- and arriving at step three with the previous take still in hand shows
    /// the last stroke on the pad as though it were about to be added to.
    /// </para>
    /// <para>
    /// It is gone whether or not it was saved, which is why the foot says so on the screen
    /// where it has not been.
    /// </para>
    /// </remarks>
    private void RecordAnother(int step)
    {
        Discard();
        GoTo(step);
    }

    /// <summary>Throws the take away and leaves the window showing nothing.</summary>
    private void Discard()
    {
        _opened = null;

        Apply(_capturing.Discard());
    }

    /// <summary>
    /// How long the stroke has been going, and how long this gesture asks for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asked for, and fair: this window tells a reader to draw for three or four seconds and
    /// then gives them no way to know how long they drew for until the stroke is over. A
    /// stopwatch turns "was that about right" into something answerable while it still can be
    /// answered.
    /// </para>
    /// <para>
    /// Counted on the pen's own clock rather than on a wall clock, so it agrees with the
    /// duration the file will carry. It therefore only moves when readings arrive, which at
    /// two hundred and forty a second is often enough to read as running.
    /// </para>
    /// </remarks>
    private void Tick()
    {
        // Between counts too, and the clock reads Running rather than Milliseconds. A
        // many-stroke take is still recording while the pen is up, and Milliseconds is the
        // span of contact -- so a clock built on it froze the instant the tip lifted and told
        // somebody their recording had stopped when it had not. Reported by the person it
        // told, who also asked whether the frozen clock and the missing hover data were the
        // same fault. They are not: this one is the display reading the wrong number.
        // A many-stroke take shows the wall clock: it is either recording or it is not, and
        // the timer is what somebody reads to know which. Every other gesture shows how long
        // the readings themselves span, because there the number is the length of one stroke.
        //
        // That second branch used to read Running, which is the pen's packet counter -- so a
        // page asking for a stroke of three to four seconds was timing it on something that
        // ran at 0.673 of real time and telling the reader to draw for half again as long as
        // it meant. Lasted answers the same span on the host clock.
        //
        // Recording is a wall clock running from when a take was armed, and it says something
        // only while this window is the one recording. A take read back from a file was armed
        // whenever it was originally recorded and was never stopped by anybody here, so Now
        // minus that is the age of the file: opening yesterday's take put 115,076 seconds on a
        // readout that also said "stopped", and it climbed every tick. Reported by the person
        // who opened one.
        //
        // Recorded is FirstSeen and deliberately not Routed: Reopened restores the counts a
        // take was written with, so one read from a file reports every reading it ever routed
        // and has seen none of them. Asking Routed here left the clock reading the file's age
        // exactly as before.
        var recordedHere = _take is { Recorded: true };

        var seconds = _capture is Capture.Armed or Capture.Drawing or Capture.Between or Capture.Taken
            && _take is not null
            ? recordedHere && _take.Gesture.ManyStrokes
                ? _take.Recording / 1000
                : _take.Lasted.Seconds
            : 0;

        this.FindControl<TextBlock>("Clock")!.Text = $"{seconds:F2} s";

        this.FindControl<TextBlock>("Wanted")!.Text = Doing();
    }

    /// <summary>
    /// The word under the clock: what the recorder is doing, in one or two words.
    /// </summary>
    /// <remarks>
    /// Not called Wants. Step two has a TextBlock of that name, the Avalonia name generator
    /// turns every x:Name into a field on this class, and a method sharing the name fails the
    /// build with a message that points at generated code rather than at either of them. The
    /// file already carries this warning next to KeepAloft, and it caught me anyway.
    /// </remarks>
    private string Doing()
    {
        return _gesture is null
            ? ""
            : _gesture.ManyStrokes
                ? _capture switch
                {
                    Capture.Armed or Capture.Drawing or Capture.Between => "recording",
                    Capture.Taken => "stopped",
                    _ => "not recording",
                }
            : Asks(_gesture) is { } pace
                ? $"{_gesture.Label} asks for {pace}"
                : $"{_gesture.Label} asks for no particular pace";
    }

    /// <summary>What a gesture asks of the clock, where it asks anything.</summary>
    private static string? Asks(Gesture gesture) => gesture.Id switch
    {
        "slow-diagonal" => "three or four seconds",
        "fast-flick" => "under half a second",
        "slow-arc" => "about three seconds",
        _ => null,
    };

    /// <summary>Whether the window was asked to keep every airborne reading.</summary>
    private bool Keeping => this.FindControl<CheckBox>("KeepAloft")?.IsChecked == true;

    /// <summary>How much is in the take so far, in words rather than a bare number.</summary>
    private static string SoFar(Take? take) => take is null
        ? "Nothing"
        : take.Strokes == 1 ? "One stroke" : $"{take.Strokes} strokes";

    /// <summary>Puts the words and the buttons where the take has got to.</summary>
    private void Stage()
    {
        var (title, state) = _capture switch
        {
            Capture.Idle when _gesture is { ManyStrokes: true } => ("Ready when you are",
                "Not recording. Press Arm to start, and everything from that moment is part of "
                + "the take -- the pen in the air as well as the strokes."),
            Capture.Idle => ("Draw when you are ready",
                "Not armed. Press Arm, and the recording starts the moment the tip touches down."),
            Capture.Armed when _take?.Gesture.ManyStrokes == true => ("Recording",
                "Recording from the moment you armed it, whether or not you are drawing. The "
                + "clock is running. Draw when you are ready, and press Stop when the series "
                + "is finished."),
            Capture.Armed => ("Draw when you are ready",
                "Armed. Waiting for the tip."),
            Capture.Drawing when _take?.Gesture.ManyStrokes == true => ("Drawing",
                $"Stroke {_take.Strokes}. Lift and draw again; press Stop when the series is "
                + "finished."),
            Capture.Drawing => ("Drawing",
                "Recording. Lift the pen when the stroke is finished."),
            Capture.Between => ("Between strokes",
                $"{SoFar(_take)} kept, and still recording. Draw the next one, or press Stop."),
            _ => (_take is null or { Holds: false } ? "Nothing taken" : "Taken",
                  _take is null ? "" : _take.Describe()),
        };

        this.FindControl<TextBlock>("TakeTitle")!.Text = title;

        // The state used to have a card of its own at the foot of the pane, repeating what
        // the clock card, the line under Arm and the footer all said. It is only worth a line
        // once there is a take to describe -- before that, "not armed" is the one thing on
        // this screen nobody has ever needed telling.
        this.FindControl<TextBlock>("Wanted")!.Text =
            _capture == Capture.Taken ? state : Doing();

        // No brief and no how-it-ends. Both said what the gesture was and how to start it,
        // which step two has just finished saying and the footer says again; between them and
        // the clock, four surfaces described the same recording. What the keys do lives in the
        // footer, which is the one that fits on every window.

        this.FindControl<TextBlock>("TakeDetail")!.Text = _capture switch
        {
            Capture.Taken when _take is { Strokes: 0, Aloft.Count: > 0 } =>
                $"No strokes, and {_take.Aloft.Count} readings of the pen in the air. That is a "
                + "recording of the hovering pen, which is a thing worth having on purpose.",
            Capture.Taken when _take is { Strokes: 0, Routed: > 0 } =>
                $"Nothing was drawn. The pen reported {_take.Routed} readings while this was "
                + "recording, and none of them were kept -- switch on \"keep every airborne "
                + "reading\" before arming if the hovering pen is what you are after.",
            Capture.Taken when _take is null or { Count: 0 } =>
                "Nothing was captured. Did the pen reach the pad?",
            Capture.Taken when _take is { Count: 1 } =>
                "One reading only. That is a tap, and is a finished take if a tap is what "
                + "was wanted.",
            Capture.Taken => $"Ended because {_take!.EndedBy}.",
            Capture.Between => "The pen is off the tablet and the clock is still running. "
                + "The gap is kept, because what a hand does between two strokes is part of "
                + "what this is recording.",
            _ => "",
        };

        // One button, two jobs, because they are never both available: a take that can be
        // stopped is a take that is already running, and a take that can be armed is not.
        var stopping = _capture is Capture.Armed or Capture.Drawing or Capture.Between
            && _take?.Gesture.ManyStrokes == true;

        var arm = this.FindControl<Button>("Arm")!;

        arm.Content = stopping ? "Stop" : "Arm";
        arm.IsEnabled = stopping || _capture is Capture.Idle or Capture.Taken;

        this.FindControl<Button>("Again")!.IsEnabled = _capture is Capture.Taken;

        Tick();
        Refresh();
    }

    /// <summary>
    /// Puts the gesture's guide on the pad, faintly, as something to follow.
    /// </summary>
    /// <remarks>
    /// Drawn on the surface rather than behind it, which is the cheap way and costs something
    /// worth naming: the guide and the ink are then the same pixels, so nothing here can show
    /// the stroke on its own. Review does not try -- it redraws the take from the readings,
    /// which is the only honest way to look at a recording anyway.
    /// </remarks>
    /// <summary>
    /// Puts the pad back the way it was, on the surface that has just replaced it.
    /// </summary>
    /// <remarks>
    /// Growing keeps what was drawn, which is right for ink and wrong for the guide: the
    /// guide is placed in fractions of the surface, so the copied one sits where it belonged
    /// on the smaller pad and a freshly drawn one sits where it belongs on this pad. Two
    /// dashed lines, a hand's breadth apart, and both of them look deliberate.
    /// <para>
    /// So everything is redrawn rather than added to. The take survives because it is data:
    /// what is on the pad has always been a picture of it rather than the thing itself.
    /// </para>
    /// </remarks>
    private void Regrown()
    {
        _pad.Clear();

        DrawGuide();

        if (_take is null) return;

        var brush = Brush(_take.FullScalePressure);

        // Every stroke in the take, not just one. A many-stroke take is a series and the
        // picture of it is the series; drawing only the last would show a pad that had lost
        // the recording it is still making.
        foreach (var stroke in _take.Drawable) brush.Draw(_pad.Surface, _take.Placed, stroke);

        _pad.Redraw();
    }

    private void DrawGuide()
    {
        if (_gesture?.Shape is not { } shape) return;

        var wide = _pad.Surface.PixelWidth;
        var high = _pad.Surface.PixelHeight;

        using var paint = new SKPaint
        {
            Color = new SKColor(0x9A, 0xA3, 0xB0, 0xA0),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2,
            PathEffect = SKPathEffect.CreateDash([12, 10], 0),
        };

        switch (shape)
        {
            case Guide.Line line:
                _pad.Surface.Canvas.DrawLine(
                    (float)(wide * line.FromX), (float)(high * line.FromY),
                    (float)(wide * line.ToX), (float)(high * line.ToY), paint);

                break;

            case Guide.Arc:
                using (var path = new SKPath())
                {
                    path.MoveTo((float)(wide * 0.1), (float)(high * 0.75));
                    path.QuadTo((float)(wide * 0.5), (float)(high * -0.15),
                        (float)(wide * 0.9), (float)(high * 0.75));

                    _pad.Surface.Canvas.DrawPath(path, paint);
                }

                break;

            case Guide.Dot:
                paint.PathEffect = null;
                _pad.Surface.Canvas.DrawCircle(wide / 2f, high / 2f, 26, paint);

                paint.Style = SKPaintStyle.Fill;
                _pad.Surface.Canvas.DrawCircle(wide / 2f, high / 2f, 3, paint);

                break;
        }

        _pad.Redraw();
    }

    // ── step four ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Redraws the take on a clean pad and says what it turned out to be.
    /// </summary>
    /// <remarks>
    /// Redrawn from the readings rather than shown as the recording pad left it. Two reasons,
    /// and the second is the one that matters: the recording pad has the guide on it, so what
    /// is there is the stroke and the thing it was traced against mixed into the same pixels;
    /// and a take redrawn from its own readings is the first thing that would go wrong if the
    /// readings were not what was drawn.
    /// </remarks>
    /// <summary>
    /// Draws the take onto the review pad, from its readings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="Review"/> because it has to be able to run twice. This pad
    /// starts at its constructed size and grows to its box when the step is laid out, and the
    /// first draw can happen before that: a stroke placed beyond the small surface is not
    /// clipped for display, it is <b>dropped</b>, and growing afterwards cannot bring it back
    /// because there is nothing left to copy.
    /// </para>
    /// <para>
    /// It went unnoticed while a take was one stroke, because one stroke rarely reaches the
    /// bottom of the pad. A series spreads down it, and the last stroke of three landed one
    /// surface row short of the edge and vanished -- with the findings beside it correctly
    /// reporting three.
    /// </para>
    /// </remarks>
    private void Replayed()
    {
        _replay.Clear();

        if (_take is null) return;

        var brush = Brush(_take.FullScalePressure);

        // Through the transform the take was placed with, not through this pad's own. The
        // take is a finished thing and is replayed as it was drawn, whatever has happened to
        // the window since.
        foreach (var stroke in _take.Drawable)
        {
            brush.Draw(_replay.Surface, _take.Placed, stroke);
        }

        // The picked one again, over the top, in the accent. Drawn second rather than drawn
        // differently in the loop above: a stroke that crosses others has to sit over them to
        // be followed, and on a cross-hatching take every stroke crosses several.
        if (_picked is { } which && which < _take.Contacts.Count
            && _take.Contacts[which].Stroke is { } only)
        {
            Brush(_take.FullScalePressure, Highlight).Draw(_replay.Surface, _take.Placed, only);
        }

        _replay.Redraw();
    }

    private void Review()
    {
        var showing = this.FindControl<TextBlock>("Showing")!;
        showing.Text = _opened is null ? "" : $"Opened · {_opened}";
        showing.IsVisible = _opened is not null;

        Replayed();
        Ledger();
        Strokes();

        var list = this.FindControl<StackPanel>("FindingList")!;
        list.Children.Clear();

        if (_take is { Strokes: 0, Aloft.Count: > 0 } aloft)
        {
            var (hovered, on) = aloft.Lasted;

            list.Children.Add(Said(new Finding(Tone.Good,
                $"{aloft.Aloft.Count} readings of the pen in the air, and no strokes",
                $"Over {Timing.Said(hovered, on)}. A recording of the hovering pen, which is a "
                + "thing worth having on purpose: it says what the tablet reports when "
                + "nothing is being drawn.")));

            // A rate needs a clock, and the pen's counter is not one. Dividing by it gave a
            // hover rate about half again what the tablet was doing -- and the comparison
            // below is against a measured 240, so a wrong rate here reads as a real finding
            // about the device.
            list.Children.Add(Said(on == StrokeRecorder.Clock.Host && hovered > 0
                ? new Finding(Tone.Plain,
                    $"The pen reported {aloft.Aloft.Count / hovered:F0} readings a second "
                    + "while hovering",
                    "Against the rate in contact, which the device conventions page puts at "
                    + "240 on this tablet. The same rate means hovering is reported like "
                    + "drawing.")
                : new Finding(Tone.Plain,
                    "How often the pen reported while hovering cannot be said",
                    "This recording carries no host timestamp, and a rate worked out from "
                    + "the pen's own stamp is readings per packet-second, which is readings "
                    + "per reading. The count above is still the count.")));

            Named();
            Refresh();

            return;
        }

        if (_take is null or { Count: 0 })
        {
            list.Children.Add(Said(new Finding(Tone.Warn, "Nothing was recorded",
                "Go back a step and draw one.")));

            Refresh();

            return;
        }

        foreach (var finding in Findings.For(_take)) list.Children.Add(Said(finding));

        Refresh();
    }

    /// <summary>One finding, with its tone carried by the stripe rather than by the words.</summary>
    private static Control Said(Finding finding) => new Border
    {
        Classes = { "finding" },
        BorderBrush = new SolidColorBrush(Color.Parse(finding.Tone switch
        {
            Tone.Good => "#3F7D4F",
            Tone.Warn => "#B07A2B",
            _ => "#B9B9B2",
        })),
        Child = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = finding.Title,
                    FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse("#2B2B28")),
                },
                new TextBlock
                {
                    Text = finding.Body,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    LineHeight = 18,
                    Foreground = new SolidColorBrush(Color.Parse("#4A4A46")),
                },
            },
        },
    };

    // ── step five ───────────────────────────────────────────────────────────────

    /// <summary>Fills the save screen in, as far as anything here can fill it in.</summary>
    private void ToSave()
    {
        if (_take is null) { Refresh(); return; }

        _saved = null;
        _suggested = "";

        // Read off the take before any box is set: setting one raises Named(), which copies
        // every box back onto the take, so the ones not yet set would overwrite these with
        // whatever the last take left in them.
        var tablet = _take.Tablet.Length > 0 ? _take.Tablet : _remembered.Tablet;
        var driver = _take.Driver.Length > 0 ? _take.Driver : _remembered.Driver;
        var firmware = _take.Firmware.Length > 0 ? _take.Firmware : _remembered.Firmware;
        var pen = _take.Pen.Length > 0 ? _take.Pen : _remembered.Pen;
        var username = _take.Username.Length > 0 ? _take.Username : _remembered.Username;
        var intent = _take.Intent;
        var notes = _take.Notes;
        var chosenName = _take.Name;

        this.FindControl<TextBox>("FileName")!.Text = "";
        this.FindControl<TextBox>("Tablet")!.Text = tablet;
        this.FindControl<TextBox>("Driver")!.Text = driver;
        this.FindControl<TextBox>("Firmware")!.Text = firmware;
        this.FindControl<TextBox>("PenModel")!.Text = pen;
        this.FindControl<TextBox>("Username")!.Text = username;
        this.FindControl<TextBox>("Intent")!.Text = intent;
        this.FindControl<TextBox>("Notes")!.Text = notes;
        this.FindControl<TextBox>("RecordingName")!.Text = chosenName;
        this.FindControl<TextBlock>("Folder")!.Text = TakesFolder;

        Named();
        Refresh();
    }

    /// <summary>
    /// One name, typed anywhere, kept everywhere and remembered.
    /// </summary>
    /// <remarks>
    /// Guarded against its own echo: setting the other box raises its change event, which
    /// would call back here and set this one, and the two would answer each other until the
    /// stack ran out.
    /// </remarks>
    private bool _naming;

    private void Named(bool tablet, string value)
    {
        if (_naming) return;

        _naming = true;

        try
        {
            value = value.Trim();

            foreach (var name in tablet ? new[] { "ProbeTablet", "Tablet" }
                                        : ["ProbeDriver", "Driver"])
            {
                var box = this.FindControl<TextBox>(name)!;

                if ((box.Text ?? "") != value) box.Text = value;
            }

            if (_take is not null)
            {
                if (tablet) _take.Tablet = value;
                else _take.Driver = value;
            }

            _remembered = tablet
                ? _remembered with { Tablet = value }
                : _remembered with { Driver = value };

            _remembered.Write();
        }
        finally
        {
            _naming = false;
        }

        if (_step == 5) Named();
    }

    /// <summary>
    /// Takes what has been typed, and keeps the suggested file name in step with it.
    /// </summary>
    /// <remarks>
    /// The suggestion stops being offered the moment the reader types their own, because a
    /// name that keeps rewriting itself under the cursor is worse than no suggestion at all.
    /// </remarks>
    private void Named()
    {
        if (_take is null) return;

        _take.Name = this.FindControl<TextBox>("RecordingName")!.Text?.Trim() ?? "";
        _take.Tablet = this.FindControl<TextBox>("Tablet")!.Text?.Trim() ?? "";
        _take.Driver = this.FindControl<TextBox>("Driver")!.Text?.Trim() ?? "";
        _take.Firmware = this.FindControl<TextBox>("Firmware")!.Text?.Trim() ?? "";
        _take.Pen = this.FindControl<TextBox>("PenModel")!.Text?.Trim() ?? "";
        _take.Username = this.FindControl<TextBox>("Username")!.Text?.Trim() ?? "";
        _take.Intent = this.FindControl<TextBox>("Intent")!.Text?.Trim() ?? "";
        _take.Notes = this.FindControl<TextBox>("Notes")!.Text?.Trim() ?? "";

        var box = this.FindControl<TextBox>("FileName")!;

        // Only while the box still holds what was last suggested. The moment it holds
        // anything else it is the reader's, and a name that rewrites itself under the cursor
        // is worse than no suggestion at all.
        if ((box.Text ?? "") == _suggested)
        {
            _suggested = Trace.Suggest(_take.Gesture, _take.Tablet, _take.At, _take.Name);
            box.Text = _suggested;
        }

        this.FindControl<TextBlock>("Preview")!.Text = Headline(_take);

        // Holds rather than Count, so a take of nothing but the hovering pen can be kept. It
        // is a recording of what the tablet does when nobody is drawing, and that is a
        // question this corpus has spent a morning failing to answer from takes that were
        // about something else.
        this.FindControl<Button>("Save")!.IsEnabled = _take is { Holds: true };

        Foot();
    }

    /// <summary>
    /// The header of the file that would be written, so nobody has to save one to see it.
    /// </summary>
    /// <remarks>
    /// The header and not the readings. What is worth checking before writing is whether the
    /// file says what made it and what its numbers mean; a thousand rows of coordinates
    /// confirm nothing a reader could act on.
    /// </remarks>
    private static string Headline(Take take) =>
        $"""
         format             {Trace.Format} v{Trace.Version}
         gesture            {take.Gesture.Id}
         name               {(take.Name.Length == 0 ? "(none: the corpus will use the file name)" : take.Name)}
         intent             {(take.Intent.Length == 0 ? "(none)" : take.Intent)}
         username           {(take.Username.Length == 0 ? "(none)" : take.Username)}
         notes              {(take.Notes.Length == 0 ? "(none)" : take.Notes)}
         recordedAt         {take.At:O}
         endedBy            {take.EndedBy}

         device
           tablet           {(take.Tablet.Length == 0 ? "(not named)" : take.Tablet)}
           driver           {(take.Driver.Length == 0 ? "(not named)" : take.Driver)}
           firmware         {(take.Firmware.Length == 0 ? "(none)" : take.Firmware)}
           pen              {(take.Pen.Length == 0 ? "(none)" : take.Pen)}
           api              {take.Api}
           fullScalePressure{take.FullScalePressure,6}
           conventions      {take.Conventions}

         placement          desktop physical pixels
           scale            {take.Placed.ScaleX:F6}, {take.Placed.ScaleY:F6}
           origin           {take.Placed.OriginX:F3}, {take.Placed.OriginY:F3}

         coordinates        desktop
           tablet           {(take.ActiveArea is { } area ? area.Describe() : "(size not reported)")}
           mm per pixel     {(take.ActiveArea is { } scale ? $"{scale.MmPerPixelX:F5} across, {scale.MmPerPixelY:F5} down" : "(not reported)")}

         columns            {string.Join(", ", Trace.Columns)}
         strokes            {take.Strokes}{Spread(take)}
         readings           {take.Count} over {Timing.Said(Timing.Spanned(take.Readings))}, {take.Polls} polls
         """;

    /// <summary>The shortest and longest stroke, where there is more than one to compare.</summary>
    private static string Spread(Take take) => take.Strokes < 2
        ? ""
        : $"  ({take.Contacts.Min(contact => contact.Count)} to "
          + $"{take.Contacts.Max(contact => contact.Count)} readings each)";

    private void Keep()
    {
        if (_take is null or { Holds: false }) return;

        Named();

        var name = this.FindControl<TextBox>("FileName")!.Text?.Trim() ?? "";

        if (name.Length == 0) name = Trace.Suggest(_take.Gesture, _take.Tablet, _take.At, _take.Name);

        try
        {
            _saved = Trace.Write(_take, TakesFolder, name);

            // Kept only once a take has been written with them, so a half-typed name in an
            // abandoned session is not what the next launch offers.
            _remembered = _remembered with
            {
                Tablet = _take.Tablet,
                Driver = _take.Driver,
                Firmware = _take.Firmware,
                Pen = _take.Pen,
                Username = _take.Username,
            };
            _remembered.Write();

            Wrote(_take.Named
                ? $"Saved to {_saved}"
                : $"Saved to {_saved}, with the tablet and the driver unnamed.");
        }
        catch (Exception bad)
        {
            // Said rather than swallowed. A recording that failed to write and looked as
            // though it had is the worst outcome this screen has.
            _saved = null;

            Wrote($"Could not save: {bad.Message}");
        }

        Foot();
    }

    /// <summary>Says where the take went, or why it did not, and shows the card that says it.</summary>
    private void Wrote(string outcome)
    {
        this.FindControl<TextBlock>("SaveState")!.Text = outcome;
        this.FindControl<Border>("SaveCard")!.IsVisible = outcome.Length > 0;
    }

    private void OpenFolder()
    {
        Directory.CreateDirectory(TakesFolder);

        using var opening = new System.Diagnostics.Process();

        opening.StartInfo.FileName = TakesFolder;
        opening.StartInfo.UseShellExecute = true;
        opening.Start();
    }

    /// <summary>The figures worth a number, which is not the same set as the figures worth showing.</summary>
    /// <remarks>
    /// Pressure, azimuth, altitude and twist used to be here too, and each of them is already on
    /// a dial three inches to the right: the lean dial is a polar plot of azimuth and altitude,
    /// the barrel dial is twist, and the bar beside them is pressure. Ten cards at two to a row
    /// was five rows of duplicate, which is most of what made this step scroll.
    /// <para>
    /// Tilt x and y stay, because no dial shows them. They carry the device's own sign
    /// convention -- on the tablet measured here a lean to the right reports a <em>negative</em>
    /// x -- and that is a fact about the hardware that the derived lean and azimuth have already
    /// thrown away.
    /// </para>
    /// <para>
    /// The api readout went the same way, for the same reason one step further up: it read
    /// "WintabDigitizer" directly beneath a combo box reading "Wintab (digitizer)".
    /// </para>
    /// <para>
    /// Pressure came back, as a figure rather than only the bar: beside the maximum the
    /// backend will report, the question on this step is what the pen is pressing now, and a
    /// bar answers a different one. A count of points seen went the other way -- how much was
    /// recorded is what the record and analysis steps are for, and a pre-flight only has to
    /// show that something is arriving.
    /// </para>
    /// </remarks>
    private Readout[] All => [_range, _area, _pressure, _tiltX, _tiltY, _rate, _batch];

    /// <summary>
    /// Where every reading of the take ended up, as a column of counts.
    /// </summary>
    /// <remarks>
    /// A ledger and not a finding. The counts used to be written into a sentence -- "218 were
    /// handed over, 104 are in strokes, 0 in the airborne record, 0 were off the pad" -- and a
    /// reader wanting to know whether they add up had to take the prose apart first. Set out
    /// in a column they add up or they visibly do not, which is the only question anybody has
    /// ever asked of them.
    /// <para>
    /// The rows above the rule belong to the layer below this window and are absent when the
    /// session could not be asked. The rows below it are this window's own, and their total is
    /// checked against what it was handed.
    /// </para>
    /// </remarks>
    private void Ledger()
    {
        var host = this.FindControl<StackPanel>("LedgerHost")!;
        host.Children.Clear();

        if (_take is not { } take) return;

        if (take.Counted is { } counted)
        {
            host.Children.Add(Tally("from the driver", counted.FromDriver));
            host.Children.Add(Tally("outside the capture region", counted.OutsideRegion));
            host.Children.Add(Tally("delivered to this window", counted.Delivered));
        }

        host.Children.Add(Tally("handed over", take.Routed, rule: true));
        host.Children.Add(Tally("in strokes", take.Count));
        host.Children.Add(Tally("in the airborne record", take.Aloft.Count));
        host.Children.Add(Tally("airborne, beside a stroke", take.KeptAlongside));
        host.Children.Add(Tally("airborne, kept nowhere", take.LeftOut));
        host.Children.Add(Tally("off the pad", take.DroppedOffPad));
        host.Children.Add(Tally("after the stop", take.AfterTheStop));

        var stored = take.Count + take.Aloft.Count + take.DroppedOffPad + take.AfterTheStop;

        // Only when it fails. A ledger that balances needs no line saying so; one that does
        // not is the most important thing on the screen.
        if (stored != take.Routed)
        {
            host.Children.Add(Tally($"unaccounted for", take.Routed - stored, alarm: true));
        }
    }

    /// <summary>
    /// Every stroke of the take, one to a row.
    /// </summary>
    /// <remarks>
    /// Durations come off <see cref="Reading.Arrived"/> and not <see cref="Reading.At"/>. The
    /// pen's own stamp advances a flat 4.166 ms per packet whatever the elapsed time, so a
    /// column of stroke lengths taken from it is a column of reading counts wearing a unit.
    /// </remarks>
    /// <summary>
    /// Opens a trace from disk and shows it on this step.
    /// </summary>
    /// <remarks>
    /// The take it makes is a reading of the file and not a recording: it has no session
    /// behind it and nothing will be added to it. That is what this step wants -- everything
    /// on it is a description of readings that have already been taken.
    /// </remarks>
    /// <summary>Whether an open is already in progress. See <see cref="Reread"/>.</summary>
    /// <remarks>
    /// A plain field and not an interlock: every path that touches it is a UI event handler,
    /// so they all run on the one thread and the only interleaving possible is the one an
    /// <c>await</c> creates, which this covers.
    /// </remarks>
    private bool _rereading;

    private async Task Reread()
    {
        var said = this.FindControl<TextBlock>("Reopened")!;

        // Two buttons call this and neither was gated. Both pickers would open, and the take
        // that won was whichever file was chosen last -- not whichever was asked for last --
        // after which it replaced the state the other one had already installed.
        if (_rereading) return;

        _rereading = true;

        try
        {
            var from = await StorageProvider.TryGetFolderFromPathAsync(TakesFolder);

            var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open a take",
                AllowMultiple = false,
                SuggestedStartLocation = from,
                FileTypeFilter = [new FilePickerFileType("Traces") { Patterns = ["*.json"] }],
            });

            if (picked.Count == 0) return;

            var path = picked[0].TryGetLocalPath();

            if (path is null)
            {
                said.Text = "That file is not on this machine.";

                return;
            }

            // Off the thread that draws. Reopen touches nothing of Avalonia's -- it reads a
            // file and builds a take -- and a long one held the window still while it parsed.
            var read = await Task.Run(() => Reopen.From(path));

            // The window can close while a picker is open, and the parse then finishes into
            // nothing. Checked after the last await rather than before, because it is the
            // await that lets the close happen.
            if (_shut) return;

            if (read.Take is not { } take)
            {
                said.Text = $"Could not open it: {read.Why}";

                return;
            }

            _capturing.Opened(take);
            _gesture = take.Gesture;

            var name = Path.GetFileNameWithoutExtension(path);

            said.Text = name;

            _opened = name;

            // Straight to the analysis, which is the only reason to open one. GoTo is what
            // moves a step; Stage only redraws the record step's own words, so setting _step
            // beside it left the reader on the pre-flight with a take loaded and nothing to
            // show for it.
            GoTo(4);
        }
        catch (Exception why)
        {
            // The picker is the platform's, and it can fail for reasons this application has
            // no say in -- a shell that will not start, a folder that has gone. Unhandled,
            // that is an exception on a void event handler, which is a crash rather than a
            // message.
            said.Text = $"Could not open it: {why.Message}";
        }
        finally
        {
            _rereading = false;
        }
    }

    private void Strokes()
    {
        var block = this.FindControl<StackPanel>("StrokeBlock")!;
        var rows = this.FindControl<StackPanel>("StrokeRows")!;

        rows.Children.Clear();

        // Out of range for this take. Reopening a shorter one with a stroke picked would
        // otherwise leave a selection pointing past the end of it.
        if (_picked >= (_take?.Contacts.Count ?? 0)) _picked = null;

        // One stroke needs no table: everything a row would say is already in the findings
        // and the ledger, and a table of one is a heading with a line under it.
        if (_take is not { Strokes: > 1 } take)
        {
            block.IsVisible = false;

            return;
        }

        block.IsVisible = true;

        rows.Children.Add(Ruled("#", "n", "ms", "px", "peak", "appr", head: true));

        for (var each = 0; each < take.Contacts.Count; each++)
        {
            var contact = take.Contacts[each];

            if (contact.Count == 0) continue;

            var readings = contact.Readings;

            var ms = (readings[^1].Arrived - readings[0].Arrived) / 1000.0;

            var length = 0.0;
            for (var step = 1; step < readings.Count; step++)
            {
                length += Math.Sqrt(Math.Pow(readings[step].X - readings[step - 1].X, 2)
                                  + Math.Pow(readings[step].Y - readings[step - 1].Y, 2));
            }

            var which = each;

            var row = Ruled(
                $"{each + 1}",
                $"{contact.Count}",
                $"{ms:F0}",
                $"{length:F0}",
                $"{readings.Max(reading => reading.Pressure):N0}",
                contact.Approach.Count > 0 ? $"{contact.Approach.Count}" : "—");

            var hit = new Border
            {
                Child = row,
                Padding = new Thickness(4, 2, 4, 2),
                CornerRadius = new CornerRadius(3),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                // Fully qualified: this file has "using StrokeFieldGuide.Brushes", so a bare
                // Brushes is the guide's namespace of brush engines and not Avalonia's palette.
                Background = _picked == which
                    ? new SolidColorBrush(Color.Parse("#F3E4D8"))
                    : Avalonia.Media.Brushes.Transparent,
            };

            // One click picks the stroke out of the drawing; two open it on its own. Clicking
            // the row it is already on clears it, so a reader can put the drawing back without
            // hunting for somewhere else to click.
            hit.PointerPressed += (_, click) =>
            {
                if (click.ClickCount >= 2)
                {
                    _picked = which;

                    Replayed();
                    Strokes();
                    Closer(which);

                    return;
                }

                _picked = _picked == which ? null : which;

                Replayed();
                Strokes();
            };

            rows.Children.Add(hit);
        }
    }

    /// <summary>
    /// Opens one stroke in the analyser.
    /// </summary>
    /// <remarks>
    /// A window rather than a step, because a reader goes into one stroke and comes back to the
    /// take: making it a step would put the take's own analysis behind a Back button and lose
    /// which stroke they had picked.
    /// </remarks>
    private void Closer(int which)
    {
        if (_take is not { } take || which >= take.Contacts.Count) return;

        if (take.Contacts[which].Count == 0) return;

        Analyser.For(take.Contacts[which], which + 1, take.Strokes, _opened ?? "this take")
                .Show(this);
    }

    /// <summary>One row of the stroke table, or its header.</summary>
    private static Control Ruled(string index, string count, string ms, string px,
                                 string peak, string approach, bool head = false)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("28,*,*,*,*,52"),
            Margin = new Thickness(0, 0, 0, head ? 3 : 2),
        };

        var ink = head ? "#7A7A74" : "#3A3A36";
        var cells = new[] { index, count, ms, px, peak, approach };

        for (var column = 0; column < cells.Length; column++)
        {
            var cell = new TextBlock
            {
                Text = cells[column],
                FontFamily = head ? FontFamily.Default : new FontFamily("Consolas,Menlo,monospace"),
                FontSize = head ? 10 : 12,
                Foreground = new SolidColorBrush(Color.Parse(ink)),
                TextAlignment = column == 0
                    ? Avalonia.Media.TextAlignment.Left
                    : Avalonia.Media.TextAlignment.Right,
                Margin = new Thickness(0, 0, 8, 0),
            };

            Grid.SetColumn(cell, column);
            grid.Children.Add(cell);
        }

        if (!head) return grid;

        return new StackPanel
        {
            Children =
            {
                grid,
                new Border
                {
                    BorderBrush = new SolidColorBrush(Color.Parse("#D8D8D2")),
                    BorderThickness = new Thickness(0, 1, 0, 0),
                    Margin = new Thickness(0, 0, 0, 4),
                },
            },
        };
    }

    /// <summary>One row of the ledger: what it is on the left, how many on the right.</summary>
    private static Control Tally(string what, long many, bool rule = false, bool alarm = false)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, rule ? 6 : 0, 0, 3),
        };

        var ink = alarm ? "#A6371F" : "#3A3A36";

        var name = new TextBlock
        {
            Text = what,
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.Parse(alarm ? ink : "#7A7A74")),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        var count = new TextBlock
        {
            Text = many.ToString("N0"),
            FontFamily = new FontFamily("Consolas,Menlo,monospace"),
            FontSize = 13,
            FontWeight = alarm ? FontWeight.SemiBold : FontWeight.Normal,
            Foreground = new SolidColorBrush(Color.Parse(ink)),
        };

        Grid.SetColumn(name, 0);
        Grid.SetColumn(count, 1);
        grid.Children.Add(name);
        grid.Children.Add(count);

        if (!rule) return grid;

        // A line where the layer changes. Above it is what the session counted, below it is
        // what this window did with what it was given, and the two are different claims.
        return new StackPanel
        {
            Children =
            {
                new Border
                {
                    BorderBrush = new SolidColorBrush(Color.Parse("#D8D8D2")),
                    BorderThickness = new Thickness(0, 1, 0, 0),
                    Margin = new Thickness(0, 2, 0, 0),
                },
                grid,
            },
        };
    }

    /// <summary>What a backend is called in a caption, as against in the enum.</summary>
    private static string Sounds(InputApi api) => api switch
    {
        InputApi.WintabSystem or InputApi.WintabDigitizer => "Wintab",
        InputApi.WmPointer => "WM_POINTER",
        _ => api.ToString(),
    };

    /// <summary>
    /// The conventions line with its wrapper and its field names taken off.
    /// </summary>
    /// <remarks>
    /// <c>PenConventions { RawUnits = TabletNative, Buttons = WintabEvent, Cursor =
    /// DeviceAssigned, Timestamp = DeviceTicks }</c> is four facts and about sixty characters of
    /// syntax around them, and at this pane's width that syntax was three wrapped lines. The
    /// values are distinctive enough to read alone -- nobody who needs to know the timestamps are
    /// device ticks is helped by being told the field is called Timestamp.
    /// </remarks>
    private static string Plainly(string conventions)
    {
        var open = conventions.IndexOf('{');
        var close = conventions.LastIndexOf('}');

        // Not the shape this expects. Show it as it came rather than a mangled half of it.
        if (open < 0 || close <= open) return conventions;

        var inside = conventions[(open + 1)..close];

        return string.Join(" · ", inside
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(pair => pair[(pair.IndexOf('=') + 1)..].Trim())
            .Where(value => value.Length > 0));
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Delete and backspace wipe the strip, on the step where there is one.
    /// </summary>
    /// <remarks>
    /// Both, because which of them means "get rid of that" is a habit rather than a rule and
    /// a reader checking a pen should not have to find out which habit this window has.
    /// <para>
    /// Not while a text box has the focus. Backspace there means backspace, and a window that
    /// wiped a drawing because somebody corrected a tablet's name would deserve what it got.
    /// </para>
    /// </remarks>
    /// <summary>
    /// What this binary was built from, as far as it can tell.
    /// </summary>
    /// <remarks>
    /// The informational version carries the commit when the build was deterministic and the
    /// source was committed at build time. It lags when neither is true -- building with
    /// uncommitted changes stamps the previous commit -- so it is shown with the file's own
    /// timestamp beside it, which does not lag.
    /// </remarks>
    private static string BuildStamp()
    {
        var assembly = Assembly.GetEntryAssembly();

        var version = assembly?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown";

        var commit = version.Contains('+') ? version[(version.IndexOf('+') + 1)..] : version;

        if (commit.Length > 7) commit = commit[..7];

        var built = assembly?.Location is { Length: > 0 } path && File.Exists(path)
            ? File.GetLastWriteTime(path).ToString("HH:mm:ss")
            : "?";

        return $"{commit}, built {built}";
    }

    /// <summary>
    /// Writes down every key this window is offered, before anything decides to ignore it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Escape was reported as doing nothing, and silence has two explanations that look
    /// identical from outside: the handler never ran, or it ran and every guard inside it
    /// declined. Guessing between them is what produced three fixes that could not work.
    /// </para>
    /// <para>
    /// So this records the key and the state that decides its fate, at the top of the handler
    /// and before the first guard. To a file rather than the window, because a diagnostic that
    /// changes focus would change the thing being diagnosed.
    /// </para>
    /// </remarks>
    private void Logged(KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Escape)) return;

        try
        {
            var where = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "StrokeFieldGuide", "keys.log");

            Directory.CreateDirectory(Path.GetDirectoryName(where)!);

            File.AppendAllText(where,
                $"{DateTime.Now:HH:mm:ss.fff} {e.Key,-6} {e.RoutedEvent?.Name,-8} "
                + $"handled={e.Handled,-5} focus={FocusManager?.GetFocusedElement()?.GetType().Name ?? "none",-12} "
                + $"active={IsActive,-5} step={_step} capture={_capture} "
                + $"many={_gesture?.ManyStrokes} take={(_take is null ? "none" : _take.Strokes + " strokes")}"
                + Environment.NewLine);
        }
        catch
        {
            // A diagnostic that throws is worse than one that is missing.
        }
    }

    /// <summary>
    /// Swallows the release of any key this window acted on, so nothing else acts on it too.
    /// </summary>
    /// <remarks>
    /// Consuming the down without the up is owning half a gesture, and the half left behind
    /// goes to whatever holds focus. A button is the likely holder here because pressing one
    /// is how somebody armed the take in the first place.
    /// </remarks>
    private void Released(object? sender, KeyEventArgs e)
    {
        Logged(e);

        if (FocusManager?.GetFocusedElement() is TextBox) return;

        if (e.Key == Key.Space) _spaceHeld = false;

        if (_step == 3 && e.Key is Key.Space or Key.Escape) e.Handled = true;
    }

    private void Pressed(object? sender, KeyEventArgs e)
    {
        Logged(e);

        if (FocusManager?.GetFocusedElement() is TextBox) return;

        // Space arms and disarms, so a reader recording one stroke after another never has to
        // put the pen down and find a button. It is taken before the canvas sees it, because
        // a canvas holds the space bar for hand-panning and would mark it handled.
        // Escape stops and does nothing else, ever. Space toggles, and a toggle is the wrong
        // shape for a key somebody presses while holding a pen they must not move: the key
        // repeats, the second press falls through to the arming branch, and the take that was
        // just stopped starts again. Reported from the pad as "sometimes it stops and rearms".
        if (e.Key == Key.Escape && _step == 3)
        {
            if (Keys.Escape(_take?.Gesture.ManyStrokes == true, _capture) == Command.Stop)
            {
                StopTake();

                e.Handled = true;
            }

            return;
        }

        if (e.Key == Key.Space && _step == 3)
        {
            // What the key means is Keys.Space's to decide and this method's to carry out.
            // Kept apart because the decision is where #72 lived, and a decision can be
            // checked without a window, a tablet or a hand.
            var wanted = Keys.Space(_spaceHeld, _gesture is { ManyStrokes: true }, _capture);

            _spaceHeld = true;

            switch (wanted)
            {
                case Command.Stop: StopTake(); break;
                case Command.Arm: ArmTake(); break;
                case Command.Discard: Discard(); break;
            }

            e.Handled = true;

            return;
        }

        if (e.Key is not (Key.Delete or Key.Back)) return;

        if (_step == 1)
        {
            Wipe();
            e.Handled = true;
        }
        else if (_step == 3 && _capture is Capture.Taken)
        {
            Discard();
            e.Handled = true;
        }
    }

    private BackendChoice? Chosen =>
        this.FindControl<ComboBox>("Backends")!.SelectedItem as BackendChoice;

    private void Chose()
    {
        var chosen = Chosen;

        // Said only when there is something wrong. What a backend is worth is on its own
        // card in the list, and a paragraph repeating it under the picker was read once.
        var worth = this.FindControl<TextBlock>("BackendWorth")!;

        worth.IsVisible = chosen is { Available: false };
        worth.Text = worth.IsVisible
            ? "Not available on this machine. Its driver is not installed, or its service is "
              + "not running."
            : "";

        // Switching backend closes what was open and opens the new one. Leaving the reader
        // to press a button in between makes the commonest use of this window -- comparing
        // what two backends say about the same pen -- three clicks instead of one, and
        // leaves a picked backend sitting there reporting nothing, which reads as the
        // backend being dead rather than unopened.
        Shut();

        if (_shown) Open();
    }

    /// <summary>Closes the session, if there is one. Safe to call when there is not.</summary>
    private void Shut()
    {
        if (_pen.Session is null) return;

        // Stops polling before closing, and does both. That order was this window's own bug
        // once: a tick that fired after the session closed drained a session that was gone.
        _pen.Stop();

        this.FindControl<TextBlock>("Conventions")!.Text = "";
        Say("", "");

        Refresh();
    }

    private void Open()
    {
        if (Chosen is not { Available: true } chosen) return;

        // The window, not the pad. A framework session listens on the control it is given,
        // and a pad is only on screen for its own step -- so a session bound to step one's
        // strip hears nothing while the reader is recording on step three's pad, and records
        // a take of no readings without anything going wrong. The Wintab and WM_POINTER
        // backends do not care, which is what makes this the sort of fault that ships: it is
        // invisible on the backend most likely to be used and total on the other.
        // The window handle is what a WM_POINTER session subclasses; a Wintab one makes its
        // own pump window and ignores it, and the Avalonia one is already attached to a
        // control. Passing it in every case is simpler than deciding here which cares.
        var failure = _pen.Start(
            chosen.Backend.Api, this, TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);

        if (failure is not null)
        {
            Say($"Could not start: {failure}", "", trouble: true);

            return;
        }

        var session = _pen.Session!;

        _haveLast = false;
        _seen = 0;
        _arrivals.Clear();

        _api.Set(session.Api.ToString());
        _range.Set(session.MaxPressure.ToString());
        _range.Relabel($"max pressure level ({Sounds(session.Api)})");
        _areaShown = ShowArea(session);

        Say("Waiting for the pen.", Plainly(session.Conventions.ToString() ?? ""));

        Refresh();
    }

    /// <summary>
    /// One batch, already drained and already stamped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Everything here runs after the arrival time was taken</b>, and that is the whole
    /// reason this is a subscriber rather than a drain of its own. It used to own the loop, and
    /// updated two readouts and walked a ring buffer between taking the packets and stamping
    /// them — so the timestamp on every published recording included the cost of that work, and
    /// the cost grew with the size of the batch, which is the very thing the queue and rate
    /// readouts measure.
    /// </para>
    /// <para>
    /// There is nowhere left to put that mistake: the batch arrives carrying its own
    /// <see cref="Batch.Arrived"/>, decided in <see cref="Draining"/> before this window is
    /// told anything.
    /// </para>
    /// </remarks>
    private void Took(Batch batch)
    {
        var session = batch.Session;

        _batch.Saw(batch.Count);

        if (batch.Count == 0) return;

        // The WM_POINTER session cannot say how big the tablet is until it has seen the pen,
        // which is now. Asked again on each batch until it can, so the readout does not say
        // "not reported" for the rest of a session that has since found out.
        if (!_areaShown) _areaShown = ShowArea(session);

        _seen += batch.Count;

        var now = Environment.TickCount64;
        for (var each = 0; each < batch.Count; each++) _arrivals.Enqueue(now);
        while (_arrivals.Count > 0 && now - _arrivals.Peek() > 1000) _arrivals.Dequeue();

        _rate.Saw(_arrivals.Count);

        var before = _take?.Count ?? 0;

        for (var each = 0; each < batch.Count; each++)
        {
            Route(batch.Points[each], batch.Readings[each], session);
        }

        // One poll, however many readings it brought. Counted here rather than inside the
        // per-reading path, which would count readings twice under another name.
        if (_take is not null && _take.Count > before) _take.Polled();

        var last = batch.Points[^1];

        // Shown from the last reading of the batch, and shown whether or not the tip is down:
        // lean and twist are reported while hovering, so the pen can be turned and watched
        // without laying any ink.
        var shown = batch.Readings[^1];

        _gauges.Show(shown);
        _takeGauges.Show(shown);

        _trace.Show(last.Pressure, session.MaxPressure);
        _takeTrace.Show(last.Pressure, session.MaxPressure);

        Aim(shown, session.MaxPressure);

        // Off the packet, not the reading: a Reading keeps a lean and an azimuth where the
        // device reported a tilt x and a tilt y, and these two boxes show what it reported.
        _tiltX.Saw(last.TiltX);
        _tiltY.Saw(last.TiltY);

        // The angles have their own boxes now, with the ranges that make them worth reading.
        // The first point is what makes step one answerable, so the foot has to hear about
        // it. Cheap enough to do on every drain rather than only on the first.
        Refresh();

        _pressure.Saw(last.Pressure);
    }

    /// <summary>
    /// Draws one reported point onto the strip, with the width following pressure.
    /// </summary>
    /// <remarks>
    /// Through this guide's own brush engine rather than a line: the point of the strip is to
    /// show what the pen reported, and the engine is the thing that turns a pressure into a
    /// width everywhere else. A separate drawing path here would be a second opinion.
    /// </remarks>
    private void Lay(PenPad pad, PenPoint point, int range, InkTransform? through = null)
    {
        if (point.Pressure == 0)
        {
            // Off the surface. Nothing to draw, and the next point starts a new stroke rather
            // than joining across the gap.
            _haveLast = false;

            return;
        }

        var brush = Brush(range);

        var from = _haveLast ? _last : point;

        brush.Draw(pad.Surface, through ?? pad.ForPen(),
            new Stroke([Reported(from), Reported(point)]));

        _last = point;
        _haveLast = true;

        pad.Redraw();
    }

    /// <summary>
    /// Puts the nib outline where the pen is, on whichever pad is on screen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Whether or not the tip is down, because that is what a cursor is for: a reader lines
    /// the nib up before pressing, and a cursor that only appears once the ink does has
    /// missed the moment it was wanted.
    /// </para>
    /// <para>
    /// <b>At the brush's full size, not the size this pressure would make.</b> The first
    /// version followed pressure, which sounds right and is not: hovering is zero pressure,
    /// so the outline was half a pixel across and invisible at exactly the moment it was
    /// being used. A cursor is for aiming, and what a reader aims with is the footprint the
    /// nib can cover; how much of it this press fills is what the ink is for.
    /// </para>
    /// <para>
    /// The shape and the angle do come from the brush, at this reading. Restating those here
    /// would be a second opinion about the one thing the reader is checking.
    /// </para>
    /// </remarks>
    private void Aim(Reading reading, int fullScale)
    {
        var pad = _step == 3 ? _pad : _strip;

        if (!pad.Covers(reading.X, reading.Y))
        {
            pad.HideNib();

            return;
        }

        var brush = Brush(fullScale);
        var one = new Stroke([reading]);

        var stamp = brush.StampAt(one, brush.Placements(one).FirstOrDefault());

        pad.ShowNib(reading.X, reading.Y, brush.Diameter, stamp.Ratio, stamp.Degrees);
    }

    /// <summary>
    /// The reported reading, with its position left exactly as reported.
    /// </summary>
    /// <remarks>
    /// The units a stroke is in are whatever the ink transform is built to expect, and a
    /// pad's is built to expect desktop pixels. So there is no arithmetic here, which is the
    /// point: an application converting coordinates by hand is an application making a claim
    /// nothing checks, and both of the ways this window once put marks in the wrong place
    /// were arithmetic written in this file.
    /// </remarks>
    /// <summary>What a packet says, in this guide's terms.</summary>
    /// <remarks>
    /// The conversion itself is <see cref="Draining.Of"/>, and the reasoning behind every
    /// field is written there. It moved out of this file when the lab started opening its own
    /// session: a second reading of the same packet is how two applications come to disagree
    /// about what the pen did.
    /// </remarks>
    private static Reading Reported(PenPoint point, long arrived = 0) =>
        Draining.Of(point, arrived);

    private void Wipe()
    {
        _strip.Clear();
        _haveLast = false;

        // The extremes belong to the marks on the strip. Wiping one and keeping the other
        // leaves a range on screen that nothing visible accounts for.
        foreach (var readout in All) readout.Forget();

        _gauges.Forget();
    }

    /// <param name="trouble">
    /// Whether <paramref name="verdict"/> is something a reader has to act on. Routine progress
    /// is not: "waiting for the pen" and "reporting, 168 points so far" are both answered by the
    /// strip and by the figures beside it, and putting them in a bordered card meant the one
    /// message that matters -- a backend that will not open -- looked exactly like the two that
    /// do not.
    /// </param>
    private void Say(string verdict, string conventions, bool trouble = false)
    {
        this.FindControl<TextBlock>("Verdict")!.Text = verdict;
        this.FindControl<Border>("Trouble")!.IsVisible = trouble && verdict.Length > 0;

        // Left alone when there is nothing to say. The conventions belong to the open session
        // and are not news that arrives with each message, so a routine Say must not wipe them.
        if (conventions.Length > 0)
        {
            this.FindControl<TextBlock>("Conventions")!.Text = conventions;
        }
        // While nothing has arrived, not while nothing is open. The session now opens by
        // itself, so tying the hint to that would take it away before it had been read.
        this.FindControl<TextBlock>("StripHint")!.IsVisible = _seen == 0;
    }

    /// <summary>
    /// Everything this window owns, let go of in one place and in the right order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The close handler used to close the pen session and nothing else. It left the poll
    /// running, so a timer went on asking a disposed session for points while other windows
    /// kept the dispatcher alive; and it left three <see cref="PenPad"/> surfaces, each
    /// holding pixels the garbage collector is in no hurry over.
    /// </para>
    /// <para>
    /// <b>The timer first.</b> Stopping the session while a drain is in flight is the one
    /// ordering that can fault, and it is the ordering that a handler written a piece at a
    /// time drifts into.
    /// </para>
    /// </remarks>

    /// <summary>Whether the backend list is being rebuilt, so its selection is not a choice.</summary>
    private bool _discovering;

    /// <summary>
    /// Asks which backends can be opened on this machine, and says so in the list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Asked when the window is up, and never cached.</b> It used to run in the
    /// constructor, before there was a window. That is the wrong moment for this particular
    /// question: a Wintab context is granted against a window, and the driver's own service
    /// can be stopped and started while this application is running -- so an answer taken
    /// before the window existed can say a backend is unavailable when it is sitting there
    /// working, and an answer taken once can be wrong for the rest of the session.
    /// </para>
    /// <para>
    /// Which is why there is a button. The recovery for the commonest case -- the driver's
    /// context table filling up, the service being restarted to clear it -- is to ask again,
    /// and a list fixed at startup makes that restart invisible until the window is closed
    /// and reopened.
    /// </para>
    /// <para>
    /// Every backend is listed either way, and the ones this machine cannot open are listed
    /// as unavailable rather than left out. A backend absent because no driver is installed
    /// and one that was never offered look the same in a shorter list.
    /// </para>
    /// </remarks>
    private void Discover()
    {
        var backends = this.FindControl<ComboBox>("Backends")!;
        var available = PenBackends.Available();

        // What is selected now, so a refresh does not move a reader off the backend they
        // picked. Rebuilding the list raises SelectionChanged, which is what _discovering is
        // for: a list being replaced is not somebody choosing.
        var chosen = (backends.SelectedItem as BackendChoice)?.Backend.Api;

        var choices = PenBackends.All
            .Select(backend => new BackendChoice(backend, available.Contains(backend.Api)))
            .ToList();

        _discovering = true;

        try
        {
            backends.ItemsSource = choices;

            backends.SelectedIndex = Choosing.Backend(
                [.. PenBackends.All.Select(backend => backend.Api)], available, chosen);
        }
        finally
        {
            _discovering = false;
        }

        Chose();
    }

    /// <summary>Set once the window has gone, so work that outlives it can stop.</summary>
    /// <remarks>
    /// An open is the one thing here that can still be running when the window closes: it is
    /// waiting on a picker somebody may never answer. Everything it would do on the way back
    /// -- install a take, move a step, write into a readout -- is about a window that is no
    /// longer on screen.
    /// </remarks>
    private bool _shut;

    private void Shutdown()
    {
        _shut = true;

        // The stream stops its own timer before closing its session.
        _pen.Dispose();

        _strip.Dispose();
        _pad.Dispose();
        _replay.Dispose();
    }

    /// <summary>A backend and whether this machine can open it.</summary>
    private sealed record BackendChoice(Backend Backend, bool Available)
    {
        public override string ToString() =>
            Available ? Backend.Name : $"{Backend.Name}  (not available)";
    }

    /// <summary>One labelled number, built in code because there are seven of them.</summary>
    /// <summary>One labelled number, and the extremes it has been seen at.</summary>
    /// <remarks>
    /// <para>
    /// The extremes are the point. A live number answers "is tilt reported at all", which
    /// takes one glance, and then stops being useful: what a reader actually wants to know
    /// is the range the device covers and whether a value that should be steady is steady.
    /// Neither can be read off a figure changing sixty times a second, and both are read off
    /// a low and a high after one scribble.
    /// </para>
    /// <para>
    /// A held-still pen is the case this makes cheap. If the low and the high are a degree
    /// apart the reading is steady enough to drive a nib angle directly; if they are ten
    /// apart it needs smoothing first, and nobody has to decide which by watching.
    /// </para>
    /// </remarks>
    private sealed class Readout(string label)
    {
        /// <summary>What a readout says when it has nothing to say.</summary>
        private const string Nothing = "—";

        private readonly TextBlock _value = new()
        {
            Text = Nothing,
            FontFamily = new FontFamily("Consolas,Menlo,monospace"),
            FontSize = 14,
        };

        private readonly TextBlock _range = new()
        {
            Text = "",
            FontFamily = new FontFamily("Consolas,Menlo,monospace"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#9A9A94")),
        };

        private double _low;
        private double _high;
        private bool _seen;

        public Control Visual => _visual;

        /// <remarks>
        /// Centred rather than stretched. A WrapPanel hands every child in a row the height of
        /// the tallest, and on the record step these sit beside gauges twice their size -- so
        /// each figure was a short line of text at the top of a tall empty box.
        /// </remarks>
        private readonly Border _visual = new()
        {
            Classes = { "readout" },
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        public void Set(string value) => _value.Text = value;

        /// <summary>Changes the caption, for a readout whose meaning depends on the session.</summary>
        public void Relabel(string text)
        {
            if (_name is not null) _name.Text = text;
        }

        private TextBlock? _name;

        /// <summary>Shows a number, and widens the range it has been seen in.</summary>
        public void Saw(double value, string format = "F0")
        {
            _value.Text = value.ToString(format);

            _low = _seen ? Math.Min(_low, value) : value;
            _high = _seen ? Math.Max(_high, value) : value;
            _seen = true;

            _range.Text = $"{_low.ToString(format)} … {_high.ToString(format)}";
        }

        /// <summary>
        /// Forgets everything, so the next scribble is measured on its own.
        /// </summary>
        /// <remarks>
        /// The reading as well as the range. Clearing only the range left the last number
        /// from the previous take sitting under an empty range on a screen that had just
        /// thrown that take away -- a figure with nothing on screen to account for it, which
        /// is the one thing a readout must never show.
        /// </remarks>
        public void Forget()
        {
            _seen = false;
            _range.Text = "";
            _value.Text = Nothing;
        }

        /// <summary>The same figures as a table row: label, value, range, on one line.</summary>
        /// <remarks>
        /// A readout is a label and a number, and <see cref="Build"/> gives it the room of a
        /// paragraph -- three stacked lines inside a bordered card, two to a row. That is the
        /// right shape for the three figures on the record step and the wrong one for the five
        /// on the pen step, where it was most of the reason that pane scrolled.
        /// <para>
        /// Same TextBlocks either way, so whatever feeds the readout does not care which shape
        /// it was built in -- except the range, which a row leaves out entirely. A readout can
        /// only be built once: the second call would move the same children into a second
        /// parent and empty the first.
        /// </para>
        /// </remarks>
        public Readout AsRow()
        {
            _value.FontSize = 13;

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Thickness(0, 0, 0, 3),
            };

            var name = new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse("#7A7A74")),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };

            _name = name;

            _value.TextAlignment = Avalonia.Media.TextAlignment.Right;

            Grid.SetColumn(name, 0);
            Grid.SetColumn(_value, 1);

            grid.Children.Add(name);
            grid.Children.Add(_value);

            // No range. The pre-flight asks whether the pen is reporting, and a number that
            // moves answers that; the low and the high are a description of a take, which is
            // the record step's job. They were also sitting against the values, because the
            // value column is right-aligned and the range began immediately after it.

            _visual.Classes.Clear();
            _visual.Padding = new Thickness(0);
            _visual.Margin = new Thickness(0);
            _visual.Child = grid;

            return this;
        }

        public Readout Build()
        {
            _visual.Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    _value,
                    new TextBlock
                    {
                        Text = label,
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Color.Parse("#7A7A74")),
                    },
                    _range,
                },
            };

            return this;
        }
    }
}
