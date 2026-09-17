using System.Text.Json;

namespace StrokeRecorder;

/// <summary>
/// The few answers that are the same every time this window opens.
/// </summary>
/// <remarks>
/// <para>
/// The tablet on the desk and the driver installed on it change perhaps twice a year, and the
/// window asked for both on every launch. Typing an answer nobody has changed is the kind of
/// friction that ends with the field left blank, which is exactly the finding review raises.
/// </para>
/// <para>
/// Kept beside the takes rather than in the registry or somewhere under AppData, because a
/// reader who wants to know what this window remembers should be able to find it in the
/// folder they already know about, read it, and delete it.
/// </para>
/// <para>
/// Nothing here is evidence. A take records the tablet it names; this only saves somebody
/// typing it again, and a take made after the tablet changed says whatever was typed then.
/// </para>
/// </remarks>
public sealed record Remembered(string Tablet = "", string Driver = "", double Diameter = 25)
{
    private static string Where => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "StrokeFieldGuide", "recorder.json");

    public static Remembered Read()
    {
        try
        {
            return File.Exists(Where)
                ? JsonSerializer.Deserialize<Remembered>(File.ReadAllText(Where)) ?? new()
                : new();
        }
        catch
        {
            // A settings file that cannot be read is not worth a word to anybody. The window
            // opens with nothing remembered, which is where it started.
            return new();
        }
    }

    public void Write()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Where)!);

            File.WriteAllText(Where,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Nor is one that cannot be written. The take is already saved by the time this
            // runs, and losing a convenience is not worth interrupting anybody over.
        }
    }
}
