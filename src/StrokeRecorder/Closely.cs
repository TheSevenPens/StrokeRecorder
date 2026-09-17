using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using StrokeKit.Strokes;

// This project has a Pen of its own — the backends — and it is not this one.
using Stroke = Avalonia.Media.Pen;

namespace StrokeRecorder;

/// <summary>
/// One stroke drawn large, with a mark on the reading being looked at.
/// </summary>
/// <remarks>
/// <para>
/// Not the brush. This draws the <b>readings</b> — a dot per reading and a line between them —
/// because the question it answers is where the samples fell, and a brush is the thing that
/// hides that by filling the gaps between them. A flick's last eight readings carry half its
/// length; the mark laid over them looks continuous and the readings underneath are 44 px
/// apart, which is the whole point and is invisible in ink.
/// </para>
/// <para>
/// The approach is drawn too, hollow, so the pen's arrival is part of the same picture as the
/// stroke. It is the only view in the application where the two are seen together.
/// </para>
/// </remarks>
public sealed class Closely : Control
{
    private IReadOnlyList<Reading> _readings = [];
    private IReadOnlyList<Reading> _approach = [];
    private int _at;

    private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#2B2B28"));
    private static readonly IBrush Faint = new SolidColorBrush(Color.Parse("#B8B8B2"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#B4541E"));

    private static readonly Stroke Thread = new(new SolidColorBrush(Color.Parse("#9AA3B0")), 1);
    private static readonly Stroke Hollow = new(new SolidColorBrush(Color.Parse("#C9CDD4")), 1);

    public void Show(IReadOnlyList<Reading> readings, IReadOnlyList<Reading> approach, int at)
    {
        _readings = readings;
        _approach = approach;
        _at = at;

        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Avalonia.Media.Brushes.White, new Rect(Bounds.Size));

        if (_readings.Count == 0) return;

        // Everything the view holds, so the stroke does not jump when the approach is long.
        var all = _approach.Concat(_readings).ToList();

        var lowX = all.Min(reading => reading.X);
        var highX = all.Max(reading => reading.X);
        var lowY = all.Min(reading => reading.Y);
        var highY = all.Max(reading => reading.Y);

        const double Edge = 26;

        var wide = Math.Max(1, highX - lowX);
        var tall = Math.Max(1, highY - lowY);

        // One scale for both axes. Two would stretch the stroke to fill the box and make every
        // angle in it a lie, on the one screen whose job is to show what the pen did.
        var scale = Math.Min((Bounds.Width - 2 * Edge) / wide, (Bounds.Height - 2 * Edge) / tall);

        Point Where(Reading reading) => new(
            Edge + (reading.X - lowX) * scale + (Bounds.Width - 2 * Edge - wide * scale) / 2,
            Edge + (reading.Y - lowY) * scale + (Bounds.Height - 2 * Edge - tall * scale) / 2);

        for (var each = 1; each < _approach.Count; each++)
        {
            context.DrawLine(Hollow, Where(_approach[each - 1]), Where(_approach[each]));
        }

        foreach (var reading in _approach)
        {
            context.DrawEllipse(null, Hollow, Where(reading), 2.5, 2.5);
        }

        for (var each = 1; each < _readings.Count; each++)
        {
            context.DrawLine(Thread, Where(_readings[each - 1]), Where(_readings[each]));
        }

        // Sized by pressure, so the shape of the pressure is visible as the shape of the dots
        // rather than only on the chart below.
        var hardest = Math.Max(1u, _readings.Max(reading => reading.Pressure));

        for (var each = 0; each < _readings.Count; each++)
        {
            var reading = _readings[each];
            var radius = 1.5 + 3.5 * reading.Pressure / hardest;

            context.DrawEllipse(each == _at ? Accent : Ink, null, Where(reading), radius, radius);
        }

        if (_at < 0 || _at >= _readings.Count) return;

        // A ring round the reading being looked at, outside the dot rather than over it: the
        // dot's size is the pressure and covering it would take away the thing being read.
        context.DrawEllipse(null, new Stroke(Accent, 1.5), Where(_readings[_at]), 9, 9);
    }
}

/// <summary>
/// One channel of a stroke, across the stroke, with the playhead on it.
/// </summary>
/// <remarks>
/// Plotted against the reading number and not against time. The two are the same picture on
/// this device — readings arrive at a fixed rate — and the reading number is the thing the
/// table beside it is indexed by, so a reader moving between them is not converting units.
/// </remarks>
public sealed class Across : Control
{
    private IReadOnlyList<double> _values = [];
    private int _at;
    private string _label = "";

    private static readonly IBrush Paper = new SolidColorBrush(Color.Parse("#FAFAF6"));
    private static readonly IBrush Faint = new SolidColorBrush(Color.Parse("#8A8A82"));
    private static readonly Stroke Line = new(new SolidColorBrush(Color.Parse("#5E7080")), 1.4);
    private static readonly Stroke Head = new(new SolidColorBrush(Color.Parse("#B4541E")), 1.5);
    private static readonly Stroke Rule = new(new SolidColorBrush(Color.Parse("#E2E2DC")), 1);

    public void Show(string label, IReadOnlyList<double> values, int at)
    {
        _label = label;
        _values = values;
        _at = at;

        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Paper, new Rect(Bounds.Size));
        context.DrawLine(Rule, new Point(0, Bounds.Height - 1), new Point(Bounds.Width, Bounds.Height - 1));

        if (_values.Count < 2) return;

        const double Edge = 4;

        var low = _values.Min();
        var high = _values.Max();
        var span = Math.Max(1e-9, high - low);

        double At(int index) => index * (Bounds.Width - 2 * Edge) / (_values.Count - 1) + Edge;
        double Up(double value) => Bounds.Height - Edge - (value - low) / span * (Bounds.Height - 2 * Edge - 12);

        var path = new PathGeometry();

        using (var figure = path.Open())
        {
            figure.BeginFigure(new Point(At(0), Up(_values[0])), false);

            for (var each = 1; each < _values.Count; each++)
            {
                figure.LineTo(new Point(At(each), Up(_values[each])));
            }

            figure.EndFigure(false);
        }

        context.DrawGeometry(null, Line, path);

        if (_at >= 0 && _at < _values.Count)
        {
            context.DrawLine(Head, new Point(At(_at), 0), new Point(At(_at), Bounds.Height));
        }

        var said = new FormattedText(
            _at >= 0 && _at < _values.Count ? $"{_label}  {_values[_at]:0.##}" : _label,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Typeface.Default, 10.5, Faint);

        context.DrawText(said, new Point(Edge, 2));
    }
}
