"""
All tunable parameters live here. Adjust sample rate / chunk size / flush
interval / display settings by editing this file only -- no need to touch
any logic code.
"""

N_CHANNELS = 16
RATE = 500_000               # samples/s per channel (max rate for 16 single-ended channels on this hardware)
CHUNK = 5_000                 # samples per DAQmx callback
FLUSH_INTERVAL_SEC = 10
FLUSH_SAMPLES = RATE * FLUSH_INTERVAL_SEC  # samples/channel accumulated before writing to disk

PLOT_REFRESH_MS = 50          # UI redraw interval in ms (20 fps), decoupled from the DAQ callback rate
DISPLAY_RATE_HZ = 2000        # decimated sample rate kept in the live-plot rolling buffer
MAX_PLOT_POINTS = 2000        # hard cap on points actually rendered per channel per frame,
                               # regardless of the selected time window -- this is what keeps
                               # redraw cost (and therefore GIL contention with the DAQ callback
                               # thread) bounded even at a 30s window

MIN_WINDOW_SEC = 1
MAX_WINDOW_SEC = 30
DEFAULT_WINDOW_SEC = 5

MIN_Y_RANGE = 0.1
MAX_Y_RANGE = 10.0
DEFAULT_Y_RANGE = 10.0

# Live-plot styling for channels that are turned off (not being acquired)
CHANNEL_ON_COLOR = "#1f77b4"
CHANNEL_OFF_COLOR = "#b0b0b0"
CHANNEL_OFF_FACECOLOR = "#e8e8e8"
