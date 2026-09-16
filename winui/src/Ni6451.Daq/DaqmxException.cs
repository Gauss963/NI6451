namespace Ni6451.Daq;

/// <summary>An NI-DAQmx call returned a negative status code.</summary>
public sealed class DaqmxException : Exception
{
    public DaqmxException(int status, string details)
        : base(BuildMessage(status, details))
    {
        Status = status;
        Details = details;
    }

    /// <summary>The raw DAQmx status code.</summary>
    public int Status { get; }

    /// <summary>The driver's extended error text, or an empty string if it was unavailable.</summary>
    public string Details { get; }

    private static string BuildMessage(int status, string details)
        => string.IsNullOrWhiteSpace(details)
            ? $"NI-DAQmx error {status}."
            : $"NI-DAQmx error {status}: {details}";
}
