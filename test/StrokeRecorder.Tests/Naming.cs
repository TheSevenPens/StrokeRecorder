using System.Text.Json;
using StrokeKit.Strokes;
using StrokeKit.Surfaces;
using WinPenKit;

namespace StrokeRecorder.Tests;

/// <summary>
/// That a recording can be given a name, and that the name is the only thing it changes.
/// </summary>
/// <remarks>
/// The corpus lets a recording's <c>name</c> be added or changed after the recording was made, and
/// nothing else, and checks that line by line (<c>tools/check_names_only.py</c>). That is only
/// workable if the recorder writes the name where adding one later alters no other line, so one of
/// these writes the same take twice and compares the files.
/// </remarks>
public class Naming
{
    private static Take Made(string name)
    {
        var take = new Take(Gestures.All[0], InputApi.WintabDigitizer, 32767, new InkTransform(1, 1, 0, 0))
        {
            Tablet = "A tablet",
            Driver = "A driver",
            Name = name,
        };

        var contact = take.Begin();

        for (var each = 0; each < 6; each++)
        {
            contact.Add(new Reading(
                X: 100 + each, Y: 200 + each, Pressure: (uint)(500 + each * 40),
                At: 1_000_000 + each * 4166, Arrived: 5_000_000 + each * 6200));
        }

        return take;
    }

    private static string Folder() => Path.Combine(Path.GetTempPath(), $"sfg-{Guid.NewGuid():N}");

    private static void Clear(string path)
    {
        var folder = Path.GetDirectoryName(path);

        if (folder is not null && Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    [Fact]
    public void A_name_survives_being_written_and_read()
    {
        const string name = "Quick \"taps\" — résumé of 3";

        var path = Trace.Write(Made(name), Folder(), "one");

        try
        {
            Assert.Equal(name, Reopen.From(path).Take!.Name);
        }
        finally
        {
            Clear(path);
        }
    }

    [Fact]
    public void A_take_without_a_name_writes_no_name_and_reads_back_with_none()
    {
        var path = Trace.Write(Made(""), Folder(), "one");

        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));

            // Absent and not an empty string: the schema requires a name to say something.
            Assert.False(json.RootElement.TryGetProperty(TraceFormat.Field.Name, out _));
            Assert.Equal("", Reopen.From(path).Take!.Name);
        }
        finally
        {
            Clear(path);
        }
    }

    [Fact]
    public void Naming_a_take_changes_one_line_of_the_file_and_no_other()
    {
        // One take written twice, so the recording time and everything else is the same object's. Two
        // calls to Made() would differ in recordedAt and prove nothing about the name.
        var take = Made("");
        var plain = Trace.Write(take, Folder(), "same");

        take.Name = "Quick taps";
        var named = Trace.Write(take, Folder(), "same");

        try
        {
            var before = File.ReadAllLines(plain);
            var after = File.ReadAllLines(named);

            var added = after.Where(line => line.StartsWith("  \"name\": ", StringComparison.Ordinal)).ToList();

            Assert.Single(added);
            Assert.Equal("  \"name\": \"Quick taps\",", added[0]);

            // Everything else is byte for byte what it was, which is the whole of the corpus's rule.
            Assert.Equal(before, after.Where(line => !line.StartsWith("  \"name\": ", StringComparison.Ordinal)));
        }
        finally
        {
            Clear(plain);
            Clear(named);
        }
    }

    [Fact]
    public void The_name_follows_the_id_so_a_later_name_adds_a_whole_line_with_its_comma()
    {
        var path = Trace.Write(Made("Quick taps"), Folder(), "one");

        try
        {
            var lines = File.ReadAllLines(path);
            var id = Array.FindIndex(lines, line => line.StartsWith("  \"id\": ", StringComparison.Ordinal));

            Assert.True(id >= 0, "no id line");
            Assert.StartsWith("  \"name\": ", lines[id + 1]);
            Assert.EndsWith(",", lines[id + 1]);
        }
        finally
        {
            Clear(path);
        }
    }

    [Fact]
    public void A_name_leads_the_suggested_file_name_and_the_gesture_does_when_there_is_none()
    {
        var gesture = Gestures.All[0];
        var at = new DateTimeOffset(2026, 10, 8, 17, 22, 52, TimeSpan.Zero);

        Assert.Equal("quick-taps-a-tablet-20261008-172252", Trace.Suggest(gesture, "A tablet", at, "Quick taps"));
        Assert.Equal($"{gesture.Id}-a-tablet-20261008-172252", Trace.Suggest(gesture, "A tablet", at));
        Assert.Equal($"{gesture.Id}-a-tablet-20261008-172252", Trace.Suggest(gesture, "A tablet", at, ""));

        // A name with nothing a file name can keep falls back rather than leading with nothing.
        Assert.Equal($"{gesture.Id}-a-tablet-20261008-172252", Trace.Suggest(gesture, "A tablet", at, "?!"));
    }

    [Theory]
    [InlineData("Quick taps")]
    [InlineData("")]
    public void What_is_written_with_or_without_a_name_is_valid_against_the_corpus_schema(string name)
    {
        var schemaPath = ActiveAreas.SchemaPath();

        Assert.True(schemaPath is not null, "no schema found. corpus/ is a submodule: git submodule update --init");

        var path = Trace.Write(Made(name), Folder(), "one");

        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));

            var result = ActiveAreas.Schema.Value.Evaluate(json.RootElement,
                new Json.Schema.EvaluationOptions { OutputFormat = Json.Schema.OutputFormat.List });

            var why = string.Join("; ", (result.Details ?? [])
                .Where(each => each.Errors is { Count: > 0 })
                .SelectMany(each => each.Errors!.Select(error => $"{each.InstanceLocation} {error.Value}"))
                .Take(4));

            Assert.True(result.IsValid, $"the corpus schema rejects what the recorder wrote: {why}");
        }
        finally
        {
            Clear(path);
        }
    }
}
