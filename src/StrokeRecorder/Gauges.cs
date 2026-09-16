using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using StrokeFieldGuide.Strokes;

// This project has a Pen of its own -- the backends -- and it is not this one.
using Stroke = Avalonia.Media.Pen;

namespace StrokeFieldGuide.Recorder;

/// <summary>
/// What the pen is doing, drawn rather than numbered.
/// </summary>
/// <remarks>
/// <para>
/// Three dials, because the numbers beside them cannot be turned into a movement of the hand.
/// A reader asked to "roll the barrel about thirty degrees" has to convert a figure into a
/// wrist, check the figure, and convert back; a reader watching a nib turn just turns it until
/// it looks right.
/// </para>
/// <para>
/// They are separate dials rather than one, because the three quantities are independent and
/// a reader needs to know which of them moved. A single picture that changed when any of them
/// did would be prettier and would answer nothing.
/// </para>
/// <para>
/// All three update while the pen is <b>hovering</b>, which is deliberate: lean and twist are
/// reported out of contact, so the pen can be turned and watched without laying any ink.
/// </para>
/// </remarks>
public sealed class Gauges : Control
{
    private const double Radius = 62;
    private const double Gap = 26;

    /// <summary>The widest lean an EMR pen senses, and so the edge of the tilt dial.</summary>
    /// <remarks>
    /// Stated by the owner of the tablet and consistent with what was measured: plus or minus
    /// 60 is the sensor's limit, manufacturers specify it, and a Cintiq 24 reached 64. The
    /// dial is drawn to that so a reader can see how much of the envelope they are using.
    /// </remarks>
    private const double WidestLean = 60;

    private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#2B2B28"));
    private static readonly IBrush Faint = new SolidColorBrush(Color.Parse("#C9C9C2"));
    private static readonly IBrush Label = new SolidColorBrush(Color.Parse("#7A7A74"));
    private static readonly IBrush Live = new SolidColorBrush(Color.Parse("#B0453A"));
    private static readonly IBrush Paper = new SolidColorBrush(Color.Parse("#FFFFFF"));

    private static readonly Stroke Hairline = new(Faint, 1);
    private static readonly Stroke Edge = new(new SolidColorBrush(Color.Parse("#D8D8D2")), 1);
    private static readonly Stroke Nibline = new(Ink, 2);
    private static readonly Stroke Needle = new(Live, 2.5);

    private double _lean;
    private Turn _azimuth;
    private Turn _twist;
    private Turn _roll;
    private bool _reporting;

    /// <summary>
    /// Every roll seen since the session opened, for the total turned.
    /// </summary>
    /// <remarks>
    /// The drawing sense, not the device's, so the total agrees in sign with the needle above
    /// it. A reader who rolls the pen one way and watches a total go down would not trust
    /// either number again.
    /// </remarks>
    private readonly List<Turn> _rolled = [];

    public Gauges() => Height = 2 * Radius + 44;

    /// <summary>How far the pen has rolled in total, which may be more than a circle.</summary>
    public double TotalRoll => _rolled.Count < 2 ? 0 : Turn.Total(_rolled);

    public void Show(Reading reading)
    {
        _lean = reading.Lean;
        _azimuth = Turn.At(reading.Azimuth);
        _twist = Turn.At(reading.Twist);

        // The drawing sense, which is the device's negated. Without it the needle went the
        // opposite way to the barrel under the hand, which is worse than no needle: a reader
        // trying to roll to a stated angle would turn the pen the wrong way and see the
        // number get further off.
        _roll = reading.Roll;
        _reporting = true;

        // Kept so the roll can be totalled the only way that survives a circle: as shortest
        // steps summed. Thinned, because a reading arrives every four milliseconds and the
        // total does not need every one of them to be right.
        if (_rolled.Count == 0 || Math.Abs(_rolled[^1].To(_roll)) > 0.1) _rolled.Add(_roll);

        InvalidateVisual();
    }

    public void Forget()
    {
        _reporting = false;
        _rolled.Clear();

        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var y = Radius + 6;
        var x = Radius + 6;

        Tilt(context, new Point(x, y));
        Nib(context, new Point(x + 2 * Radius + Gap, y));
        Barrel(context, new Point(x + 4 * Radius + 2 * Gap, y));
    }

    /// <summary>
    /// Which way the pen leans and how far, as a point on a dial.
    /// </summary>
    /// <remarks>
    /// Plotted from the lean vector rather than from the two numbers, for the reason the
    /// wrapping-angles page gives: near upright the azimuth is not a measurement, and a dot
    /// that flies around the rim while the pen is standing still would be reporting noise as
    /// though it were a hand.
    /// </remarks>
    private void Tilt(DrawingContext context, Point centre)
    {
        context.DrawEllipse(Paper, Edge, centre, Radius, Radius);

        foreach (var ring in new[] { 20.0, 40.0 })
        {
            var at = Radius * ring / WidestLean;

            context.DrawEllipse(null, Hairline, centre, at, at);
        }

        context.DrawLine(Hairline, new Point(centre.X - Radius, centre.Y), new Point(centre.X + Radius, centre.Y));
        context.DrawLine(Hairline, new Point(centre.X, centre.Y - Radius), new Point(centre.X, centre.Y + Radius));

        // Labelled, because the whole of this correction was about which way the dial faces.
        Write(context, "N", centre.X, centre.Y - Radius - 1, 10, Label);
        Write(context, "E", centre.X + Radius + 7, centre.Y - 7, 10, Label);

        if (_reporting)
        {
            // Across is east and Down is south, so this is already the screen's frame: north
            // up, east right. It was not, and the dial said so -- tipping the far end of the
            // pen east put the dot due south, a quarter turn out, because an azimuth was
            // being read as an angle from the x axis rather than as the bearing it is.
            var leaning = Leaning.From(_lean, _azimuth);
            var scale = Radius / WidestLean;
            var at = new Point(centre.X + leaning.Across * scale, centre.Y + leaning.Down * scale);

            context.DrawLine(Needle, centre, at);
            context.DrawEllipse(Live, null, at, 5, 5);
        }

        Caption(context, centre, "lean", _reporting ? $"{_lean:F0}° of {WidestLean:F0}" : "—");
    }

    /// <summary>
    /// The nib a brush would stamp, turned the way the pen is leaning.
    /// </summary>
    /// <remarks>
    /// This is <c>Held.ToTheLean</c> drawn: the shape the guide would lay if the azimuth were
    /// driving it. It is here so that "the nib follows the lean" can be watched rather than
    /// read, and so that a nib flipping a half-turn -- which is what the wrapping-angles page
    /// is about -- would be seen the moment it happened.
    /// </remarks>
    private void Nib(DrawingContext context, Point centre)
    {
        context.DrawEllipse(Paper, Edge, centre, Radius, Radius);

        if (_reporting)
        {
            // Turned to the azimuth, and only while there is one. An upright pen leans
            // nowhere, and a nib that kept spinning there would be the defect this shows.
            // The drawing angle, because this is a stamp. The bearing is what the caption
            // says, since that is the number a hand can be talked about in.
            var turned = Leaning.From(_lean, _azimuth).Direction;

            // Nothing at all below a lean the device cannot resolve: the azimuth is a whole
            // degree on this tablet, so at a lean of one the direction is worth little and
            // the nib would jitter round on a hand that was holding still.
            if (_lean < 1) turned = null;

            if (turned is { } along)
            {
                using (context.PushTransform(
                           Matrix.CreateRotation(along.Radians)
                           * Matrix.CreateTranslation(centre.X, centre.Y)))
                {
                    context.DrawEllipse(Ink, Nibline, new Point(0, 0), Radius - 12, (Radius - 12) * 0.26);
                }
            }
            else
            {
                // Outlined rather than filled. A solid disc reads as something being wrong,
                // and nothing is: the pen is upright and a nib driven by the lean has no
                // angle to be at. The empty ring says "no direction" where the blob said
                // "error".
                context.DrawEllipse(null, Hairline, centre, Radius - 12, Radius - 12);
            }

            Caption(context, centre, "nib, turned to the lean",
                turned is null
                    ? "upright: no direction"
                    : $"bearing {Leaning.From(_lean, _azimuth).Azimuth!.Value.Degrees:F0}°");

            return;
        }

        Caption(context, centre, "nib, turned to the lean", "—");
    }

    /// <summary>
    /// Barrel rotation, as a mark that goes round with the pen.
    /// </summary>
    /// <remarks>
    /// The notch is the point. An angle in a readout says 324 and means nothing to a hand; a
    /// notch at four o'clock that moves when the barrel is rolled says which way and how far
    /// without anybody converting anything. The total beneath it is the one number here that
    /// a readout cannot give, because it survives crossing zero and a readout does not.
    /// </remarks>
    private void Barrel(DrawingContext context, Point centre)
    {
        context.DrawEllipse(Paper, Edge, centre, Radius, Radius);

        for (var tick = 0; tick < 12; tick++)
        {
            var at = Turn.At(tick * 30).Radians;
            var inner = tick % 3 == 0 ? Radius - 10 : Radius - 5;

            context.DrawLine(Hairline,
                new Point(centre.X + Math.Cos(at) * inner, centre.Y + Math.Sin(at) * inner),
                new Point(centre.X + Math.Cos(at) * Radius, centre.Y + Math.Sin(at) * Radius));
        }

        if (_reporting)
        {
            var at = _roll.Radians;
            var rim = new Point(centre.X + Math.Cos(at) * (Radius - 6), centre.Y + Math.Sin(at) * (Radius - 6));

            context.DrawLine(Needle, centre, rim);
            context.DrawEllipse(Live, null, rim, 6, 6);

            // The device's own number in the caption, because that is what lands in the file
            // and what a reader will see if they open one. The needle is the hand's.
            Caption(context, centre, "barrel", $"{_twist.Degrees:F0}° reported, turned {TotalRoll:F0}°");

            return;
        }

        Caption(context, centre, "barrel", "—");
    }

    private static void Caption(DrawingContext context, Point centre, string what, string value)
    {
        Write(context, what, centre.X, centre.Y + Radius + 4, 11, Label);
        Write(context, value, centre.X, centre.Y + Radius + 19, 12, Ink);
    }

    private static void Write(DrawingContext context, string text, double x, double y,
                              double size, IBrush brush)
    {
        var laid = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Typeface.Default, size, brush);

        context.DrawText(laid, new Point(x - laid.Width / 2, y));
    }
}
