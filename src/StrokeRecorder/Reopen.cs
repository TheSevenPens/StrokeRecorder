using System.Text.Json;
using StrokeKit.Strokes;
using StrokeKit.Surfaces;
using WinPenKit;

namespace StrokeRecorder;

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
/// six the approach measured on it, seven the username, notes and firmware. A file that is missing a column gets the field's default,
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

            if (!root.TryGetProperty(TraceFormat.Field.Columns, out var columns))
            {
                return new(null, "No columns: this is not a trace this recorder wrote.");
            }

            var layout = new TraceFormat.Layout(columns);

            if (!layout.Positioned)
            {
                return new(null, "The columns do not carry a position and a pressure.");
            }

            var gestureId = Text(root, TraceFormat.Field.Gesture);
            var gesture = Gestures.All.FirstOrDefault(each => each.Id == gestureId)
                          ?? Gestures.All[0];

            var api = Enum.TryParse<InputApi>(Text(root, TraceFormat.Field.Device, TraceFormat.Field.Api), out var parsed)
                ? parsed
                : InputApi.WintabDigitizer;

            var full = Number(root, TraceFormat.Field.Device, TraceFormat.Field.FullScalePressure);

            var take = new Take(gesture, api, full > 0 ? (int)full : 32767, Placement(root))
            {
                // The day it was recorded, not the day it was reopened. Without this a
                // recording from last September came back stamped today, and saving it
                // again wrote that over the only record of when it happened.
                At = DateTimeOffset.TryParse(Text(root, TraceFormat.Field.RecordedAt), out var when)
                    ? when
                    : DateTimeOffset.Now,
                Tablet = Text(root, TraceFormat.Field.Device, TraceFormat.Field.Tablet),
                Driver = Text(root, TraceFormat.Field.Device, TraceFormat.Field.Driver),
                Firmware = Text(root, TraceFormat.Field.Device, TraceFormat.Field.Firmware),
                Conventions = Text(root, TraceFormat.Field.Device, TraceFormat.Field.Conventions),
                Intent = Text(root, TraceFormat.Field.Intent),
                Username = Text(root, TraceFormat.Field.Username),
                Notes = Text(root, TraceFormat.Field.Notes),
                EndedBy = Text(root, TraceFormat.Field.EndedBy),
            };

            // Version one: no strokes array, every reading at the top level, one stroke.
            if (root.TryGetProperty(TraceFormat.Field.Readings, out var flat) && flat.ValueKind == JsonValueKind.Array)
            {
                var only = take.Begin();

                foreach (var row in flat.EnumerateArray()) only.Add(layout.Of(row));
            }

            if (root.TryGetProperty(TraceFormat.Field.Strokes, out var strokes) && strokes.ValueKind == JsonValueKind.Array)
            {
                foreach (var stroke in strokes.EnumerateArray())
                {
                    var contact = take.Begin();

                    if (stroke.TryGetProperty(TraceFormat.Field.Readings, out var rows))
                    {
                        foreach (var row in rows.EnumerateArray()) contact.Add(layout.Of(row));
                    }

                    if (stroke.TryGetProperty(TraceFormat.Field.Approach, out var approach))
                    {
                        contact.Approaching(
                            approach.EnumerateArray().Select(layout.Of).ToList(),
                            stroke.TryGetProperty(TraceFormat.Field.LastSeenInTheAirMs, out var since)
                                ? (long)(since.GetDouble() * 1000)
                                : null,
                            null);
                    }

                    if (stroke.TryGetProperty(TraceFormat.Field.Departure, out var departure))
                    {
                        foreach (var row in departure.EnumerateArray()) contact.Departing(layout.Of(row));
                    }

                    contact.EndedBy = stroke.TryGetProperty(TraceFormat.Field.EndedBy, out var why)
                        ? why.GetString() ?? ""
                        : "";
                }
            }

            if (root.TryGetProperty(TraceFormat.Field.Aloft, out var aloft) && aloft.ValueKind == JsonValueKind.Array)
            {
                take.KeepAll(aloft.EnumerateArray().Select(layout.Of).ToList());
            }

            // The counts the session reported, where the file carries them. Restored so the
            // ledger says the same thing it said on the day, rather than reporting the
            // absence of a session that has long since closed.
            if (root.TryGetProperty(TraceFormat.Field.Counted, out var counted))
            {
                take.Counted = (
                    (long)Number(counted, TraceFormat.Field.FromDriver),
                    (long)Number(counted, TraceFormat.Field.OutsideRegion),
                    (long)Number(counted, TraceFormat.Field.Delivered));
            }

            take.Reopened(
                (int)Number(root, TraceFormat.Field.HandedOver),
                (int)Number(root, TraceFormat.Field.OffThePad),
                (int)Number(root, TraceFormat.Field.AfterTheStop),
                (int)Number(root, TraceFormat.Field.AirborneNotKept),
                (int)Number(root, TraceFormat.Field.AirborneKeptAlongside));

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
        if (!root.TryGetProperty(TraceFormat.Field.Placement, out var placed)) return new InkTransform(1, 1, 0, 0);

        return new InkTransform(
            Number(placed, TraceFormat.Field.ScaleX) is var sx and not 0 ? sx : 1,
            Number(placed, TraceFormat.Field.ScaleY) is var sy and not 0 ? sy : 1,
            Number(placed, TraceFormat.Field.OriginX),
            Number(placed, TraceFormat.Field.OriginY));
    }

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
