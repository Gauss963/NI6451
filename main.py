"""
USB-6451 連續擷取 GUI 雛形
============================
功能：
  1. 選擇儲存目錄
  2. 按 Start：16 個單端 AI 通道 (ai0:15) 以指定取樣率連續擷取
  3. 即時顯示 16 條 trace（顯示最新一個 chunk，非長時間卷動視窗）
  4. 按 Stop：停止擷取，把 16 條完整 trace 存成一個 .npz

注意（雛形限制，正式使用前請留意）：
  - 目前把所有資料都留在記憶體（RAM）中，錄製時間拉長會佔用大量記憶體。
    16ch * 500kS/s * 8 bytes(float64) ≈ 64 MB/秒。錄 5 分鐘 ≈ 19 GB。
    若要長時間錄製，建議把 callback 中的 chunk 直接寫入磁碟（例如逐步寫入
    memmap 的 .npy，或用 np.save 分段檔案），Stop 時再組合，而不是全部留在 RAM。
  - 目前 AI 使用預設 RSE (single-ended) 模式、±10V 範圍，如需差動/其他範圍
    請自行調整 add_ai_voltage_chan 的參數。
  - device 名稱預設 "Dev1"，請依你在 MAX / Hardware Configuration Utility
    中看到的實際名稱修改。
"""

import sys
import os
import time
from datetime import datetime

import numpy as np
from PySide6.QtCore import Qt, QObject, Signal
from PySide6.QtWidgets import (
    QApplication, QMainWindow, QWidget, QVBoxLayout, QHBoxLayout,
    QPushButton, QLabel, QFileDialog, QSpinBox, QLineEdit, QMessageBox,
    QGroupBox, QFormLayout
)
import pyqtgraph as pg

import nidaqmx
from nidaqmx.constants import AcquisitionType, TerminalConfiguration
from nidaqmx.stream_readers import AnalogMultiChannelReader

N_CHANNELS = 16


class DAQWorker(QObject):
    """負責建立/控制 nidaqmx Task，並把每個 chunk 的資料丟回 UI 執行緒。"""

    chunk_ready = Signal(np.ndarray)   # shape: (N_CHANNELS, chunk_size)
    error = Signal(str)
    stopped = Signal()

    def __init__(self):
        super().__init__()
        self.task = None
        self.reader = None
        self.samples_per_chunk = None
        self.recorded_chunks = []  # list of np.ndarray, 之後 concatenate

    def start(self, device: str, rate: float, samples_per_chunk: int):
        try:
            self.task = nidaqmx.Task()
            for ch in range(N_CHANNELS):
                self.task.ai_channels.add_ai_voltage_chan(
                    f"{device}/ai{ch}",
                    terminal_config=TerminalConfiguration.RSE,
                    min_val=-10.0,
                    max_val=10.0,
                )
            self.task.timing.cfg_samp_clk_timing(
                rate=rate,
                sample_mode=AcquisitionType.CONTINUOUS,
                samps_per_chan=int(rate * 2),  # 內部緩衝區約 2 秒份
            )
            self.reader = AnalogMultiChannelReader(self.task.in_stream)
            self.samples_per_chunk = samples_per_chunk
            self.recorded_chunks = []

            self.task.register_every_n_samples_acquired_into_buffer_event(
                samples_per_chunk, self._callback
            )
            self.task.start()
        except Exception as e:
            self.error.emit(str(e))
            self._cleanup()

    def _callback(self, task_handle, every_n_samples_event_type,
                  number_of_samples, callback_data):
        try:
            buf = np.zeros((N_CHANNELS, number_of_samples), dtype=np.float64)
            self.reader.read_many_sample(
                buf,
                number_of_samples_per_channel=number_of_samples,
                timeout=10.0,
            )
            # 保留一份副本供最後存檔用
            self.recorded_chunks.append(buf.copy())
            # 丟一份給 UI 畫圖（PySide6 signal 會自動跨執行緒排入主執行緒佇列）
            self.chunk_ready.emit(buf)
        except Exception as e:
            self.error.emit(str(e))
        return 0  # nidaqmx 要求 callback 回傳 int

    def stop(self) -> np.ndarray | None:
        """停止擷取並回傳組合後的完整資料 (N_CHANNELS, total_samples)。"""
        self._cleanup()
        if not self.recorded_chunks:
            return None
        full = np.concatenate(self.recorded_chunks, axis=1)
        self.recorded_chunks = []
        return full

    def _cleanup(self):
        if self.task is not None:
            try:
                self.task.close()
            except Exception:
                pass
            self.task = None
            self.reader = None


class MainWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("USB-6451 連續擷取雛形")
        self.resize(1000, 700)

        self.save_dir = None
        self.worker = DAQWorker()
        self.worker.chunk_ready.connect(self.on_chunk_ready)
        self.worker.error.connect(self.on_error)

        central = QWidget()
        self.setCentralWidget(central)
        layout = QVBoxLayout(central)

        # --- 設定區 ---
        settings_box = QGroupBox("擷取設定")
        form = QFormLayout(settings_box)

        self.device_edit = QLineEdit("Dev1")
        form.addRow("Device 名稱：", self.device_edit)

        self.rate_spin = QSpinBox()
        self.rate_spin.setRange(1000, 500000)
        self.rate_spin.setSingleStep(1000)
        self.rate_spin.setValue(500000)
        self.rate_spin.setSuffix(" S/s per channel")
        form.addRow("取樣率：", self.rate_spin)

        self.chunk_spin = QSpinBox()
        self.chunk_spin.setRange(100, 100000)
        self.chunk_spin.setValue(5000)
        self.chunk_spin.setSuffix(" samples/callback")
        form.addRow("Callback chunk 大小：", self.chunk_spin)

        layout.addWidget(settings_box)

        # --- 目錄選擇 + 控制按鈕 ---
        ctrl_layout = QHBoxLayout()
        self.dir_label = QLabel("尚未選擇儲存目錄")
        self.dir_btn = QPushButton("選擇儲存目錄")
        self.dir_btn.clicked.connect(self.choose_dir)
        self.start_btn = QPushButton("Start")
        self.start_btn.clicked.connect(self.start_acquisition)
        self.start_btn.setEnabled(False)
        self.stop_btn = QPushButton("Stop")
        self.stop_btn.clicked.connect(self.stop_acquisition)
        self.stop_btn.setEnabled(False)

        ctrl_layout.addWidget(self.dir_btn)
        ctrl_layout.addWidget(self.dir_label, stretch=1)
        ctrl_layout.addWidget(self.start_btn)
        ctrl_layout.addWidget(self.stop_btn)
        layout.addLayout(ctrl_layout)

        # --- 即時波形 ---
        self.plot_widget = pg.GraphicsLayoutWidget()
        layout.addWidget(self.plot_widget, stretch=1)
        self.curves = []
        for ch in range(N_CHANNELS):
            p = self.plot_widget.addPlot(row=ch, col=0)
            p.setLabel("left", f"ai{ch}")
            p.showAxis("bottom", False)
            p.setYRange(-10, 10)
            curve = p.plot(pen=pg.intColor(ch, hues=N_CHANNELS))
            self.curves.append(curve)

        self.status_label = QLabel("狀態：待機")
        layout.addWidget(self.status_label)

    def choose_dir(self):
        d = QFileDialog.getExistingDirectory(self, "選擇儲存目錄")
        if d:
            self.save_dir = d
            self.dir_label.setText(d)
            self.start_btn.setEnabled(True)

    def start_acquisition(self):
        device = self.device_edit.text().strip()
        rate = self.rate_spin.value()
        chunk = self.chunk_spin.value()

        self.dir_btn.setEnabled(False)
        self.start_btn.setEnabled(False)
        self.device_edit.setEnabled(False)
        self.rate_spin.setEnabled(False)
        self.chunk_spin.setEnabled(False)

        self.worker.start(device, rate, chunk)
        if self.worker.task is not None:
            self.stop_btn.setEnabled(True)
            self.status_label.setText(f"狀態：擷取中 ({rate} S/s x {N_CHANNELS}ch)")
        else:
            # start 失敗，UI 復原
            self._reset_controls()

    def stop_acquisition(self):
        self.stop_btn.setEnabled(False)
        self.status_label.setText("狀態：停止中，正在整理資料...")
        data = self.worker.stop()

        if data is None:
            QMessageBox.warning(self, "提示", "沒有擷取到任何資料。")
        else:
            fname = datetime.now().strftime("daq_%Y%m%d_%H%M%S.npz")
            fpath = os.path.join(self.save_dir, fname)
            save_dict = {f"ai{ch}": data[ch] for ch in range(N_CHANNELS)}
            save_dict["sample_rate"] = np.array(self.rate_spin.value())
            np.savez(fpath, **save_dict)
            n_samples = data.shape[1]
            self.status_label.setText(
                f"狀態：已儲存 {fpath}（每通道 {n_samples} 點）"
            )

        self._reset_controls()

    def _reset_controls(self):
        self.dir_btn.setEnabled(True)
        self.start_btn.setEnabled(self.save_dir is not None)
        self.device_edit.setEnabled(True)
        self.rate_spin.setEnabled(True)
        self.chunk_spin.setEnabled(True)

    def on_chunk_ready(self, chunk: np.ndarray):
        # 雛形：只顯示最新一個 chunk，不做長時間卷動視窗
        for ch in range(N_CHANNELS):
            self.curves[ch].setData(chunk[ch])

    def on_error(self, msg: str):
        QMessageBox.critical(self, "DAQ 錯誤", msg)
        self.stop_btn.setEnabled(False)
        self._reset_controls()
        self.status_label.setText("狀態：發生錯誤")

    def closeEvent(self, event):
        if self.worker.task is not None:
            self.worker.stop()
        event.accept()


def main():
    app = QApplication(sys.argv)
    win = MainWindow()
    win.show()
    sys.exit(app.exec())


if __name__ == "__main__":
    main()