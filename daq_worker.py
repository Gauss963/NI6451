"""
DAQWorker: creates and controls the nidaqmx Task, using only the channels
the user selected in the UI.
  - Every 10 seconds, already-acquired data is written to temporary raw
    files on disk (one file per active channel), so data is never held
    entirely in RAM.
  - stop_acquisition() is fast: it just stops the task and flushes the
    last partial buffer. Merging the temp files into a single .npz is
    handled separately by FinalizeWorker on a background thread.

Notes:
  - AI channels use the default RSE (single-ended) mode with a +/-10V range.
  - The device name should match what NI-MAX / the Hardware Configuration
    Utility reports for your hardware.
  - Only channels the user actually connected should be selected -- leaving
    unused channels in the scan list exposes them to multiplexer ghosting
    from neighboring active channels (floating inputs have very high
    source impedance, so injected charge from the mux can't settle).
"""

import os
from datetime import datetime

import numpy as np
from PySide6.QtCore import QObject, Signal

import nidaqmx
from nidaqmx.constants import AcquisitionType, TerminalConfiguration
from nidaqmx.stream_readers import AnalogMultiChannelReader

from config import RATE, CHUNK, FLUSH_SAMPLES


class DAQWorker(QObject):
    """Creates/controls the nidaqmx Task, flushes data to a temp file every
    10 seconds, and hands off to FinalizeWorker on stop_acquisition()."""

    chunk_ready = Signal(np.ndarray)   # shape: (n_active_channels, chunk_size), for live plotting
    error = Signal(str)

    def __init__(self):
        super().__init__()
        self.task = None
        self.reader = None

        self.channels = []            # active channel numbers, e.g. [0, 3, 7]
        self.save_dir = None
        self.tmp_dir = None
        self.tmp_files = []           # one open file handle per active channel
        self.flush_buffer = []        # list of (n_active, chunk) ndarrays not yet flushed to disk
        self.flush_buffer_samples = 0
        self.total_samples_written = 0

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
    def start(self, device: str, save_dir: str, channels: list):
        self.channels = list(channels)
        n_active = len(self.channels)

        self.save_dir = save_dir
        self.tmp_dir = os.path.join(
            save_dir, f"_tmp_{datetime.now().strftime('%Y%m%d_%H%M%S')}"
        )
        self.flush_buffer = []
        self.flush_buffer_samples = 0
        self.total_samples_written = 0

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

            self.task.register_every_n_samples_acquired_into_buffer_event(
                CHUNK, self._callback
            )
            self.task.start()
        except Exception as e:
            self.error.emit(str(e))
            self._cleanup_task()
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
        return 0  # nidaqmx requires the callback to return an int

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

    def _close_tmp_files(self):
        for f in self.tmp_files:
            try:
                f.close()
            except Exception:
                pass
        self.tmp_files = []

    def stop_acquisition(self):
        """Stop the DAQmx task and flush any remaining buffered data to disk.
        This is fast (no large I/O) -- merging the temp files into a single
        .npz is handled separately by FinalizeWorker so it can run in the
        background without blocking the GUI.
        Returns (tmp_dir, n_samples_per_channel, channels), or None if
        nothing was recorded."""
        self._cleanup_task()          # make sure no more data can arrive
        self._flush_to_disk()          # flush any leftover partial (<10s) buffer
        self._close_tmp_files()

        if self.total_samples_written == 0:
            self._remove_tmp_dir()
            return None

        return self.tmp_dir, self.total_samples_written, list(self.channels)
