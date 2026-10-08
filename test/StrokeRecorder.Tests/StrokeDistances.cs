using StrokeKit.Strokes;
using StrokeKit.Surfaces;
using WinPenKit;

namespace StrokeRecorder.Tests;

/// <summary>
/// That a stroke's length and speed are reported in millimetres, per axis, and only where they can be.
/// </summary>
/// <remarks>
/// The area is the one measured on the machine this was written on, a 349 x 195 mm tablet mapped
/// across 3840 x 3240 pixels: not square, so a pixel moved across and a pixel moved down are
/// different distances and a test on a square mapping would pass for a single scale.
/// </remarks>
public class StrokeDistances
{
    private static readonly ActiveArea Measured = ActiveArea.From(new PenPhysicalArea(
        349, 195, 349, 195, 3840, 3240));

    private static Reading At(double x, double y, long arrivedUs, uint pressure = 500) =>
        new(X: x, Y: y, Pressure: pressure, At: arrivedUs, Arrived: arrivedUs);

    private static Take Drawn(ActiveArea? area, params Reading[][] strokes)
    {
        var take = new Take(Gestures.All[0], InputApi.WintabDigitizer, 32767,
            new InkTransform(1, 1, 0, 0))
        {
            ActiveArea = area,
        };

        foreach (var stroke in strokes)
        {
            var contact = take.Begin();

            foreach (var reading in stroke) contact.Add(reading);
        }

        return take;
    }

    [Fact]
    public void A_stroke_across_is_scaled_by_the_horizontal_figure_and_one_down_by_the_vertical()
    {
        var across = Distances.Of([At(0, 0, 0), At(1000, 0, 1_000_000)], Measured);
        var down = Distances.Of([At(0, 0, 0), At(0, 1000, 1_000_000)], Measured);

        Assert.Equal(1000 * 349.0 / 3840, across.PathMm, 6);
        Assert.Equal(1000 * 195.0 / 3240, down.PathMm, 6);

        // The same number of pixels, and not the same distance.
        Assert.True(across.PathMm > down.PathMm * 1.4);
    }

    [Fact]
    public void Speed_is_path_over_host_time()
    {
        var stroke = Distances.Of([At(0, 0, 0), At(500, 0, 250_000), At(1000, 0, 500_000)], Measured);

        Assert.Equal(0.5, stroke.Seconds);
        Assert.Equal(stroke.PathMm / 0.5, stroke.MmPerSecond);
    }

    [Fact]
    public void Jitter_lengthens_the_path_and_not_the_chord()
    {
        // Out and back by ten pixels at every step, ending where a straight stroke would.
        var steady = Distances.Of([At(0, 0, 0), At(100, 0, 100_000), At(200, 0, 200_000)], Measured);
        var jittery = Distances.Of(
            [At(0, 0, 0), At(110, 0, 50_000), At(90, 0, 100_000), At(210, 0, 150_000), At(200, 0, 200_000)],
            Measured);

        Assert.Equal(steady.ChordMm, jittery.ChordMm, 9);
        Assert.True(jittery.PathMm > steady.PathMm);
    }

    [Fact]
    public void A_stroke_with_no_host_clock_has_a_length_and_no_speed()
    {
        // Arrived zero throughout; the pen's own stamp moves, and is a packet counter.
        var stroke = Distances.Of(
            [new Reading(X: 0, Y: 0, Pressure: 500, At: 0), new Reading(X: 100, Y: 0, Pressure: 500, At: 4166)],
            Measured);

        Assert.True(stroke.PathMm > 0);
        Assert.Null(stroke.Seconds);
        Assert.Null(stroke.MmPerSecond);
    }

    [Fact]
    public void A_stroke_of_one_reading_is_left_out()
    {
        var take = Drawn(Measured,
            [At(0, 0, 0)],
            [At(0, 0, 0), At(100, 0, 100_000)]);

        Assert.Single(Distances.Of(take, Measured));
    }

    // ---- the finding --------------------------------------------------------

    private static Finding? Said(Take take) =>
        Findings.For(take).FirstOrDefault(each =>
            each.Title.Contains("mm of path") || each.Title.Contains("not millimetres"));

    [Fact]
    public void A_take_with_a_size_says_its_length_and_speed_in_millimetres()
    {
        var finding = Said(Drawn(Measured,
            [At(0, 0, 0), At(1000, 0, 500_000), At(2000, 0, 1_000_000)]));

        Assert.NotNull(finding);
        Assert.Contains("mm of path", finding.Title);
        Assert.Contains("mm end to end", finding.Title);
        Assert.Contains("mm/s on the host clock", finding.Body);
    }

    [Fact]
    public void A_take_without_a_size_says_so_rather_than_saying_nothing()
    {
        var finding = Said(Drawn(null, [At(0, 0, 0), At(1000, 0, 500_000)]));

        Assert.NotNull(finding);
        Assert.Contains("not millimetres", finding.Title);
        Assert.DoesNotContain("mm of path", finding.Title);
    }

    [Fact]
    public void A_take_with_no_strokes_has_no_lengths()
    {
        // Findings.For is not asked of an empty take: the window says "nothing was recorded" first.
        Assert.Empty(Distances.Of(Drawn(Measured), Measured));
    }
}
