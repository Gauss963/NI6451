"""
LiveTraceWidget: real-time display of the 16 AI channels, each as its own
row (checkbox + small line plot), arranged in a left column (ai0-ai7) and
a right column (ai8-ai15) -- same overall layout as before, just with the
on/off checkbox sitting directly next to its own plot instead of in a
separate panel. Unchecking a channel greys its plot out immediately, with
no need to press Start first.

Design notes:
  - The DAQ callback fires roughly every 10 ms (see config.RATE / config.CHUNK),
    far faster than any GUI can usefully redraw. Incoming chunks are
    decimated down to config.DISPLAY_RATE_HZ and stored in a RollingBuffer;
    that decimated buffer is what actually gets plotted. This keeps
    redraws cheap regardless of the acquisition rate and lets the user
    pick an arbitrary time window without holding full-rate data in memory.
  - Decimation only affects what's shown on screen. The full-rate data
    written to disk by DAQWorker is untouched.
"""

import numpy as np
from PySide6.QtCore import QTimer
from PySide6.QtWidgets import (
    QWidget, QVBoxLayout, QHBoxLayout, QGridLayout, QLabel, QSpinBox,
    QDoubleSpinBox, QPushButton
)

from config import (
    N_CHANNELS, RATE, PLOT_REFRESH_MS, DISPLAY_RATE_HZ, MAX_PLOT_POINTS,
    MIN_WINDOW_SEC, MAX_WINDOW_SEC, DEFAULT_WINDOW_SEC,
    MIN_Y_RANGE, MAX_Y_RANGE, DEFAULT_Y_RANGE,
)
from rolling_buffer import RollingBuffer
from channel_plot import ChannelPlot

DECIMATION_STRIDE = max(1, RATE // DISPLAY_RATE_HZ)
BUFFER_CAPACITY = MAX_WINDOW_SEC * DISPLAY_RATE_HZ


class LiveTraceWidget(QWidget):
    def __init__(self, parent=None):
        super().__init__(parent)

        self.window_sec = DEFAULT_WINDOW_SEC
        self.active_channels = []   # AI channel numbers currently being acquired, ascending
        self.buffer = None          # RollingBuffer, sized once acquisition starts

        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)

        # --- controls ---
        ctrl_layout = QHBoxLayout()
        ctrl_layout.addWidget(QLabel("Time window (s):"))
        self.window_spin = QSpinBox()
        self.window_spin.setRange(MIN_WINDOW_SEC, MAX_WINDOW_SEC)
        self.window_spin.setValue(DEFAULT_WINDOW_SEC)
        self.window_spin.valueChanged.connect(self._on_window_changed)
        ctrl_layout.addWidget(self.window_spin)

        ctrl_layout.addSpacing(20)
        ctrl_layout.addWidget(QLabel("Y range (+/- V):"))
        self.yrange_spin = QDoubleSpinBox()
        self.yrange_spin.setRange(MIN_Y_RANGE, MAX_Y_RANGE)
        self.yrange_spin.setSingleStep(0.5)
        self.yrange_spin.setValue(DEFAULT_Y_RANGE)
        self.yrange_spin.valueChanged.connect(self._on_yrange_changed)
        ctrl_layout.addWidget(self.yrange_spin)

        ctrl_layout.addSpacing(20)
        self.select_all_btn = QPushButton("Select All")
        self.select_all_btn.clicked.connect(self._select_all)
        self.select_none_btn = QPushButton("Select None")
        self.select_none_btn.clicked.connect(self._select_none)
        ctrl_layout.addWidget(self.select_all_btn)
        ctrl_layout.addWidget(self.select_none_btn)

        ctrl_layout.addStretch(1)
        layout.addLayout(ctrl_layout)

        # --- per-channel rows: left column ai0-7, right column ai8-15 ---
        grid = QGridLayout()
        grid.setHorizontalSpacing(20)
        grid.setVerticalSpacing(2)
        half = N_CHANNELS // 2
        self.channel_plots = [None] * N_CHANNELS
        for ch in range(N_CHANNELS):
            cp = ChannelPlot(ch)
            row = ch % half
            col = 0 if ch < half else 1
            grid.addWidget(cp, row, col)
            self.channel_plots[ch] = cp
        layout.addLayout(grid)
        layout.addStretch(1)

        self._timer = QTimer(self)
        self._timer.setInterval(PLOT_REFRESH_MS)
        self._timer.timeout.connect(self._redraw)

    # ---------- public API ----------
    def enabled_channels(self) -> list:
        """Currently checked channel numbers, in ascending order."""
        return [cp.channel for cp in self.channel_plots if cp.is_enabled()]

    def set_locked(self, locked: bool):
        """Disable the checkboxes while an acquisition is running."""
        for cp in self.channel_plots:
            cp.set_locked(locked)
        self.select_all_btn.setEnabled(not locked)
        self.select_none_btn.setEnabled(not locked)

    def start(self, enabled_channels: list):
        self.active_channels = list(enabled_channels)
        self.buffer = RollingBuffer(len(self.active_channels), BUFFER_CAPACITY)
        for cp in self.channel_plots:
            cp.clear()
        self._timer.start()

    def stop(self):
        self._timer.stop()

    def push_chunk(self, chunk: np.ndarray):
        """Called from the DAQ callback (high frequency). Decimates the
        incoming full-rate chunk and appends it to the rolling display buffer.
        `chunk` has one row per active channel, in the same order as
        self.active_channels."""
        if self.buffer is None:
            return
        decimated = chunk[:, ::DECIMATION_STRIDE]
        self.buffer.push(decimated)

    # ---------- internal ----------
    def _on_window_changed(self, value):
        self.window_sec = value

    def _on_yrange_changed(self, value):
        for cp in self.channel_plots:
            cp.set_y_range(value)

    def _select_all(self):
        for cp in self.channel_plots:
            cp.checkbox.setChecked(True)

    def _select_none(self):
        for cp in self.channel_plots:
            cp.checkbox.setChecked(False)

    def _redraw(self):
        if self.buffer is None or not self.active_channels:
            return

        n_samples = int(self.window_sec * DISPLAY_RATE_HZ)
        data = self.buffer.get_last(n_samples)
        n = data.shape[1]
        if n == 0:
            return

        # Cap the number of points actually rendered, regardless of the
        # selected time window -- keeps redraw cost (and GIL contention
        # with the DAQ callback thread) bounded even at a 30s window.
        stride = max(1, n // MAX_PLOT_POINTS)
        data = data[:, ::stride]
        n = data.shape[1]
        effective_rate = DISPLAY_RATE_HZ / stride
        t = (np.arange(n) - n) / effective_rate  # seconds, ending at (approximately) 0

        for i, ch in enumerate(self.active_channels):
            self.channel_plots[ch].update_data(t, data[i])
