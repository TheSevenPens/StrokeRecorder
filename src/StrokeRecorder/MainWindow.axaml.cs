using System.Reflection;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using WinPenKit.Diagnostics;
using Avalonia.Threading;
using SkiaSharp;
using StrokeFieldGuide.Brushes;
using Nib = StrokeFieldGuide.Brushes.Brush;
using StrokeFieldGuide.Canvas;
using StrokeFieldGuide.Strokes;
using StrokeFieldGuide.Surfaces;
using StrokeFieldGuide.Views;
using WinPenKit;

namespace StrokeFieldGuide.Recorder;

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
public partial class MainWindow : Window
{
    /// <summary>How often the session is drained, in milliseconds.</summary>
    /// <remarks>
    /// A poll rather than a callback, because that is the shape <see cref="IPenSession"/>
    /// offers: it queues points on its own thread and hands them over when asked. Sixteen
    /// milliseconds is a frame, and a tablet reporting at 200 Hz will hand over three or four
    /// at a time -- which is worth seeing rather than hiding, so the readout says how many.
    /// </remarks>
    private const int PollMilliseconds = 16;

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
    private readonly DispatcherTimer _poll;

    private readonly Readout _api = new("api");
    private readonly Readout _range = new("full-scale pressure");
    private readonly Readout _pressure = new("pressure");
    private readonly Readout _tiltX = new("tilt x");
    private readonly Readout _tiltY = new("tilt y");
    private readonly Readout _azimuth = new("azimuth");
    private readonly Readout _altitude = new("altitude");
    private readonly Readout _twist = new("twist");
    private readonly Readout _rate = new("points a second");
    private readonly Readout _batch = new("per poll");

    private IPenSession? _session;

    /// <summary>Whether the window exists yet, which a session needs and a constructor has not.</summary>
    private bool _shown;

    /// <summary>Which step is on screen, counting from one as the rail does.</summary>
    private int _step = 1;

    private Gesture? _gesture;

    private Capture _capture = Capture.Idle;

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
    private Take? _take;

    /// <summary>
    /// The pen in the air, kept for a quarter of a second in case it lands.
    /// </summary>
    /// <remarks>
    /// Held on the window rather than on the take, because the readings that matter most
    /// arrive <b>before</b> the take exists: a take is created by the first contact, and the
    /// approach to that contact is already over by then.
    /// </remarks>
    private readonly List<Reading> _hover = [];

    /// <summary>
    /// Airborne readings kept before a take exists, for the debugging option only.
    /// </summary>
    /// <remarks>
    /// The same reason <see cref="_hover"/> lives here: a take is created by the first
    /// contact, and what the pen did on the way to it is already over by then.
    /// </remarks>
    private readonly List<Reading> _aloft = [];

    /// <summary>The session's own counts when the take was armed, to subtract from later.</summary>
    private (long, long, long)? _armedAt;

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
    private long _lastSpace;

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
    private const long HoverKept = 250_000;

    private readonly Readout _took = new("readings");
    private readonly Readout _lasted = new("milliseconds");
    private readonly Readout _takePressure = new("pressure");

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
        _pad.Grew += (_, _) => Regrown();

        // The same treatment for the review pad, and for the same reason. Without it a stroke
        // that landed past the pad's constructed size is gone before anybody sees the step.
        _replay.Grew += (_, _) => { if (_step == 4) Replayed(); };
        this.FindControl<Panel>("PadHost")!.Children.Add(_pad);

        foreach (var readout in All)
        {
            this.FindControl<WrapPanel>("Readouts")!.Children.Add(readout.Build().Visual);
        }

        var backends = this.FindControl<ComboBox>("Backends")!;
        var available = Pen.Available();

        // Every backend is listed, and the ones this machine cannot open are listed as
        // unavailable rather than left out. A backend that is absent because no driver is
        // installed and one that was never offered look the same in a shorter list.
        backends.ItemsSource = Pen.All
            .Select(backend => new BackendChoice(backend, available.Contains(backend.Api)))
            .ToList();

        backends.SelectedIndex = Pen.All
            .Select((backend, index) => (backend, index))
            .Where(pair => available.Contains(pair.backend.Api))
            .Select(pair => pair.index)
            .DefaultIfEmpty(0)
            .First();

        backends.SelectionChanged += (_, _) => Chose();

        this.FindControl<Button>("Start")!.Click += (_, _) => StartOrStop();
        this.FindControl<Button>("Clear")!.Click += (_, _) => Wipe();

        BuildGestures();

        // First in the row, so the dials sit beside the figures rather than under them.
        this.FindControl<WrapPanel>("TakeReadouts")!.Children.Add(_takeGauges);
        this.FindControl<WrapPanel>("TakeReadouts")!.Children.Add(_takeTrace);

        foreach (var readout in Taken)
        {
            this.FindControl<WrapPanel>("TakeReadouts")!.Children.Add(readout.Build().Visual);
        }

        this.FindControl<Button>("Save")!.Click += (_, _) => Keep();
        this.FindControl<Button>("ShowFolder")!.Click += (_, _) => OpenFolder();

        this.FindControl<TextBox>("FileName")!.TextChanged += (_, _) => Foot();

        foreach (var box in new[] { "Tablet", "Driver", "Intent" })
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

        _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PollMilliseconds) };
        _poll.Tick += (_, _) =>
        {
            Drain();

            // Whether or not that brought any readings. A wall clock that only moved when the
            // pen reported would be the same misleading thing wearing a different number.
            if (_step == 3 && _take is { Gesture.ManyStrokes: true }) Tick();
        };

        Chose();

        // Not from the constructor. A WM_POINTER session subclasses the window handle and a
        // Wintab one wants a window in the foreground, and at this point there is no window
        // -- TryGetPlatformHandle answers null and the session starts against nothing.
        Opened += (_, _) => { _shown = true; Open(); };

        Closed += (_, _) => Close(_session);
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
            1 when _session is null => "No session open.",
            1 when _seen == 0 => "Draw on the strip once, and the pen will have proved itself.",
            1 => $"{_seen} points reported through {_session!.Api}. Ready.",

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
            new TextBlock
            {
                Text = gesture.Detail,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.Parse("#4A4A46")),
                LineHeight = 19,
            },
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

    private Readout[] Taken => [_took, _lasted, _takePressure];

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
    private Nib Brush(int fullScale) => new(
        _diameter, SKColors.Black.WithAlpha(0xD0), 0.25, Buildup.PerStamp,
        new Width(Math.Min(0.5, _diameter / 20), _diameter, (uint)Math.Max(1, fullScale)),
        SpacedBy.Diameters,
        Nib: _round ? null : new StrokeFieldGuide.Brushes.Nib(0.3, 0, Held.ToTheLean));

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
    private void Route(PenPoint point, IPenSession session)
    {
        if (_step == 1)
        {
            Lay(_strip, point, session.MaxPressure);

            return;
        }

        if (_step == 3) Record(point, session);
    }

    private void Record(PenPoint point, IPenSession session)
    {
        var reading = Reported(point);

        _take?.Routing(reading.At);

        // The pen in the air. Kept whenever a take could be affected by it: before one starts,
        // between its strokes, and for a moment after the last one -- and dropped the instant
        // the tip goes down, because from there the stroke itself is the record.
        if (!reading.InContact)
        {
            // Unfiltered and before anything else, because the whole point of the option is to
            // see what the recorder is choosing to drop.
            if (Keeping)
            {
                // Asked on every reading, not once when the take was armed. The switch is on
                // the same screen as the recording and there is no reason somebody cannot
                // reach for it halfway through.
                if (_take is null) _aloft.Add(reading); else _take.Keep(reading);
            }

            if (Airborne(reading)) Hovering(reading);
        }

        // Only over the pad. A tablet reports the pen wherever it is, so without this the tap
        // that presses Arm is itself recorded as a stroke -- it armed, took a two-reading
        // take from the same tap, and read as the button un-arming itself.
        //
        // A pen that wanders off the pad mid-stroke ends the take, which is the same thing
        // the tip lifting does and is the honest reading of it: what happened after that is
        // not on this drawing.
        if (!_pad.Covers(reading.X, reading.Y))
        {
            if (_capture == Capture.Drawing)
            {
                _take!.Current!.EndedBy = "the pen left the pad";

                // On a many-stroke take this ends the stroke and not the take. Wandering off
                // the pad is the same event as a lift for the stroke being drawn, and the
                // take is only over when somebody says it is.
                if (_take.Gesture.ManyStrokes)
                {
                    _capture = Capture.Between;
                }
                else
                {
                    _take.EndedBy = "the pen left the pad";
                    _capture = Capture.Taken;
                }

                _haveLast = false;

                Stage();
            }

            // Counted, because this is the one path that reaches here and stores nothing.
            // An airborne reading was already kept above; a reading with the tip down that
            // lands off the pad is dropped entirely, and until this tally existed there was
            // no way to tell that from the device going quiet.
            if (reading.InContact) _take?.DroppedOne();

            return;
        }

        // Written as ifs rather than a switch on purpose. The first version used `goto case
        // Capture.Drawing` to fall from the first contact into the collecting branch, and C#
        // sent it to the *unguarded* Drawing label -- the one that handles the pen lifting.
        // So every take ended on the reading that started it: nought readings, state Taken,
        // and a window that looked like it had recorded something.
        if (_capture == Capture.Armed && reading.InContact)
        {
            // The transform is taken here, once, and every reading in this take is placed
            // through it. See Take for why it cannot be taken at save time.
            // Already made, if arming made it. A many-stroke take exists from the moment it
            // was armed and the first contact adds a stroke to it rather than creating it.
            _take ??= new Take(_gesture!, session.Api, session.MaxPressure, _pad.ForPen())
            {
                Conventions = session.Conventions.ToString() ?? "",
            };
            _take.KeepAll(_aloft);
            _aloft.Clear();
            var opening = Approaching(reading);
            _take.Begin().Approaching(opening.Readings, opening.SinceLastSeen, opening.Last);

            _capture = Capture.Drawing;
            _haveLast = false;
        }

        if (_capture == Capture.Drawing)
        {
            if (reading.InContact)
            {
                _take!.Add(reading);

                Lay(_pad, point, session.MaxPressure, _take.Placed);

                _took.Saw(_take.Count);
                _lasted.Saw(_take.Milliseconds);
                _takePressure.Saw(reading.Pressure);

                Tick();
            }
            else
            {
                // The tip lifted. For most gestures that is the end of the take and needs no
                // button; for a many-stroke one it is the end of a stroke and nothing more.
                _capture = _take!.Gesture.ManyStrokes ? Capture.Between : Capture.Taken;
                _haveLast = false;
            }
        }
        else if (_capture == Capture.Between && reading.InContact)
        {
            // Another stroke in the same take. Nothing is cleared and nothing is reset: the
            // pad keeps what is already on it, so the series accumulates into one picture,
            // and the clock keeps running so the gap between the strokes stays measurable.
            var next = _take!.Begin();

            var coming = Approaching(reading);
            next.Approaching(coming.Readings, coming.SinceLastSeen, coming.Last);
            next.Add(reading);

            Lay(_pad, point, session.MaxPressure, _take.Placed);

            _capture = Capture.Drawing;
            _haveLast = false;
        }
        else if (_capture == Capture.Taken && reading.InContact)
        {
            // Drawing again after a take starts the next one, with no button in between.
            // Pressing Arm for every stroke is the wrong shape for what somebody recording
            // actually does, which is draw, look, draw again.
            //
            // The previous take is held right up to this moment rather than thrown away when
            // the last one finished, so a take is only lost by starting another -- and a
            // reader who wants to keep it presses Next before putting the pen down.
            //
            // Never on a many-stroke gesture, and this is the whole of the "it stops and
            // restarts" fault. Stopping one of those with the tip still down leaves the state
            // Taken while the pen is still in contact, so the very next reading four
            // milliseconds later arrived here and began a fresh recording. The stop worked
            // perfectly every time; a new take replaced it before anybody could see.
            //
            // A many-stroke take is armed deliberately, and stopped deliberately. Drawing
            // after it has been stopped is somebody finishing their stroke, not asking for
            // another recording.
            if (_gesture is { ManyStrokes: true })
            {
                // Counted, not merely discarded. These readings were handed to this window and
                // are deliberately not kept, and a take whose received count exceeds what it
                // stored looks exactly like data going missing -- which is the question this
                // recorder exists to answer honestly.
                _take!.OneAfterTheStop();

                Stage();

                return;
            }

            Restart(session, reading);

            _take!.Add(reading);

            Lay(_pad, point, session.MaxPressure, _take.Placed);
        }

        Stage();
    }

    /// <summary>
    /// Whether an airborne reading is the pen being somewhere, or the pen being gone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Wintab device stops reporting when the pen is lifted out of range, and the last
    /// packet before it goes is not a measurement: the position repeats the one before it and
    /// the orientation comes back as an altitude of 90 and an azimuth of 0 -- a pen standing
    /// perfectly upright and aimed due north, at the moment there is no pen. Kept, that is a
    /// false vertical at the end of every departure, in the one array whose purpose is to say
    /// how the pen left.
    /// </para>
    /// <para>
    /// <b>Recognised by what it is, and not by the session's proximity flag.</b> That was
    /// tried first and is why this comment is long. <c>WintabDigitizerSession</c> declares
    /// <c>PenCapabilities.Proximity</c> and fills <c>Status</c> from the packet's
    /// <c>pkStatus</c>, so asking <c>IsInProximity</c> looks like the clean answer -- but on
    /// this driver bit zero is not set on ordinary hover packets, so the guard rejected
    /// <b>every</b> airborne reading and a whole recording came back with no approach and no
    /// departure at all. The capability is declared; the bit does not mean what the property
    /// assumes. Filed against WinPenKit rather than worked around there.
    /// </para>
    /// <para>
    /// So all three of the artefact's marks are required together: no lean, no azimuth, and a
    /// position identical to the reading before it. A real pen would have to be exactly
    /// vertical, exactly north and exactly still to be dropped by this, and it would be one
    /// hover reading. That is the limit, and it is worth stating rather than implying the test
    /// is exact.
    /// </para>
    /// </remarks>
    private bool Airborne(Reading reading) =>
        reading.Lean != 0
        || reading.Azimuth != 0
        || _hover.Count == 0
        || _hover[^1].X != reading.X
        || _hover[^1].Y != reading.Y;

    /// <summary>
    /// Keeps one airborne reading, and forgets any that are now too old to matter.
    /// </summary>
    /// <remarks>
    /// Also the other half of the departure: while the state is Between or Taken there is a
    /// stroke that has just ended, and a reading arriving within the window belongs to it.
    /// </remarks>
    private void Hovering(Reading reading)
    {
        _hover.Add(reading);

        // Trimmed against the newest reading rather than a wall clock, so this agrees with
        // the timestamps the file will carry even if the polling falls behind.
        while (_hover.Count > 0 && reading.At - _hover[0].At > HoverKept) _hover.RemoveAt(0);

        if (_capture is not (Capture.Between or Capture.Taken)) return;

        if (_take?.Current is not { Count: > 0 } just) return;

        if (reading.At - just.Readings[^1].At <= HoverKept) just.Departing(reading);
    }

    /// <summary>
    /// The airborne readings within the window before a landing, oldest first.
    /// </summary>
    /// <remarks>
    /// Measured back from the landing itself rather than from the newest reading held, which
    /// are not the same instant: the pen can hover a while and then be reported in contact
    /// several milliseconds later, and the window a reader cares about ends where the stroke
    /// begins.
    /// </remarks>
    private (IReadOnlyList<Reading> Readings, long? SinceLastSeen, Reading? Last) Approaching(Reading landing)
    {
        var approach = _hover.Where(seen => landing.At - seen.At <= HoverKept).ToList();

        // How long ago the pen was last reported in the air, whether or not any of it was
        // inside the window. This is what says why an approach is empty, and an empty
        // approach with no explanation is what three takes in a row produced.
        var since = _hover.Count > 0 ? landing.At - _hover[^1].At : (long?)null;

        // Strictly the window, and nothing older. An earlier version kept the most recent
        // airborne reading however old it was, on the reasoning that a device reporting on
        // change says nothing precisely when nothing has changed, so the last reading was
        // still current. That reasoning was wrong: measured, the pen is moving at 134 to 255
        // px/s at the moment reporting stops, so a reading from 383 ms earlier describes a
        // pen that has since travelled an unknown distance.
        //
        // Why reporting stops is not yet known. Until it is, the honest thing is an empty
        // approach and an accurate age beside it, rather than a stale reading in an array
        // whose name says it describes the arrival.

        // Cleared, so the next stroke in the take cannot be handed this one's approach. A
        // stroke that lands with nothing in front of it should say so.
        var last = _hover.Count > 0 ? _hover[^1] : (Reading?)null;

        _hover.Clear();

        return (approach, since, last);
    }

    /// <summary>Begins a take where one has just finished, on the same gesture.</summary>
    private void Restart(IPenSession session, Reading landing)
    {
        foreach (var readout in Taken) readout.Forget();

        _pad.Clear();
        DrawGuide();

        _take = new Take(_gesture!, session.Api, session.MaxPressure, _pad.ForPen())
        {
            Conventions = session.Conventions.ToString() ?? "",
        };
        _take.KeepAll(_aloft);
        _aloft.Clear();
        var arriving = Approaching(landing);
        _take.Begin().Approaching(arriving.Readings, arriving.SinceLastSeen, arriving.Last);

        _capture = Capture.Drawing;
        _haveLast = false;
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

    private void StopTake()
    {
        if (_take is null || _capture is not (Capture.Armed or Capture.Drawing or Capture.Between)) return;

        // Only when the same session answered both times, and only when every counter has
        // gone forwards. A count that went backwards is a session that restarted underneath
        // the take, and the difference across that is not a smaller number -- it is not a
        // number at all. Reported as absent, which is honest, rather than as an impossibility
        // somebody has to notice for themselves.
        _take.Counted =
            ReferenceEquals(_session, _countedFrom)
            && Counts(_session) is { } now
            && _armedAt is { } then
            && now.Item1 >= then.Item1 && now.Item2 >= then.Item2 && now.Item3 >= then.Item3
                ? (now.Item1 - then.Item1, now.Item2 - then.Item2, now.Item3 - then.Item3)
                : null;

        if (_capture == Capture.Drawing && _take.Current is { } drawing)
        {
            drawing.EndedBy = "the recording was stopped mid-stroke";
        }

        _take.StoppedAt = DateTimeOffset.Now;

        _take.EndedBy = _take.Strokes == 0
            ? "the recording was stopped before anything was drawn"
            : "the recording was stopped";

        _capture = Capture.Taken;
        _haveLast = false;

        Stage();
    }

    /// <summary>
    /// Arms the recorder, and on a many-stroke gesture starts the take then and there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For the six single-stroke gestures the take still begins at the first contact, which
    /// is what they mean: one attempt at one stroke, and nothing before it is part of it.
    /// </para>
    /// <para>
    /// A many-stroke take is different and was wrong. The recording is a <b>period</b>, not a
    /// stroke, and a period somebody starts by pressing a button. Beginning at the first
    /// contact meant the clock could not run until the pen touched down, there was nothing to
    /// count readings against before then, and the pen in the air on the way to the first
    /// stroke belonged to no take. Asked for from the pad: press Arm, and the recording is
    /// running from that moment whether or not anything is being drawn.
    /// </para>
    /// </remarks>
    private void ArmTake()
    {
        _take = null;
        _capture = Capture.Armed;
        _haveLast = false;

        if (_gesture is { ManyStrokes: true } && _session is { IsRunning: true } armed)
        {
            // The transform is frozen here rather than at the first contact. Take says why it
            // cannot be taken at save time; arming happens on this step with the pad on screen
            // and the window settled, which satisfies the same requirement and gives the whole
            // series one transform instead of the first stroke's.
            _take = new Take(_gesture, armed.Api, armed.MaxPressure, _pad.ForPen())
            {
                Conventions = armed.Conventions.ToString() ?? "",
            };

            _take.KeepAll(_aloft);

            _armedAt = Counts(armed);
            _countedFrom = armed;
        }

        // Whatever the pen did before somebody pressed Arm is not the approach to the stroke
        // they are about to draw.
        _hover.Clear();
        _aloft.Clear();

        foreach (var readout in Taken) readout.Forget();

        _takeGauges.Forget();

        _pad.Clear();
        DrawGuide();

        Stage();
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

    private void Discard()
    {
        _take = null;
        _capture = Capture.Idle;

        _hover.Clear();
        _aloft.Clear();

        foreach (var readout in Taken) readout.Forget();

        _takeGauges.Forget();

        _pad.Clear();
        DrawGuide();

        Stage();
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
        // the timer is what somebody reads to know which. Every other gesture shows the pen's
        // own clock, because there the number is the length of one stroke and the page asks
        // for it in seconds.
        var seconds = _capture is Capture.Armed or Capture.Drawing or Capture.Between or Capture.Taken
            && _take is not null
            ? (_take.Gesture.ManyStrokes ? _take.Recording : _take.Running) / 1000
            : 0;

        this.FindControl<TextBlock>("Clock")!.Text = $"{seconds:F2} s";

        this.FindControl<TextBlock>("Wanted")!.Text = _gesture is null
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
        this.FindControl<TextBlock>("TakeState")!.Text = state;

        this.FindControl<TextBlock>("TakeBrief")!.Text = _gesture?.Detail ?? "";

        // How a take ends is not the same sentence for every gesture, and it used to be
        // written into the XAML as though it were. A gesture that keeps recording across
        // lifts makes the old wording exactly wrong: it says there is nothing to press, on
        // the one screen where something has to be.
        this.FindControl<TextBlock>("HowItEnds")!.Text = _gesture?.ManyStrokes == true
            ? "Press Arm to start. From that moment the recording is running -- the clock, the "
              + "pen in the air, and every stroke you draw -- and it keeps going across pen "
              + "lifts, so the whole series lands in one file with the gaps intact. Press "
              + "Stop, Escape or the space bar when the series is finished. None of those can "
              + "start one, so it is the only key you need; Escape only ever stops."
            : "The recording starts when the tip touches down and ends when it lifts, so "
              + "there is nothing to press at either end. The guide on the pad is there to "
              + "follow and is not part of what is recorded.";

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

        _replay.Redraw();
    }

    private void Review()
    {
        Replayed();

        var list = this.FindControl<StackPanel>("FindingList")!;
        list.Children.Clear();

        if (_take is { Strokes: 0, Aloft.Count: > 0 } aloft)
        {
            list.Children.Add(Said(new Finding(Tone.Good,
                $"{aloft.Aloft.Count} readings of the pen in the air, and no strokes",
                $"Over {aloft.Running:F0} ms. A recording of the hovering pen, which is a "
                + "thing worth having on purpose: it says what the tablet reports when "
                + "nothing is being drawn.")));

            list.Children.Add(Said(new Finding(Tone.Plain,
                $"The pen reported {1000 * aloft.Aloft.Count / Math.Max(1, aloft.Running):F0} "
                + "readings a second while hovering",
                "Against the rate in contact, which the device conventions page puts at 240 "
                + "on this tablet. The same rate means hovering is reported like drawing.")));

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

        this.FindControl<TextBox>("FileName")!.Text = "";
        this.FindControl<TextBox>("Tablet")!.Text = _take.Tablet.Length > 0
            ? _take.Tablet : _remembered.Tablet;

        this.FindControl<TextBox>("Driver")!.Text = _take.Driver.Length > 0
            ? _take.Driver : _remembered.Driver;

        this.FindControl<TextBox>("Intent")!.Text = _take.Intent;
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

        _take.Tablet = this.FindControl<TextBox>("Tablet")!.Text?.Trim() ?? "";
        _take.Driver = this.FindControl<TextBox>("Driver")!.Text?.Trim() ?? "";
        _take.Intent = this.FindControl<TextBox>("Intent")!.Text?.Trim() ?? "";

        var box = this.FindControl<TextBox>("FileName")!;

        // Only while the box still holds what was last suggested. The moment it holds
        // anything else it is the reader's, and a name that rewrites itself under the cursor
        // is worse than no suggestion at all.
        if ((box.Text ?? "") == _suggested)
        {
            _suggested = Trace.Suggest(_take.Gesture, _take.Tablet, _take.At);
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
         intent             {(take.Intent.Length == 0 ? "(none)" : take.Intent)}
         recordedAt         {take.At:O}
         endedBy            {take.EndedBy}

         device
           tablet           {(take.Tablet.Length == 0 ? "(not named)" : take.Tablet)}
           driver           {(take.Driver.Length == 0 ? "(not named)" : take.Driver)}
           api              {take.Api}
           fullScalePressure{take.FullScalePressure,6}
           conventions      {take.Conventions}

         placement          desktop physical pixels
           scale            {take.Placed.ScaleX:F6}, {take.Placed.ScaleY:F6}
           origin           {take.Placed.OriginX:F3}, {take.Placed.OriginY:F3}

         columns            {string.Join(", ", Trace.Columns)}
         strokes            {take.Strokes}{Spread(take)}
         readings           {take.Count} over {take.Milliseconds:F0} ms, {take.Polls} polls
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

        if (name.Length == 0) name = Trace.Suggest(_take.Gesture, _take.Tablet, _take.At);

        try
        {
            _saved = Trace.Write(_take, TakesFolder, name);

            // Kept only once a take has been written with them, so a half-typed name in an
            // abandoned session is not what the next launch offers.
            _remembered = _remembered with { Tablet = _take.Tablet, Driver = _take.Driver };
            _remembered.Write();

            this.FindControl<TextBlock>("SaveState")!.Text = _take.Named
                ? $"Saved to {_saved}"
                : $"Saved to {_saved}, with the tablet and the driver unnamed.";
        }
        catch (Exception bad)
        {
            // Said rather than swallowed. A recording that failed to write and looked as
            // though it had is the worst outcome this screen has.
            _saved = null;

            this.FindControl<TextBlock>("SaveState")!.Text =
                $"Could not save: {bad.Message}";
        }

        Foot();
    }

    private void OpenFolder()
    {
        Directory.CreateDirectory(TakesFolder);

        using var opening = new System.Diagnostics.Process();

        opening.StartInfo.FileName = TakesFolder;
        opening.StartInfo.UseShellExecute = true;
        opening.Start();
    }

    private Readout[] All =>
        [_api, _range, _pressure, _tiltX, _tiltY, _azimuth, _altitude, _twist, _rate, _batch];

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
        if (e.Key == Key.Escape && _step == 3
            && _capture is Capture.Armed or Capture.Drawing or Capture.Between
            && _take?.Gesture.ManyStrokes == true)
        {
            StopTake();

            e.Handled = true;

            return;
        }

        if (e.Key == Key.Space && _step == 3)
        {
            // No debounce where the key can only stop. Stopping a stopped take does nothing,
            // so a repeat is harmless -- and a time guard could swallow a real stop that
            // happened to follow a previous press closely, which is worse than the thing it
            // was guarding against. It remains below, on the branch that can still arm.

            // On a many-stroke gesture the space bar can only ever stop. It cannot arm, and
            // it cannot restart what it just stopped.
            //
            // Debouncing the auto-repeat was not enough, and the reason is that a toggle is
            // the wrong thing here however carefully it is guarded: any second press starts a
            // recording, and somebody who is not certain the first one registered will press
            // again. Reported twice from the pad, the second time on a build that had already
            // been "fixed".
            //
            // Nothing is lost by refusing. A many-stroke take is armed deliberately, before
            // the pen is in position, and the button is the right place for that.
            // Space is the toggle it always was: it starts a recording when none is running
            // and stops the one that is.
            //
            // It was made stop-only for a while, to keep it from restarting a take it had just
            // stopped. That removed the only way to start a recording without reaching for the
            // screen, which is the thing this key exists to avoid -- and the restart was never
            // its fault. Two other mechanisms were doing it: the key-up activating whichever
            // button held focus, and the pen, still in contact, reaching the branch that starts
            // a new take when somebody draws after finishing one. Both are fixed, so the toggle
            // is safe again.
            //
            // Escape stays stop-only, for when stopping is the only thing wanted.
            if (_gesture is { ManyStrokes: true })
            {
                if (_capture is Capture.Armed or Capture.Drawing or Capture.Between) StopTake();
                else ArmTake();
            }
            else
            {
                // The toggle, for the six single-stroke gestures. Here a repeat can still
                // start something, so the time guard earns its place.
                var now = Environment.TickCount64;

                if (now - _lastSpace < 400)
                {
                    e.Handled = true;

                    return;
                }

                _lastSpace = now;

                if (_capture is Capture.Armed) Discard();
                else ArmTake();
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

        this.FindControl<Button>("Start")!.IsEnabled = chosen is { Available: true };

        // Switching backend closes what was open and opens the new one. Leaving the reader
        // to press a button in between makes the commonest use of this window -- comparing
        // what two backends say about the same pen -- three clicks instead of one, and
        // leaves a picked backend sitting there reporting nothing, which reads as the
        // backend being dead rather than unopened.
        Shut();

        if (_shown) Open();
    }

    private void StartOrStop()
    {
        if (_session is not null) Shut();
        else Open();
    }

    /// <summary>Closes the session, if there is one. Safe to call when there is not.</summary>
    private void Shut()
    {
        if (_session is null) return;

        Close(_session);
        _session = null;

        _poll.Stop();

        this.FindControl<Button>("Start")!.Content = "Start";

        Say("Session closed.", "");

        Refresh();
    }

    private void Open()
    {
        var button = this.FindControl<Button>("Start")!;

        if (Chosen is not { Available: true } chosen) return;

        // The window, not the pad. A framework session listens on the control it is given,
        // and a pad is only on screen for its own step -- so a session bound to step one's
        // strip hears nothing while the reader is recording on step three's pad, and records
        // a take of no readings without anything going wrong. The Wintab and WM_POINTER
        // backends do not care, which is what makes this the sort of fault that ships: it is
        // invisible on the backend most likely to be used and total on the other.
        var session = Pen.Open(chosen.Backend.Api, this);

        // The window handle is what a WM_POINTER session subclasses; a Wintab one makes its
        // own pump window and ignores it, and the Avalonia one is already attached to a
        // control. Passing it in every case is simpler than deciding here which cares.
        var failure = session.Start(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);

        if (failure is not null)
        {
            session.Dispose();

            Say($"Could not start: {failure}", "");

            return;
        }

        _session = session;
        _haveLast = false;
        _seen = 0;
        _arrivals.Clear();

        button.Content = "Stop";

        _api.Set(session.Api.ToString());
        _range.Set(session.MaxPressure.ToString());

        Say("Waiting for the pen.",
            $"{session.Conventions}");

        _poll.Start();

        Refresh();
    }

    private void Drain()
    {
        if (_session is not { IsRunning: true } session) return;

        var points = session.DrainPoints();

        _batch.Saw(points.Length);

        if (points.Length == 0) return;

        _seen += points.Length;

        var now = Environment.TickCount64;
        for (var each = 0; each < points.Length; each++) _arrivals.Enqueue(now);
        while (_arrivals.Count > 0 && now - _arrivals.Peek() > 1000) _arrivals.Dequeue();

        _rate.Saw(_arrivals.Count);

        var before = _take?.Count ?? 0;

        foreach (var point in points) Route(point, session);

        // One poll, however many readings it brought. Counted here rather than inside the
        // per-reading path, which would count readings twice under another name.
        if (_take is not null && _take.Count > before) _take.Polled();

        var last = points[^1];

        // Shown from the last point of the batch, and shown whether or not the tip is down:
        // lean and twist are reported while hovering, so the pen can be turned and watched
        // without laying any ink.
        var shown = Reported(last);

        _gauges.Show(shown);
        _takeGauges.Show(shown);

        _trace.Show(last.Pressure, session.MaxPressure);
        _takeTrace.Show(last.Pressure, session.MaxPressure);

        Aim(shown, session.MaxPressure);

        _pressure.Saw(last.Pressure);
        _tiltX.Saw(last.TiltX);
        _tiltY.Saw(last.TiltY);
        _azimuth.Saw(last.Azimuth);
        _altitude.Saw(last.Altitude);
        _twist.Saw(last.Twist);

        // The angles have their own boxes now, with the ranges that make them worth reading.
        // The first point is what makes step one answerable, so the foot has to hear about
        // it. Cheap enough to do on every drain rather than only on the first.
        Refresh();

        Say($"Reporting. {_seen} points so far.",
            $"reported by {last.Source}, buttons {last.Buttons}");

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
    private static Reading Reported(PenPoint point) =>
        new(point.DesktopX, point.DesktopY, point.Pressure, point.TimestampMicroseconds,
            // Altitude counts up from the tablet and a lean counts away from vertical, so
            // one is the other subtracted from a right angle. Azimuth and twist come across
            // untouched: both are already the angle the guide wants.
            // Wintab's pkZ. Kept because of what it explains rather than what it draws: the
            // airborne record stops for seconds at a time while the pen sits perfectly still
            // in every column recorded here, and resumes 4 ms after it moves. The driver is
            // asked for a packet on any change (lcMoveMask is PK.ALL), so something it can
            // see must be changing during the hover that is invisible in x, y, lean, azimuth
            // and twist. Height is the obvious candidate and was not being kept.
            Height: point.Z,

            // Raw. Two open questions live in these bits: whether a Wintab queue overflow is
            // what makes the airborne record stop before a stroke, which bit 1 would say and
            // which nothing has ever read; and what bit 0 actually means, since asking
            // IsInProximity for it rejected every hovering reading on this driver.
            Status: point.Status,
            Lean: 90 - point.Altitude, Azimuth: point.Azimuth, Twist: point.Twist);

    private void Wipe()
    {
        _strip.Clear();
        _haveLast = false;

        // The extremes belong to the marks on the strip. Wiping one and keeping the other
        // leaves a range on screen that nothing visible accounts for.
        foreach (var readout in All) readout.Forget();

        _gauges.Forget();
    }

    private void Say(string verdict, string conventions)
    {
        this.FindControl<TextBlock>("Verdict")!.Text = verdict;
        this.FindControl<TextBlock>("Conventions")!.Text = conventions;
        // While nothing has arrived, not while nothing is open. The session now opens by
        // itself, so tying the hint to that would take it away before it had been read.
        this.FindControl<TextBlock>("StripHint")!.IsVisible = _seen == 0;
    }

    private static void Close(IPenSession? session)
    {
        if (session is null) return;

        session.Stop();
        session.Dispose();
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
            FontSize = 16,
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

        private readonly Border _visual = new()
        {
            Classes = { "readout" },
            Margin = new Thickness(0, 0, 8, 8),
        };

        public void Set(string value) => _value.Text = value;

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
