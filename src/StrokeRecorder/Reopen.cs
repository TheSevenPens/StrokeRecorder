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

            // Counts are not pixels. A recording in the device's own counts, from a tool that
            // reads the driver directly, has no desktop and no placement; opened here its counts
            // would be drawn as pixels, and look like a stroke until somebody measured it.
            if (Space(root) != TraceFormat.CoordinateSpace.Desktop)
            {
                return new(null, "This recording is in the tablet's own digitizer counts, made by a tool "
                    + "that reads the device directly. This recorder replays desktop positions and "
                    + "cannot show it.");
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
                Pen = Text(root, TraceFormat.Field.Device, TraceFormat.Field.Pen),
                Conventions = Text(root, TraceFormat.Field.Device, TraceFormat.Field.Conventions),
                ActiveArea = Area(root),
                Intent = Text(root, TraceFormat.Field.Intent),
                Name = Text(root, TraceFormat.Field.Name),
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

    /// <summary>
    /// What <c>coordinates.space</c> says, where the file has one. A file before version eight
    /// has none and its positions are the desktop's, which is not the same as unknown.
    /// </summary>
    private static string Space(JsonElement root) =>
        Text(root, TraceFormat.Field.Coordinates, TraceFormat.Field.Space) is { Length: > 0 } named
            ? named
            : TraceFormat.CoordinateSpace.Desktop;

    /// <summary>
    /// The tablet's size and scale, where a desktop recording states them. Null for a file that
    /// does not, which is every file before version eight and every one from a backend that
    /// could not be asked -- and not a tablet of no size.
    /// </summary>
    /// <remarks>
    /// All six or none. A file that names the surface but gives no scale cannot turn a pixel into
    /// a distance, and an area with a zero scale would read as a claim, so it is not one.
    /// </remarks>
    private static ActiveArea? Area(JsonElement root)
    {
        if (!root.TryGetProperty(TraceFormat.Field.Coordinates, out var area)
            || area.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var scale = (X: Number(area, TraceFormat.Field.MmPerPixelX), Y: Number(area, TraceFormat.Field.MmPerPixelY));
        var (width, height) = (Number(area, TraceFormat.Field.WidthMm), Number(area, TraceFormat.Field.HeightMm));

        if (scale.X <= 0 || scale.Y <= 0 || width <= 0 || height <= 0) return null;

        // The mapped part defaults to the whole where a file leaves it out, which is what the
        // format says the mapping is unless somebody chose another.
        var mappedWidth = Number(area, TraceFormat.Field.MappedWidthMm);
        var mappedHeight = Number(area, TraceFormat.Field.MappedHeightMm);

        return new ActiveArea(
            width,
            height,
            mappedWidth > 0 ? mappedWidth : width,
            mappedHeight > 0 ? mappedHeight : height,
            scale.X,
            scale.Y);
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
