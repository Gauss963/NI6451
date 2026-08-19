"""
Minimal CLI test: watch one digital line on the USB-6451 for a TTL trigger
pulse coming from another DAQ (e.g. TraNET EPC Trigger Out), and print
whenever a rising edge is detected.

Edit DEVICE / LINE below to match your setup, then just run:
    python test_trigger.py

Note: this uses simple software-timed polling (a read() call in a tight
loop), which is fine for a quick "am I even getting the signal" check but
can miss very short pulses since it's not hardware-clocked. If you need to
reliably catch short/fast pulses, the next step is hardware change
detection or a sample-clock-synced digital task (see daq_worker.py).
"""

import time

import nidaqmx

DEVICE = "Dev2"           # <-- change to match your NI-MAX device name
LINE = "port0/line0"      # <-- change to whichever PFI/DIO line you wired the trigger to


def main():
    with nidaqmx.Task() as task:
        task.di_channels.add_di_chan(f"{DEVICE}/{LINE}")
        print(f"Watching {DEVICE}/{LINE} for TTL trigger... (Ctrl+C to stop)")

        last = False
        count = 0
        try:
            while True:
                val = task.read()
                if val and not last:
                    count += 1
                    print(f"[{time.strftime('%H:%M:%S')}] Trigger #{count} detected (rising edge)")
                last = val
                time.sleep(0.0005)  # poll roughly every 0.5 ms
        except KeyboardInterrupt:
            print("\nStopped.")


if __name__ == "__main__":
    main()