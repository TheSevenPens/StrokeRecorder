using Avalonia;

namespace StrokeRecorder;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // No self-test here. The Lab is a checked artefact and this is a tool: what it
        // produces is recordings, and whether they are any good is a question for the person
        // who drew them rather than for a check.

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // UseWin32().UseSkia() rather than UsePlatformDetect(), which ships in Avalonia.Desktop
    // and pulls backends this Windows-only application cannot load. UseHarfBuzz() because
    // Avalonia 12 split text shaping out of Skia and the application throws without it.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseWin32()
        .UseSkia()
        .UseHarfBuzz()
        .LogToTrace();
}
