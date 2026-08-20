"""
Simple example: read a daq_app .npz file, plot every recorded channel with
matplotlib, and show it.

Edit NPZ_PATH below, then just run:
    python read_example.py
"""

import numpy as np
import matplotlib.pyplot as plt

NPZ_PATH = "./T0207-raw-run5-20260820_204711.npz"   # <-- change to your actual .npz file

d = np.load(NPZ_PATH)

rate = int(d["sample_rate"])
channels = d["channels"]              # e.g. [0, 3, 7] -- which ai channels were recorded
trig_idx = int(d["trigger_sample_index"])   # -1 if no trigger was captured

n_samples = len(d[f"ai{channels[0]}"])
if trig_idx >= 0:
    t = (np.arange(n_samples) - trig_idx) / rate   # t = 0 at the trigger
else:
    t = np.arange(n_samples) / rate                # t = 0 at start of recording

fig, axes = plt.subplots(len(channels), 1, sharex=True, figsize=(10, 2 * len(channels)))
if len(channels) == 1:
    axes = [axes]

for ax, ch in zip(axes, channels):
    ax.plot(t, d[f"ai{ch}"], linewidth=0.8)
    ax.set_ylabel(f"ai{ch} [V]")

if trig_idx >= 0:
    for ax in axes:
        ax.axvline(0, color="red", linewidth=0.8, linestyle="--")

axes[-1].set_xlabel("Time [s]")
fig.tight_layout()
plt.savefig(NPZ_PATH.replace(".npz", ".png"), dpi=300)
# plt.show()