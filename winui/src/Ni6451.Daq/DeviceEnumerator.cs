namespace Ni6451.Daq;

/// <summary>
/// Discovers the NI-DAQmx devices currently visible to the driver. Port of the Python
/// <c>devices.py</c>.
/// </summary>
public static class DeviceEnumerator
{
    /// <summary>
    /// The names of all devices NI-DAQmx currently sees, e.g. <c>["Dev1"]</c>.
    /// Throws <see cref="DaqmxException"/> if the driver rejects the query, or
    /// <see cref="DllNotFoundException"/> if NI-DAQmx is not installed at all.
    /// </summary>
    public static IReadOnlyList<string> ListDevices()
    {
        string names = NiDaqmx.GetSystemDeviceNames();
        if (string.IsNullOrWhiteSpace(names)) return [];

        return names
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    /// <summary>
    /// Whether the NI-DAQmx runtime can be loaded at all. Used to show a useful message
    /// instead of an unhandled <see cref="DllNotFoundException"/> on a machine without the
    /// driver installed.
    /// </summary>
    public static bool IsDriverAvailable()
    {
        try
        {
            NiDaqmx.GetSystemDeviceNames();
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
        catch (DaqmxException)
        {
            return true;   // driver answered, it just didn't like the call
        }
    }
}
