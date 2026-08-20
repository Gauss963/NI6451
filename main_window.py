"""
MainWindow: assembles the UI (device selection, output folder, Start/Stop,
live trace display with inline channel checkboxes, live sensor readout) and
wires user actions to DAQWorker / FinalizeWorker.
"""

from PySide6.QtCore import QTimer
from PySide6.QtGui import QIntValidator
from PySide6.QtWidgets import (
    QMainWindow, QWidget, QVBoxLayout, QHBoxLayout,
    QPushButton, QLabel, QFileDialog, QComboBox, QMessageBox,
    QGroupBox, QFormLayout, QCheckBox, QLineEdit, QSpinBox
)

from config import RATE, CHUNK, FLUSH_INTERVAL_SEC, DEFAULT_TRIGGER_LINE, DEFAULT_CAPTURE_TRIGGER
from daq_worker import DAQWorker
from finalize_worker import FinalizeWorker
from devices import list_devices
from plot_widget import LiveTraceWidget
from unit_conversion import get_normal_stress, get_shear_stress, get_LVDT_displacement

READOUT_INTERVAL_MS = 100  # 10 fps -- deliberately slower than the plot's own refresh rate

# ai0 normal-stress setup: fault type -> available thicknesses, in (label, meters) pairs.
# 2D is fixed at 50 cm inside get_normal_stress() itself, so the value passed for it doesn't matter.
THICKNESS_1D_OPTIONS = [("5 cm", 0.05), ("10 cm", 0.10)]
THICKNESS_2D_OPTIONS = [("50 cm (fixed, 9 pistons)", 0.50)]

# fixed sensor-to-channel wiring for the live readout
CH_NORMAL_STRESS = 0   # ai0
CH_SHEAR_STRESS = 1    # ai1
CH_LVDT = 2             # ai2


class MainWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("USB-6451 Continuous Acquisition Prototype")
        self.resize(1100, 850)

        self.save_dir = None
        self._error_dialog_open = False
        self.finalize_worker = None
        self._latest_voltages = {}   # {channel_number: most recent voltage sample}
        self._current_sh = "0000"
        self._current_rn = 0

        self.worker = DAQWorker()
        self.worker.chunk_ready.connect(self.on_chunk_ready)
        self.worker.error.connect(self.on_error)

        central = QWidget()
        self.setCentralWidget(central)
        layout = QVBoxLayout(central)

        # --- live sensor readout: Trigger status pinned left, sensor values pinned right ---
        readout_layout = QHBoxLayout()
        self.trigger_status_label = self._make_readout_label("Trigger: No", color="red")
        readout_layout.addWidget(self.trigger_status_label)
        readout_layout.addStretch(1)
        self.normal_stress_label = self._make_readout_label("Normal: -- MPa", color="blue")
        self.shear_stress_label = self._make_readout_label("Shear: -- MPa", color="blue")
        self.lvdt_label = self._make_readout_label("LVDT: -- mm", color="blue")
        readout_layout.addWidget(self.normal_stress_label)
        readout_layout.addWidget(self.shear_stress_label)
        readout_layout.addWidget(self.lvdt_label)
        layout.addLayout(readout_layout)

        self._readout_timer = QTimer(self)
        self._readout_timer.setInterval(READOUT_INTERVAL_MS)
        self._readout_timer.timeout.connect(self._update_readout)

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

        # --- ai0 normal-stress fault setup ---
        fault_box = QGroupBox("ai0: Normal Stress Setup")
        fault_form = QFormLayout(fault_box)

        fault_row = QHBoxLayout()
        self.fault_type_combo = QComboBox()
        self.fault_type_combo.addItems(["1D", "2D"])
        self.fault_type_combo.currentTextChanged.connect(self._on_fault_type_changed)
        self.fault_thickness_combo = QComboBox()
        fault_row.addWidget(QLabel("Fault type:"))
        fault_row.addWidget(self.fault_type_combo)
        fault_row.addSpacing(16)
        fault_row.addWidget(QLabel("Thickness:"))
        fault_row.addWidget(self.fault_thickness_combo)
        fault_row.addStretch(1)
        fault_form.addRow(fault_row)
        layout.addWidget(fault_box)
        self._on_fault_type_changed(self.fault_type_combo.currentText())  # populate thickness options

        # --- output naming ---
        naming_box = QGroupBox("Output Naming")
        naming_form = QFormLayout(naming_box)
        naming_row = QHBoxLayout()
        self.sh_edit = QLineEdit("0114")
        self.sh_edit.setValidator(QIntValidator(0, 9999))
        self.sh_edit.setFixedWidth(60)
        self.rn_spin = QSpinBox()
        self.rn_spin.setRange(0, 999999)
        self.rn_spin.setValue(1)
        naming_row.addWidget(QLabel("SH (4-digit, zero-padded):"))
        naming_row.addWidget(self.sh_edit)
        naming_row.addSpacing(16)
        naming_row.addWidget(QLabel("RN (run number):"))
        naming_row.addWidget(self.rn_spin)
        naming_row.addStretch(1)
        naming_form.addRow(naming_row)
        self.filename_preview_label = QLabel()
        naming_form.addRow(self.filename_preview_label)
        self.sh_edit.textChanged.connect(self._update_filename_preview)
        self.rn_spin.valueChanged.connect(self._update_filename_preview)
        layout.addWidget(naming_box)
        self._update_filename_preview()

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

    # ---------- small UI helpers ----------
    def _make_readout_label(self, text: str, color: str = "blue") -> QLabel:
        label = QLabel(text)
        label.setStyleSheet(f"color: {color}; font-size: 22px; font-weight: bold;")
        return label

    def _on_fault_type_changed(self, fault_type: str):
        self.fault_thickness_combo.clear()
        options = THICKNESS_1D_OPTIONS if fault_type == "1D" else THICKNESS_2D_OPTIONS
        for label, _ in options:
            self.fault_thickness_combo.addItem(label)
        self.fault_thickness_combo.setEnabled(fault_type == "1D")

    def _current_fault_thickness_m(self) -> float:
        fault_type = self.fault_type_combo.currentText()
        options = THICKNESS_1D_OPTIONS if fault_type == "1D" else THICKNESS_2D_OPTIONS
        idx = max(0, self.fault_thickness_combo.currentIndex())
        return options[idx][1]

    def _update_filename_preview(self):
        sh = self.sh_edit.text().strip().zfill(4)
        rn = self.rn_spin.value()
        self.filename_preview_label.setText(
            f"Will save as: T{sh}-raw-run{rn}-<timestamp>.npz"
        )

    # ---------- device list ----------
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

    # ---------- acquisition control ----------
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
        self.fault_type_combo.setEnabled(False)
        self.fault_thickness_combo.setEnabled(False)
        self.sh_edit.setEnabled(False)
        self.rn_spin.setEnabled(False)
        self.trace_widget.set_locked(True)

        # capture the naming/fault settings now, so later edits don't retroactively
        # affect the file this run is about to produce
        self._current_sh = self.sh_edit.text().strip().zfill(4)
        self._current_rn = self.rn_spin.value()

        capture_trigger = self.capture_trigger_checkbox.isChecked()
        trigger_line = self.trigger_line_edit.text().strip()

        self._latest_voltages = {}
        self.trigger_status_label.setText("Trigger: No")
        self.trigger_status_label.setStyleSheet("color: red; font-size: 22px; font-weight: bold;")

        self.worker.start(device, self.save_dir, enabled,
                           capture_trigger=capture_trigger, trigger_line=trigger_line)
        if self.worker.task is not None:
            self.stop_btn.setEnabled(True)
            self.status_label.setText(f"Status: acquiring ({RATE:,} S/s x {len(enabled)}ch)")
            self.trace_widget.start(enabled)
            self._readout_timer.start()
        else:
            self.trace_widget.set_locked(False)
            self._reset_controls()

    def stop_acquisition(self):
        self.stop_btn.setEnabled(False)
        self.trace_widget.stop()
        self._readout_timer.stop()

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
            tmp_dir, n_samples, self.save_dir, channels, trigger_sample_index,
            sh=self._current_sh, rn=self._current_rn,
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
        self.fault_type_combo.setEnabled(True)
        self._on_fault_type_changed(self.fault_type_combo.currentText())
        self.sh_edit.setEnabled(True)
        self.rn_spin.setEnabled(True)
        self.trace_widget.set_locked(False)

    # ---------- live data ----------
    def on_chunk_ready(self, chunk):
        self.trace_widget.push_chunk(chunk)
        # cheap: just remember each active channel's most recent sample for the readout
        for i, ch in enumerate(self.trace_widget.active_channels):
            self._latest_voltages[ch] = float(chunk[i, -1])

    def _update_readout(self):
        if self.worker.trigger_sample_index is not None:
            self.trigger_status_label.setText("Trigger: Yes")
            self.trigger_status_label.setStyleSheet("color: blue; font-size: 22px; font-weight: bold;")
        else:
            self.trigger_status_label.setText("Trigger: No")
            self.trigger_status_label.setStyleSheet("color: red; font-size: 22px; font-weight: bold;")

        v0 = self._latest_voltages.get(CH_NORMAL_STRESS)
        if v0 is None:
            self.normal_stress_label.setText("Normal: -- MPa")
        else:
            fault_type = self.fault_type_combo.currentText()
            thickness_m = self._current_fault_thickness_m()
            normal_mpa = get_normal_stress(v0, fault_type, thickness_m) / 1e6
            self.normal_stress_label.setText(f"Normal: {normal_mpa:.1f} MPa")

        v1 = self._latest_voltages.get(CH_SHEAR_STRESS)
        if v1 is None:
            self.shear_stress_label.setText("Shear: -- MPa")
        else:
            shear_mpa = get_shear_stress(v1) / 1e6
            self.shear_stress_label.setText(f"Shear: {shear_mpa:.1f} MPa")

        v2 = self._latest_voltages.get(CH_LVDT)
        if v2 is None:
            self.lvdt_label.setText("LVDT: -- mm")
        else:
            lvdt_mm = get_LVDT_displacement(v2) * 1000
            self.lvdt_label.setText(f"LVDT: {lvdt_mm:.1f} mm")

    def on_error(self, msg: str):
        # Treat a hardware error the same as pressing Stop: properly close
        # the task(s) and try to salvage whatever was already captured,
        # instead of leaving an orphaned task running in the background
        # with the Stop button disabled (which used to let a second Start
        # open a second task on top of the still-running first one).
        if self.worker.task is not None:
            self.stop_acquisition()
        else:
            self.trace_widget.stop()
            self._readout_timer.stop()
            self._reset_controls()

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