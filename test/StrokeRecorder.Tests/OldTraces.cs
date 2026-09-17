using StrokeKit.Strokes;

namespace StrokeRecorder.Tests;

/// <summary>
/// That both readers take a whole file whose layout is not this version's.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="StrokeFieldGuide.Tests"/> pins the column handling a row at a time. This is the
/// same generality at the level a reader actually meets it: a file on disk, opened by the
/// public entry point, with a shape no recording in the corpus has.
/// </para>
/// <para>
/// Both readers, in one place, because they are the two halves that used to know the format
/// separately and disagree about it. A fixture that only one of them was asked to read would
/// be testing half of the thing that broke.
/// </para>
/// </remarks>
public class OldTraces : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("trace-layouts").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Wrote(string name, string json)
    {
        var path = Path.Combine(_folder, name + ".json");

        File.WriteAllText(path, json);

        return path;
    }

    /// <summary>
    /// A version-one trace: readings at the top level, seven columns, no strokes array.
    /// </summary>
    /// <remarks>
    /// Twelve of these are in the corpus and are deliberately never rewritten — they are
    /// evidence, cited by number in the notes. So this shape has to stay readable for as long
    /// as they are kept, which is indefinitely.
    /// </remarks>
    private const string VersionOne = """
        {
          "format": "stroke-field-guide/take",
          "formatVersion": 1,
          "gesture": "single-stroke",
          "device": { "tablet": "An old tablet", "fullScalePressure": 1023 },
          "columns": ["at", "x", "y", "pressure", "lean", "azimuth", "twist"],
          "readings": [
            [0, 10.5, 20.5, 400, 15.5, 90.5, 0],
            [4166, 11.5, 21.5, 500, 16.5, 91.5, 0],
            [8332, 12.5, 22.5, 600, 17.5, 92.5, 0]
          ]
        }
        """;

    /// <summary>The same three readings, with the columns shuffled and two unknown ones added.</summary>
    private const string Shuffled = """
        {
          "format": "stroke-field-guide/take",
          "formatVersion": 6,
          "gesture": "single-stroke",
          "device": { "tablet": "Somebody else's tablet", "fullScalePressure": 8191 },
          "columns": ["pressure", "y", "x", "barometer", "at", "twist", "azimuth", "lean"],
          "strokes": [
            {
              "endedBy": "the tip lifted",
              "readings": [
                [400, 20.5, 10.5, 1013, 0, 0, 90.5, 15.5],
                [500, 21.5, 11.5, 1013, 4166, 0, 91.5, 16.5],
                [600, 22.5, 12.5, 1013, 8332, 0, 92.5, 17.5]
              ]
            }
          ]
        }
        """;

    [Fact]
    public void TheLibraryReadsAVersionOneTrace()
    {
        var strokes = Traces.Read(Wrote("version-one", VersionOne));

        var only = Assert.Single(strokes);

        Assert.Equal(3, only.Count);
        Assert.Equal(10.5, only[0].X);
        Assert.Equal(600u, only[2].Pressure);

        // No height column in version one, and the absence is real rather than a zero measured.
        Assert.All(only, reading => Assert.Equal(0, reading.Height));
    }

    [Fact]
    public void TheRecorderReopensAVersionOneTrace()
    {
        var reopened = Reopen.From(Wrote("version-one", VersionOne));

        Assert.Null(reopened.Why);

        Assert.NotNull(reopened.Take);

        var take = reopened.Take;

        Assert.Equal(3, take.Count);
        Assert.Equal(1023, take.FullScalePressure);
        Assert.Equal("An old tablet", take.Tablet);
    }

    /// <summary>
    /// The fixture that would have caught a reader counting slots rather than reading names.
    /// </summary>
    /// <remarks>
    /// Nothing this tool writes looks like this, which is exactly why it is here. A reader that
    /// assumed the current order would take the pressure as a timestamp and produce a stroke
    /// that looked fine until somebody measured it.
    /// </remarks>
    [Fact]
    public void BothReadersFollowAShuffledColumnList()
    {
        var path = Wrote("shuffled", Shuffled);

        var only = Assert.Single(Traces.Read(path));
        var reopened = Reopen.From(path);

        Assert.NotNull(reopened.Take);

        var take = reopened.Take;

        foreach (var readings in new[] { only, take.Readings })
        {
            Assert.Equal(3, readings.Count);

            Assert.Equal(10.5, readings[0].X);
            Assert.Equal(20.5, readings[0].Y);
            Assert.Equal(400u, readings[0].Pressure);
            Assert.Equal(15.5, readings[0].Lean);
            Assert.Equal(0, readings[0].At);

            Assert.Equal(8332, readings[2].At);
            Assert.Equal(600u, readings[2].Pressure);
        }
    }

    /// <summary>A file that is not a trace says so, rather than throwing.</summary>
    [Fact]
    public void RefusesAFileWithNoColumns()
    {
        var path = Wrote("not-a-trace", """{ "format": "something else" }""");

        Assert.NotNull(Reopen.From(path).Why);
        Assert.Throws<InvalidOperationException>(() => Traces.Read(path));
    }

    /// <summary>Columns that carry no position are not a stroke, whatever else they carry.</summary>
    [Fact]
    public void RefusesColumnsWithNoPosition()
    {
        var path = Wrote("no-position", """
            {
              "columns": ["at", "lean", "azimuth", "twist"],
              "strokes": [ { "readings": [[0, 1, 2, 3]] } ]
            }
            """);

        Assert.NotNull(Reopen.From(path).Why);
        Assert.Throws<InvalidOperationException>(() => Traces.Read(path));
    }
}
