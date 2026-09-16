using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
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

        // Step three's own pad. Two of them rather than one moved between panels, because
        // they hold different things for different lengths of time: the strip is scribbled
        // on and wiped, and the pad carries one take and the guide it was drawn against.
        _replay = new PenPad(1200, 700);
        this.FindControl<Panel>("ReviewHost")!.Children.Add(_replay);

        _pad = new PenPad(1200, 700);
        _pad.Grew += (_, _) => DrawGuide();
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

        this.FindControl<Button>("Arm")!.Click += (_, _) => ArmTake();
        this.FindControl<Button>("Again")!.Click += (_, _) => Discard();

        this.FindControl<Button>("AgainSame")!.Click += (_, _) => RecordAnother(3);
        this.FindControl<Button>("AgainOther")!.Click += (_, _) => RecordAnother(2);

        this.FindControl<Button>("Back")!.Click += (_, _) => GoTo(_step - 1);
        this.FindControl<Button>("Next")!.Click += (_, _) => GoTo(_step + 1);

        GoTo(1);

        _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PollMilliseconds) };
        _poll.Tick += (_, _) => Drain();

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
        if (step == 3 && _take is null) ArmTake();
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
        // A session that is open and has reported at least one point. Open is not enough:
        // the whole of step one is the difference between a backend that answers and one
        // that merely opened, and walking on at "open" throws that away.
        1 => _session is not null && _seen > 0,
        2 => _gesture is not null,

        // A take with something in it. One reading counts, because a tap is one reading and
        // is a real recording -- refusing it would refuse the gesture that exists to show
        // what a press and a release report when nothing moves.
        3 => _take is { Count: > 0 } && _capture == Capture.Taken,

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

            3 when _capture == Capture.Armed => "Armed. Put the tip down and the take starts.",
            3 when _take is null => $"{_gesture?.Label}. Arm, then draw.",
            3 when _capture == Capture.Drawing => "Recording. Lift the pen to finish.",
            3 when _capture == Capture.Taken =>
                $"{_take!.Describe()}. Review is the next step, and is not built yet.",
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

        this.FindControl<TextBlock>("Wants")!.Text = gesture.Wants;

        Refresh();
    }

    private Readout[] Taken => [_took, _lasted, _takePressure];

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

        // Written as ifs rather than a switch on purpose. The first version used `goto case
        // Capture.Drawing` to fall from the first contact into the collecting branch, and C#
        // sent it to the *unguarded* Drawing label -- the one that handles the pen lifting.
        // So every take ended on the reading that started it: nought readings, state Taken,
        // and a window that looked like it had recorded something.
        if (_capture == Capture.Armed && reading.InContact)
        {
            // The transform is taken here, once, and every reading in this take is placed
            // through it. See Take for why it cannot be taken at save time.
            _take = new Take(_gesture!, session.Api, session.MaxPressure, _pad.ForPen())
            {
                Conventions = session.Conventions.ToString() ?? "",
            };
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
            }
            else
            {
                // The tip lifted, which is the end of the take and needs no button.
                _capture = Capture.Taken;
                _haveLast = false;
            }
        }

        Stage();
    }

    private void ArmTake()
    {
        _take = null;
        _capture = Capture.Armed;
        _haveLast = false;

        foreach (var readout in Taken) readout.Forget();

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

        foreach (var readout in Taken) readout.Forget();

        _pad.Clear();
        DrawGuide();

        Stage();
    }

    /// <summary>Puts the words and the buttons where the take has got to.</summary>
    private void Stage()
    {
        var (title, state) = _capture switch
        {
            Capture.Idle => ("Draw when you are ready",
                "Not armed. Press Arm, and the recording starts the moment the tip touches down."),
            Capture.Armed => ("Draw when you are ready",
                "Armed. Waiting for the tip."),
            Capture.Drawing => ("Drawing",
                "Recording. Lift the pen when the stroke is finished."),
            _ => (_take is null or { Count: 0 } ? "Nothing taken" : "Taken",
                  _take is null ? "" : _take.Describe()),
        };

        this.FindControl<TextBlock>("TakeTitle")!.Text = title;
        this.FindControl<TextBlock>("TakeState")!.Text = state;

        this.FindControl<TextBlock>("TakeBrief")!.Text = _gesture?.Detail ?? "";

        this.FindControl<TextBlock>("TakeDetail")!.Text = _capture switch
        {
            Capture.Taken when _take is null or { Count: 0 } =>
                "Nothing was captured. Did the pen reach the pad?",
            Capture.Taken when _take is { Count: 1 } =>
                "One reading only. That is a tap, and is a finished take if a tap is what "
                + "was wanted.",
            Capture.Taken => $"Ended because {_take!.EndedBy}.",
            _ => "",
        };

        this.FindControl<Button>("Arm")!.IsEnabled = _capture is Capture.Idle or Capture.Taken;
        this.FindControl<Button>("Again")!.IsEnabled = _capture is Capture.Taken;

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
    private void Review()
    {
        _replay.Clear();

        if (_take?.Stroke is { } stroke)
        {
            var brush = new Nib(
                26, SKColors.Black.WithAlpha(0xD0), 0.25, Buildup.PerStamp,
                new Width(0.5, 26, (uint)Math.Max(1, _take.FullScalePressure)),
                SpacedBy.Diameters);

            // Through the transform the take was placed with, not through this pad's own.
            // The take is a finished thing and is replayed as it was drawn, whatever has
            // happened to the window since.
            brush.Draw(_replay.Surface, _take.Placed, stroke);

            _replay.Redraw();
        }

        var list = this.FindControl<StackPanel>("FindingList")!;
        list.Children.Clear();

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
        this.FindControl<TextBox>("Intent")!.Text = _take.Intent;
        this.FindControl<TextBlock>("Folder")!.Text = TakesFolder;

        Named();
        Refresh();
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

        this.FindControl<Button>("Save")!.IsEnabled = _take is { Count: > 0 };

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
         readings           {take.Count} over {take.Milliseconds:F0} ms, {take.Polls} polls
         """;

    private void Keep()
    {
        if (_take is null or { Count: 0 }) return;

        Named();

        var name = this.FindControl<TextBox>("FileName")!.Text?.Trim() ?? "";

        if (name.Length == 0) name = Trace.Suggest(_take.Gesture, _take.Tablet, _take.At);

        try
        {
            _saved = Trace.Write(_take, TakesFolder, name);

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

    private BackendChoice? Chosen =>
        this.FindControl<ComboBox>("Backends")!.SelectedItem as BackendChoice;

    private void Chose()
    {
        var chosen = Chosen;

        this.FindControl<TextBlock>("BackendWorth")!.Text = chosen is null
            ? ""
            : chosen.Available
                ? $"What it is worth: {chosen.Backend.Worth}."
                : "Not available on this machine. Its driver is not installed, or its service "
                    + "is not running.";

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

        var brush = new Nib(
            26, SKColors.Black.WithAlpha(0xD0), 0.25, Buildup.PerStamp,
            new Width(0.5, 26, (uint)Math.Max(1, range)), SpacedBy.Diameters);

        var from = _haveLast ? _last : point;

        brush.Draw(pad.Surface, through ?? pad.ForPen(),
            new Stroke([Reported(from), Reported(point)]));

        _last = point;
        _haveLast = true;

        pad.Redraw();
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
            Lean: 90 - point.Altitude, Azimuth: point.Azimuth, Twist: point.Twist);

    private void Wipe()
    {
        _strip.Clear();
        _haveLast = false;

        // The extremes belong to the marks on the strip. Wiping one and keeping the other
        // leaves a range on screen that nothing visible accounts for.
        foreach (var readout in All) readout.Forget();
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
