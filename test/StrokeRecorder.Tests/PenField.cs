using System.Text.Json;
using StrokeKit.Strokes;
using StrokeKit.Surfaces;
using WinPenKit;

namespace StrokeRecorder.Tests;

/// <summary>
/// That a recording can say which pen it was made with, and says nothing when nobody did.
/// </summary>
/// <remarks>
/// Written inside <c>device</c> and only when it says something: the schema rejects an empty pen, and
/// an absent one is how "not said" is written. Unlike a recording's name it is evidence, so the
/// corpus will not let it be added to a recording afterwards, which makes getting it right when the
/// recording is made the only chance.
/// </remarks>
public class PenField
{
    private static Take Made(string pen)
    {
        var take = new Take(Gestures.All[0], InputApi.WintabDigitizer, 32767, new InkTransform(1, 1, 0, 0))
        {
            Tablet = "A tablet",
            Driver = "A driver",
            Firmware = "1.2.3",
            Pen = pen,
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
    public void A_pen_survives_being_written_and_read()
    {
        var path = Trace.Write(Made("ACP-700"), Folder(), "one");

        try
        {
            Assert.Equal("ACP-700", Reopen.From(path).Take!.Pen);
        }
        finally
        {
            Clear(path);
        }
    }

    [Fact]
    public void The_pen_is_in_the_device_block_and_nowhere_else()
    {
        var path = Trace.Write(Made("ACP-700"), Folder(), "one");

        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var root = json.RootElement;

            Assert.Equal("ACP-700", root.GetProperty(TraceFormat.Field.Device).GetProperty(TraceFormat.Field.Pen).GetString());
            Assert.False(root.TryGetProperty(TraceFormat.Field.Pen, out _), "pen is a device field, not a top-level one");
        }
        finally
        {
            Clear(path);
        }
    }

    [Fact]
    public void A_take_without_a_pen_writes_none_and_reads_back_with_none()
    {
        var path = Trace.Write(Made(""), Folder(), "one");

        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));

            // Absent and not an empty string: the schema requires a pen to say something.
            Assert.False(json.RootElement.GetProperty(TraceFormat.Field.Device).TryGetProperty(TraceFormat.Field.Pen, out _));
            Assert.Equal("", Reopen.From(path).Take!.Pen);
        }
        finally
        {
            Clear(path);
        }
    }

    [Fact]
    public void A_recording_from_before_the_pen_existed_reopens_with_none()
    {
        var folder = Folder();
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "old.json");

        File.WriteAllText(path, """
            {
              "format": "stroke-field-guide/take",
              "formatVersion": 7,
              "gesture": "single-stroke",
              "device": { "tablet": "A tablet", "firmware": "1", "fullScalePressure": 32767 },
              "columns": ["at", "x", "y", "pressure"],
              "strokes": [ { "readings": [[0, 10, 20, 400], [4166, 11, 21, 500]] } ]
            }
            """);

        try
        {
            var back = Reopen.From(path);

            Assert.True(back.Take is not null, back.Why);
            Assert.Equal("", back.Take!.Pen);
        }
        finally
        {
            Clear(path);
        }
    }

    [Fact]
    public void A_remembered_pen_survives_the_settings_file()
    {
        var round = JsonSerializer.Deserialize<Remembered>(
            JsonSerializer.Serialize(new Remembered(Tablet: "T", Firmware: "1", Username: "u", Pen: "ACP-700")));

        Assert.Equal("ACP-700", round!.Pen);
        Assert.Equal("1", round.Firmware);
    }

    /// <summary>
    /// The settings file on somebody's machine was written before there was a pen. Opening the window
    /// with it must give an empty pen, not a null that the next line dereferences.
    /// </summary>
    [Fact]
    public void A_settings_file_from_before_the_pen_gives_an_empty_pen_not_a_null()
    {
        var old = """{ "Tablet": "Wacom Cintiq 24", "Driver": "6.4.14-1", "Diameter": 25, "Firmware": "1.2.3", "Username": "me" }""";

        var read = JsonSerializer.Deserialize<Remembered>(old)!;

        Assert.NotNull(read.Pen);
        Assert.Equal("", read.Pen);
        Assert.Equal("1.2.3", read.Firmware);
    }

    [Theory]
    [InlineData("ACP-700")]
    [InlineData("")]
    public void What_is_written_with_or_without_a_pen_is_valid_against_the_corpus_schema(string pen)
    {
        Assert.True(ActiveAreas.SchemaPath() is not null, "no schema found. corpus/ is a submodule: git submodule update --init");

        var path = Trace.Write(Made(pen), Folder(), "one");

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
