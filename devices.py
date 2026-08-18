"""Helpers for discovering NI-DAQmx devices currently visible to the driver."""

import nidaqmx.system


def list_devices():
    """Return the names of all devices NI-DAQmx currently sees, e.g. ['Dev1']."""
    system = nidaqmx.system.System.local()
    return [d.name for d in system.devices]
