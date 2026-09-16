namespace Ni6451.Core;

/// <summary>Fault geometry used by the normal/shear stress conversions.</summary>
public enum FaultType
{
    OneD,
    TwoD,
}

public static class FaultTypeExtensions
{
    public static string ToLabel(this FaultType t) => t == FaultType.OneD ? "1D" : "2D";

    public static FaultType Parse(string label) => label switch
    {
        "1D" => FaultType.OneD,
        "2D" => FaultType.TwoD,
        _ => throw new ArgumentOutOfRangeException(nameof(label), label, "Fault type must be \"1D\" or \"2D\"."),
    };
}

/// <summary>
/// Sensor voltage -> physical unit conversions. Direct port of the Python
/// <c>unit_conversion.py</c> on the <c>main</c> branch; the coefficients and the
/// piston/fault geometry are unchanged, so both implementations report the same
/// numbers for the same input voltage.
/// </summary>
public static class UnitConversion
{
    private const double Mm = 1e-3;
    private const double FaultLengthM = 500 * Mm;

    private const double PressureSlope = 60.482;
    private const double PressureIntercept = -5.497;

    private const double LvdtSlope = 0.504;
    private const double LvdtIntercept = 8.946;
    private const double Cm = 1e-2;

    /// <summary>Oil pressure in Pa.</summary>
    public static double GetOilPressure(double voltage)
        => (PressureSlope * voltage + PressureIntercept) * 1e5;

    /// <summary>
    /// Normal stress in Pa. For <see cref="FaultType.TwoD"/> the fault thickness is
    /// fixed at 50 cm internally (9 pistons), matching the Python behaviour, so the
    /// <paramref name="faultThicknessM"/> argument is ignored in that case.
    /// </summary>
    public static double GetNormalStress(double voltage, FaultType faultType, double faultThicknessM)
    {
        double pistonArea;
        if (faultType == FaultType.OneD)
        {
            pistonArea = 6.21e-3 * 3;
        }
        else
        {
            pistonArea = 6.21e-3 * 9;
            faultThicknessM = 500 * Mm;
        }

        double pressure = GetOilPressure(voltage);
        double faultArea = faultThicknessM * FaultLengthM;
        return pressure * pistonArea / faultArea;
    }

    /// <summary>
    /// Shear stress in Pa. Same 2D thickness override as <see cref="GetNormalStress"/>.
    /// </summary>
    public static double GetShearStress(double voltage, FaultType faultType, double faultThicknessM)
    {
        double pistonArea;
        if (faultType == FaultType.OneD)
        {
            pistonArea = 12.67e-3 * 1;
        }
        else
        {
            pistonArea = 12.67e-3 * 3;
            faultThicknessM = 500 * Mm;
        }

        double pressure = GetOilPressure(voltage);
        double faultArea = faultThicknessM * FaultLengthM;
        return pressure * pistonArea / faultArea;
    }

    /// <summary>LVDT displacement in metres.</summary>
    public static double GetLvdtDisplacement(double voltage)
        => (LvdtSlope * voltage + LvdtIntercept) * Cm;
}
