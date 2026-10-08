using WinPenKit;

namespace StrokeRecorder;

/// <summary>
/// How big the tablet is, and how big a desktop pixel of a reading is on it, as a trace states
/// it.
/// </summary>
/// <remarks>
/// <para>
/// The readings carry <c>x</c> and <c>y</c> in desktop pixels and nothing about what a pixel
/// is, so two recordings of the same gesture on two tablets, or on one tablet mapped two ways,
/// cover different distances for the same count and the file cannot say so. This is the missing
/// scale.
/// </para>
/// <para>
/// <b>The two scales are separate because they are not the same.</b> A tablet mapped across a
/// desktop of another shape is stretched, so a pixel moved sideways and a pixel moved down are
/// different distances on the tablet. Measured on the machine this was written on: a 349 x 195
/// mm surface mapped to 3840 x 3240 pixels, 0.091 mm across and 0.060 mm down. A single
/// "millimetres per pixel" would be right in one direction and wrong in the other, so
/// <see cref="Millimetres"/> takes the two components rather than a length in pixels.
/// </para>
/// <para>
/// Absent, not zero, where the backend cannot say -- a tablet with no size is a claim, and a
/// backend that did not ask has made none.
/// </para>
/// </remarks>
/// <param name="WidthMm">The whole sensing surface, horizontally.</param>
/// <param name="HeightMm">The whole sensing surface, vertically.</param>
/// <param name="MappedWidthMm">The part of it the driver mapped to the desktop.</param>
/// <param name="MappedHeightMm">The part of it the driver mapped to the desktop.</param>
/// <param name="MmPerPixelX">Millimetres on the tablet for one pixel of <c>x</c>.</param>
/// <param name="MmPerPixelY">Millimetres on the tablet for one pixel of <c>y</c>.</param>
public readonly record struct ActiveArea(
    double WidthMm,
    double HeightMm,
    double MappedWidthMm,
    double MappedHeightMm,
    double MmPerPixelX,
    double MmPerPixelY)
{
    /// <summary>What a session said about its tablet, as a trace carries it.</summary>
    public static ActiveArea From(PenPhysicalArea area) => new(
        area.DeviceWidthMm,
        area.DeviceHeightMm,
        area.MappedWidthMm,
        area.MappedHeightMm,
        area.MillimetresPerPixelX,
        area.MillimetresPerPixelY);

    /// <summary>
    /// How far a movement of this many pixels, each way, is on the tablet.
    /// </summary>
    /// <remarks>
    /// Each component is scaled by its own axis before they are combined. Scaling the length in
    /// pixels by either axis's figure is wrong whenever the two differ.
    /// </remarks>
    public double Millimetres(double dxPixels, double dyPixels) =>
        Math.Sqrt(
            Math.Pow(dxPixels * MmPerPixelX, 2) +
            Math.Pow(dyPixels * MmPerPixelY, 2));

    /// <summary>The surface in words, with the mapped part where it is a different size.</summary>
    public string Describe()
    {
        var whole = $"{WidthMm:0.#} × {HeightMm:0.#} mm";

        // Half a millimetre is inside the rounding of a driver's resolution; more than that is a
        // mapping somebody chose, and the figure that matters for distance.
        return Math.Abs(MappedWidthMm - WidthMm) > 0.5 || Math.Abs(MappedHeightMm - HeightMm) > 0.5
            ? $"{whole} (mapped {MappedWidthMm:0.#} × {MappedHeightMm:0.#} mm)"
            : whole;
    }
}
