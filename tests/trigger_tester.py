"""
trigger_tester_sync.py

Hardware-clock-synced TTL trigger test. Instead of software-polling the
line over USB (which is limited by USB round-trip latency and can miss
closely-spaced triggers -- see trigger_tester.py), this opens a DI task
whose sample clock and start trigger are both locked to an AI task's
internal clock. That means the trigger line is sampled at exactly RATE
samples/second by the device's own hardware timing, so edges only need to
be farther apart than 1/RATE to be caught -- at 500 kS/s that's 2 us,
plenty for a Window-mode trigger firing repeatedly.

Edit DEVICE / TRIGGER_LINE / RATE below to match your setup, then just run:
    python trigger_tester_sync.py

Note: AI_CHANNEL just needs to exist on the device to generate the shared
sample clock -- it doesn't need to be connected to anything meaningful,
its readings are discarded here.
"""

import numpy as np
import nidaqmx
from nidaqmx.constants import AcquisitionType, LineGrouping

DEVICE = "Dev2"
TRIGGER_LINE = "port0/line0"   # PFI0 -- where TraNET Trigger Out (Pin 1) is wired
AI_CHANNEL = "ai0"             # any channel; only used to generate the shared sample clock
RATE = 500_000                 # samples/s -- trigger-detection resolution is 1/RATE seconds
CHUNK = 5_000                  # samples read per loop iteration


def main():
    with nidaqmx.Task() as ai_task, nidaqmx.Task() as di_task:
        ai_task.ai_channels.add_ai_voltage_chan(f"{DEVICE}/{AI_CHANNEL}")
        ai_task.timing.cfg_samp_clk_timing(rate=RATE, sample_mode=AcquisitionType.CONTINUOUS)

        di_task.di_channels.add_di_chan(
            f"{DEVICE}/{TRIGGER_LINE}", line_grouping=LineGrouping.CHAN_PER_LINE
        )
        # lock the DI task's sample clock to the AI task's internal clock
        di_task.timing.cfg_samp_clk_timing(
            rate=RATE,
            source=f"/{DEVICE}/ai/SampleClock",
            sample_mode=AcquisitionType.CONTINUOUS,
        )
        # lock the DI task's start to the AI task's start, so sample 0 lines up on both
        di_task.triggers.start_trigger.cfg_dig_edge_start_trig(f"/{DEVICE}/ai/StartTrigger")

        print(f"Watching {DEVICE}/{TRIGGER_LINE} for TTL trigger, "
              f"hardware-synced at {RATE:,} S/s... (Ctrl+C to stop)")

        di_task.start()   # arms and waits for the AI task's start trigger
        ai_task.start()   # fires the shared start trigger -- both tasks begin on the same sample

        sample_index = 0
        count = 0
        last = False
        try:
            while True:
                data = di_task.read(number_of_samples_per_channel=CHUNK, timeout=10.0)
                data = np.asarray(data, dtype=bool)
                if data.size == 0:
                    continue

                # detect rising edges, including one that straddles the previous chunk boundary
                extended = np.concatenate(([last], data))
                edges = np.where(np.diff(extended.astype(np.int8)) == 1)[0]
                for e in edges:
                    global_index = sample_index + e
                    t = global_index / RATE
                    count += 1
                    print(f"Trigger #{count} detected at sample {global_index} (t = {t:.6f} s)")

                last = bool(data[-1])
                sample_index += data.size

                # AI side must be drained too, or its internal buffer will overflow
                ai_task.read(number_of_samples_per_channel=CHUNK, timeout=10.0)
        except KeyboardInterrupt:
            print("\nStopped.")


if __name__ == "__main__":
    main()