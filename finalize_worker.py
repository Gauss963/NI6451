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
    finished_ok = Signal(str, int, object)   # (npz_path, n_samples_per_channel, trigger_sample_index)
    error = Signal(str)

    def __init__(self, tmp_dir: str, n_samples: int, save_dir: str, channels: list,
                 trigger_sample_index=None, sh: str = "0000", rn: int = 0, parent=None):
        super().__init__(parent)
        self.tmp_dir = tmp_dir
        self.n_samples = n_samples
        self.save_dir = save_dir
        self.channels = list(channels)
        self.trigger_sample_index = trigger_sample_index
        self.sh = sh
        self.rn = rn

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
        out_path = None
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
            # -1 means trigger capture was off or no trigger edge was seen during the recording
            save_dict["trigger_sample_index"] = np.array(
                self.trigger_sample_index if self.trigger_sample_index is not None else -1
            )

            timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
            fname = f"T{self.sh}-raw-run{self.rn}-{timestamp}.npz"
            out_path = os.path.join(self.save_dir, fname)
            np.savez(out_path, **save_dict)

            del save_dict  # release memmap references before deleting the files
            self._remove_tmp_dir()

            self.finished_ok.emit(out_path, n, self.trigger_sample_index)
        except Exception as e:
            # Don't leave a broken/empty .npz behind if np.savez failed partway
            # through. The raw temp files are intentionally NOT deleted here --
            # they're the only copy of the captured data if this failed, so
            # they're left in place for manual recovery.
            if out_path is not None and os.path.exists(out_path):
                try:
                    os.remove(out_path)
                except Exception:
                    pass
            self.error.emit(str(e))
