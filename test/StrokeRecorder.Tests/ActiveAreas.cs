using System.Text.Json;
using StrokeKit.Strokes;
using StrokeKit.Surfaces;
using WinPenKit;

namespace StrokeRecorder.Tests;

/// <summary>
/// That a recording says how big the tablet was, and says nothing where it could not be asked.
/// </summary>
/// <remarks>
/// <para>
/// The figures in the fixtures are the ones measured on the machine this was written on: a
/// 349 x 195 mm Wacom, mapped across a 3840 x 3240 desktop. They are chosen because that
/// mapping is <b>not</b> square -- a pixel is 0.091 mm across and 0.060 mm down -- and a test
/// built on a square one would pass for an implementation that used a single scale.
/// </para>
/// <para>
/// No tablet needed. What a driver returns is read by the library and was checked against a
/// real one; what the recorder does with the answer is checkable without.
/// </para>
/// </remarks>
public class ActiveAreas
{
    private static readonly PenPhysicalArea Measured = new(
        DeviceWidthMm: 349, DeviceHeightMm: 195,
        MappedWidthMm: 349, MappedHeightMm: 195,
        MappedWidthPixels: 3840, MappedHeightPixels: 3240);

    private static Take Made(ActiveArea? area)
    {
        var take = new Take(Gestures.All[0], InputApi.WintabDigitizer, 32767,
            new InkTransform(1, 1, 0, 0))
        {
            Tablet = "A tablet",
            Driver = "A driver",
            ActiveArea = area,
        };

        var contact = take.Begin();

        for (var each = 0; each < 5; each++)
        {
            contact.Add(new Reading(
                X: 100 + each, Y: 200 + each, Pressure: (uint)(500 + each * 40),
                At: 1_000_000 + each * 4166, Arrived: 5_000_000 + each * 6200));
        }

        return take;
    }

    private static string Written(Take take) =>
        Trace.Write(take, Path.Combine(Path.GetTempPath(), $"sfg-{Guid.NewGuid():N}"), "one");

    private static void Clear(string path)
    {
        var folder = Path.GetDirectoryName(path);

        if (folder is not null && Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    // ---- the scale ----------------------------------------------------------

    [Fact]
    public void A_pixel_is_a_different_distance_across_than_down_when_the_mapping_stretches_the_tablet()
    {
        var area = ActiveArea.From(Measured);

        Assert.Equal(349.0 / 3840, area.MmPerPixelX, 9);
        Assert.Equal(195.0 / 3240, area.MmPerPixelY, 9);
        Assert.NotEqual(area.MmPerPixelX, area.MmPerPixelY, 3);
    }

    [Fact]
    public void A_distance_scales_each_axis_by_its_own_figure()
    {
        var area = ActiveArea.From(Measured);

        // A whole mapped width, sideways, is exactly the tablet's width: the scale and the
        // extent have to agree, or one of them is wrong.
        Assert.Equal(349, area.Millimetres(3840, 0), 6);
        Assert.Equal(195, area.Millimetres(0, 3240), 6);

        // Both at once is the hypotenuse of the two scaled components, and not either axis's
        // figure times the length in pixels.
        var diagonal = area.Millimetres(3840, 3240);

        Assert.Equal(Math.Sqrt(349.0 * 349 + 195.0 * 195), diagonal, 6);
    }

    // ---- the words ----------------------------------------------------------

    [Fact]
    public void The_description_leaves_out_the_mapped_part_when_it_is_the_whole()
    {
        Assert.Equal("349 × 195 mm", ActiveArea.From(Measured).Describe());
    }

    [Fact]
    public void The_description_names_the_mapped_part_when_it_is_a_different_size()
    {
        var partial = ActiveArea.From(Measured with { MappedWidthMm = 200, MappedHeightMm = 112.5 });

        Assert.Equal("349 × 195 mm (mapped 200 × 112.5 mm)", partial.Describe());
    }

    // ---- the file -----------------------------------------------------------

    [Fact]
    public void The_area_survives_being_written_and_read()
    {
        var path = Written(Made(ActiveArea.From(Measured)));

        try
        {
            var back = Reopen.From(path);

            Assert.True(back.Take is not null, $"could not be read back: {back.Why}");

            var area = Assert.NotNull(back.Take!.ActiveArea);

            Assert.Equal(349, area.WidthMm, 3);
            Assert.Equal(195, area.HeightMm, 3);
            Assert.Equal(349, area.MappedWidthMm, 3);
            Assert.Equal(195, area.MappedHeightMm, 3);

            // Written to six places, so the figure that comes back is the rounded one.
            Assert.Equal(349.0 / 3840, area.MmPerPixelX, 6);
            Assert.Equal(195.0 / 3240, area.MmPerPixelY, 6);
        }
        finally
        {
            Clear(path);
        }
    }

    [Fact]
    public void A_rewritten_take_states_the_same_area()
    {
        var first = Written(Made(ActiveArea.From(Measured)));

        try
        {
            var reopened = Reopen.From(first).Take!;
            var second = Written(reopened);

            try
            {
                Assert.Equal(reopened.ActiveArea, Reopen.From(second).Take!.ActiveArea);
            }
            finally
            {
                Clear(second);
            }
        }
        finally
        {
            Clear(first);
        }
    }

    [Fact]
    public void A_take_whose_backend_could_not_say_writes_no_area_and_reads_back_without_one()
    {
        var path = Written(Made(null));

        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));

            var coordinates = json.RootElement.GetProperty(TraceFormat.Field.Coordinates);

            // Still says what x and y are, and says nothing about size. Absent, not zero: a
            // tablet of no size would be a claim nobody made.
            Assert.Equal("desktop", coordinates.GetProperty(TraceFormat.Field.Space).GetString());
            Assert.False(coordinates.TryGetProperty(TraceFormat.Field.WidthMm, out _));
            Assert.False(coordinates.TryGetProperty(TraceFormat.Field.MmPerPixelX, out _));
            Assert.Null(TraceFormat.MillimetresPerUnit(json.RootElement));
            Assert.Null(Reopen.From(path).Take!.ActiveArea);
        }
        finally
        {
            Clear(path);
        }
    }

    [Fact]
    public void The_file_says_it_is_the_version_that_carries_the_area()
    {
        var path = Written(Made(ActiveArea.From(Measured)));

        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));

            Assert.True(json.RootElement.GetProperty(TraceFormat.Field.Version).GetInt32() >= 8);
        }
        finally
        {
            Clear(path);
        }
    }

    [Fact]
    public void A_version_seven_trace_reopens_with_no_area()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"sfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "seven.json");

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
            Assert.Null(back.Take!.ActiveArea);
        }
        finally
        {
            Clear(path);
        }
    }

    // ---- the capture --------------------------------------------------------

    [Fact]
    public void A_take_begun_through_a_device_carries_that_devices_area()
    {
        var area = ActiveArea.From(Measured);

        var capturing = new Capturing(() => new InkTransform(1, 1, 0, 0));
        capturing.Choose(Gestures.All[0],
            new Capturing.Device(InputApi.WintabDigitizer, 32767, "test", area));
        capturing.Arm(null);
        capturing.Took(
            new Reading(X: 10, Y: 100, Pressure: 600, At: 6_200, Arrived: 6_200),
            overThePad: true);

        Assert.Equal(area, capturing.Take!.ActiveArea);
    }

    // ---- both kinds of recording --------------------------------------------

    [Fact]
    public void The_scale_a_reader_asks_for_is_the_one_this_recorder_wrote()
    {
        var path = Written(Made(ActiveArea.From(Measured)));

        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));

            var scale = TraceFormat.MillimetresPerUnit(json.RootElement);

            Assert.NotNull(scale);
            Assert.Equal(349.0 / 3840, scale.Value.X, 6);
            Assert.Equal(195.0 / 3240, scale.Value.Y, 6);
        }
        finally
        {
            Clear(path);
        }
    }

    /// <summary>
    /// A recording in the tablet's own counts, as a tool that reads the driver directly writes
    /// it, in the shape the corpus defines.
    /// </summary>
    [Fact]
    public void A_recording_in_the_tablets_own_counts_is_refused_rather_than_drawn_as_pixels()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"sfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "tablet.json");

        File.WriteAllText(path, """
            {
              "format": "stroke-field-guide/take",
              "formatVersion": 8,
              "gesture": "freeform",
              "device": { "tablet": "A tablet", "fullScalePressure": 8191 },
              "coordinates": {
                "space": "tablet", "units": "digitizer counts",
                "maxX": 62500, "maxY": 39062, "widthMm": 224.0, "heightMm": 126.0
              },
              "columns": ["arrived", "x", "y", "pressure"],
              "strokes": [ { "readings": [[0, 31000, 20000, 400], [6200, 31010, 20010, 500]] } ]
            }
            """);

        try
        {
            var back = Reopen.From(path);

            Assert.Null(back.Take);
            Assert.Contains("digitizer counts", back.Why);
        }
        finally
        {
            Clear(path);
        }
    }

    /// <summary>
    /// What the writer produces, against the schema the corpus publishes. The file used to pass
    /// whatever it said, because nothing checked it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void What_is_written_is_valid_against_the_corpus_schema(bool withArea)
    {
        var schemaPath = SchemaPath();

        Assert.True(schemaPath is not null,
            "no schema found. corpus/ is a submodule: git submodule update --init");

        var schema = Schema.Value;
        var path = Written(Made(withArea ? ActiveArea.From(Measured) : null));

        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));

            var result = schema.Evaluate(json.RootElement,
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

    /// <summary>Loaded once: the library refuses to register one schema id twice in a process.</summary>
    private static readonly Lazy<Json.Schema.JsonSchema> Schema = new(
        () => Json.Schema.JsonSchema.FromText(File.ReadAllText(SchemaPath()!)));

    private static string? SchemaPath()
    {
        for (var at = new DirectoryInfo(AppContext.BaseDirectory); at is not null; at = at.Parent)
        {
            var here = Path.Combine(at.FullName, "corpus", "schema", "take.schema.json");

            if (File.Exists(here)) return here;
        }

        return null;
    }
}
