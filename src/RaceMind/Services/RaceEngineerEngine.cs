using RaceMind.Models;

namespace RaceMind.Services;

/// <summary>
/// Live stint analysis pipeline. Every telemetry sample is processed as it arrives;
/// CSV files are never reopened to produce the report.
/// </summary>
public sealed class RaceEngineerEngine
{
    private const double Gravity = 9.80665;
    private readonly SetupRecommendationEngine _setup = new();
    private readonly DriverProfileStore _profiles = new();
    private readonly List<CornerEvent> _events = new();
    private readonly List<double> _lapTimes = new();

    private CornerAccumulator? _corner;
    private int _nonCornerSamples;
    private int _currentLap;
    private DateTime _lapStartedUtc;
    private TelemetrySnapshot? _last;
    private TireState _tires = TireState.Empty;
    private TireState _startTires = TireState.Empty;
    private long _absSamples;
    private long _tcSamples;

    public void Reset(TelemetrySnapshot first)
    {
        _events.Clear();
        _lapTimes.Clear();
        _corner = null;
        _nonCornerSamples = 0;
        _currentLap = first.Lap;
        _lapStartedUtc = first.Timestamp;
        _last = first;
        _tires = TireState.From(first);
        _startTires = _tires;
        _absSamples = 0;
        _tcSamples = 0;
    }

    public void Process(TelemetrySnapshot s)
    {
        _last = s;
        _tires = _tires.Update(s);
        if (s.AbsActive) _absSamples++;
        if (s.TcActive) _tcSamples++;

        TrackLap(s);

        var lateralG = Math.Abs(s.LocalAccelX) / Gravity;
        var inCorner = s.SpeedKph >= 45 && lateralG >= 0.22 && Math.Abs(s.Steering) >= 0.035;

        if (inCorner)
        {
            _nonCornerSamples = 0;
            _corner ??= new CornerAccumulator();
            _corner.Add(s, DeterminePhase(s, _corner.SampleCount));
        }
        else if (_corner is not null)
        {
            _nonCornerSamples++;
            if (_nonCornerSamples >= 8)
                FinalizeCorner();
        }
    }

    public RaceEngineerReport Finish()
    {
        FinalizeCorner();
        var last = _last ?? throw new InvalidOperationException("RaceEngineerEngine has no telemetry samples.");

        var entry = BuildPhase(CornerPhase.Entry);
        var mid = BuildPhase(CornerPhase.Mid);
        var exit = BuildPhase(CornerPhase.Exit);
        var laps = BuildLapSummary();
        var degradation = BuildDegradation(laps);
        var handlingScore = HandlingScore(entry, mid, exit);
        var comparison = _profiles.CompareAndStore(
            last.Driver, last.DriverSteamId, last.Vehicle, last.Track,
            laps.BestLapSeconds, handlingScore);

        var recommendations = _setup.Build(
            entry, mid, exit,
            last.FrontAntiRollBar, last.RearAntiRollBar, last.RearBrakeBias,
            _absSamples, _tcSamples);

        var headline = BuildHeadline(entry, mid, exit);
        var evidence = BuildEvidence(entry, mid, exit, recommendations);

        return new RaceEngineerReport(
            last.Driver,
            last.DriverSteamId,
            last.Vehicle,
            last.Track,
            _lapTimes.Count,
            _events.Count,
            entry,
            mid,
            exit,
            _tires.ToSummary(),
            laps,
            degradation,
            comparison,
            recommendations,
            headline,
            evidence);
    }

    private void TrackLap(TelemetrySnapshot s)
    {
        if (_currentLap <= 0)
        {
            _currentLap = s.Lap;
            _lapStartedUtc = s.Timestamp;
            return;
        }

        if (s.Lap <= _currentLap)
            return;

        var elapsed = (s.Timestamp - _lapStartedUtc).TotalSeconds;
        if (elapsed is > 20 and < 900)
            _lapTimes.Add(elapsed);

        _currentLap = s.Lap;
        _lapStartedUtc = s.Timestamp;
    }

    private static CornerPhase DeterminePhase(TelemetrySnapshot s, int eventSamples)
    {
        if (s.Brake > 0.06 || (eventSamples < 12 && s.Throttle < 0.35))
            return CornerPhase.Entry;
        if (s.Throttle > 0.38 && s.Brake < 0.04)
            return CornerPhase.Exit;
        return CornerPhase.Mid;
    }

    private void FinalizeCorner()
    {
        if (_corner is null)
            return;

        if (_corner.SampleCount >= 10)
            _events.Add(_corner.Finish());

        _corner = null;
        _nonCornerSamples = 0;
    }

    private PhaseHandlingSummary BuildPhase(CornerPhase phase)
    {
        var observations = _events
            .Select(e => e.Phases.TryGetValue(phase, out var p) ? (Event: e, Phase: p) : default)
            .Where(x => x.Phase is not null && x.Phase.Samples >= 4)
            .ToList();

        if (observations.Count == 0)
            return EmptyPhase(phase);

        var under = 0;
        var over = 0;
        var neutral = 0;
        var speedBands = new Dictionary<CornerSpeedBand, int>();

        foreach (var item in observations)
        {
            var state = Classify(item.Phase!);
            if (state == HandlingState.Understeer) under++;
            else if (state == HandlingState.Oversteer) over++;
            else neutral++;

            var band = SpeedBand(item.Phase!.AverageSpeedKph);
            speedBands[band] = speedBands.GetValueOrDefault(band) + 1;
        }

        var total = observations.Count;
        var dominant = Math.Max(under, Math.Max(over, neutral));
        var dominance = dominant / (double)total;
        var stateResult = total < 3
            ? HandlingState.Insufficient
            : under == dominant && dominance >= 0.50 ? HandlingState.Understeer
            : over == dominant && dominance >= 0.50 ? HandlingState.Oversteer
            : neutral == dominant && dominance >= 0.50 ? HandlingState.Neutral
            : HandlingState.Mixed;

        var evidenceFactor = Math.Min(1.0, total / 8.0);
        var confidence = stateResult == HandlingState.Insufficient
            ? 0
            : Math.Clamp((int)Math.Round(dominance * evidenceFactor * 100), 0, 99);

        return new PhaseHandlingSummary(
            phase,
            stateResult,
            confidence,
            total,
            under,
            over,
            neutral,
            speedBands.OrderByDescending(x => x.Value).First().Key,
            observations.Average(x => x.Phase!.AverageFrontSlip),
            observations.Average(x => x.Phase!.AverageRearSlip),
            observations.Average(x => x.Phase!.AverageFrontLoadN),
            observations.Average(x => x.Phase!.AverageRearLoadN),
            observations.Average(x => x.Phase!.AverageSpeedKph));
    }

    private static HandlingState Classify(PhaseObservation p)
    {
        var reference = Math.Max(0.20, (p.AverageFrontSlip + p.AverageRearSlip) / 2.0);
        var imbalance = (p.AverageFrontSlip - p.AverageRearSlip) / reference;
        if (imbalance > 0.15) return HandlingState.Understeer;
        if (imbalance < -0.15) return HandlingState.Oversteer;
        return HandlingState.Neutral;
    }

    private LapPerformanceSummary BuildLapSummary()
    {
        if (_lapTimes.Count == 0)
            return new LapPerformanceSummary(Array.Empty<double>(), 0, 0, 0, 0);

        var best = _lapTimes.Min();
        var avg = _lapTimes.Average();
        var std = Math.Sqrt(_lapTimes.Select(x => Math.Pow(x - avg, 2)).Average());
        var consistency = avg <= 0 ? 0 : Math.Clamp(100.0 - (std / avg * 100.0), 0, 100);
        var trend = _lapTimes.Count >= 2 ? LinearSlope(_lapTimes) : 0;
        return new LapPerformanceSummary(_lapTimes.ToArray(), best, avg, consistency, trend);
    }

    private DegradationSummary BuildDegradation(LapPerformanceSummary laps)
    {
        if (_events.Count < 4)
            return new DegradationSummary("Dati insufficienti", 0, 0, 0, 0, 0);

        var split = Math.Max(1, _events.Count / 3);
        var early = _events.Take(split).SelectMany(x => x.Phases.Values).ToList();
        var late = _events.TakeLast(split).SelectMany(x => x.Phases.Values).ToList();

        var earlyFront = early.Count == 0 ? 0 : early.Average(x => x.AverageFrontSlip);
        var lateFront = late.Count == 0 ? 0 : late.Average(x => x.AverageFrontSlip);
        var earlyRear = early.Count == 0 ? 0 : early.Average(x => x.AverageRearSlip);
        var lateRear = late.Count == 0 ? 0 : late.Average(x => x.AverageRearSlip);

        var frontChange = PercentChange(earlyFront, lateFront);
        var rearChange = PercentChange(earlyRear, lateRear);
        var frontWearChange = ((_tires.FlWear + _tires.FrWear) - (_startTires.FlWear + _startTires.FrWear)) / 2.0;
        var rearWearChange = ((_tires.RlWear + _tires.RrWear) - (_startTires.RlWear + _startTires.RrWear)) / 2.0;
        var paceChange = laps.CompletedLapTimesSeconds.Count >= 2
            ? laps.CompletedLapTimesSeconds.Last() - laps.CompletedLapTimesSeconds.First()
            : 0;

        var trend = Math.Abs(frontChange - rearChange) < 5
            ? "Bilanciamento stabile"
            : frontChange > rearChange
                ? "Aumento progressivo del sottosterzo"
                : "Aumento progressivo del sovrasterzo";

        return new DegradationSummary(trend, frontChange, rearChange, frontWearChange, rearWearChange, paceChange);
    }

    private static string BuildHeadline(params PhaseHandlingSummary[] phases)
    {
        var strongest = phases
            .Where(x => x.State is HandlingState.Understeer or HandlingState.Oversteer)
            .OrderByDescending(x => x.ConfidencePercent)
            .FirstOrDefault();

        if (strongest is null)
            return phases.All(x => x.State == HandlingState.Insufficient)
                ? "Analisi dinamica insufficiente"
                : "Bilanciamento complessivamente neutro o misto";

        return $"{(strongest.State == HandlingState.Understeer ? "Sottosterzo" : "Sovrasterzo")} ricorrente {PhaseText(strongest.Phase)} nelle curve {SpeedText(strongest.DominantSpeedBand)}";
    }

    private static string BuildEvidence(
        PhaseHandlingSummary entry,
        PhaseHandlingSummary mid,
        PhaseHandlingSummary exit,
        IReadOnlyList<SetupRecommendation> recommendations)
    {
        var strongest = new[] { entry, mid, exit }
            .Where(x => x.State is HandlingState.Understeer or HandlingState.Oversteer)
            .OrderByDescending(x => x.ConfidencePercent)
            .FirstOrDefault();

        if (strongest is null)
            return "Nessuna evidenza dominante abbastanza forte da giustificare una diagnosi specifica.";

        var affected = strongest.State == HandlingState.Understeer ? strongest.UndersteerEvents : strongest.OversteerEvents;
        var recommendationText = recommendations.Count > 0
            ? $" Suggerimento principale: {recommendations[0].Change}"
            : " Nessuna modifica setup viene proposta finché la confidenza non supera la soglia minima.";

        return $"{affected}/{strongest.Events} eventi concordanti; slip medio anteriore {strongest.AverageFrontSlip:0.00} m/s contro {strongest.AverageRearSlip:0.00} m/s al posteriore.{recommendationText}";
    }

    private static PhaseHandlingSummary EmptyPhase(CornerPhase phase) => new(
        phase, HandlingState.Insufficient, 0, 0, 0, 0, 0,
        CornerSpeedBand.Medium, 0, 0, 0, 0, 0);

    private static double HandlingScore(params PhaseHandlingSummary[] phases)
    {
        var values = phases.Where(x => x.Events > 0).Select(x => x.State switch
        {
            HandlingState.Understeer => x.ConfidencePercent / 100.0,
            HandlingState.Oversteer => -x.ConfidencePercent / 100.0,
            _ => 0
        }).ToList();
        return values.Count == 0 ? 0 : values.Average();
    }

    private static double LinearSlope(IReadOnlyList<double> values)
    {
        var n = values.Count;
        var meanX = (n - 1) / 2.0;
        var meanY = values.Average();
        var num = 0d;
        var den = 0d;
        for (var i = 0; i < n; i++)
        {
            num += (i - meanX) * (values[i] - meanY);
            den += Math.Pow(i - meanX, 2);
        }
        return den == 0 ? 0 : num / den;
    }

    private static double PercentChange(double before, double after) => Math.Abs(before) < 0.001 ? 0 : ((after - before) / Math.Abs(before)) * 100.0;
    private static CornerSpeedBand SpeedBand(double speed) => speed < 105 ? CornerSpeedBand.Low : speed < 185 ? CornerSpeedBand.Medium : CornerSpeedBand.High;
    private static string PhaseText(CornerPhase phase) => phase == CornerPhase.Entry ? "in ingresso" : phase == CornerPhase.Mid ? "a centro curva" : "in uscita";
    private static string SpeedText(CornerSpeedBand band) => band == CornerSpeedBand.Low ? "lente" : band == CornerSpeedBand.Medium ? "medio-veloci" : "veloci";

    private sealed class CornerAccumulator
    {
        private readonly Dictionary<CornerPhase, PhaseAccumulator> _phases = new();
        public int SampleCount { get; private set; }

        public void Add(TelemetrySnapshot s, CornerPhase phase)
        {
            SampleCount++;
            if (!_phases.TryGetValue(phase, out var acc))
                _phases[phase] = acc = new PhaseAccumulator();
            acc.Add(s);
        }

        public CornerEvent Finish() => new(_phases.ToDictionary(x => x.Key, x => x.Value.Finish()));
    }

    private sealed class PhaseAccumulator
    {
        private int _samples;
        private double _frontSlip;
        private double _rearSlip;
        private double _frontLoad;
        private double _rearLoad;
        private double _speed;

        public void Add(TelemetrySnapshot s)
        {
            var frontSlip = (WheelSlip(s.FrontLeft) + WheelSlip(s.FrontRight)) / 2.0;
            var rearSlip = (WheelSlip(s.RearLeft) + WheelSlip(s.RearRight)) / 2.0;
            if (!double.IsFinite(frontSlip) || !double.IsFinite(rearSlip)) return;

            _samples++;
            _frontSlip += frontSlip;
            _rearSlip += rearSlip;
            _frontLoad += (s.FrontLeft.TireLoadN + s.FrontRight.TireLoadN) / 2.0;
            _rearLoad += (s.RearLeft.TireLoadN + s.RearRight.TireLoadN) / 2.0;
            _speed += s.SpeedKph;
        }

        public PhaseObservation Finish()
        {
            var n = Math.Max(1, _samples);
            return new PhaseObservation(_samples, _frontSlip / n, _rearSlip / n, _frontLoad / n, _rearLoad / n, _speed / n);
        }
    }

    private static double WheelSlip(WheelTelemetry w)
    {
        var lat = w.LateralPatchVelMS - w.LateralGroundVelMS;
        var lon = w.LongitudinalPatchVelMS - w.LongitudinalGroundVelMS;
        return Math.Sqrt(lat * lat + lon * lon);
    }

    private sealed record CornerEvent(IReadOnlyDictionary<CornerPhase, PhaseObservation> Phases);
    private sealed record PhaseObservation(int Samples, double AverageFrontSlip, double AverageRearSlip, double AverageFrontLoadN, double AverageRearLoadN, double AverageSpeedKph);

    private sealed record TireState(
        double FlPressure, double FrPressure, double RlPressure, double RrPressure,
        double FlPeak, double FrPeak, double RlPeak, double RrPeak,
        double FlWear, double FrWear, double RlWear, double RrWear)
    {
        public static TireState Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        public static TireState From(TelemetrySnapshot s) => new(
            s.FrontLeft.PressureKpa, s.FrontRight.PressureKpa, s.RearLeft.PressureKpa, s.RearRight.PressureKpa,
            Peak(s.FrontLeft), Peak(s.FrontRight), Peak(s.RearLeft), Peak(s.RearRight),
            s.FrontLeft.Wear, s.FrontRight.Wear, s.RearLeft.Wear, s.RearRight.Wear);

        public TireState Update(TelemetrySnapshot s) => new(
            s.FrontLeft.PressureKpa, s.FrontRight.PressureKpa, s.RearLeft.PressureKpa, s.RearRight.PressureKpa,
            Math.Max(FlPeak, Peak(s.FrontLeft)), Math.Max(FrPeak, Peak(s.FrontRight)), Math.Max(RlPeak, Peak(s.RearLeft)), Math.Max(RrPeak, Peak(s.RearRight)),
            s.FrontLeft.Wear, s.FrontRight.Wear, s.RearLeft.Wear, s.RearRight.Wear);

        public TireSnapshotSummary ToSummary() => new(
            FlPressure, FrPressure, RlPressure, RrPressure,
            FlPeak, FrPeak, RlPeak, RrPeak,
            FlWear, FrWear, RlWear, RrWear);

        private static double Peak(WheelTelemetry w) => new[] { w.TempLeftC, w.TempCenterC, w.TempRightC }
            .Where(x => double.IsFinite(x) && x > -100 && x < 300)
            .DefaultIfEmpty(0)
            .Max();
    }
}
