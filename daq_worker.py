"""
DAQWorker: creates and controls the nidaqmx Task, using only the channels
the user selected in the UI. Optionally also opens a second DI task,
sample-clock-synced to the AI task, to record which AI sample index lines
up with the first rising edge of an external TTL trigger (e.g. another
DAQ's Trigger Out).

  - Every 10 seconds, already-acquired AI data is written to temporary raw
    files on disk (one file per active channel), so data is never held
    entirely in RAM.
  - stop_acquisition() is fast: it just stops the tasks and flushes the
    last partial buffer. Merging the temp files into a single .npz is
    handled separately by FinalizeWorker on a background thread.

Notes:
  - AI channels use the default RSE (single-ended) mode with a +/-10V range.
  - The device name should match what NI-MAX / the Hardware Configuration
    Utility reports for your hardware.
  - Only channels the user actually connected should be selected -- leaving
    unused channels in the scan list exposes them to multiplexer ghosting
    from neighboring active channels.
  - Trigger capture: the DI task's sample clock and start trigger are both
    locked to the AI task's ("/{device}/ai/SampleClock", "/{device}/ai/
    StartTrigger"), so DI sample N and AI sample N were taken at the same
    instant. Because many external trigger sources latch high after firing
    (rather than pulsing per event), only the first rising edge is recorded.
"""

import os
from datetime import datetime

import numpy as np
from PySide6.QtCore import QObject, Signal

import nidaqmx
from nidaqmx.constants import AcquisitionType, TerminalConfiguration, LineGrouping
from nidaqmx.stream_readers import AnalogMultiChannelReader

from config import RATE, CHUNK, FLUSH_SAMPLES


class DAQWorker(QObject):
    """Creates/controls the nidaqmx Task(s), flushes AI data to a temp file
    every 10 seconds, tracks the first TTL trigger's sample index, and
    hands off to FinalizeWorker on stop_acquisition()."""

    chunk_ready = Signal(np.ndarray)   # shape: (n_active_channels, chunk_size), for live plotting
    error = Signal(str)

    def __init__(self):
        super().__init__()
        self.task = None       # AI task
        self.reader = None
        self.di_task = None    # optional trigger-capture DI task, synced to the AI sample clock

        self.channels = []            # active AI channel numbers, e.g. [0, 3, 7]
        self.save_dir = None
        self.tmp_dir = None
        self.tmp_files = []           # one open file handle per active channel
        self.flush_buffer = []        # list of (n_active, chunk) ndarrays not yet flushed to disk
        self.flush_buffer_samples = 0
        self.total_samples_written = 0

        self.capture_trigger = False
        self.trigger_sample_index = None   # AI sample index of the first trigger rising edge, if found
        self._trigger_last_value = False
        self._sample_counter = 0            # running AI sample count since acquisition start

    # ---------- file helpers ----------
    def _tmp_channel_path(self, position: int) -> str:
        """`position` is the index into self.channels, not the AI channel number."""
        return os.path.join(self.tmp_dir, f"ai{self.channels[position]}.raw")

    def _remove_tmp_dir(self):
        try:
            for i in range(len(self.channels)):
                p = self._tmp_channel_path(i)
                if os.path.exists(p):
                    os.remove(p)
            if self.tmp_dir and os.path.isdir(self.tmp_dir):
                os.rmdir(self.tmp_dir)
        except Exception:
            pass  # cleanup failure shouldn't break the main flow; leave for manual cleanup

    # ---------- acquisition control ----------
    def start(self, device: str, save_dir: str, channels: list,
              capture_trigger: bool = False, trigger_line: str = "port0/line0"):
        self.channels = list(channels)
        n_active = len(self.channels)

        self.save_dir = save_dir
        self.tmp_dir = os.path.join(
            save_dir, f"_tmp_{datetime.now().strftime('%Y%m%d_%H%M%S')}"
        )
        self.flush_buffer = []
        self.flush_buffer_samples = 0
        self.total_samples_written = 0

        self.capture_trigger = capture_trigger
        self.trigger_sample_index = None
        self._trigger_last_value = False
        self._sample_counter = 0

        try:
            os.makedirs(self.tmp_dir, exist_ok=True)
            self.tmp_files = [
                open(self._tmp_channel_path(i), "wb") for i in range(n_active)
            ]

            self.task = nidaqmx.Task()
            for ch in self.channels:
                self.task.ai_channels.add_ai_voltage_chan(
                    f"{device}/ai{ch}",
                    terminal_config=TerminalConfiguration.RSE,
                    min_val=-10.0,
                    max_val=10.0,
                )
            self.task.timing.cfg_samp_clk_timing(
                rate=RATE,
                sample_mode=AcquisitionType.CONTINUOUS,
                samps_per_chan=RATE * 5,  # driver-side buffer, ~5 seconds worth of headroom
            )
            self.reader = AnalogMultiChannelReader(self.task.in_stream)

            if self.capture_trigger:
                self.di_task = nidaqmx.Task()
                self.di_task.di_channels.add_di_chan(
                    f"{device}/{trigger_line}", line_grouping=LineGrouping.CHAN_PER_LINE
                )
                # lock the DI sample clock and start to the AI task's, so DI
                # sample N and AI sample N are taken at the same instant
                self.di_task.timing.cfg_samp_clk_timing(
                    rate=RATE,
                    source=f"/{device}/ai/SampleClock",
                    sample_mode=AcquisitionType.CONTINUOUS,
                )
                self.di_task.triggers.start_trigger.cfg_dig_edge_start_trig(
                    f"/{device}/ai/StartTrigger"
                )

            self.task.register_every_n_samples_acquired_into_buffer_event(
                CHUNK, self._callback
            )

            if self.capture_trigger:
                self.di_task.start()   # arms and waits for the AI task's start trigger
            self.task.start()          # fires the shared start trigger -- both tasks begin together
        except Exception as e:
            self.error.emit(str(e))
            self._cleanup_task()
            self._cleanup_di_task()
            self._close_tmp_files()
            self._remove_tmp_dir()

    def _callback(self, task_handle, every_n_samples_event_type,
                  number_of_samples, callback_data):
        try:
            n_active = len(self.channels)
            buf = np.zeros((n_active, number_of_samples), dtype=np.float64)
            self.reader.read_many_sample(
                buf,
                number_of_samples_per_channel=number_of_samples,
                timeout=10.0,
            )
            self.flush_buffer.append(buf)
            self.flush_buffer_samples += number_of_samples

            self.chunk_ready.emit(buf)

            if self.flush_buffer_samples >= FLUSH_SAMPLES:
                self._flush_to_disk()
        except Exception as e:
            self.error.emit(str(e))
            return 0

        if self.capture_trigger and self.di_task is not None:
            self._read_trigger_chunk(number_of_samples)

        self._sample_counter += number_of_samples
        return 0  # nidaqmx requires the callback to return an int

    def _read_trigger_chunk(self, number_of_samples: int):
        """Reads (drains) the DI task every callback so its buffer never
        overflows. Only bothers looking for the rising edge until the first
        one is found -- most external trigger sources latch high after
        firing rather than pulsing per event, so there's nothing more to
        find after that."""
        try:
            di_data = self.di_task.read(
                number_of_samples_per_channel=number_of_samples, timeout=10.0
            )
        except Exception as e:
            self.error.emit(f"Trigger input read error: {e}")
            return

        if self.trigger_sample_index is not None:
            return

        di_arr = np.asarray(di_data, dtype=bool)
        if di_arr.size == 0:
            return

        extended = np.concatenate(([self._trigger_last_value], di_arr))
        edges = np.where(np.diff(extended.astype(np.int8)) == 1)[0]
        if edges.size:
            self.trigger_sample_index = int(self._sample_counter + edges[0])
        self._trigger_last_value = bool(di_arr[-1])

    def _flush_to_disk(self):
        if not self.flush_buffer:
            return
        combined = np.concatenate(self.flush_buffer, axis=1)  # (n_active, n)
        for i in range(len(self.channels)):
            self.tmp_files[i].write(combined[i].tobytes())
        self.total_samples_written += combined.shape[1]
        self.flush_buffer = []
        self.flush_buffer_samples = 0

    def _cleanup_task(self):
        if self.task is not None:
            try:
                self.task.close()
            except Exception:
                pass
            self.task = None
            self.reader = None

    def _cleanup_di_task(self):
        if self.di_task is not None:
            try:
                self.di_task.close()
            except Exception:
                pass
            self.di_task = None

    def _close_tmp_files(self):
        for f in self.tmp_files:
            try:
                f.close()
            except Exception:
                pass
        self.tmp_files = []

    def stop_acquisition(self):
        """Stop the DAQmx task(s) and flush any remaining buffered data to
        disk. This is fast (no large I/O) -- merging the temp files into a
        single .npz is handled separately by FinalizeWorker so it can run
        in the background without blocking the GUI.
        Returns (tmp_dir, n_samples_per_channel, channels, trigger_sample_index),
        or None if nothing was recorded. trigger_sample_index is None if
        trigger capture was off or no trigger was seen."""
        self._cleanup_task()          # make sure no more AI data can arrive
        self._cleanup_di_task()
        self._flush_to_disk()          # flush any leftover partial (<10s) buffer
        self._close_tmp_files()

        if self.total_samples_written == 0:
            self._remove_tmp_dir()
            return None

        return self.tmp_dir, self.total_samples_written, list(self.channels), self.trigger_sample_index
