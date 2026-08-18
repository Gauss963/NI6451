"""
ChannelPlot: one row combining a checkbox (channel on/off) with a small
embedded Matplotlib line plot for that channel. Toggling the checkbox
immediately restyles the plot (grey = off) regardless of whether an
acquisition is currently running -- this is purely a UI state, decided
before Start is pressed.
"""

import numpy as np
from PySide6.QtCore import Signal
from PySide6.QtWidgets import QWidget, QHBoxLayout, QCheckBox

from matplotlib.figure import Figure
from matplotlib.backends.backend_qtagg import FigureCanvasQTAgg

from config import CHANNEL_ON_COLOR, CHANNEL_OFF_COLOR, CHANNEL_OFF_FACECOLOR, DEFAULT_Y_RANGE


class ChannelPlot(QWidget):
    toggled = Signal(int, bool)  # (channel number, checked)

    def __init__(self, channel: int, parent=None):
        super().__init__(parent)
        self.channel = channel
        self.enabled = True
        self.y_range = DEFAULT_Y_RANGE

        layout = QHBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(4)

        self.checkbox = QCheckBox(f"ai{channel}")
        self.checkbox.setChecked(True)
        self.checkbox.setFixedWidth(60)
        self.checkbox.toggled.connect(self._on_toggled)
        layout.addWidget(self.checkbox)

        self.fig = Figure(figsize=(6, 0.9))
        self.canvas = FigureCanvasQTAgg(self.fig)
        self.canvas.setFixedHeight(80)
        layout.addWidget(self.canvas, stretch=1)

        self.ax = self.fig.add_axes([0.07, 0.10, 0.91, 0.86])
        self.ax.tick_params(labelsize=6)
        (self.line,) = self.ax.plot([], [], linewidth=0.8)
        self._apply_style()
        self._apply_ylim()

    # ---------- public API ----------
    def is_enabled(self) -> bool:
        return self.enabled

    def set_locked(self, locked: bool):
        self.checkbox.setEnabled(not locked)

    def set_y_range(self, y_range: float):
        self.y_range = y_range
        self._apply_ylim()
        self.canvas.draw_idle()

    def update_data(self, t: np.ndarray, y: np.ndarray):
        """Only actually redraws if this channel is currently enabled."""
        if not self.enabled:
            return
        self.line.set_data(t, y)
        if len(t):
            self.ax.set_xlim(t[0], 0)
        self.canvas.draw_idle()

    def clear(self):
        self.line.set_data([], [])
        self.canvas.draw_idle()

    # ---------- internal ----------
    def _on_toggled(self, checked: bool):
        self.enabled = checked
        self._apply_style()
        if not checked:
            self.line.set_data([], [])
        self.canvas.draw_idle()
        self.toggled.emit(self.channel, checked)

    def _apply_style(self):
        if self.enabled:
            self.ax.set_facecolor("white")
            self.line.set_color(CHANNEL_ON_COLOR)
            self.checkbox.setStyleSheet("")
        else:
            self.ax.set_facecolor(CHANNEL_OFF_FACECOLOR)
            self.line.set_color(CHANNEL_OFF_COLOR)
            self.checkbox.setStyleSheet("color: #909090;")

    def _apply_ylim(self):
        self.ax.set_ylim(-self.y_range, self.y_range)
        self.ax.set_yticks([-self.y_range, 0, self.y_range])
