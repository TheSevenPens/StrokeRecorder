using System.Text;
using System.Text.Json;
using StrokeFieldGuide.Strokes;

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

    /// <summary>
    /// Four, since readings gained the device's own status word.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Version four adds a <c>status</c> column, raw and undecoded. On Wintab it is
    /// <c>pkStatus</c>, and it is kept because two open questions are questions about what
    /// its bits mean -- a queue overflow nothing has ever checked, and a proximity bit that
    /// does not behave as its name suggests.
    /// </para>
    /// <remarks>
    /// <para>
    /// Version three adds a <c>height</c> column, from Wintab's <c>pkZ</c>. The columns are
    /// declared in the file, so a reader that takes them from there rather than counting
    /// positions needs no change; one that assumed seven does.
    /// </para>
    /// <remarks>
    /// <para>
    /// Version one put a single <c>readings</c> array at the top level, because a take was a
    /// single stroke and nothing else was imaginable. Version two replaces it with
    /// <c>strokes</c>, an array of objects each holding their own <c>readings</c> -- so a
    /// one-stroke take is an array of one rather than a special case, and there is one shape
    /// to read instead of two.
    /// </para>
    /// <para>
    /// Each stroke may also carry <c>approach</c> and <c>departure</c> -- the pen in the air
    /// for up to a quarter of a second either side of it, in the same columns and on the same
    /// clock. Both are absent where there is nothing to report, which is an ordinary answer
    /// rather than a fault: a pen already resting on the tablet has no approach.
    /// </para>
    /// <para>
    /// The twelve version-one traces already recorded are <b>left as they are</b>. They are
    /// evidence, they are cited by number in the notes, and rewriting them to tidy the format
    /// would churn the corpus without adding a reading. Anything reading these files takes
    /// both shapes: a top-level <c>readings</c> is a take of one stroke.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// Version five adds <c>arrived</c>: the host clock, beside the pen's own. Four columns of
    /// version four are unchanged and a reader of either can take both.
    /// <para>
    /// Version six changes no columns. It marks the take where <c>approach</c>,
    /// <c>departure</c> and <c>lastSeenInTheAirMs</c> started being decided on that host clock
    /// rather than the pen's -- so an approach present in a version-six file was within a
    /// quarter second of the landing in real time, and one in an earlier file was within a
    /// quarter second of a counter. Every empty approach in the corpus before this is a
    /// reading the recorder was given and discarded.
    /// </para>
    /// </remarks>
    public const int Version = 6;

    /// <summary>What goes in, in order, so a reader does not have to guess at the tuples.</summary>
    /// <remarks>
    /// Arrays rather than objects per reading. A stroke is thousands of them and the key names
    /// repeated that many times are most of the file; the column list says what each slot is
    /// once.
    /// </remarks>
    public static readonly string[] Columns =
        ["at", "arrived", "x", "y", "pressure", "height", "status", "lean", "azimuth", "twist"];

    /// <summary>What the two clocks are, said in the file so a reader need not be told.</summary>
    public const string Clocks =
        "'at' is the pen's own timestamp and 'arrived' is this application's clock, both in "
        + "microseconds from the take's first reading. They are independent: a difference in "
        + "'at' with no matching difference in 'arrived' is the device stamping a packet late, "
        + "not the pen falling silent. Readings that reached the application in the same poll "
        + "share an 'arrived' exactly.";

    /// <summary>
    /// Writes a take, and answers where it went.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Written beside the target and moved into place.</b> Straight into the target with
    /// <c>File.Create</c>, an existing recording was truncated the instant the new one began
    /// — so anything that went wrong part way through left neither file. A recording is
    /// evidence somebody drew once and cannot draw again.
    /// </para>
    /// <para>
    /// <b>A name already taken is not overwritten.</b> It gets a suffix and the caller is
    /// told where the file actually went, which is why this returns a path rather than
    /// nothing. Suggested names carry a timestamp to the second, so two takes in one second
    /// collide — rare with a hand on the button and ordinary once saving is automatic.
    /// </para>
    /// </remarks>
    public static string Write(Take take, string folder, string name)
    {
        Directory.CreateDirectory(folder);

        var path = Free(folder, name.EndsWith(".json") ? name : name + ".json");
        var partial = path + ".writing";

        WriteTo(partial, take);

        // Moved rather than copied, which is one filesystem operation: the file is either
        // the old one or the whole new one, and never half of either.
        File.Move(partial, path);

        return path;
    }

    /// <summary>The first name in this folder that is not taken.</summary>
    private static string Free(string folder, string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var path = Path.Combine(folder, name);

        for (var next = 2; File.Exists(path); next++)
        {
            path = Path.Combine(folder, $"{stem}-{next}.json");
        }

        return path;
    }

    private static void WriteTo(string path, Take take)
    {
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
        json.WriteNumber("strokeCount", take.Strokes);
        json.WriteBoolean("keptEveryAirborneReading", take.KeepingAloft);
        json.WriteNumber("readingsHandedToTheRecorder", take.Routed);
        json.WriteNumber("readingsDroppedForBeingOffThePad", take.DroppedOffPad);
        json.WriteNumber("readingsAfterTheRecordingStopped", take.AfterTheStop);

        // From beneath the session's own filtering, where the backend can say. The difference
        // between what the driver delivered and what the session passed on is the one number
        // this window cannot work out for itself, and is the difference between "the tablet
        // stopped reporting" and "the library discarded it".
        if (take.Counted is { } counted)
        {
            json.WriteStartObject("whatTheSessionCounted");
            json.WriteNumber("packetsFromTheDriver", counted.FromDriver);
            json.WriteNumber("packetsOutsideTheCaptureRegion", counted.OutsideRegion);
            json.WriteNumber("pointsDelivered", counted.Delivered);
            json.WriteEndObject();
        }

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

        json.WriteString("clocks", Clocks);

        json.WriteStartArray("columns");
        foreach (var column in Columns) json.WriteStringValue(column);
        json.WriteEndArray();

        // Every timestamp in the file is relative to this one, across all the strokes rather
        // than per stroke. That is what keeps the gaps between them measurable: a stroke
        // whose readings started at its own zero would say how long it took and lose how long
        // the pen had been off the tablet before it, which is exactly what a series is for.
        // The first contact, or -- on a take that never touched down -- the first reading of
        // the pen in the air. Without the fallback a hover-only take writes absolute device
        // ticks, which have no stated origin and are meaningless on their own.
        var start = take.Count > 0
            ? take.Readings[0].At
            : take.Aloft.Count > 0 ? take.Aloft[0].At : 0;

        // The same reading, on the other clock. Rebased separately because the two origins are
        // unrelated -- one is a device tick and the other is this process starting -- and
        // subtracting a shared start would leave one of the columns meaningless.
        var began = take.Count > 0
            ? take.Readings[0].Arrived
            : take.Aloft.Count > 0 ? take.Aloft[0].Arrived : 0;

        // Whether this take has a host clock at all, decided once for the whole file rather
        // than per reading.
        //
        // Per reading, the test was `Arrived == 0`, and that conflates two different things:
        // a recording made before the recorder had a second clock, where the absence is real,
        // and a reading whose arrival happens to be zero -- which after rebasing is the
        // *first reading of every take*. So a take that had been read back and written out
        // again emitted a null for its first arrival, and reading that null threw. Every one
        // of the 33 published recordings failed to survive a second round trip.
        var hasHostClock = take.Contacts.Any(contact =>
                               contact.Readings.Any(reading => reading.Arrived != 0)
                               || contact.Approach.Any(reading => reading.Arrived != 0)
                               || contact.Departure.Any(reading => reading.Arrived != 0))
                           || take.Aloft.Any(reading => reading.Arrived != 0);

        json.WriteStartArray("strokes");

        foreach (var contact in take.Contacts)
        {
            json.WriteStartObject();
            json.WriteString("endedBy", contact.EndedBy);
            json.WriteNumber("readingCount", contact.Count);

            // Why an approach is empty, where it is. Written whether or not there is one,
            // because the number is the answer either way: a large gap means the pen had left
            // range and there was nothing to keep, a small one with no approach is a fault.
            //
            // On the host's clock, like the window it is compared against. It used to be the
            // pen's, which reports a landing as arriving a tenth of a second or more after the
            // last hover reading when nothing of the sort happened -- so this number said an
            // approach was too old to keep while the approach sat inside the window. Traces
            // before version 6 carry the pen's figure here and it is not a duration.
            if (contact.SinceLastSeen is { } since)
            {
                json.WriteNumber("lastSeenInTheAirMs", Math.Round(since / 1000.0, 1));
            }
            else
            {
                json.WriteString("lastSeenInTheAir", "the pen was not reported in the air at all");
            }

            // The pen in the air either side of the stroke, in the same columns and on the
            // same clock as the readings. Their pressure is zero by definition -- they are
            // kept for the position and the angles, which are the only record of how the pen
            // arrived and how it left.
            if (contact.Approach.Count > 0)
            {
                json.WritePropertyName("approach");
                json.WriteRawValue(Rows(contact.Approach, start, began, hasHostClock), skipInputValidation: true);
            }

            if (contact.Departure.Count > 0)
            {
                json.WritePropertyName("departure");
                json.WriteRawValue(Rows(contact.Departure, start, began, hasHostClock), skipInputValidation: true);
            }

            // Written as raw text, one reading to a line. An indenting writer puts every
            // number on its own line, which for a four-second stroke at two hundred readings
            // a second is some seven thousand lines of one integer each -- a file nobody
            // scrolls through and a diff nobody reads. The header stays indented, because
            // that part is read.
            json.WritePropertyName("readings");
            json.WriteRawValue(Rows(contact.Readings, start, began, hasHostClock), skipInputValidation: true);

            json.WriteEndObject();
        }

        json.WriteEndArray();

        // Everything the pen reported with the tip up, unfiltered, when the take was asked for
        // it. At the take's level rather than a stroke's, because most of it belongs to the
        // gaps between strokes and not to either side of them.
        if (take.KeepingAloft)
        {
            json.WriteString("aloftNote",
                "Every reading taken with the tip up, unfiltered, including the packets that "
                + "mean the pen has left range. Recorded to see what the recorder is choosing "
                + "to drop. Not evidence about a stroke.");

            json.WritePropertyName("aloft");
            json.WriteRawValue(Rows(take.Aloft, start, began, hasHostClock), skipInputValidation: true);
        }

        json.WriteEndObject();
        json.Flush();
    }

    /// <param name="start">
    /// The take's first reading, which every timestamp in the file is measured from. Passed in
    /// rather than taken per stroke, because a pen's timestamp has no stated origin: a
    /// difference between two of them is meaningful and one on its own is not, and the
    /// differences worth keeping include the ones that span a pen lift.
    /// </param>
    /// <param name="began">
    /// The same reading's arrival, which the <c>arrived</c> column is measured from. A separate
    /// origin from <paramref name="start"/> on purpose: the two clocks are unrelated and
    /// rebasing both against one of them would destroy the comparison the column exists for.
    /// </param>
    /// <param name="hasHostClock">
    /// Whether this take carries a host clock at all. A property of the take rather than of a
    /// reading: rebasing makes the first arrival of every take zero, so a per-reading test for
    /// zero calls that reading's real timestamp missing.
    /// </param>
    private static string Rows(
        IReadOnlyList<Reading> readings, long start, long began, bool hasHostClock)
    {
        if (readings.Count == 0) return "[]";

        var rows = new StringBuilder();
        rows.AppendLine("[");

        for (var each = 0; each < readings.Count; each++)
        {
            var reading = readings[each];

            rows.Append("        [")
                .Append(reading.At - start).Append(", ")
                .Append(hasHostClock ? (reading.Arrived - began).ToString() : "null").Append(", ")
                .Append(Round(reading.X, 3)).Append(", ")
                .Append(Round(reading.Y, 3)).Append(", ")
                .Append(reading.Pressure).Append(", ")
                .Append(Round(reading.Height, 2)).Append(", ")
                .Append(reading.Status).Append(", ")
                .Append(Round(reading.Lean, 2)).Append(", ")
                .Append(Round(reading.Azimuth, 2)).Append(", ")
                .Append(Round(reading.Twist, 2))
                .AppendLine(each == readings.Count - 1 ? "]" : "],");
        }

        return rows.Append("      ]").ToString();
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
