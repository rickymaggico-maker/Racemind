namespace RaceMind.Models;

public sealed record TelemetrySnapshot(
    DateTime Timestamp,
    string Driver,
    string Vehicle,
    string Track,
    int Lap,
    double SpeedKph,
    double Throttle,
    double Brake,
    double Steering,
    int Gear,
    double FuelLitres,
    bool InGarage,
    bool InPitLane);
