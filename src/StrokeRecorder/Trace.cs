using System.Text;
using System.Text.Json;

namespace StrokeFieldGuide.Recorder;

/// <summary>
/// A take written down, in a form somebody who was not there can read.
/// </summary>
/// <remarks>
/// <para>
/// The file has to carry what the readings cannot. A pressure is a raw count and means
/// nothing without the device's full scale; a position is a desktop pixel and means nothing
/// without the transform it was placed through; and a stroke means nothing without what the
/// hand was asked to do. All three go in the header, and a reader with the file needs nothing
/// else.
/// </para>
/// <para>
/// Deliberately not the browser recorder's format. That one records pointer events with
/// coalesced samples inside them and tilt as an x and a y, and neither matches what a
/// WinPenKit session produces or what this guide decided to keep. Writing a file that looked
/// like the other one would be the more convenient lie.
/// </para>
/// </remarks>
public static class Trace
{
    public const string Format = "stroke-field-guide/take";
    public const int Version = 1;

    /// <summary>What goes in, in order, so a reader does not have to guess at the tuples.</summary>
    /// <remarks>
    /// Arrays rather than objects per reading. A stroke is thousands of them and the key names
    /// repeated that many times are most of the file; the column list says what each slot is
    /// once.
    /// </remarks>
    public static readonly string[] Columns =
        ["at", "x", "y", "pressure", "lean", "azimuth", "twist"];

    public static string Write(Take take, string folder, string name)
    {
        Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, name.EndsWith(".json") ? name : name + ".json");

        using var stream = File.Create(path);
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        json.WriteStartObject();

        json.WriteString("format", Format);
        json.WriteNumber("formatVersion", Version);
        json.WriteString("id", Path.GetFileNameWithoutExtension(path));
        json.WriteString("gesture", take.Gesture.Id);
        json.WriteString("intent", take.Intent);
        json.WriteString("recordedAt", take.At.ToString("O"));
        json.WriteString("endedBy", take.EndedBy);

        json.WriteStartObject("device");
        json.WriteString("tablet", take.Tablet);
        json.WriteString("driver", take.Driver);
        json.WriteString("api", take.Api.ToString());

        // The number the readings do not carry and cannot. Without it every pressure below
        // is an integer with no meaning.
        json.WriteNumber("fullScalePressure", take.FullScalePressure);
        json.WriteString("conventions", take.Conventions);
        json.WriteEndObject();

        // What x and y are, which is not a surface pixel. They are desktop pixels as
        // reported, and this is the transform that was frozen when the tip went down.
        json.WriteStartObject("placement");
        json.WriteString("units", "desktop physical pixels, as reported by the session");
        json.WriteString("note",
            "multiply by scale and add origin, per axis, for the surface pixel this was drawn at");
        json.WriteNumber("scaleX", take.Placed.ScaleX);
        json.WriteNumber("scaleY", take.Placed.ScaleY);
        json.WriteNumber("originX", take.Placed.OriginX);
        json.WriteNumber("originY", take.Placed.OriginY);
        json.WriteEndObject();

        json.WriteStartArray("columns");
        foreach (var column in Columns) json.WriteStringValue(column);
        json.WriteEndArray();

        // Written as raw text, one reading to a line. An indenting writer puts every number
        // on its own line, which for a four-second stroke at two hundred readings a second is
        // some seven thousand lines of one integer each -- a file nobody scrolls through and
        // a diff nobody reads. The header above stays indented, because that part is read.
        json.WritePropertyName("readings");
        json.WriteRawValue(Rows(take), skipInputValidation: true);

        json.WriteEndObject();
        json.Flush();

        return path;
    }

    private static string Rows(Take take)
    {
        if (take.Readings.Count == 0) return "[]";

        // Relative to the first, because a pen's timestamp has no stated origin. A difference
        // between two of them is meaningful and one of them on its own is not.
        var start = take.Readings[0].At;

        var rows = new StringBuilder();
        rows.AppendLine("[");

        for (var each = 0; each < take.Readings.Count; each++)
        {
            var reading = take.Readings[each];

            rows.Append("    [")
                .Append(reading.At - start).Append(", ")
                .Append(Round(reading.X, 3)).Append(", ")
                .Append(Round(reading.Y, 3)).Append(", ")
                .Append(reading.Pressure).Append(", ")
                .Append(Round(reading.Lean, 2)).Append(", ")
                .Append(Round(reading.Azimuth, 2)).Append(", ")
                .Append(Round(reading.Twist, 2))
                .AppendLine(each == take.Readings.Count - 1 ? "]" : "],");
        }

        return rows.Append("  ]").ToString();
    }

    /// <summary>Invariant, because a file read on a machine with another decimal point is not a file.</summary>
    private static string Round(double value, int places) =>
        Math.Round(value, places).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// A file name nobody has to think about: what it is, what made it, and when.
    /// </summary>
    /// <remarks>
    /// The tablet is in the name because the commonest thing anybody does with a folder of
    /// these is compare two devices, and a folder where that needs opening each file is a
    /// folder nobody compares anything in.
    /// </remarks>
    public static string Suggest(Gesture gesture, string tablet, DateTimeOffset at) =>
        string.Join("-",
            new[] { gesture.Id, Slug(tablet), at.ToString("yyyyMMdd-HHmmss") }
                .Where(part => part.Length > 0));

    private static string Slug(string name)
    {
        var slug = new StringBuilder();
        var dash = false;

        foreach (var letter in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(letter))
            {
                slug.Append(letter);
                dash = false;
            }
            else if (!dash && slug.Length > 0)
            {
                slug.Append('-');
                dash = true;
            }
        }

        return slug.ToString().Trim('-');
    }
}
