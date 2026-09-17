using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using StrokeFieldGuide.Strokes;

namespace StrokeFieldGuide.Recorder;

/// <summary>
/// One stroke, taken apart.
/// </summary>
/// <remarks>
/// <para>
/// Four views of the same stroke and <b>one playhead</b> through all of them. Stepping a
/// reading at a time, clicking a row, scrubbing or playing all move the same index, and the
/// table, the drawing and every channel follow it. That is the whole mechanism, and it is what
/// makes "look at what happens as it leaves the surface" a thing to do rather than to describe.
/// </para>
/// <para>
/// It exists because the hard parts of a stroke are a handful of readings wide. A flick's last
/// eight readings carry half its length; the landing crosses from nothing to seventy per cent
/// of full scale inside one pressure sample. Neither is visible in a drawn mark, and neither
/// survives being described in a sentence — which is most of why the notes in this repository
/// took three days and four wrong answers to arrive at what the files had said all along.
/// </para>
/// </remarks>
public partial class Analyser : Window
{
    private IReadOnlyList<Reading> _readings = [];
    private IReadOnlyList<Reading> _approach = [];

    private readonly Closely _closely = new();
    private readonly List<(Across View, string Label, IReadOnlyList<double> Values)> _channels = [];
    private readonly List<Border> _rows = [];

    private DispatcherTimer? _playing;
    private int _at;

    public Analyser() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Opens the window on one stroke of a take.</summary>
    public static Analyser For(Contact contact, int which, int outOf, string take)
    {
        var window = new Analyser();

        window.Lay(contact, which, outOf, take);

        return window;
    }

    private void Lay(Contact contact, int which, int outOf, string take)
    {
        _readings = contact.Readings;
        _approach = contact.Approach;

        Title = $"Stroke {which} — {take}";

        this.FindControl<TextBlock>("Which")!.Text = $"Stroke {which} of {outOf}";

        // Real milliseconds, off the host clock. The pen's own stamp advances a flat 4.166 ms
        // per packet whatever the elapsed time, so a duration taken from it is the reading
        // count in disguise — which is the figure directly beside it.
        var ms = _readings.Count > 1
            ? (_readings[^1].Arrived - _readings[0].Arrived) / 1000.0
            : 0;

        var length = 0.0;

        for (var each = 1; each < _readings.Count; each++)
        {
            length += Math.Sqrt(Math.Pow(_readings[each].X - _readings[each - 1].X, 2)
                              + Math.Pow(_readings[each].Y - _readings[each - 1].Y, 2));
        }

        this.FindControl<TextBlock>("Summary")!.Text =
            $"{_readings.Count} readings · {ms:F0} ms · {length:F0} px · "
            + $"{(_approach.Count > 0 ? $"{_approach.Count} in the approach" : "no approach kept")}";

        this.FindControl<Panel>("StrokeHost")!.Children.Add(_closely);

        Channel("pressure", _readings.Select(reading => (double)reading.Pressure).ToList());
        Channel("speed px/s", Speed());
        Channel("lean", _readings.Select(reading => reading.Lean).ToList());

        Rows();
        Wire();
        Move(0);
    }

    /// <summary>
    /// How fast the pen was travelling at each reading, in pixels a second.
    /// </summary>
    /// <remarks>
    /// Over the step before each reading, on the host clock, and repeated for the first —
    /// which has no step before it and would otherwise open every stroke with a zero that is
    /// not a measurement.
    /// </remarks>
    private IReadOnlyList<double> Speed()
    {
        var speeds = new double[_readings.Count];

        for (var each = 1; each < _readings.Count; each++)
        {
            var seconds = (_readings[each].Arrived - _readings[each - 1].Arrived) / 1e6;

            speeds[each] = seconds <= 0
                ? speeds[each - 1]
                : Math.Sqrt(Math.Pow(_readings[each].X - _readings[each - 1].X, 2)
                          + Math.Pow(_readings[each].Y - _readings[each - 1].Y, 2)) / seconds;
        }

        if (speeds.Length > 1) speeds[0] = speeds[1];

        return speeds;
    }

    /// <summary>
    /// Adds a channel, as the values it takes across the whole stroke.
    /// </summary>
    /// <remarks>
    /// Values and not a function of one reading, because a speed belongs to a <em>pair</em> of
    /// readings and cannot be got from one. Computing the column once at the start also keeps
    /// the playhead cheap: moving it redraws, and does not remeasure the stroke.
    /// </remarks>
    private void Channel(string label, IReadOnlyList<double> values)
    {
        var view = new Across { Height = 62 };

        this.FindControl<StackPanel>("ChannelHost")!.Children.Add(view);

        _channels.Add((view, label, values));
    }

    private static readonly string[] Columns = ["#", "ms", "x", "y", "P", "lean", "az", "h"];

    private void Rows()
    {
        var head = this.FindControl<StackPanel>("ReadingHead")!;

        head.Children.Add(Line(Columns, head: true));

        var rows = this.FindControl<StackPanel>("ReadingRows")!;

        for (var each = 0; each < _readings.Count; each++)
        {
            var reading = _readings[each];
            var ms = (reading.Arrived - _readings[0].Arrived) / 1000.0;

            var row = new Border
            {
                Child = Line([
                    $"{each}",
                    $"{ms:F0}",
                    $"{reading.X:F1}",
                    $"{reading.Y:F1}",
                    $"{reading.Pressure}",
                    $"{reading.Lean:F0}",
                    $"{reading.Azimuth:F0}",
                    $"{reading.Height:F0}",
                ]),
                Padding = new Avalonia.Thickness(3, 1, 3, 1),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };

            var which = each;

            row.PointerPressed += (_, _) => Move(which);

            _rows.Add(row);
            rows.Children.Add(row);
        }
    }

    private static Control Line(IReadOnlyList<string> cells, bool head = false)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("34,44,64,64,58,42,42,*"),
        };

        for (var column = 0; column < cells.Count; column++)
        {
            var cell = new TextBlock
            {
                Text = cells[column],
                FontFamily = head ? FontFamily.Default : new FontFamily("Consolas,Menlo,monospace"),
                FontSize = head ? 10 : 11.5,
                Foreground = new SolidColorBrush(Color.Parse(head ? "#8A8A82" : "#3A3A36")),
                TextAlignment = column == 0 ? TextAlignment.Left : TextAlignment.Right,
                Margin = new Avalonia.Thickness(0, 0, 8, 0),
            };

            Grid.SetColumn(cell, column);
            grid.Children.Add(cell);
        }

        return grid;
    }

    private void Wire()
    {
        this.FindControl<Button>("Shut")!.Click += (_, _) => Close();

        this.FindControl<Button>("ToStart")!.Click += (_, _) => Move(0);
        this.FindControl<Button>("Back")!.Click += (_, _) => Move(_at - 1);
        this.FindControl<Button>("On")!.Click += (_, _) => Move(_at + 1);
        this.FindControl<Button>("ToEnd")!.Click += (_, _) => Move(_readings.Count - 1);

        this.FindControl<Button>("Play")!.Click += (_, _) => PlayOrStop();

        KeyDown += (_, key) =>
        {
            if (key.Key == Avalonia.Input.Key.Left) Move(_at - 1);
            if (key.Key == Avalonia.Input.Key.Right) Move(_at + 1);
            if (key.Key == Avalonia.Input.Key.Space) PlayOrStop();
            if (key.Key == Avalonia.Input.Key.Escape) Close();
        };

        Closed += (_, _) => Stop();
    }

    /// <summary>
    /// Plays the stroke back at a twentieth of the speed it was drawn.
    /// </summary>
    /// <remarks>
    /// Slow on purpose. At the rate it was recorded a flick is over in sixty milliseconds, and
    /// a replay at that speed shows exactly what watching the hand showed, which is nothing.
    /// </remarks>
    private void PlayOrStop()
    {
        if (_playing is not null)
        {
            Stop();

            return;
        }

        if (_at >= _readings.Count - 1) Move(0);

        _playing = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };

        _playing.Tick += (_, _) =>
        {
            if (_at >= _readings.Count - 1)
            {
                Stop();

                return;
            }

            Move(_at + 1);
        };

        _playing.Start();

        this.FindControl<Button>("Play")!.Content = "❚❚ pause";
    }

    private void Stop()
    {
        _playing?.Stop();
        _playing = null;

        this.FindControl<Button>("Play")!.Content = "▶ play";
    }

    /// <summary>Moves the playhead, and everything that follows it.</summary>
    private void Move(int to)
    {
        if (_readings.Count == 0) return;

        _at = Math.Clamp(to, 0, _readings.Count - 1);

        _closely.Show(_readings, _approach, _at);

        foreach (var (view, label, values) in _channels) view.Show(label, values, _at);

        for (var each = 0; each < _rows.Count; each++)
        {
            _rows[each].Background = each == _at
                ? new SolidColorBrush(Color.Parse("#F3E4D8"))
                : Avalonia.Media.Brushes.Transparent;
        }

        if (_at < _rows.Count) _rows[_at].BringIntoView();

        var ms = (_readings[_at].Arrived - _readings[0].Arrived) / 1000.0;

        this.FindControl<TextBlock>("Playhead")!.Text =
            $"reading {_at} of {_readings.Count - 1}  ·  {ms:F0} ms";
    }
}
