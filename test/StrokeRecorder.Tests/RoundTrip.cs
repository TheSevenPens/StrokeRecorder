using StrokeRecorder;
using StrokeKit.Strokes;
using StrokeKit.Surfaces;
using WinPenKit;

namespace StrokeRecorder.Tests;

/// <summary>
/// That a recording written by this tool can be read back by it.
/// </summary>
/// <remarks>
/// <para>
/// The most basic property the recorder has, and it was never checked. Reported on
/// <c>#85</c> by a reviewer who compiled a probe against these types and found that all 33
/// published recordings reopen, and that all 33 <b>rewritten</b> ones do not.
/// </para>
/// <para>
/// This matters more than an ordinary defect because the recorder is the instrument. Its
/// output is published as <c>StrokeCorpus</c> and is the test set five page checks draw
/// against, so a recording it cannot read back is evidence it cannot return to.
/// </para>
/// </remarks>
public class RoundTrip
{
    /// <summary>A take with a host clock, built without a tablet or a window.</summary>
    private static Take Made(bool withHostClock)
    {
        var take = new Take(Gestures.All[0], InputApi.WintabDigitizer, 32767,
            new InkTransform(1, 1, 0, 0))
        {
            Tablet = "A tablet",
            Driver = "A driver",
            Intent = "Made for a test.",
        };

        var contact = take.Begin();

        for (var each = 0; each < 12; each++)
        {
            contact.Add(new Reading(
                X: 100 + each, Y: 200 + each * 2,
                Pressure: (uint)(500 + each * 40),
                At: 1_000_000 + each * 4166,
                Height: 3 + each,
                Status: 0,
                Lean: 20 + each, Azimuth: 90, Twist: 10,
                // The host clock, or its absence. Both are real: a recording made before
                // the recorder had a second clock carries no arrival at all.
                Arrived: withHostClock ? 5_000_000 + each * 6200 : 0));
        }

        contact.EndedBy = "the pen lifted";

        return take;
    }

    private static string Written(Take take)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"sfg-{Guid.NewGuid():N}");

        return Trace.Write(take, folder, "one");
    }

    private static void Clear(string path)
    {
        var folder = Path.GetDirectoryName(path);

        if (folder is not null && Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    [Fact]
    public void A_take_with_a_host_clock_survives_being_written_and_read()
    {
        var path = Written(Made(withHostClock: true));

        try
        {
            var back = Reopen.From(path);

            Assert.True(back.Take is not null,
                $"a take this recorder wrote could not be read back: {back.Why}");
        }
        finally
        {
            Clear(path);
        }
    }

    [Fact]
    public void A_take_with_no_host_clock_survives_being_written_and_read()
    {
        var path = Written(Made(withHostClock: false));

        try
        {
            var back = Reopen.From(path);

            Assert.True(back.Take is not null,
                $"a take with no host clock could not be read back: {back.Why}");
        }
        finally
        {
            Clear(path);
        }
    }

    /// <summary>
    /// That reading a recording and writing it out again gives a readable recording.
    /// </summary>
    /// <remarks>
    /// The case the reviewer found, and the one the two above do not reach. Arrival times are
    /// <b>rebased on write</b>, so the first reading of a reopened take has an arrival of
    /// exactly zero -- and the writer treats zero as "this recording has no host clock" and
    /// emits a JSON null, which the reader then calls <c>GetDouble</c> on.
    /// <para>
    /// So the fault needs two round trips to show, which is why writing one and reading it
    /// back was not enough to find it.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_take_read_and_written_again_is_still_readable()
    {
        var once = Written(Made(withHostClock: true));

        try
        {
            var back = Reopen.From(once);

            Assert.True(back.Take is not null, $"first read failed: {back.Why}");

            var twice = Written(back.Take!);

            try
            {
                var again = Reopen.From(twice);

                Assert.True(again.Take is not null,
                    $"a take this recorder read and wrote again could not be read: {again.Why}");
            }
            finally
            {
                Clear(twice);
            }
        }
        finally
        {
            Clear(once);
        }
    }

    /// <summary>Every published recording, read and written and read again.</summary>
    /// <remarks>
    /// Over the corpus rather than over one made-up take, because the corpus is what people
    /// actually open: 33 recordings across six format versions, which is a wider set of
    /// shapes than anybody would think to construct.
    /// </remarks>
    [Fact]
    public void Every_published_recording_survives_a_round_trip()
    {
        var corpus = Traces.Folder();

        Assert.True(corpus is not null,
            "no recordings found. corpus/ is a submodule: git submodule update --init");

        var broken = new List<string>();
        var checkedAny = 0;

        foreach (var path in Directory.EnumerateFiles(corpus!, "*.json").Order())
        {
            var opened = Reopen.From(path);

            if (opened.Take is null) continue;

            var folder = Path.Combine(Path.GetTempPath(), $"sfg-{Guid.NewGuid():N}");

            try
            {
                var again = Trace.Write(opened.Take, folder, "again");

                var back = Reopen.From(again);

                if (back.Take is null) broken.Add($"{Path.GetFileName(path)}: {back.Why}");

                checkedAny++;
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        Assert.True(checkedAny > 0, "no recording could be opened at all");

        Assert.True(broken.Count == 0,
            $"{broken.Count} of {checkedAny} recordings could not be read after this recorder "
            + $"wrote them out:\n{string.Join("\n", broken.Take(5))}");
    }
}
