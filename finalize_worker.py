"""
FinalizeWorker: merges the temporary per-channel raw files into a single
.npz file on a background QThread. This step is I/O-bound and can take a
long time for large recordings, so it must not run on the GUI thread or
the application will appear to hang.
"""

import os
from datetime import datetime

import numpy as np
from PySide6.QtCore import QThread, Signal

from config import RATE


class FinalizeWorker(QThread):
    finished_ok = Signal(str, int)   # (npz_path, n_samples_per_channel)
    error = Signal(str)

    def __init__(self, tmp_dir: str, n_samples: int, save_dir: str, channels: list, parent=None):
        super().__init__(parent)
        self.tmp_dir = tmp_dir
        self.n_samples = n_samples
        self.save_dir = save_dir
        self.channels = list(channels)

    def _tmp_channel_path(self, position: int) -> str:
        return os.path.join(self.tmp_dir, f"ai{self.channels[position]}.raw")

    def _remove_tmp_dir(self):
        try:
            for i in range(len(self.channels)):
                p = self._tmp_channel_path(i)
                if os.path.exists(p):
                    os.remove(p)
            if os.path.isdir(self.tmp_dir):
                os.rmdir(self.tmp_dir)
        except Exception:
            pass  # leftover temp files are harmless; can be deleted manually

    def run(self):
        try:
            n = self.n_samples
            save_dict = {}
            for i, ch in enumerate(self.channels):
                # np.memmap + np.savez stream the data through in chunks
                # rather than loading the whole array into RAM at once.
                save_dict[f"ai{ch}"] = np.memmap(
                    self._tmp_channel_path(i), dtype=np.float64, mode="r", shape=(n,)
                )
            save_dict["sample_rate"] = np.array(RATE)
            save_dict["channels"] = np.array(self.channels)

            fname = datetime.now().strftime("daq_%Y%m%d_%H%M%S.npz")
            out_path = os.path.join(self.save_dir, fname)
            np.savez(out_path, **save_dict)

            del save_dict  # release memmap references before deleting the files
            self._remove_tmp_dir()

            self.finished_ok.emit(out_path, n)
        except Exception as e:
            self.error.emit(str(e))
