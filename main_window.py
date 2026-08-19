"""
MainWindow: assembles the UI (device selection, output folder, Start/Stop,
live trace display with inline channel checkboxes) and wires user actions
to DAQWorker / FinalizeWorker.
"""

from PySide6.QtWidgets import (
    QMainWindow, QWidget, QVBoxLayout, QHBoxLayout,
    QPushButton, QLabel, QFileDialog, QComboBox, QMessageBox,
    QGroupBox, QFormLayout, QCheckBox, QLineEdit
)

from config import RATE, CHUNK, FLUSH_INTERVAL_SEC, DEFAULT_TRIGGER_LINE, DEFAULT_CAPTURE_TRIGGER
from daq_worker import DAQWorker
from finalize_worker import FinalizeWorker
from devices import list_devices
from plot_widget import LiveTraceWidget


class MainWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("USB-6451 Continuous Acquisition Prototype")
        self.resize(1100, 800)

        self.save_dir = None
        self._error_dialog_open = False
        self.finalize_worker = None
        self.worker = DAQWorker()
        self.worker.chunk_ready.connect(self.on_chunk_ready)
        self.worker.error.connect(self.on_error)

        central = QWidget()
        self.setCentralWidget(central)
        layout = QVBoxLayout(central)

        # --- settings: device selection ---
        settings_box = QGroupBox("Acquisition Settings")
        form = QFormLayout(settings_box)

        device_row = QHBoxLayout()
        self.device_combo = QComboBox()
        self.device_combo.setEditable(True)  # allow manual entry as a fallback
        self.refresh_btn = QPushButton("Refresh")
        self.refresh_btn.clicked.connect(self.refresh_devices)
        device_row.addWidget(self.device_combo, stretch=1)
        device_row.addWidget(self.refresh_btn)
        form.addRow("Device:", device_row)

        form.addRow(QLabel(
            f"Fixed: {RATE:,} S/s/ch, chunk {CHUNK:,} samples, "
            f"flush to disk every {FLUSH_INTERVAL_SEC}s"
        ))

        trigger_row = QHBoxLayout()
        self.capture_trigger_checkbox = QCheckBox("Capture TTL trigger on:")
        self.capture_trigger_checkbox.setChecked(DEFAULT_CAPTURE_TRIGGER)
        self.trigger_line_edit = QLineEdit(DEFAULT_TRIGGER_LINE)
        self.trigger_line_edit.setFixedWidth(120)
        trigger_row.addWidget(self.capture_trigger_checkbox)
        trigger_row.addWidget(self.trigger_line_edit)
        trigger_row.addStretch(1)
        form.addRow(trigger_row)

        layout.addWidget(settings_box)

        # --- output folder + Start/Stop ---
        ctrl_layout = QHBoxLayout()
        self.dir_label = QLabel("No output folder selected")
        self.dir_btn = QPushButton("Choose Output Folder")
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

        # --- live trace display (checkbox + plot per channel, built in) ---
        self.trace_widget = LiveTraceWidget()
        layout.addWidget(self.trace_widget, stretch=1)

        self.status_label = QLabel("Status: idle")
        layout.addWidget(self.status_label)

        self.refresh_devices()

    def refresh_devices(self):
        current = self.device_combo.currentText()
        try:
            devices = list_devices()
        except Exception as e:
            self.status_label.setText(f"Status: could not query NI-DAQmx devices ({e})")
            return

        self.device_combo.clear()
        if devices:
            self.device_combo.addItems(devices)
            if current and current in devices:
                self.device_combo.setCurrentText(current)
        else:
            self.status_label.setText(
                "Status: no NI-DAQmx devices found (check driver install / USB connection)"
            )

    def choose_dir(self):
        d = QFileDialog.getExistingDirectory(self, "Choose Output Folder")
        if d:
            self.save_dir = d
            self.dir_label.setText(d)
            self.start_btn.setEnabled(True)

    def start_acquisition(self):
        device = self.device_combo.currentText().strip()
        if not device:
            QMessageBox.warning(self, "Notice", "Please select or enter a device name.")
            return

        enabled = self.trace_widget.enabled_channels()
        if not enabled:
            QMessageBox.warning(self, "Notice", "Select at least one channel to acquire.")
            return

        self.dir_btn.setEnabled(False)
        self.start_btn.setEnabled(False)
        self.device_combo.setEnabled(False)
        self.refresh_btn.setEnabled(False)
        self.capture_trigger_checkbox.setEnabled(False)
        self.trigger_line_edit.setEnabled(False)
        self.trace_widget.set_locked(True)

        capture_trigger = self.capture_trigger_checkbox.isChecked()
        trigger_line = self.trigger_line_edit.text().strip()

        self.worker.start(device, self.save_dir, enabled,
                           capture_trigger=capture_trigger, trigger_line=trigger_line)
        if self.worker.task is not None:
            self.stop_btn.setEnabled(True)
            self.status_label.setText(f"Status: acquiring ({RATE:,} S/s x {len(enabled)}ch)")
            self.trace_widget.start(enabled)
        else:
            self.trace_widget.set_locked(False)
            self._reset_controls()

    def stop_acquisition(self):
        self.stop_btn.setEnabled(False)
        self.trace_widget.stop()

        result = self.worker.stop_acquisition()
        if result is None:
            QMessageBox.warning(self, "Notice", "No data was acquired.")
            self.status_label.setText("Status: idle")
            self._reset_controls()
            return

        tmp_dir, n_samples, channels, trigger_sample_index = result
        self.status_label.setText(
            f"Status: saving {n_samples:,} samples/channel in the background, please wait..."
        )
        # Controls stay disabled until the background save finishes, so a new
        # acquisition can't be started while the previous one is still being written.
        self.finalize_worker = FinalizeWorker(
            tmp_dir, n_samples, self.save_dir, channels, trigger_sample_index
        )
        self.finalize_worker.finished_ok.connect(self.on_finalize_finished)
        self.finalize_worker.error.connect(self.on_finalize_error)
        self.finalize_worker.start()

    def on_finalize_finished(self, out_path: str, n_samples: int, trigger_sample_index):
        if trigger_sample_index is not None:
            trig_txt = f", trigger at sample {trigger_sample_index:,}"
        else:
            trig_txt = ""
        self.status_label.setText(
            f"Status: saved {out_path} ({n_samples:,} samples/channel{trig_txt})"
        )
        self._reset_controls()

    def on_finalize_error(self, msg: str):
        QMessageBox.critical(self, "Save Error", msg)
        self.status_label.setText("Status: error while saving")
        self._reset_controls()

    def _reset_controls(self):
        self.dir_btn.setEnabled(True)
        self.start_btn.setEnabled(self.save_dir is not None)
        self.device_combo.setEnabled(True)
        self.refresh_btn.setEnabled(True)
        self.capture_trigger_checkbox.setEnabled(True)
        self.trigger_line_edit.setEnabled(True)
        self.trace_widget.set_locked(False)

    def on_chunk_ready(self, chunk):
        self.trace_widget.push_chunk(chunk)

    def on_error(self, msg: str):
        self.trace_widget.stop()
        self.stop_btn.setEnabled(False)
        self._reset_controls()
        self.status_label.setText("Status: error")

        if self._error_dialog_open:
            return  # a dialog for a previous (likely related) error is already showing
        self._error_dialog_open = True
        QMessageBox.critical(self, "DAQ Error", msg)
        self._error_dialog_open = False

    def closeEvent(self, event):
        if self.worker.task is not None:
            self.worker.stop_acquisition()
        if self.finalize_worker is not None and self.finalize_worker.isRunning():
            self.finalize_worker.wait()
        event.accept()
