using RaceMind.Models;

namespace RaceMind.Services;

public sealed record DynamicsStintSummary(
    long CornerSamples,
    double PeakLateralG,
    double AverageFrontGripFraction,
    double AverageRearGripFraction,
    double AverageFrontPressureKpa,
    double AverageRearPressureKpa,
    double PeakFrontTireTempC,
    double PeakRearTireTempC,
    long AbsActiveSamples,
    long TcActiveSamples);

/// <summary>
/// First stage of the RaceMind vehicle-dynamics engine.
/// It processes native LMU telemetry while the stint is happening; it does not
/// reopen or analyse the CSV after the stint. This stage intentionally exposes
/// measured evidence only. Handling diagnosis is added once these signals are
/// validated against real LMU sessions.
/// </summary>
public sealed class DynamicsAnalyzer
{
    private const double Gravity = 9.80665;
    private long _cornerSamples;
    private double _frontGripSum;
    private double _rearGripSum;
    private double _frontPressureSum;
    private double _rearPressureSum;
    private double _peakLateralG;
    private double _peakFrontTemp;
    private double _peakRearTemp;
    private long _absSamples;
    private long _tcSamples;

    public void Reset()
    {
        _cornerSamples = 0;
        _frontGripSum = 0;
        _rearGripSum = 0;
        _frontPressureSum = 0;
        _rearPressureSum = 0;
        _peakLateralG = 0;
        _peakFrontTemp = 0;
        _peakRearTemp = 0;
        _absSamples = 0;
        _tcSamples = 0;
    }

    public void Process(TelemetrySnapshot s)
    {
        if (s.AbsActive) _absSamples++;
        if (s.TcActive) _tcSamples++;

        _peakFrontTemp = Math.Max(_peakFrontTemp, MaxTireTemp(s.FrontLeft, s.FrontRight));
        _peakRearTemp = Math.Max(_peakRearTemp, MaxTireTemp(s.RearLeft, s.RearRight));

        var lateralG = Math.Abs(s.LocalAccelX) / Gravity;
        _peakLateralG = Math.Max(_peakLateralG, lateralG);

        // Only samples with meaningful lateral load belong to the cornering data set.
        // This avoids straights and most pit-lane noise without pretending to identify
        // a specific corner before track segmentation is implemented.
        if (s.SpeedKph < 60 || lateralG < 0.35 || Math.Abs(s.Steering) < 0.05)
            return;

        _cornerSamples++;
        _frontGripSum += (s.FrontLeft.GripFraction + s.FrontRight.GripFraction) / 2.0;
        _rearGripSum += (s.RearLeft.GripFraction + s.RearRight.GripFraction) / 2.0;
        _frontPressureSum += (s.FrontLeft.PressureKpa + s.FrontRight.PressureKpa) / 2.0;
        _rearPressureSum += (s.RearLeft.PressureKpa + s.RearRight.PressureKpa) / 2.0;
    }

    public DynamicsStintSummary Finish()
    {
        var count = Math.Max(1, _cornerSamples);
        return new DynamicsStintSummary(
            _cornerSamples,
            _peakLateralG,
            _frontGripSum / count,
            _rearGripSum / count,
            _frontPressureSum / count,
            _rearPressureSum / count,
            _peakFrontTemp,
            _peakRearTemp,
            _absSamples,
            _tcSamples);
    }

    private static double MaxTireTemp(params WheelTelemetry[] wheels) => wheels
        .SelectMany(w => new[] { w.TempLeftC, w.TempCenterC, w.TempRightC })
        .Where(v => double.IsFinite(v) && v > -100 && v < 300)
        .DefaultIfEmpty(0)
        .Max();
}
