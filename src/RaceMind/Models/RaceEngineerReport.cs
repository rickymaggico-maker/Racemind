namespace RaceMind.Models;

public enum CornerPhase
{
    Entry,
    Mid,
    Exit
}

public enum HandlingState
{
    Insufficient,
    Neutral,
    Understeer,
    Oversteer,
    Mixed
}

public enum CornerSpeedBand
{
    Low,
    Medium,
    High
}

public sealed record PhaseHandlingSummary(
    CornerPhase Phase,
    HandlingState State,
    int ConfidencePercent,
    int Events,
    int UndersteerEvents,
    int OversteerEvents,
    int NeutralEvents,
    double AverageFrontSlip,
    double AverageRearSlip,
    double AverageFrontLoadN,
    double AverageRearLoadN,
    double AverageSpeedKph);

public sealed record TireSnapshotSummary(
    double FrontLeftPressureKpa,
    double FrontRightPressureKpa,
    double RearLeftPressureKpa,
    double RearRightPressureKpa,
    double FrontLeftPeakTempC,
    double FrontRightPeakTempC,
    double RearLeftPeakTempC,
    double RearRightPeakTempC,
    double FrontLeftWear,
    double FrontRightWear,
    double RearLeftWear,
    double RearRightWear);

public sealed record LapPerformanceSummary(
    IReadOnlyList<double> CompletedLapTimesSeconds,
    double BestLapSeconds,
    double AverageLapSeconds,
    double ConsistencyPercent,
    double PaceTrendSecondsPerLap);

public sealed record DegradationSummary(
    string HandlingTrend,
    double FrontSlipChangePercent,
    double RearSlipChangePercent,
    double FrontWearChange,
    double RearWearChange,
    double PaceChangeSeconds);

public sealed record SetupRecommendation(
    string Problem,
    string Evidence,
    string Change,
    int ConfidencePercent,
    string ExpectedEffect);

public sealed record DriverComparisonSummary(
    int PreviousStints,
    string PaceComparison,
    string BalanceComparison,
    double BestLapDeltaSeconds,
    double HandlingScoreDelta);

public sealed record RaceEngineerReport(
    string Driver,
    ulong DriverSteamId,
    string Vehicle,
    string Track,
    int CompletedLaps,
    int CornerEvents,
    PhaseHandlingSummary Entry,
    PhaseHandlingSummary Mid,
    PhaseHandlingSummary Exit,
    TireSnapshotSummary Tires,
    LapPerformanceSummary Laps,
    DegradationSummary Degradation,
    DriverComparisonSummary DriverComparison,
    IReadOnlyList<SetupRecommendation> Recommendations,
    string Headline,
    string EvidenceSummary);
