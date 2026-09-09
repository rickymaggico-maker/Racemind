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
    long TcActiveSamples,
    string HandlingBalance,
    int HandlingConfidencePercent,
    long UndersteerSamples,
    long OversteerSamples,
    long NeutralSamples,
    double AverageFrontSlip,
    double AverageRearSlip);

/// <summary>
/// RaceMind live vehicle-dynamics engine. It processes native LMU telemetry
/// while the stint is happening; the CSV is only historical storage.
/// Handling balance is classified from front/rear tyre slip during meaningful
/// cornering samples. Low-evidence stints are explicitly reported as inconclusive.
/// </summary>
public sealed class DynamicsAnalyzer
{
    private const double Gravity = 9.80665;
    private long _cornerSamples;
    private double _frontGripSum;
    private double _rearGripSum;
    private double _frontPressureSum;
    private double _rearPressureSum;
    private double _frontSlipSum;
    private double _rearSlipSum;
    private double _peakLateralG;
    private double _peakFrontTemp;
    private double _peakRearTemp;
    private long _absSamples;
    private long _tcSamples;
    private long _understeerSamples;
    private long _oversteerSamples;
    private long _neutralSamples;

    public void Reset()
    {
        _cornerSamples = 0;
        _frontGripSum = 0;
        _rearGripSum = 0;
        _frontPressureSum = 0;
        _rearPressureSum = 0;
        _frontSlipSum = 0;
        _rearSlipSum = 0;
        _peakLateralG = 0;
        _peakFrontTemp = 0;
        _peakRearTemp = 0;
        _absSamples = 0;
        _tcSamples = 0;
        _understeerSamples = 0;
        _oversteerSamples = 0;
        _neutralSamples = 0;
    }

    public void Process(TelemetrySnapshot s)
    {
        if (s.AbsActive) _absSamples++;
        if (s.TcActive) _tcSamples++;

        _peakFrontTemp = Math.Max(_peakFrontTemp, MaxTireTemp(s.FrontLeft, s.FrontRight));
        _peakRearTemp = Math.Max(_peakRearTemp, MaxTireTemp(s.RearLeft, s.RearRight));

        var lateralG = Math.Abs(s.LocalAccelX) / Gravity;
        _peakLateralG = Math.Max(_peakLateralG, lateralG);

        // Exclude straights, pit noise and tiny steering corrections.
        if (s.SpeedKph < 60 || lateralG < 0.35 || Math.Abs(s.Steering) < 0.05)
            return;

        var frontSlip = (WheelSlip(s.FrontLeft) + WheelSlip(s.FrontRight)) / 2.0;
        var rearSlip = (WheelSlip(s.RearLeft) + WheelSlip(s.RearRight)) / 2.0;

        if (!double.IsFinite(frontSlip) || !double.IsFinite(rearSlip))
            return;

        _cornerSamples++;
        _frontSlipSum += frontSlip;
        _rearSlipSum += rearSlip;
        _frontGripSum += (s.FrontLeft.GripFraction + s.FrontRight.GripFraction) / 2.0;
        _rearGripSum += (s.RearLeft.GripFraction + s.RearRight.GripFraction) / 2.0;
        _frontPressureSum += (s.FrontLeft.PressureKpa + s.FrontRight.PressureKpa) / 2.0;
        _rearPressureSum += (s.RearLeft.PressureKpa + s.RearRight.PressureKpa) / 2.0;

        // Compare axle slip rather than a fixed absolute threshold, so the result
        // adapts to different cars, speeds and tyre compounds. A 15% separation is
        // required before calling the sample understeer/oversteer.
        var reference = Math.Max(0.15, (frontSlip + rearSlip) / 2.0);
        var imbalance = (frontSlip - rearSlip) / reference;

        if (imbalance > 0.15)
            _understeerSamples++;
        else if (imbalance < -0.15)
            _oversteerSamples++;
        else
            _neutralSamples++;
    }

    public DynamicsStintSummary Finish()
    {
        var count = Math.Max(1, _cornerSamples);
        var balance = "Analisi insufficiente";
        var confidence = 0;

        if (_cornerSamples >= 100)
        {
            var dominant = Math.Max(_understeerSamples, Math.Max(_oversteerSamples, _neutralSamples));
            var dominance = dominant / (double)_cornerSamples;
            confidence = Math.Clamp((int)Math.Round(dominance * Math.Min(1.0, _cornerSamples / 500.0) * 100.0), 0, 99);

            if (_understeerSamples == dominant && dominance >= 0.45)
                balance = "Sottosterzo prevalente";
            else if (_oversteerSamples == dominant && dominance >= 0.45)
                balance = "Sovrasterzo prevalente";
            else if (_neutralSamples == dominant && dominance >= 0.45)
                balance = "Bilanciamento neutro";
            else
                balance = "Bilanciamento misto";
        }

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
            _tcSamples,
            balance,
            confidence,
            _understeerSamples,
            _oversteerSamples,
            _neutralSamples,
            _frontSlipSum / count,
            _rearSlipSum / count);
    }

    private static double WheelSlip(WheelTelemetry w)
    {
        // Difference between tyre contact-patch velocity and road-relative velocity.
        // The vector magnitude captures both lateral and longitudinal slip.
        var lat = w.LateralPatchVelMS - w.LateralGroundVelMS;
        var lon = w.LongitudinalPatchVelMS - w.LongitudinalGroundVelMS;
        return Math.Sqrt(lat * lat + lon * lon);
    }

    private static double MaxTireTemp(params WheelTelemetry[] wheels) => wheels
        .SelectMany(w => new[] { w.TempLeftC, w.TempCenterC, w.TempRightC })
        .Where(v => double.IsFinite(v) && v > -100 && v < 300)
        .DefaultIfEmpty(0)
        .Max();
}
