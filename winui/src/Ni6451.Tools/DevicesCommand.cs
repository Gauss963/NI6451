using Ni6451.Daq;

namespace Ni6451.Tools;

internal static class DevicesCommand
{
    public static int Run()
    {
        try
        {
            IReadOnlyList<string> devices = DeviceEnumerator.ListDevices();
            if (devices.Count == 0)
            {
                Console.WriteLine("No NI-DAQmx devices found (check driver install / USB connection).");
                return 1;
            }

            foreach (string d in devices) Console.WriteLine(d);
            return 0;
        }
        catch (DllNotFoundException)
        {
            Console.Error.WriteLine("NI-DAQmx is not installed on this machine (nicaiu.dll not found).");
            return 3;
        }
        catch (DaqmxException e)
        {
            Console.Error.WriteLine(e.Message);
            return 3;
        }
    }
}
