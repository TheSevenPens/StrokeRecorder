using WinPenKit;
using WinPenKit.Avalonia;

namespace StrokeFieldGuide.Recorder;

/// <summary>
/// The input APIs this tool can record through, and what each one is worth knowing for.
/// </summary>
/// <remarks>
/// <para>
/// Offered rather than chosen, because which API a recording came through is one of the
/// things a recording is evidence about. The same hand on the same tablet reports different
/// numbers through Wintab and through WM_POINTER, and a trace that cannot say which it used
/// cannot be compared with one that can.
/// </para>
/// <para>
/// <b>The pressure range is the reason this tool exists.</b> A reading is a raw count, and
/// the count means nothing without the device's full scale -- which the reading does not
/// carry. WinPenKit will say what it is, and the difference between backends is not
/// cosmetic: the Wintab sessions ask the device, and the pointer sessions declare 1024,
/// which is the API's fixed range rather than anything the hardware was asked about.
/// </para>
/// </remarks>
public sealed record Backend(InputApi Api, string Name, string Worth)
{
    public override string ToString() => Name;
}

public static class Pen
{
    /// <summary>Every backend this tool can open, whether or not it is available today.</summary>
    public static IReadOnlyList<Backend> All =>
    [
        new(InputApi.WintabDigitizer, "Wintab (digitizer)",
            "the device's own coordinates and its own pressure range, which is the highest "
            + "resolution anything here can report"),

        new(InputApi.WintabSystem, "Wintab (system)",
            "the same driver mapped onto screen coordinates, which is what most applications "
            + "using Wintab actually see"),

        new(InputApi.WmPointer, "WM_POINTER",
            "Windows' own pointer stack, declaring a fixed range of 1024 rather than asking "
            + "the device"),

        new(InputApi.AvaloniaPointer, "Avalonia pointer",
            "what a plain Avalonia application gets without doing anything special, and the "
            + "one most likely to disagree with the others"),
    ];

    /// <summary>Which of them can be opened on this machine right now.</summary>
    /// <remarks>
    /// Avalonia's own pointer input is always there; the rest depend on a driver being
    /// installed and a service running, so the list is asked for rather than assumed.
    /// </remarks>
    public static IReadOnlyList<InputApi> Available()
    {
        var available = PenSessionFactory.GetAvailableApis().ToList();

        available.Add(InputApi.AvaloniaPointer);

        return available;
    }

    /// <summary>
    /// Opens a session for a backend, attaching it to the control where a framework backend
    /// needs one.
    /// </summary>
    public static IPenSession Open(InputApi api, global::Avalonia.Controls.Control surface) =>
        api == InputApi.AvaloniaPointer
            ? new AvaloniaPointerSession(surface)
            : PenSessionFactory.Create(api);
}
