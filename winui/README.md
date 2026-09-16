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

Output lands in `winui/artifacts/win-x64/`. Or by hand:

```powershell
dotnet publish winui/src/Ni6451.App/Ni6451.App.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained true -o out
```

### Without a Windows machine — GitHub Actions

`.github/workflows/build-winui.yml` builds on `windows-latest` and uploads the published
folder as a workflow artifact. Push this branch (or trigger the workflow manually from the
Actions tab) and download `Ni6451-win-x64` from the run.

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
ni6451 selftest                                 # no hardware needed, runs on any OS
```

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
