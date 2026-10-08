# StrokeRecorder

Records what a pen actually reports, so that a claim about a stroke can be checked against one
somebody drew rather than one somebody generated.

It is where [StrokeCorpus](https://github.com/TheSevenPens/StrokeCorpus) comes from, and it is
the instrument the measurements in
[StrokeFieldGuide](https://github.com/TheSevenPens/StrokeFieldGuide) were taken with.

## What it does

Five steps, in one window.

1. **Pre-flight.** Pick a backend — Wintab digitizer, Wintab system, WM_POINTER, or Avalonia's
   own pointer input — and draw once on a strip to prove the pen is reporting. The device's
   full-scale pressure, its conventions, the packet rate and the queue depth are all on screen,
   because a recording made through a backend that was quietly reporting nothing looks exactly
   like a recording of a still hand.
2. **Gesture.** Choose what the hand is being asked to do. A brief, so a take is evidence about
   something rather than a scribble.
3. **Record.** Space to arm, space to stop, escape to discard.
4. **Analysis.** What was captured: every reading, the two clocks, the approach and departure,
   and a ledger that has to balance.
5. **Save.** Named, and written as a trace.

## The trace format

Each recording is one JSON file, `stroke-field-guide/take`, currently version 8, which also says what `x` and `y` are and, on Wintab, how big the tablet is in millimetres. The format is
documented in StrokeCorpus:
[FORMAT.md](https://github.com/TheSevenPens/StrokeCorpus/blob/main/FORMAT.md) (also at
[`corpus/FORMAT.md`](corpus/FORMAT.md) in a recursive clone). It covers the fields, the
columns, the version history, and what a reader must not assume.

The code that defines it, and that the writer and both readers share, is
[`TraceFormat.cs`](vendor/StrokeKit/src/StrokeKit/Strokes/TraceFormat.cs) in StrokeKit.

## Why the backend is a choice rather than a setting

The same hand on the same tablet reports different numbers through different APIs, and a
recording that cannot say which it used cannot be compared with one that can. The Wintab
sessions ask the device its pressure range; the pointer sessions declare a fixed 1024, which is
the API's range rather than anything the hardware was asked about. That difference is not
cosmetic and it is recorded in every trace.

## What the positions mean

`x` and `y` are desktop pixels, and where the backend can ask the driver, the trace also says how
many millimetres a pixel is on each axis and how big the tablet is. Two things about that are easy
to get wrong, and both have been, once:

- **The size is the driver's claim, not a measurement.** Nothing here has checked it against a
  ruler. And the *mapped* figures are the context's rectangles: a driver that crops the tablet to
  one display delivers only part of it while they still describe all of it. The millimetres a pixel
  stay right; the extent may not.
- **The recorder cannot show you a tablet's edges.** It keeps only readings over its pad, which is a
  part of the screen, so the corners are unreachable ([#16](https://github.com/TheSevenPens/StrokeRecorder/issues/16)).

To find out what a given tablet's coordinates really are, use the sweep probe that ships with
WinPenKit, which compares the pen's whole range with the cursor in the tablet's own counts, and read
its notes first: [`Docs/PEN-SWEEP.md`](https://github.com/TheSevenPens/WinPenKit/blob/main/Docs/PEN-SWEEP.md).
They include how a probe that was not per-monitor DPI aware produced a convincing wrong answer about
this very question.

## The two clocks

Every reading carries two timestamps and they measure different things.

`at` is the pen's own. On the tablet measured here it is **a packet counter rather than a
clock**: it advances a flat 4.166 ms per packet delivered, runs at 0.673 of real time, and
resynchronises at every contact transition.

`arrived` is this application's clock, stamped when a batch was taken off the driver's queue.
Every reading that came across together carries the same value **exactly**, so "did these two
packets reach the application in the same poll" is an equality check rather than an argument
about a timer's resolution.

A single clock cannot tell "the device stopped sending" from "the device stamped late". Five
explanations for a gap in recorded data died on that before the second one existed.

## The ledger

Every reading handed to the window is in exactly one column: in a stroke, in the airborne
record, airborne and kept beside a stroke, airborne and kept nowhere, off the pad, or arrived
after the stop. If they do not add up to what arrived, the recorder says so and calls it its
own fault rather than the pen's.

That is the whole point of the thing. **Left out is not the same as lost**, and an instrument
that cannot tell them apart is not much use for the question this one exists to answer.

## Building

Two submodules, one of which has a submodule of its own, so the clone is recursive:

```
git clone --recurse-submodules https://github.com/TheSevenPens/StrokeRecorder.git
cd StrokeRecorder
dotnet build StrokeRecorder.slnx
```

An existing clone catches up with `git submodule update --init --recursive`.

- `vendor/StrokeKit` is [StrokeKit](https://github.com/TheSevenPens/StrokeKit), the drawing
  core — readings, strokes, the trace format, and the pen stream this polls. WinPenKit is a
  submodule of *that*, which is why the clone has to be recursive.
- `corpus/` is StrokeCorpus. The tests read all 33 published recordings back through the same
  reader that writes them, which is how a round-trip fault that reached every published file
  was found; without the submodule those cases have nothing to run against.

Both are pinned. Neither is published as a NuGet package yet — the kit's API is still moving,
and a package boundary on an API that is still moving is a cost with no benefit.

**A note on Windows paths.** WinPenKit sits two submodules deep and carries some long
documentation paths, so a checkout under an already-deep directory can exceed `MAX_PATH`.
Cloning somewhere short is enough; `git config core.longpaths true` is the other answer.

`net10.0-windows`, because WinPenKit targets it.

## Tests

65, and none of them needs a tablet or a window. What the recorder does with readings it is
given — the capture state machine, the accounting, the time semantics, the trace round trip —
is all checkable without hardware, and that is where a fault sat long enough to reach every
published recording before anybody looked.

What they do not cover is a real pen. That part is checked by recording with one and reading
the file back.
