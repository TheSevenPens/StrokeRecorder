using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

using Stroke = Avalonia.Media.Pen;

namespace StrokeRecorder;

/// <summary>
/// Pressure over the last second or two, as a line rather than a number.
/// </summary>
/// <remarks>
/// <para>
/// Asked for, and the reason is a property of the hardware rather than of the display: a pen
/// is <b>overreactive at the bottom of its range</b>. Held just hard enough to register, the
/// reported pressure moves about far more than the hand does, and that is the thing pressure
/// curves and smoothing exist to take out.
/// </para>
/// <para>
/// A figure cannot show it. By the time a reader has read one number the next has replaced it,
/// and bouncing between two values looks exactly like a value that is merely changing. A trace
/// shows the shape of the movement, which is what "it bounces" means.
/// </para>
/// <para>
/// The line is drawn from the readings kept here rather than from anything the take holds,
/// because it must run while the pen is hovering and between takes, and a take is neither.
/// </para>
/// </remarks>
public sealed class PressureTrace : Control
{
    /// <summary>About two seconds at the 240 a second this tablet reports.</summary>
    private const int Kept = 480;

    private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#2B2B28"));
    private static readonly IBrush Label = new SolidColorBrush(Color.Parse("#7A7A74"));
    private static readonly IBrush Fill = new SolidColorBrush(Color.Parse("#E3DFD2"));
    private static readonly IBrush Paper = new SolidColorBrush(Color.Parse("#FFFFFF"));

    private static readonly Stroke Edge = new(new SolidColorBrush(Color.Parse("#D8D8D2")), 1);
    private static readonly Stroke Hairline = new(new SolidColorBrush(Color.Parse("#E8E6DE")), 1);
    private static readonly Stroke Line = new(new SolidColorBrush(Color.Parse("#B0453A")), 1.5);

    private readonly Queue<double> _seen = new();

    private int _full = 1;
    private uint _now;

    /// <param name="width">
    /// Nought to fill whatever space there is, which is what the probe step wants: the trace
    /// is the one instrument that is better for being wider, because width is time.
    /// </param>
    public PressureTrace(double width, double height)
    {
        if (width > 0) Width = width;

        Height = height;
        MinWidth = 160;
    }

    public void Show(uint pressure, int fullScale)
    {
        _full = Math.Max(1, fullScale);
        _now = pressure;

        _seen.Enqueue(pressure / (double)_full);

        while (_seen.Count > Kept) _seen.Dequeue();

        InvalidateVisual();
    }

    public void Forget()
    {
        _seen.Clear();
        _now = 0;

        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        // The laid-out width, not the requested one: this control is allowed to fill, and
        // Width is NaN when it does.
        var wide = Bounds.Width;

        if (wide <= 0) return;

        var plot = new Rect(0, 0, wide, Height - 16);

        context.DrawRectangle(Paper, Edge, plot);

        // A tenth of full scale, which is where the bouncing lives: the band a pen is
        // overreactive in is the bottom of its range, and a grid line there says how far up
        // it the trace is sitting.
        for (var part = 1; part < 10; part++)
        {
            var y = plot.Bottom - plot.Height * part / 10.0;

            context.DrawLine(Hairline, new Point(0, y), new Point(wide, y));
        }

        if (_seen.Count > 1)
        {
            var seen = _seen.ToArray();
            var step = wide / Kept;
            var left = wide - seen.Length * step;

            var figure = new PathFigure
            {
                StartPoint = new Point(left, plot.Bottom - plot.Height * seen[0]),
                IsClosed = false,
            };

            for (var each = 1; each < seen.Length; each++)
            {
                figure.Segments!.Add(new LineSegment
                {
                    Point = new Point(left + each * step, plot.Bottom - plot.Height * seen[each]),
                });
            }

            var path = new PathGeometry();
            path.Figures!.Add(figure);

            context.DrawGeometry(null, Line, path);

            // The newest value as a bar down the right, so the current reading is readable
            // without following the line to its end.
            var height = plot.Height * seen[^1];

            context.DrawRectangle(Fill, null,
                new Rect(wide - 6, plot.Bottom - height, 6, height));
        }

        var text = new FormattedText(
            $"pressure  {_now} of {_full}  ({100.0 * _now / _full:F1}%)",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Typeface.Default, 11, Label);

        context.DrawText(text, new Point(0, Height - 14));
    }
}
