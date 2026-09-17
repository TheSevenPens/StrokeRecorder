using System.Text.Json;
using StrokeFieldGuide.Strokes;
using StrokeFieldGuide.Surfaces;
using WinPenKit;

namespace StrokeFieldGuide.Recorder;

/// <summary>
/// Reads a trace back into a <see cref="Take"/>.
/// </summary>
/// <remarks>
/// <para>
/// The recorder could write a take and never read one, which made every screen after the
/// record step unreachable without a tablet and a hand. Thirty-one recordings sat in
/// <c>traces/</c> and none of them could be put in front of the analysis it was built for.
/// </para>
/// <para>
/// So this is not only a convenience for a reader wanting yesterday's take back. It is what
/// makes the analysis step checkable at all: every recording in the corpus is a case, including
/// the awkward ones — a take of nothing but the hovering pen, a take of a single tap, a take
/// whose counts do not balance.
/// </para>
/// <para>
/// <b>Reads every version.</b> Version one put the readings at the top level with no strokes;
/// two introduced the strokes array; three added height, four status, five the host clock and
/// six the approach measured on it. A file that is missing a column gets the field's default,
/// which is what a reader of an old trace should get: the absence is real.
/// </para>
/// </remarks>
public static class Reopen
{
    /// <summary>What a trace could not be read as, or the take it was.</summary>
    public readonly record struct Reading(Take? Take, string? Why);

    public static Reading From(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            using var json = JsonDocument.Parse(file);

            var root = json.RootElement;

            if (!root.TryGetProperty("columns", out var columns))
            {
                return new(null, "No columns: this is not a trace this recorder wrote.");
            }

            var at = Column(columns, "at");
            var arrived = Column(columns, "arrived");
            var x = Column(columns, "x");
            var y = Column(columns, "y");
            var pressure = Column(columns, "pressure");
            var height = Column(columns, "height");
            var status = Column(columns, "status");
            var lean = Column(columns, "lean");
            var azimuth = Column(columns, "azimuth");
            var twist = Column(columns, "twist");

            if (x < 0 || y < 0 || pressure < 0)
            {
                return new(null, "The columns do not carry a position and a pressure.");
            }

            var gestureId = Text(root, "gesture");
            var gesture = Gestures.All.FirstOrDefault(each => each.Id == gestureId)
                          ?? Gestures.All[0];

            var api = Enum.TryParse<InputApi>(Text(root, "device", "api"), out var parsed)
                ? parsed
                : InputApi.WintabDigitizer;

            var full = Number(root, "device", "fullScalePressure");

            var take = new Take(gesture, api, full > 0 ? (int)full : 32767, Placement(root))
            {
                // The day it was recorded, not the day it was reopened. Without this a
                // recording from last September came back stamped today, and saving it
                // again wrote that over the only record of when it happened.
                At = DateTimeOffset.TryParse(Text(root, "recordedAt"), out var when)
                    ? when
                    : DateTimeOffset.Now,
                Tablet = Text(root, "device", "tablet"),
                Driver = Text(root, "device", "driver"),
                Conventions = Text(root, "device", "conventions"),
                Intent = Text(root, "intent"),
                EndedBy = Text(root, "endedBy"),
            };

            Strokes.Reading Read(JsonElement row) => new(
                X: Cell(row, x), Y: Cell(row, y),
                Pressure: (uint)Math.Max(0, Cell(row, pressure)),
                At: (long)Cell(row, at),
                Height: Cell(row, height),
                Status: (uint)Math.Max(0, Cell(row, status)),
                Lean: Cell(row, lean), Azimuth: Cell(row, azimuth), Twist: Cell(row, twist),
                Arrived: (long)Cell(row, arrived));

            // Version one: no strokes array, every reading at the top level, one stroke.
            if (root.TryGetProperty("readings", out var flat) && flat.ValueKind == JsonValueKind.Array)
            {
                var only = take.Begin();

                foreach (var row in flat.EnumerateArray()) only.Add(Read(row));
            }

            if (root.TryGetProperty("strokes", out var strokes) && strokes.ValueKind == JsonValueKind.Array)
            {
                foreach (var stroke in strokes.EnumerateArray())
                {
                    var contact = take.Begin();

                    if (stroke.TryGetProperty("readings", out var rows))
                    {
                        foreach (var row in rows.EnumerateArray()) contact.Add(Read(row));
                    }

                    if (stroke.TryGetProperty("approach", out var approach))
                    {
                        contact.Approaching(
                            approach.EnumerateArray().Select(Read).ToList(),
                            stroke.TryGetProperty("lastSeenInTheAirMs", out var since)
                                ? (long)(since.GetDouble() * 1000)
                                : null,
                            null);
                    }

                    if (stroke.TryGetProperty("departure", out var departure))
                    {
                        foreach (var row in departure.EnumerateArray()) contact.Departing(Read(row));
                    }

                    contact.EndedBy = stroke.TryGetProperty("endedBy", out var why)
                        ? why.GetString() ?? ""
                        : "";
                }
            }

            if (root.TryGetProperty("aloft", out var aloft) && aloft.ValueKind == JsonValueKind.Array)
            {
                take.KeepAll(aloft.EnumerateArray().Select(Read).ToList());
            }

            // The counts the session reported, where the file carries them. Restored so the
            // ledger says the same thing it said on the day, rather than reporting the
            // absence of a session that has long since closed.
            if (root.TryGetProperty("whatTheSessionCounted", out var counted))
            {
                take.Counted = (
                    (long)Number(counted, "packetsFromTheDriver"),
                    (long)Number(counted, "packetsOutsideTheCaptureRegion"),
                    (long)Number(counted, "pointsDelivered"));
            }

            take.Reopened(
                (int)Number(root, "readingsHandedToTheRecorder"),
                (int)Number(root, "readingsDroppedForBeingOffThePad"),
                (int)Number(root, "readingsAfterTheRecordingStopped"),
                (int)Number(root, "readingsAirborneAndNotKept"));

            return take.Holds
                ? new(take, null)
                : new(null, "The file has no readings in it.");
        }
        catch (Exception bad)
        {
            return new(null, bad.Message);
        }
    }

    private static InkTransform Placement(JsonElement root)
    {
        if (!root.TryGetProperty("placement", out var placed)) return new InkTransform(1, 1, 0, 0);

        return new InkTransform(
            Number(placed, "scaleX") is var sx and not 0 ? sx : 1,
            Number(placed, "scaleY") is var sy and not 0 ? sy : 1,
            Number(placed, "originX"),
            Number(placed, "originY"));
    }

    /// <summary>Which slot a column sits in, or -1 when the file does not carry it.</summary>
    private static int Column(JsonElement columns, string name)
    {
        var slot = 0;

        foreach (var column in columns.EnumerateArray())
        {
            if (column.GetString() == name) return slot;

            slot++;
        }

        return -1;
    }

    /// <summary>A cell of a row, or zero where the column is absent or the row is short.</summary>
    /// <remarks>
    /// <b>A null is an absence, not an error.</b> The writer emits null for a column a take
    /// does not carry -- the host clock on anything recorded before there was one -- and
    /// calling <c>GetDouble</c> on it throws. Reading a file this tool had written was enough
    /// to hit it, which is how 33 of 33 published recordings failed a second round trip.
    /// </remarks>
    private static double Cell(JsonElement row, int column) =>
        column >= 0 && column < row.GetArrayLength() && row[column].ValueKind == JsonValueKind.Number
            ? row[column].GetDouble()
            : 0;

    private static string Text(JsonElement root, params string[] path)
    {
        var at = root;

        foreach (var step in path)
        {
            if (!at.TryGetProperty(step, out at)) return "";
        }

        return at.ValueKind == JsonValueKind.String ? at.GetString() ?? "" : "";
    }

    private static double Number(JsonElement root, params string[] path)
    {
        var at = root;

        foreach (var step in path)
        {
            if (!at.TryGetProperty(step, out at)) return 0;
        }

        return at.ValueKind == JsonValueKind.Number ? at.GetDouble() : 0;
    }
}
