"""
ChannelSelector: lets the user choose which of the 16 AI channels are
actually scanned. Unselected channels are excluded from the DAQmx task
entirely -- so unconnected/floating inputs are never sampled, which also
avoids the multiplexer ghosting they'd otherwise pick up from an active
neighboring channel (see daq_worker.py / the settling-time notes in the
USB-6451 manual).
"""

from PySide6.QtWidgets import (
    QWidget, QGridLayout, QHBoxLayout, QVBoxLayout, QCheckBox, QPushButton, QLabel
)

from config import N_CHANNELS


class ChannelSelector(QWidget):
    def __init__(self, parent=None):
        super().__init__(parent)

        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)

        header = QHBoxLayout()
        header.addWidget(QLabel("Channels to acquire:"))
        self.select_all_btn = QPushButton("Select All")
        self.select_all_btn.clicked.connect(self._select_all)
        self.select_none_btn = QPushButton("Select None")
        self.select_none_btn.clicked.connect(self._select_none)
        header.addWidget(self.select_all_btn)
        header.addWidget(self.select_none_btn)
        header.addStretch(1)
        layout.addLayout(header)

        grid = QGridLayout()
        self.checkboxes = []
        half = N_CHANNELS // 2
        for ch in range(N_CHANNELS):
            cb = QCheckBox(f"ai{ch}")
            cb.setChecked(True)
            row = ch % half
            col = 0 if ch < half else 1
            grid.addWidget(cb, row, col)
            self.checkboxes.append(cb)
        layout.addLayout(grid)

    def enabled_channels(self):
        """Return the currently checked channel numbers, in ascending order."""
        return [ch for ch, cb in enumerate(self.checkboxes) if cb.isChecked()]

    def set_locked(self, locked: bool):
        """Disable editing while an acquisition is running."""
        for cb in self.checkboxes:
            cb.setEnabled(not locked)
        self.select_all_btn.setEnabled(not locked)
        self.select_none_btn.setEnabled(not locked)

    def _select_all(self):
        for cb in self.checkboxes:
            cb.setChecked(True)

    def _select_none(self):
        for cb in self.checkboxes:
            cb.setChecked(False)
