# USB-6451 Continuous Acquisition Prototype

A PySide6 desktop app for continuous, multi-channel analog data acquisition with an NI USB-6451 (or compatible NI-DAQmx device). It streams up to 16 analog input channels at a fixed high sample rate, shows a live decimated preview per channel, and writes full-rate data to disk in the background.

## Features

- Auto-detects NI-DAQmx devices, with manual entry as a fallback
- Per-channel enable/disable via checkboxes next to each live trace
- Live plot per channel with adjustable time window and Y-axis range
- Continuous acquisition at a fixed rate (500,000 S/s/channel by default), streamed to temporary per-channel files every 10 seconds so full-rate data is never held entirely in RAM
- Background merge of temp files into a single `.npz` output file on stop, so the GUI never blocks on I/O
- Only selected channels are added to the DAQmx task, avoiding multiplexer ghosting from unused floating inputs

## Requirements

- Python 3
- [PySide6](https://pypi.org/project/PySide6/)
- [nidaqmx](https://pypi.org/project/nidaqmx/) (NI-DAQmx Python API)
- [NumPy](https://pypi.org/project/numpy/)
- NI-DAQmx driver installed, with a supported NI DAQ device connected

```bash
pip install PySide6 nidaqmx numpy
```

## Usage

```bash
python main.py
```

1. Select or refresh the NI-DAQmx device.
2. Choose an output folder for recordings.
3. Check the channels you want to acquire (`ai0`-`ai15`).
4. Click **Start** to begin acquisition; live traces update in the plot area.
5. Click **Stop** to end acquisition. Data is saved as `daq_YYYYMMDD_HHMMSS.npz` in the output folder, with one array per channel (e.g. `ai0`, `ai3`), plus `sample_rate` and `channels`.

## Configuration

All tunable parameters (sample rate, chunk size, flush interval, plot refresh rate, display decimation rate, time window and Y-range limits) live in [config.py](config.py).

## Project Structure

| File | Purpose |
| --- | --- |
| [main.py](main.py) | Application entry point |
| [main_window.py](main_window.py) | Main window UI and control wiring |
| [config.py](config.py) | Central location for all tunable parameters |
| [devices.py](devices.py) | NI-DAQmx device discovery |
| [daq_worker.py](daq_worker.py) | Creates/runs the DAQmx task and flushes acquired data to temp files |
| [finalize_worker.py](finalize_worker.py) | Background thread that merges temp files into the final `.npz` |
| [plot_widget.py](plot_widget.py) | Live multi-channel trace display (decimated rolling buffer) |
| [channel_plot.py](channel_plot.py) | Single-channel plot + enable checkbox |
| [channel_select.py](channel_select.py) | Standalone channel selector widget |
| [rolling_buffer.py](rolling_buffer.py) | Fixed-capacity circular buffer for live display data |

## Notes

- AI channels use RSE (single-ended) mode with a ±10 V range.
- Only the channels selected in the UI are added to the acquisition task; leaving unused channels unselected avoids exposing them to multiplexer ghosting from active neighboring channels.
- The live plot is decimated for display purposes only — the data written to disk is always full-rate.
