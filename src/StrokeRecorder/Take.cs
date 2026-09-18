using StrokeKit.Strokes;
using StrokeKit.Surfaces;
using WinPenKit;

namespace StrokeRecorder;

/// <summary>Where a take has got to.</summary>
public enum Capture
{
    /// <summary>Nothing armed. Drawing on the pad does nothing.</summary>
    Idle,

    /// <summary>Armed and waiting. The next reading in contact starts the take.</summary>
    Armed,

    /// <summary>The tip is down and readings are being kept.</summary>
    Drawing,

    /// <summary>
    /// The tip lifted, and the take is not over. The next contact starts another stroke.
    /// </summary>
    /// <remarks>
    /// Only reachable on a gesture that asks for many strokes. Every other gesture treats the
    /// lift as the end, which is what <see cref="Taken"/> is, and the difference between the
    /// two states is the whole of what "keep recording" means here.
    /// </remarks>
    Between,

    /// <summary>The tip lifted. There is a stroke to look at.</summary>
    Taken,
}

/// <summary>
/// One contact inside a take: tip down to tip up, and why it ended.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is what the file calls a stroke</b>, because that is what it is once it is
/// finished. It is called a contact here because while the pen is down it is not finished
/// yet, and <see cref="Strokes.Stroke"/> is the library's name for the finished form.
/// </para>
/// <para>
/// A take used to be exactly one of these and the distinction did not need a name. It does
/// now: a gesture that asks for many strokes produces one take holding several, and the
/// boundaries between them are data -- what the hand did between two strokes is a question
/// somebody will want to ask of these recordings.
/// </para>
/// </remarks>
public sealed class Contact
{
    private readonly List<Reading> _readings = [];
    private readonly List<Reading> _approach = [];
    private readonly List<Reading> _departure = [];

    public IReadOnlyList<Reading> Readings => _readings;

    public int Count => _readings.Count;

    /// <summary>
    /// The pen in the air on the way down, for up to a quarter of a second before it landed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A tablet reports the pen while it hovers, and until now this window threw all of that
    /// away. It is the only record of <b>how the pen arrived</b>: how fast it was travelling
    /// and how it was held, in the moments before the pressure sensor had anything to say.
    /// </para>
    /// <para>
    /// This exists to make one specific question answerable. A pen driven at the tablet
    /// quickly lands hard, and the first few milliseconds of a fast stroke read much heavier
    /// than the rest of it. Whether that spike is predictable from the approach -- and so
    /// whether a renderer could do something about it -- cannot be asked of a recording that
    /// begins at the moment of contact.
    /// </para>
    /// <para>
    /// <b>It used to come back empty, and that was this recorder's fault.</b> The paragraph
    /// here said the device stopped reporting the hovering pen before a landing, for 264 ms to
    /// 3.9 seconds, and that nothing in software could be done about it. Every word of that was
    /// wrong. The device reports continuously; the window that decided what fell inside the
    /// quarter second was measuring with the pen's own timestamp, which advances a flat 4.166
    /// ms per packet whatever the elapsed time and jumps forward at each landing. A reading
    /// genuinely 20 ms old measured two seconds old and was discarded.
    /// <para>
    /// Measured on a clock that measures time, every landing of eight was inside the window
    /// and the approaches came back 37 to 42 readings long. So this array now records what the
    /// pen did on the way down, which is what it was added for, and
    /// <see cref="SinceLastSeen"/> says how long the gap really was.
    /// </para>
    /// </para>
    /// </remarks>
    public IReadOnlyList<Reading> Approach => _approach;

    /// <summary>The pen in the air after the lift, for up to a quarter of a second.</summary>
    /// <remarks>
    /// The other end of the same question. A stroke's last readings fall away as the pen
    /// leaves, and how quickly it left is not in them.
    /// </remarks>
    public IReadOnlyList<Reading> Departure => _departure;

    /// <summary>Why this stroke ended, for a reader who has only the file.</summary>
    public string EndedBy { get; set; } = "the pen lifted";

    public void Add(Reading reading) => _readings.Add(reading);

    /// <summary>
    /// How long before this stroke landed the pen was last reported in the air, in
    /// microseconds, or null if it was not reported at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The number that turns an empty <see cref="Approach"/> from a mystery into a fact. An
    /// empty approach has two quite different causes -- the pen was out of range, or the
    /// recorder failed to keep what it was given -- and without this they look identical in
    /// the file. Three takes were recorded before anybody could say which had happened, and
    /// the guess was wrong twice.
    /// </para>
    /// <para>
    /// A large gap with an empty approach means the pen was gone and there was nothing to
    /// keep. A <b>small</b> gap with an empty approach is a fault, and would be the thing to
    /// go and look at.
    /// </para>
    /// </remarks>
    public long? SinceLastSeen { get; private set; }

    /// <summary>
    /// The last reading of the pen in the air before this stroke, however old.
    /// </summary>
    /// <remarks>
    /// Kept separately from <see cref="Approach"/>, which holds only what fell inside the
    /// window and is empty for every gap longer than it. This is the one the measurement
    /// needs: how far the pen travelled between the last time it was seen and the moment it
    /// landed. Across every gap recorded so far that distance is under a pixel, and the claim
    /// this repository now makes is that it always will be.
    /// </remarks>
    public Reading? LastAirborne { get; private set; }

    public void Approaching(IEnumerable<Reading> readings, long? sinceLastSeen, Reading? lastAirborne)
    {
        _approach.Clear();
        _approach.AddRange(readings);

        SinceLastSeen = sinceLastSeen;
        LastAirborne = lastAirborne;
    }

    public void Departing(Reading reading) => _departure.Add(reading);

    /// <summary>The readings as a stroke, for anything that draws one.</summary>
    public Stroke? Stroke => _readings.Count == 0 ? null : new Stroke(_readings);

    /// <summary>
    /// How long the tip was down, in milliseconds, <b>on the pen's own clock</b>.
    /// </summary>
    /// <remarks>
    /// <b>Not elapsed time.</b> The pen's timestamp is a packet counter: it advances a flat
    /// 4.166 ms per packet delivered whatever the elapsed time, and on the hardware measured
    /// here it runs at 0.673 of real time. Anything wanting how long a recording actually
    /// took should ask <see cref="Timing.Spanned"/>, which answers on the host clock where
    /// there is one and says so where there is not.
    /// <para>
    /// Kept because the counter is real evidence about the device, and because the traces
    /// carry it. It is the interpretation as seconds that was wrong.
    /// </para>
    /// </remarks>
    public double Milliseconds =>
        _readings.Count < 2 ? 0 : (_readings[^1].At - _readings[0].At) / 1000.0;
}

/// <summary>
/// One attempt at one gesture: the readings, and enough about the circumstances to read them
/// again later.
/// </summary>
/// <remarks>
/// <para>
/// <b>The circumstances are frozen when the take starts, not when it is saved.</b> This is
/// carried over from the browser recorder, where it was learned the expensive way: by save
/// time the drawing area is on a different screen of the wizard, its box measures zero, and a
/// trace came out looking complete while being unable to place a single position. The same
/// trap is here in a different shape -- the pad is not on screen during review, and the view
/// it was drawn through is recentred whenever the window is resized.
/// </para>
/// <para>
/// So the transform is taken once, at the moment the pen goes down, and every reading in the
/// take is placed through that one. A window dragged to another monitor mid-stroke then
/// produces a take that is wrong in a visible, explainable way rather than one that is
/// half in each frame of reference.
/// </para>
/// </remarks>
public sealed class Take(Gesture gesture, InputApi api, int fullScalePressure, InkTransform placed)
{
    private readonly List<Contact> _contacts = [];
    private readonly List<Reading> _aloft = [];

    /// <summary>
    /// Every reading taken while the tip was up, unfiltered, when the take was asked for them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not evidence about a stroke and not meant to be. This exists because three takes in a
    /// row came back with an empty approach and the file could not say why: the pen being out
    /// of range and the recorder dropping what it was given produce exactly the same absence.
    /// Keeping the lot, including the packets that mean the pen has left, is what lets
    /// somebody look rather than infer.
    /// </para>
    /// <para>
    /// Off by default because it is the only thing here that records what happens <b>between</b>
    /// strokes at full rate, and a minute of hovering is more readings than the drawing.
    /// </para>
    /// </remarks>
    public IReadOnlyList<Reading> Aloft => _aloft;

    /// <summary>Whether any airborne reading was kept, so an empty list is not ambiguous.</summary>
    /// <remarks>
    /// Set by the keeping rather than declared up front. The switch was read once, when the
    /// take was armed, so turning it on after arming recorded nothing and the window said
    /// "0 readings" over a clock showing four seconds. Whether to keep them is the caller's
    /// decision on every reading; this only reports whether it ever said yes.
    /// </remarks>
    public bool KeepingAloft { get; private set; }

    /// <summary>Keeps one airborne reading.</summary>
    public void Keep(Reading reading)
    {
        _aloft.Add(reading);
        KeepingAloft = true;
    }

    /// <summary>Keeps the ones that arrived before this take existed.</summary>
    public void KeepAll(IEnumerable<Reading> readings)
    {
        var before = _aloft.Count;

        _aloft.AddRange(readings);

        if (_aloft.Count > before) KeepingAloft = true;
    }

    /// <summary>
    /// The strokes in this take, in the order they were drawn. Never empty once drawing has
    /// begun, and holding exactly one for every gesture but the many-stroke one.
    /// </summary>
    public IReadOnlyList<Contact> Contacts => _contacts;

    /// <summary>
    /// Every reading in the take, strokes run together.
    /// </summary>
    /// <remarks>
    /// A flattened view rather than the storage, because most of what reads a take asks
    /// distributional questions -- what pressures were seen, how far the pen leaned -- and
    /// those do not care where one stroke ended and the next began.
    /// <para>
    /// <b>Anything sequence-sensitive should read <see cref="Contacts"/> instead.</b> Two
    /// consecutive readings here can be a pen lift and a landing somewhere else entirely, and
    /// a speed or a direction computed across that pair is a measurement of nothing.
    /// </para>
    /// </remarks>
    public IReadOnlyList<Reading> Readings =>
        [.. _contacts.SelectMany(contact => contact.Readings)];

    public int Count => _contacts.Sum(contact => contact.Count);

    /// <summary>How many strokes the take holds.</summary>
    public int Strokes => _contacts.Count;

    /// <summary>The stroke being drawn, or the last one drawn.</summary>
    public Contact? Current => _contacts.Count == 0 ? null : _contacts[^1];

    /// <summary>Opens another stroke. Called when the tip goes down.</summary>
    public Contact Begin()
    {
        var contact = new Contact();

        _contacts.Add(contact);

        return contact;
    }

    /// <summary>Keeps a reading, on the stroke being drawn.</summary>
    public void Add(Reading reading) => (Current ?? Begin()).Add(reading);

    public Gesture Gesture => gesture;

    public InputApi Api => api;

    /// <summary>The device's full scale, which the readings do not carry and cannot.</summary>
    public int FullScalePressure => fullScalePressure;

    /// <summary>The transform the whole take was placed through. Frozen at the first contact.</summary>
    public InkTransform Placed => placed;

    /// <summary>When this take was recorded.</summary>
    /// <remarks>
    /// Settable, so that reopening a recording keeps the day it was made. Read-only with a
    /// <c>Now</c> default, a recording from last September reopened as one made today, and
    /// saving it again wrote that over the only record of when it happened. A trace is
    /// evidence and the date is part of it.
    /// </remarks>
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;

    /// <summary>
    /// How many polls contributed to this take.
    /// </summary>
    /// <remarks>
    /// Kept because readings and polls are two different clocks and the ratio is the only
    /// thing that says whether the device is reporting faster than this window wakes up. A
    /// recorder that never counted its own wake-ups could report a rate that was its frame
    /// rate wearing the tablet's name.
    /// </remarks>
    public int Polls { get; private set; }

    public void Polled() => Polls++;

    /// <summary>
    /// Readings that reached the recorder and were stored nowhere, counted by why.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is one path that drops a reading: the tip is down but the pen is not over the
    /// pad, so the take cannot have it and the airborne record will not take it either. It
    /// was invisible until it mattered, and then it mattered a great deal -- the airborne
    /// record stops several hundred milliseconds before every landing and nobody could say
    /// whether that was the device going quiet or this window throwing readings away.
    /// </para>
    /// <para>
    /// A tally rather than the readings themselves. If it is non-zero the next question is
    /// which ones, and that is what the airborne switch is for.
    /// </para>
    /// </remarks>
    public int DroppedOffPad { get; private set; }

    public void DroppedOne() => DroppedOffPad++;

    /// <summary>
    /// Readings that arrived with the tip still down after the recording had been stopped.
    /// </summary>
    /// <remarks>
    /// Not a fault and not a loss: the recording was over, and somebody finishing the stroke
    /// they were in the middle of is not asking for it to be kept. But they were handed to
    /// this window, so they have to be accounted for somewhere, or the take reports more
    /// readings received than stored and the difference looks like data going missing. It did
    /// exactly that on the first take recorded after the stop behaviour changed.
    /// </remarks>
    public int AfterTheStop { get; private set; }

    public void OneAfterTheStop() => AfterTheStop++;

    /// <summary>
    /// Airborne readings deliberately not kept, because the airborne record was not asked for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A column of its own, because without one these readings were handed over, counted as
    /// routed, and then appeared in nothing — so a perfectly ordinary recording with the
    /// airborne record switched off reported that most of its readings had gone missing. On a
    /// take of 137 readings with 18 in strokes, 116 were reported unaccounted for; every one
    /// of them was a hovering reading left out on purpose.
    /// </para>
    /// <para>
    /// <b>Left out is not the same as lost</b>, and an instrument that cannot tell them apart
    /// is not much use for the question this one exists to answer.
    /// </para>
    /// </remarks>
    public int LeftOut => _airborneNotKept == 0
        ? 0
        : Math.Max(0, _airborneNotKept - Alongside);

    /// <summary>
    /// Airborne readings that were kept after all, beside the stroke they belong to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The other half of the number above, and the reason it had to be split. A hovering
    /// reading is counted as not kept the moment it arrives, and may then be adopted into a
    /// stroke's approach or departure when the tip goes down — so a take reporting "498
    /// airborne and not kept" had 233 of them sitting in the file, and said so nowhere.
    /// </para>
    /// <para>
    /// Zero while the airborne record is being kept, because then every airborne reading is in
    /// <see cref="Aloft"/> and an approach is a second copy of one already counted. Counting it
    /// again here would make the ledger add up to more than arrived.
    /// </para>
    /// </remarks>
    public int KeptAlongside => _airborneNotKept == 0 ? 0 : Math.Min(Alongside, _airborneNotKept);

    /// <summary>Every reading held as some stroke's approach or departure.</summary>
    private int Alongside => _contacts.Sum(each => each.Approach.Count + each.Departure.Count);

    /// <summary>
    /// Airborne readings handed over while the airborne record was switched off.
    /// </summary>
    /// <remarks>
    /// Raw, and split into <see cref="LeftOut"/> and <see cref="KeptAlongside"/> for reporting.
    /// The two always add back to this, so the ledger balances however the split falls.
    /// </remarks>
    private int _airborneNotKept;

    /// <summary>One airborne reading, arriving while none are being kept.</summary>
    /// <remarks>
    /// Whether it stays unkept is not known yet: the tip may come down within the approach
    /// window and adopt it. That is why this counts, and the split above reports.
    /// </remarks>
    public void OneLeftOut() => _airborneNotKept++;

    /// <summary>
    /// Restores the counts a take was written with, for one read back from a file.
    /// </summary>
    /// <remarks>
    /// These three are counted as readings arrive and cannot be recomputed from what was
    /// kept -- that is the whole point of them, since the interesting case is a reading that
    /// was handed over and stored nowhere. A take reopened without them would show a ledger
    /// claiming nothing was ever handed over, which is the one reading of it that is never
    /// true.
    /// </remarks>
    public void Reopened(
        int routed, int offPad, int afterTheStop, int leftOut = 0, int keptAlongside = 0)
    {
        Routed = routed;
        DroppedOffPad = offPad;
        AfterTheStop = afterTheStop;

        // The raw count, from the two halves the file reports. The split is recomputed from
        // the contacts that were just read back, so a file written before this existed still
        // restores correctly: its whole figure lands in the raw counter and splits itself.
        _airborneNotKept = leftOut + keptAlongside;
    }

    /// <summary>Every reading the recorder was handed while this take was open.</summary>
    public int Routed { get; private set; }

    /// <summary>
    /// The most recent reading of any kind, in contact or not, on the pen's own clock.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Milliseconds"/>, which is the span of <b>contact</b> and is
    /// what the file reports. This is what a clock on the wall should show while a take is
    /// running: a many-stroke take is still recording between its strokes, and a clock that
    /// froze the moment the tip lifted told somebody their recording had stopped when it had
    /// not.
    /// </remarks>
    public long LastSeen { get; private set; }

    /// <summary>The first reading of any kind, which on a many-stroke take is the arming.</summary>
    public long? FirstSeen { get; private set; }

    /// <summary>
    /// What the session says it was given, taken when the take started and when it ended.
    /// </summary>
    /// <remarks>
    /// The count the recorder cannot take for itself. Every take reconciles internally --
    /// readings handed over equals contact plus airborne plus dropped -- and that says nothing
    /// about readings that were never handed over. These come from beneath the session's own
    /// filtering, so the difference between them and <see cref="Routed"/> is the session's
    /// doing rather than this window's.
    /// </remarks>
    public (long FromDriver, long OutsideRegion, long Delivered)? Counted { get; set; }

    /// <summary>The same two ends on the host's clock, where the readings carried one.</summary>
    /// <remarks>
    /// Kept beside <see cref="FirstSeen"/> rather than instead of it. The pen's counter is
    /// evidence about the device and the traces carry it; what it is not is a measure of how
    /// long anything took. See <see cref="Lasted"/>.
    /// </remarks>
    public long LastArrived { get; private set; }

    public long? FirstArrived { get; private set; }

    /// <summary>Whether any reading carried a host stamp at all.</summary>
    /// <remarks>
    /// Asked as "was anything ever nonzero", the same question <c>Timing.Available</c> asks,
    /// and not "is the first one nonzero". A take's first arrival is legitimately zero once
    /// the format has rebased it, so a zero cannot be read as a missing stamp on its own.
    /// </remarks>
    private bool _stamped;

    public void Routing(Reading reading)
    {
        Routed++;

        FirstSeen ??= reading.At;

        if (reading.At > LastSeen) LastSeen = reading.At;

        FirstArrived ??= reading.Arrived;

        if (reading.Arrived > LastArrived) LastArrived = reading.Arrived;

        _stamped |= reading.Arrived != 0;
    }

    /// <summary>
    /// How long the take has been running, in milliseconds, hover included, on the pen's clock.
    /// </summary>
    /// <remarks>
    /// From the <b>first reading the take saw</b>, not the first contact. On a many-stroke
    /// take that is the moment somebody armed it, which is when they think the recording
    /// started and is therefore what a clock should agree with.
    /// <para>
    /// <b>Not elapsed time</b>, for the reason <see cref="Milliseconds"/> gives. Anything
    /// showing this to a reader as a length of time wants <see cref="Lasted"/>.
    /// </para>
    /// </remarks>
    public double Running => FirstSeen is { } from ? (LastSeen - from) / 1000.0 : 0;

    /// <summary>
    /// How long the take lasted and on which clock, hover included, end to end.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same span <see cref="Running"/> covers -- everything the take saw, from arming to
    /// the last reading -- answered on the host clock where the readings carried one. That is
    /// the number to show anybody, because it is the only one of the two that is in seconds.
    /// </para>
    /// <para>
    /// The clock comes back with the number so that a caller cannot print one without the
    /// other by accident, which is how the pen's counter reached six separate readouts.
    /// <see cref="Timing.Said"/> renders the pair for a one-line readout.
    /// </para>
    /// </remarks>
    public (double Seconds, Clock On) Lasted
    {
        get
        {
            if (_stamped && FirstArrived is { } arrived)
            {
                return ((LastArrived - arrived) / 1e6, Clock.Host);
            }

            // Nothing was routed through here, so this take was read back from a file rather
            // than recorded. Its readings carry the clock instead.
            if (Routed == 0)
            {
                var held = Timing.Spanned(Readings.Count > 0 ? Readings : Aloft);

                if (held.On != Clock.None) return held;
            }

            return FirstSeen is { } seen
                ? ((LastSeen - seen) / 1e6, Clock.Pen)
                : (0, Clock.None);
        }
    }

    /// <summary>When the recording was stopped, or null while it is still going.</summary>
    public DateTimeOffset? StoppedAt { get; set; }

    /// <summary>
    /// How long this take has been recording, on the wall clock, from arming to stopping.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately <b>not</b> the pen's clock. <see cref="Running"/> advances only when
    /// readings arrive, so it stalls whenever the pen is held still -- and a stalled clock is
    /// read by anybody looking at it as "the recording has stopped". It had not. Reported from
    /// the pad, and the confusion is entirely the display's fault: a timer is the one thing on
    /// this screen people take as the answer to "is it on?".
    /// </para>
    /// <para>
    /// The pen's clock remains the right measure of a stroke and of a take's contents, and is
    /// what the file carries. This is for the person watching.
    /// </para>
    /// </remarks>
    public double Recording => ((StoppedAt ?? DateTimeOffset.Now) - At).TotalMilliseconds;

    /// <summary>Whether anything at all was recorded: a stroke, or the pen in the air.</summary>
    /// <remarks>
    /// A take with no strokes is ordinarily nothing. It is <b>not</b> nothing when the
    /// airborne switch is on: a recording of the pen hovering and never touching down is a
    /// legitimate thing to want, and is exactly what was wanted the day this was written.
    /// </remarks>
    public bool Holds => Count > 0 || Aloft.Count > 0;

    /// <summary>Why the take ended, for a reader who has only the file.</summary>
    /// <remarks>
    /// The take's own ending, which is not the same as the last stroke's. A many-stroke take
    /// ends because somebody stopped it; the stroke before that ended because the pen lifted.
    /// </remarks>
    public string EndedBy { get; set; } = "the pen lifted";

    /// <summary>
    /// What made it, named by the person who was there.
    /// </summary>
    /// <remarks>
    /// Not discoverable. WinPenKit can say which API answered and what its full scale is; it
    /// cannot say which tablet is on the desk or which driver build is installed, and both
    /// are what somebody comparing two recordings actually wants. So they are typed in, and
    /// a take with them empty says so rather than guessing.
    /// </remarks>
    public string Tablet { get; set; } = "";

    public string Driver { get; set; } = "";

    /// <summary>What the hand was asked to do. Starts as the gesture's, and can be edited.</summary>
    public string Intent { get; set; } = gesture.Intent;

    /// <summary>What the backend normalises and what it leaves alone.</summary>
    public string Conventions { get; set; } = "";

    public bool Named => Tablet.Length > 0 && Driver.Length > 0;

    /// <summary>
    /// How long the take lasted, in milliseconds, from the pen's own clock.
    /// </summary>
    /// <remarks>
    /// The pen's clock and not the recorder's: a reading arrives when the poll happens to run,
    /// which is up to a frame after the pen reported it, and a duration measured off arrivals
    /// is a duration measured off this application's scheduler.
    /// </remarks>
    /// <remarks>
    /// <para>
    /// End to end across the whole take, so on a many-stroke take this includes the time the
    /// pen spent off the tablet between strokes. That is deliberate: it is the take's
    /// duration, and the pauses are part of what was recorded.
    /// </para>
    /// </remarks>
    public double Milliseconds
    {
        get
        {
            var first = _contacts.FirstOrDefault(contact => contact.Count > 0);
            var last = _contacts.LastOrDefault(contact => contact.Count > 0);

            return first is null || last is null
                ? 0
                : (last.Readings[^1].At - first.Readings[0].At) / 1000.0;
        }
    }

    /// <summary>Every stroke in the take, for anything that draws them.</summary>
    public IEnumerable<Stroke> Drawable =>
        _contacts.Select(contact => contact.Stroke).OfType<Stroke>();

    /// <summary>
    /// The take as one stroke, where it holds exactly one.
    /// </summary>
    /// <remarks>
    /// Null on a many-stroke take rather than the strokes run together, which would be a
    /// stroke that travels between the lift and the landing and was never drawn.
    /// </remarks>
    public Stroke? Stroke => _contacts.Count == 1 ? _contacts[0].Stroke : null;

    /// <summary>
    /// What the recording is, in one line, for somebody who was not holding the pen.
    /// </summary>
    public string Describe()
    {
        // A take with no strokes used to describe itself as "0 readings over 0 ms" beside a
        // clock reading four seconds, because both numbers are about contact and nothing had
        // touched down. What was actually recorded is the pen in the air, and that is what it
        // should say.
        var what = Strokes switch
        {
            0 when Aloft.Count > 0 => $"No strokes, {Aloft.Count} readings of the pen in the air",
            0 => "Nothing",
            1 => $"{Count} readings",
            _ => $"{Strokes} strokes, {Count} readings",
        };

        // One clock for both branches. This used to pick Running or Milliseconds by whether
        // anything had touched down -- two different spans, and both of them the pen's counter
        // presented as milliseconds, so the sentence read the same whichever it was.
        var (seconds, on) = Lasted;

        return $"{what} over {Timing.Said(seconds, on)} through {Api}, "
            + $"full scale {FullScalePressure}";
    }
}
