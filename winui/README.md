# USB-6451 Continuous Acquisition — WinUI 3 rewrite

A C# / WinUI 3 (Windows App SDK) port of the PySide6 application that lives at the
repository root on the `main` branch. Same hardware, same acquisition strategy, same output
files — different UI stack and no Python runtime.

Everything new lives under `winui/`. **No file on the `main` branch is modified by this
branch**, apart from four pointer lines appended to the root `README.md`, so `main` keeps
working exactly as it did and the two implementations can be maintained side by side.

---

## Why this exists, and what "forward compatible" means here

The recordings are the contract between the two implementations. This port writes byte-level
`numpy`-compatible `.npz` archives, so:

- `examples/read_example.py` on `main` reads files produced by the WinUI app **unchanged**.
- The WinUI `ni6451 dump` command reads files produced by the Python app on `main`.

Concretely, every recording contains the same arrays it always did:

| Array | dtype | Shape | Meaning |
| --- | --- | --- | --- |
| `ai{n}` | `<f8` | `(N,)` | One array per recorded channel, full-rate volts |
| `sample_rate` | `<i8` | `()` | Samples/s per channel |
| `channels` | `<i8` | `(k,)` | Which `ai` channels were recorded |
| `trigger_sample_index` | `<i8` | `()` | First TTL rising edge, or `-1` if none |

The file name keeps the `T{SH}-raw-run{RN}-{yyyyMMdd_HHmmss}.npz` pattern. Members are
stored uncompressed, exactly as `numpy.savez` writes them.

Reading is deliberately more permissive than writing: the integer arrays are also accepted
as `<i4`, because NumPy 1.x on Windows makes `np.array(500000)` an `int32` — so archives the
Python application already wrote on the lab machines store `sample_rate`, `channels` and
`trigger_sample_index` in the narrower dtype.

Verified both directions with NumPy 2.x: archives written by `FinalizeJob` load in NumPy
bit-identically (including the `t = 0` at the trigger arithmetic `read_example.py` performs),
and `ni6451 dump` reads `numpy.savez` archives in both the `int32` and `int64` flavours.

`ni6451 selftest` asserts all of this — the `.npy` header text, the 64-byte data alignment,
the stored (uncompressed) ZIP members, the 0-D shape of the scalars — and runs on any OS
without hardware.

---

## Building the x64 Windows executable

> **WinUI 3 cannot be cross-compiled.** The Windows App SDK build chain — the XAML compiler,
> MIDL, the C#/WinRT projection generator, and the Windows SDK build tools — ships as Windows
> PE binaries that MSBuild invokes directly. There is no macOS or Linux host for them, and no
> `dotnet publish -r win-x64` from a non-Windows machine will produce this `.exe`. The two
> supported routes are below.

### On a Windows machine

Install [.NET SDK 8.0](https://dotnet.microsoft.com/download/dotnet/8.0), then:

```powershell
pwsh winui/build/build-win-x64.ps1
```

Output lands in `winui/artifacts/win-x64/`. Or by hand — note the `cd`, which is not optional:

```powershell
cd winui; dotnet publish src/Ni6451.App/Ni6451.App.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained true -o ../artifacts/win-x64
```

**`winui/global.json` pins the build to the .NET 8 SDK, and it only applies to `dotnet`
commands run from inside `winui/`.** Windows App SDK 1.6 resolves its PRI build task against
the selected SDK's directory layout, so building under a newer SDK fails with `MSB4062`. It
lives in `winui/` rather than the repository root so that `main` is unaffected.

The Windows App SDK and Win2D versions are pinned to a matched pair
(`1.6.241114003` / `1.3.2` — the exact dependency Win2D declares). Floating either one pulls
the two package generations into the same build, which shows up as a XAML compiler that exits
non-zero with no diagnostic at all.

### Without a Windows machine — GitHub Actions

`.github/workflows/build-winui.yml` builds on `windows-latest`. Every push to this branch
produces two downloads, and a tagged push additionally publishes a Release:

| Trigger | Where it lands | Notes |
| --- | --- | --- |
| Any push to `6451-WinUI`, or **Run workflow** in the Actions tab | The run's **Artifacts** section: `Ni6451-win-x64` | Needs a GitHub login, expires after 90 days, always a `.zip` |
| Pushing a tag like `v2.0.0` | **Releases**, as `Ni6451-v2.0.0-win-x64.zip` | Permanent, public, no login needed |

```bash
git tag v2.0.0 && git push origin v2.0.0
```

### What you get

An **unpackaged, self-contained** folder: `Ni6451.exe` next to the .NET runtime and the
Windows App SDK. The target machine needs nothing pre-installed except the NI-DAQmx driver,
which supplies `nicaiu.dll`. Ship the whole folder — `Ni6451.exe` alone will not start.

Requirements to run: Windows 10 1809 (build 17763) or newer, x64, plus NI-DAQmx.

---

## Project layout

| Project | TFM | Purpose |
| --- | --- | --- |
| `src/Ni6451.Core` | `net8.0` | Config, rolling buffer, unit conversions, `.npy`/`.npz` container, spool files, the finalize step. Platform-neutral, so it builds and self-tests on any OS. |
| `src/Ni6451.Daq` | `net8.0` | NI-DAQmx P/Invoke, device discovery, the acquisition task, the hardware-synced trigger monitor. Compiles anywhere; only *runs* on Windows with the driver installed. |
| `src/Ni6451.App` | `net8.0-windows10.0.19041.0` | The WinUI 3 application. Windows-only build. |
| `src/Ni6451.Tools` | `net8.0` | `ni6451` command line companion: `devices`, `dump`, `trigger-test`, `selftest`. |

## Where each Python file went

| `main` branch (Python) | This branch (C#) |
| --- | --- |
| `main.py` | `src/Ni6451.App/App.xaml.cs` |
| `main_window.py` | `src/Ni6451.App/MainWindow.xaml` + `.xaml.cs` |
| `config.py` | `src/Ni6451.Core/AppConfig.cs` |
| `rolling_buffer.py` | `src/Ni6451.Core/RollingBuffer.cs` |
| `unit_conversion.py` | `src/Ni6451.Core/UnitConversion.cs` |
| `devices.py` | `src/Ni6451.Daq/DeviceEnumerator.cs` |
| `daq_worker.py` | `src/Ni6451.Daq/DaqAcquisition.cs` (+ `NiDaqmx.cs`, `ChannelSpool.cs`) |
| `finalize_worker.py` | `src/Ni6451.Core/FinalizeJob.cs` (+ `NpzWriter.cs`, `NpyFormat.cs`) |
| `plot_widget.py` | `src/Ni6451.App/Controls/LiveTraceView.xaml(.cs)` |
| `channel_plot.py` | `src/Ni6451.App/Controls/ChannelPlot.xaml(.cs)` |
| `channel_select.py` | folded into `LiveTraceView` (as it already was in the Qt UI) |
| `tests/trigger_tester.py` | `src/Ni6451.Daq/TriggerMonitor.cs` + `ni6451 trigger-test` |
| `examples/read_example.py` | `src/Ni6451.Core/NpzReader.cs` + `ni6451 dump` (the Python script still works as-is) |
| *(no equivalent)* | `src/Ni6451.Core/SpoolManifest.cs` — crash recovery |
| *(no equivalent)* | `src/Ni6451.Core/AcquisitionStats.cs` — live pipeline telemetry |

## The acquisition pipeline

The single biggest structural change from the Python version. `daq_worker.py` did everything
on the DAQmx callback thread — read, decimate for the plot, buffer, and every ten seconds
write 640 MB to disk — because under the GIL splitting that across threads would not have
bought anything. Here the stages genuinely run at the same time:

```
  DAQmx callback thread          spool writer thread            monitor thread
  ─────────────────────          ───────────────────            ──────────────
  read AI buffer          ──┐    drain bounded queue            drain lossy queue
  read DI (trigger)         ├──► write per-channel files  ┐     raise MonitorChunkReady
  take one strided copy   ──┘    (fanned out over the     │     │
  post to both queues            thread pool)             │     ▼
                                 flush + manifest         │     rolling buffer ──► 16 Win2D plots
                                                          ▼
                                                     ai0.raw … ai15.raw
```

- The **write queue is bounded** (`AppConfig.WriteQueueCapacity`, ~2 s). If the disk falls
  behind, the queue fills and the callback waits — real backpressure, visible in the status
  bar's *Queue* meter long before anything is at risk.
- The **monitor queue is lossy** (drops its oldest frame when full). A slow or stalled UI
  degrades the live plot and can never apply backpressure to the recording.
- Decimation happens on the callback thread, but the rolling buffer is touched only from the
  monitor thread. The UI holds that buffer's lock while copying out a frame — several
  megabytes at a 30 s window — and that delay must never reach the hardware path.
- Per-channel spool writes are fanned out with `Parallel.For` above
  `AppConfig.ParallelWriteThreshold` channels. They target independent `FileStream` objects,
  so the copies and any buffer-full syscalls overlap. This is precisely what the GIL made
  pointless in Python.
- The merge double-buffers its reads: the next block is pulled from the spool file with
  overlapped I/O while the current one is written into the archive.

## Data safety

| Failure | What protects you |
| --- | --- |
| The app crashes | Data is spooled continuously, never held in a 640 MB RAM buffer. Everything already written survives in the OS page cache. |
| Power cut | Spool files are `fsync`'d every `AppConfig.DurableFlushIntervalSec` (30 s), on top of the 10 s flush out of the process. |
| Either, mid-run | A `manifest.json` beside the spool records the channels, rate, run numbering and trigger index, rewritten atomically on every flush. |
| Recovering afterwards | Choosing an output folder scans it for interrupted runs and offers **Recover now**. Same thing from the CLI: `ni6451 recover <folder> --apply`. |
| Disk too slow | The *Queue* meter shows how far behind the writer is; a run that came close reports it afterwards. |
| Driver buffer overrun | DAQmx `-200279` / `-200361` are detected specifically, counted, and reported as data loss — the recording up to that point is still saved. |
| A failed merge | The partial `.npz` is deleted, but the spool files and manifest are deliberately kept, so the run can simply be recovered. |

The sample count used during recovery is re-derived from the raw file lengths rather than
read from the manifest, and the *minimum* across channels is taken — so a run cut off
mid-chunk yields an aligned recording rather than one ragged channel.

## Dependency swaps

| Python | C# |
| --- | --- |
| PySide6 | WinUI 3 / Windows App SDK |
| `nidaqmx` package | direct P/Invoke into `nicaiu.dll` |
| NumPy arrays | `double[]` / `Span<double>` |
| `np.savez` | `NpzWriter` (writes the same container) |
| Matplotlib canvases | Win2D `CanvasControl` |
| `QThread` | `Task.Run` + a dedicated spool-writer thread |
| Qt signals | C# events, marshalled with `DispatcherQueue` |

---

## The command line tool

```bash
ni6451 devices                                  # list NI-DAQmx devices
ni6451 dump recording.npz                       # summarise a recording
ni6451 dump recording.npz --csv out.csv         # ... and export CSV
ni6451 trigger-test --device Dev2 --line port0/line0
ni6451 recover /path/to/output                  # list interrupted runs
ni6451 recover /path/to/output --apply          # ... and merge them into .npz files
ni6451 selftest                                 # no hardware needed, runs on any OS
```

---

## The interface

Laid out for Windows 11 rather than transliterated from the Qt window:

- **Mica** window backdrop and an extended title bar.
- **Two panes.** Everything you set before pressing Start lives in a fixed left rail; the
  right side is the live experiment. The old single vertical stack pushed the traces — the
  thing you actually watch — below the fold.
- **Start and Stop are pinned** below the scrolling rail, so they can never be scrolled out
  of reach during a run.
- **Readout tiles** for normal stress, shear stress, LVDT and trigger state, as large
  tabular figures that do not jitter as they update ten times a second.
- **Inline `InfoBar` notifications instead of modal dialogs.** A modal error dialog during a
  run is actively harmful: it steals focus from the Stop button while the hardware keeps
  streaming. Errors, data loss and recovery offers all appear as dismissible bars.
- **A telemetry status bar**: elapsed time, bytes written, throughput, and the write-queue
  meter, plus a progress bar for the merge (which used to be an opaque wait).
- **Light and dark**, including the Win2D plots — their palettes live in `AppConfig`
  alongside the other tunable parameters and switch with `ActualTheme`.
- Free disk space is translated into *minutes of recording at the current channel count*,
  which is the number that actually matters before pressing Start.

---

## Behavioural differences from `main`

These are deliberate. Everything not listed here behaves as it did.

1. **Spooling is continuous instead of bursty.** The Python version accumulated ten seconds
   of samples in RAM (~640 MB at 16 channels) and wrote them in one burst. Here every chunk
   goes to a bounded queue and a dedicated writer thread spools it immediately, so peak
   memory is a couple of hundred MB regardless of run length and the driver's callback
   thread never blocks on disk I/O. The files on disk are identical; only the timing of the
   writes changed. Disk is still flushed on the same ten-second cadence.

2. **The live readout no longer depends on `ai0` being selected.** In `main_window.py` the
   fault type and thickness were only read inside the `ai0` branch, so a run with `ai0`
   deselected and `ai1` selected raised `NameError` when formatting the shear stress. Both
   values are now read once per tick.

3. **The plot point cap is a genuine upper bound.** `plot_widget.py` computed
   `stride = n // MAX_PLOT_POINTS`, which still rendered up to ~2x the cap for some window
   sizes. The stride now rounds up.

4. **`FaultType` is an enum, not a string.** `unit_conversion.py` left `piston_area`
   unbound if it was handed anything other than `'1D'` or `'2D'`; that is unrepresentable
   now. The numbers for `1D` and `2D` are unchanged, including the 2D override that pins
   the fault thickness to 50 cm.

---

## Notes carried over from the original

- AI channels use RSE (single-ended) mode with a ±10 V range.
- Only the channels selected in the UI are added to the acquisition task; leaving unused
  channels unselected avoids exposing them to multiplexer ghosting from active neighbours.
- The live plot is decimated for display only — the data written to disk is always full-rate.
- Trigger capture locks the DI task's sample clock and start trigger to the AI task's, so DI
  sample N and AI sample N are taken at the same instant. Only the first rising edge is
  recorded, because most external trigger sources latch high after firing.
