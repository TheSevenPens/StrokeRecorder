using StrokeFieldGuide.Recorder;
using StrokeFieldGuide.Strokes;
using StrokeFieldGuide.Surfaces;
using WinPenKit;

namespace StrokeFieldGuide.Recorder.Tests;

/// <summary>
/// That saving a recording cannot lose one.
/// </summary>
/// <remarks>
/// Reported on <c>#85</c>, and worth fixing before <c>#66</c> rather than after: that issue
/// makes saving automatic, which turns a rare collision into an ordinary one. A recording is
/// evidence somebody drew once and cannot draw again.
/// </remarks>
public class Persistence
{
    private static Take Made(string intent = "Made for a test.") =>
        Filled(new Take(Gestures.All[0], InputApi.WintabDigitizer, 32767,
            new InkTransform(1, 1, 0, 0))
        {
            Tablet = "A tablet",
            Intent = intent,
        });

    private static Take Filled(Take take)
    {
        var contact = take.Begin();

        for (var each = 0; each < 6; each++)
        {
            contact.Add(new Reading(
                X: 10 + each, Y: 20 + each, Pressure: (uint)(400 + each * 50),
                At: 1_000_000 + each * 4166, Height: 0, Status: 0,
                Lean: 10, Azimuth: 90, Twist: 0, Arrived: 7_000_000 + each * 6200));
        }

        contact.EndedBy = "the pen lifted";

        return take;
    }

    private static string Folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"sfg-{Guid.NewGuid():N}");

        Directory.CreateDirectory(folder);

        return folder;
    }

    [Fact]
    public void Saving_over_an_existing_name_does_not_destroy_the_existing_recording()
    {
        var folder = Folder();

        try
        {
            var first = Trace.Write(Made("the first"), folder, "take");
            var wasThere = File.ReadAllText(first);

            var second = Trace.Write(Made("the second"), folder, "take");

            Assert.NotEqual(first, second);

            Assert.True(File.Exists(first), "the first recording is gone");

            Assert.Equal(wasThere, File.ReadAllText(first));

            Assert.Contains("the second", File.ReadAllText(second));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Both_recordings_can_still_be_read()
    {
        var folder = Folder();

        try
        {
            Trace.Write(Made("the first"), folder, "take");
            Trace.Write(Made("the second"), folder, "take");

            foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
            {
                var back = Reopen.From(path);

                Assert.True(back.Take is not null, $"{Path.GetFileName(path)}: {back.Why}");
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    /// <summary>That nothing half-written is left where a recording should be.</summary>
    /// <remarks>
    /// The file is built beside its target and moved into place, so the only two states the
    /// target can be in are the old recording and the whole new one. Checked by looking for
    /// leftovers rather than by interrupting a write, which is not reachable from a test.
    /// </remarks>
    [Fact]
    public void A_completed_save_leaves_nothing_half_written_behind()
    {
        var folder = Folder();

        try
        {
            Trace.Write(Made(), folder, "take");

            var left = Directory.EnumerateFiles(folder)
                .Select(Path.GetFileName)
                .Where(name => name is not null && !name.EndsWith(".json"))
                .ToList();

            Assert.True(left.Count == 0, $"left behind: {string.Join(", ", left)}");
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    /// <summary>That reopening a recording keeps the day it was made.</summary>
    /// <remarks>
    /// A recording from last September came back stamped today, because <c>Take.At</c>
    /// defaulted to now and nothing restored it. Saving it again then wrote today's date over
    /// the only record of when it happened.
    /// </remarks>
    [Fact]
    public void A_reopened_recording_keeps_the_date_it_was_recorded()
    {
        var folder = Folder();

        try
        {
            var path = Trace.Write(Made(), folder, "take");

            // Written by hand rather than by waiting: the point is a date this process could
            // not have produced.
            var text = File.ReadAllText(path);
            var was = "2000-01-01T09:30:00.0000000+00:00";

            var start = text.IndexOf("\"recordedAt\": \"", StringComparison.Ordinal)
                        + "\"recordedAt\": \"".Length;
            var end = text.IndexOf('"', start);

            File.WriteAllText(path, text[..start] + was + text[end..]);

            var back = Reopen.From(path);

            Assert.True(back.Take is not null, back.Why);

            Assert.Equal(2000, back.Take!.At.Year);

            // And it survives being written out again, which is where it was being lost.
            var again = Trace.Write(back.Take!, folder, "again");

            Assert.Contains("2000-01-01", File.ReadAllText(again));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
