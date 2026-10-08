using System.Text;
using System.Text.Json;
using StrokeKit.Strokes;

namespace StrokeRecorder;

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
    /// <summary>The format this writes, which <see cref="TraceFormat"/> defines.</summary>
    /// <remarks>
    /// Named here as well so the recorder's own screens can say what they wrote without
    /// reaching past the writer, but the definition is not here: writing a column the readers
    /// do not know about is the whole class of fault the owner exists to make impossible.
    /// </remarks>
    public const string Format = TraceFormat.Format;

    /// <inheritdoc cref="TraceFormat.Version"/>
    public const int Version = TraceFormat.Version;

    /// <summary>What x and y are, in words, in both the placement and the coordinates.</summary>
    private const string DesktopUnits = "desktop physical pixels, as reported by the session";

    /// <inheritdoc cref="TraceFormat.Names"/>
    public static IReadOnlyList<string> Columns => TraceFormat.Names;

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

        json.WriteString(TraceFormat.Field.Format, Format);
        json.WriteNumber(TraceFormat.Field.Version, Version);
        json.WriteString(TraceFormat.Field.Id, Path.GetFileNameWithoutExtension(path));
        json.WriteString(TraceFormat.Field.Gesture, take.Gesture.Id);
        json.WriteString(TraceFormat.Field.Intent, take.Intent);
        json.WriteString(TraceFormat.Field.Username, take.Username);
        json.WriteString(TraceFormat.Field.Notes, take.Notes);
        json.WriteString(TraceFormat.Field.RecordedAt, take.At.ToString("O"));
        json.WriteString(TraceFormat.Field.EndedBy, take.EndedBy);
        json.WriteNumber(TraceFormat.Field.StrokeCount, take.Strokes);
        json.WriteBoolean(TraceFormat.Field.KeptEveryAirborneReading, take.KeepingAloft);
        json.WriteNumber(TraceFormat.Field.HandedOver, take.Routed);
        json.WriteNumber(TraceFormat.Field.OffThePad, take.DroppedOffPad);
        json.WriteNumber(TraceFormat.Field.AfterTheStop, take.AfterTheStop);
        json.WriteNumber(TraceFormat.Field.AirborneNotKept, take.LeftOut);
        json.WriteNumber(TraceFormat.Field.AirborneKeptAlongside, take.KeptAlongside);

        // From beneath the session's own filtering, where the backend can say. The difference
        // between what the driver delivered and what the session passed on is the one number
        // this window cannot work out for itself, and is the difference between "the tablet
        // stopped reporting" and "the library discarded it".
        if (take.Counted is { } counted)
        {
            json.WriteStartObject(TraceFormat.Field.Counted);
            json.WriteNumber(TraceFormat.Field.FromDriver, counted.FromDriver);
            json.WriteNumber(TraceFormat.Field.OutsideRegion, counted.OutsideRegion);
            json.WriteNumber(TraceFormat.Field.Delivered, counted.Delivered);
            json.WriteEndObject();
        }

        json.WriteStartObject(TraceFormat.Field.Device);
        json.WriteString(TraceFormat.Field.Tablet, take.Tablet);
        json.WriteString(TraceFormat.Field.Driver, take.Driver);
        json.WriteString(TraceFormat.Field.Firmware, take.Firmware);
        json.WriteString(TraceFormat.Field.Api, take.Api.ToString());

        // The number the readings do not carry and cannot. Without it every pressure below
        // is an integer with no meaning.
        json.WriteNumber(TraceFormat.Field.FullScalePressure, take.FullScalePressure);
        json.WriteString(TraceFormat.Field.Conventions, take.Conventions);

        json.WriteEndObject();

        // What x and y are, which is not a surface pixel. They are desktop pixels as
        // reported, and this is the transform that was frozen when the tip went down.
        json.WriteStartObject(TraceFormat.Field.Placement);
        json.WriteString(TraceFormat.Field.Units, DesktopUnits);
        json.WriteString(TraceFormat.Field.Note,
            "multiply by scale and add origin, per axis, for the surface pixel this was drawn at");
        json.WriteNumber(TraceFormat.Field.ScaleX, take.Placed.ScaleX);
        json.WriteNumber(TraceFormat.Field.ScaleY, take.Placed.ScaleY);
        json.WriteNumber(TraceFormat.Field.OriginX, take.Placed.OriginX);
        json.WriteNumber(TraceFormat.Field.OriginY, take.Placed.OriginY);
        json.WriteEndObject();

        // What x and y are, said in the form the format gives it. Always the desktop form: this
        // records desktop positions, whatever the backend, and the tablet form is for a tool that
        // reads the device's own counts.
        //
        // The physical size is absent where the backend could not be asked -- every one but the
        // two Wintab ones -- because a size of zero would be a claim. Millimetres to the
        // micrometre, and the per-pixel scales to six places, which is finer than a tablet's own
        // resolution, so rounding here costs nothing a reader could have used.
        json.WriteStartObject(TraceFormat.Field.Coordinates);
        json.WriteString(TraceFormat.Field.Space, TraceFormat.CoordinateSpace.Desktop);
        json.WriteString(TraceFormat.Field.Units, DesktopUnits);

        if (take.ActiveArea is { } area)
        {
            json.WriteNumber(TraceFormat.Field.WidthMm, Math.Round(area.WidthMm, 3));
            json.WriteNumber(TraceFormat.Field.HeightMm, Math.Round(area.HeightMm, 3));
            json.WriteNumber(TraceFormat.Field.MappedWidthMm, Math.Round(area.MappedWidthMm, 3));
            json.WriteNumber(TraceFormat.Field.MappedHeightMm, Math.Round(area.MappedHeightMm, 3));
            json.WriteNumber(TraceFormat.Field.MmPerPixelX, Math.Round(area.MmPerPixelX, 6));
            json.WriteNumber(TraceFormat.Field.MmPerPixelY, Math.Round(area.MmPerPixelY, 6));
        }

        json.WriteEndObject();

        json.WriteString(TraceFormat.Field.Clocks, TraceFormat.Clocks);

        json.WriteStartArray(TraceFormat.Field.Columns);
        foreach (var column in TraceFormat.Names) json.WriteStringValue(column);
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

        var from = new TraceFormat.Origins(start, began, hasHostClock);

        json.WriteStartArray(TraceFormat.Field.Strokes);

        foreach (var contact in take.Contacts)
        {
            json.WriteStartObject();
            json.WriteString(TraceFormat.Field.EndedBy, contact.EndedBy);
            json.WriteNumber(TraceFormat.Field.ReadingCount, contact.Count);

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
                json.WriteNumber(TraceFormat.Field.LastSeenInTheAirMs, Math.Round(since / 1000.0, 1));
            }
            else
            {
                json.WriteString(TraceFormat.Field.LastSeenInTheAir, "the pen was not reported in the air at all");
            }

            // The pen in the air either side of the stroke, in the same columns and on the
            // same clock as the readings. Their pressure is zero by definition -- they are
            // kept for the position and the angles, which are the only record of how the pen
            // arrived and how it left.
            if (contact.Approach.Count > 0)
            {
                json.WritePropertyName(TraceFormat.Field.Approach);
                json.WriteRawValue(Rows(contact.Approach, from), skipInputValidation: true);
            }

            if (contact.Departure.Count > 0)
            {
                json.WritePropertyName(TraceFormat.Field.Departure);
                json.WriteRawValue(Rows(contact.Departure, from), skipInputValidation: true);
            }

            json.WritePropertyName(TraceFormat.Field.Readings);
            json.WriteRawValue(Rows(contact.Readings, from), skipInputValidation: true);

            json.WriteEndObject();
        }

        json.WriteEndArray();

        // Everything the pen reported with the tip up, unfiltered, when the take was asked for
        // it. At the take's level rather than a stroke's, because most of it belongs to the
        // gaps between strokes and not to either side of them.
        if (take.KeepingAloft)
        {
            json.WriteString(TraceFormat.Field.AloftNote,
                "Every reading taken with the tip up, unfiltered, including the packets that "
                + "mean the pen has left range. Recorded to see what the recorder is choosing "
                + "to drop. Not evidence about a stroke.");

            json.WritePropertyName(TraceFormat.Field.Aloft);
            json.WriteRawValue(Rows(take.Aloft, from), skipInputValidation: true);
        }

        json.WriteEndObject();
        json.Flush();
    }

    /// <param name="from">
    /// What the timestamps are measured from, and whether the take has a host clock at all.
    /// </param>
    /// <remarks>
    /// Written as raw text, one reading to a line. An indenting writer puts every number on its
    /// own line, which for a four-second stroke at two hundred readings a second is some seven
    /// thousand lines of one integer each — a file nobody scrolls through and a diff nobody
    /// reads. The header stays indented, because that part is read.
    /// <para>
    /// <b>The cells themselves are not spelled out here.</b> They used to be, in the column
    /// order, beside a separate list that stated that order — so the two could disagree and
    /// nothing would say so. <see cref="TraceFormat.Row"/> now produces them from the same list
    /// the file declares.
    /// </para>
    /// </remarks>
    private static string Rows(IReadOnlyList<Reading> readings, TraceFormat.Origins from)
    {
        if (readings.Count == 0) return "[]";

        var rows = new StringBuilder();
        rows.AppendLine("[");

        for (var each = 0; each < readings.Count; each++)
        {
            rows.Append("        [")
                .Append(TraceFormat.Row(readings[each], from))
                .AppendLine(each == readings.Count - 1 ? "]" : "],");
        }

        return rows.Append("      ]").ToString();
    }

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
