using System.IO.MemoryMappedFiles;
using System.Text;
using RaceMind.Models;

namespace RaceMind.Services;

/// <summary>
/// Reads LMU's native LMU_Data shared-memory mapping. It never emits demo data.
/// Layout follows LMU's built-in SharedMemoryInterface (pack=4).
/// </summary>
public sealed class TelemetryService
{
    private const string MapName = "LMU_Data";
    private const long MappingSize = 324_820;

    private const long OffScoring = 1_632;
    private const int ScoringInfoSize = 548;
    private const int ScoringStreamSizeField = 12;
    private const long OffVehicleScoring = OffScoring + ScoringInfoSize + ScoringStreamSizeField;
    private const int VehicleScoringSize = 584;
    private const int SId = 0;
    private const int SDriverName = 4;
    private const int SIsPlayer = 196;
    private const int SInPits = 198;
    private const int SInGarageStall = 507;
    private const int SSteamId = 536;

    private const long OffActiveVehicles = 128_464;
    private const long OffPlayerVehicleIdx = 128_465;
    private const long OffPlayerHasVehicle = 128_466;
    private const long OffTelemetryInfo = 128_468;
    private const int VehicleTelemetrySize = 1_888;

    // TelemInfoV01 offsets, pack=4.
    private const int VId = 0;
    private const int VElapsed = 12;
    private const int VLapNumber = 20;
    private const int VVehicleName = 32;
    private const int VTrackName = 96;
    private const int VLocalVel = 184;
    private const int VLocalAccel = 208;
    private const int VLocalRot = 304;
    private const int VGear = 352;
    private const int VThrottle = 388;
    private const int VBrake = 396;
    private const int VSteering = 404;
    private const int VFrontRideHeight = 484;
    private const int VRearRideHeight = 492;
    private const int VFrontDownforce = 508;
    private const int VRearDownforce = 516;
    private const int VFuel = 524;
    private const int VCurrentSector = 600;
    private const int VRearBrakeBias = 664;
    private const int VAbsActive = 746;
    private const int VTcActive = 747;
    private const int VFrontAntiRollBar = 762;
    private const int VRearAntiRollBar = 764;
    private const int VWheels = 848;
    private const int WheelSize = 260;

    // TelemWheelV01 offsets, pack=4.
    private const int WSuspensionDeflection = 0;
    private const int WRideHeight = 8;
    private const int WSuspensionForce = 16;
    private const int WBrakeTemp = 24;
    private const int WBrakePressure = 32;
    private const int WRotation = 40;
    private const int WLateralPatchVel = 48;
    private const int WLongitudinalPatchVel = 56;
    private const int WLateralGroundVel = 64;
    private const int WLongitudinalGroundVel = 72;
    private const int WCamber = 80;
    private const int WLateralForce = 88;
    private const int WLongitudinalForce = 96;
    private const int WTireLoad = 104;
    private const int WGripFraction = 112;
    private const int WPressure = 120;
    private const int WTemperature = 128;
    private const int WWear = 152;
    private const int WCarcassTemp = 204;
    private const int WOptimalTemp = 236;

    public bool IsConnected { get; private set; }
    public event EventHandler<TelemetrySnapshot>? SnapshotReceived;
    public event EventHandler<bool>? ConnectionChanged;

    public Task StartAsync(CancellationToken token) => Task.Run(async () =>
    {
        MemoryMappedFile? map = null;
        MemoryMappedViewAccessor? view = null;

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (map is null || view is null)
                {
                    map = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
                    view = map.CreateViewAccessor(0, MappingSize, MemoryMappedFileAccess.Read);
                    SetConnected(true);
                }

                if (view.ReadByte(OffPlayerHasVehicle) == 0)
                {
                    await Task.Delay(100, token);
                    continue;
                }

                var idx = view.ReadByte(OffPlayerVehicleIdx);
                var active = view.ReadByte(OffActiveVehicles);
                if (idx >= 104 || idx >= active)
                {
                    await Task.Delay(50, token);
                    continue;
                }

                var playerBase = OffTelemetryInfo + idx * VehicleTelemetrySize;
                TelemetrySnapshot? snapshot = null;

                for (var attempt = 0; attempt < 3 && snapshot is null; attempt++)
                {
                    var before = view.ReadDouble(playerBase + VElapsed);
                    var id = view.ReadInt32(playerBase + VId);
                    var lap = view.ReadInt32(playerBase + VLapNumber);
                    var vehicle = ReadFixedUtf8(view, playerBase + VVehicleName, 64);
                    var track = ReadFixedUtf8(view, playerBase + VTrackName, 64);
                    var vx = view.ReadDouble(playerBase + VLocalVel);
                    var vy = view.ReadDouble(playerBase + VLocalVel + 8);
                    var vz = view.ReadDouble(playerBase + VLocalVel + 16);
                    var accelX = view.ReadDouble(playerBase + VLocalAccel);
                    var accelY = view.ReadDouble(playerBase + VLocalAccel + 8);
                    var accelZ = view.ReadDouble(playerBase + VLocalAccel + 16);
                    var pitchRate = view.ReadDouble(playerBase + VLocalRot);
                    var yawRate = view.ReadDouble(playerBase + VLocalRot + 8);
                    var rollRate = view.ReadDouble(playerBase + VLocalRot + 16);
                    var gear = view.ReadInt32(playerBase + VGear);
                    var throttle = Clamp01(view.ReadDouble(playerBase + VThrottle));
                    var brake = Clamp01(view.ReadDouble(playerBase + VBrake));
                    var steering = Math.Clamp(view.ReadDouble(playerBase + VSteering), -1d, 1d);
                    var frontRideHeight = Math.Max(0d, view.ReadDouble(playerBase + VFrontRideHeight));
                    var rearRideHeight = Math.Max(0d, view.ReadDouble(playerBase + VRearRideHeight));
                    var frontDownforce = view.ReadDouble(playerBase + VFrontDownforce);
                    var rearDownforce = view.ReadDouble(playerBase + VRearDownforce);
                    var fuel = Math.Max(0d, view.ReadDouble(playerBase + VFuel));
                    var currentSector = view.ReadInt32(playerBase + VCurrentSector);
                    var rearBrakeBias = Clamp01(view.ReadDouble(playerBase + VRearBrakeBias));
                    var absActive = view.ReadByte(playerBase + VAbsActive) != 0;
                    var tcActive = view.ReadByte(playerBase + VTcActive) != 0;
                    var frontArb = view.ReadByte(playerBase + VFrontAntiRollBar);
                    var rearArb = view.ReadByte(playerBase + VRearAntiRollBar);
                    var fl = ReadWheel(view, playerBase + VWheels + 0 * WheelSize);
                    var fr = ReadWheel(view, playerBase + VWheels + 1 * WheelSize);
                    var rl = ReadWheel(view, playerBase + VWheels + 2 * WheelSize);
                    var rr = ReadWheel(view, playerBase + VWheels + 3 * WheelSize);
                    var after = view.ReadDouble(playerBase + VElapsed);

                    if (id < 0 || before != after)
                        continue;

                    var (driver, steamId, scoringInPits, inGarage) = ReadPlayerScoring(view, id);
                    var speed = Math.Sqrt(vx * vx + vy * vy + vz * vz) * 3.6;
                    var inPitLane = currentSector < 0 || scoringInPits;

                    snapshot = new TelemetrySnapshot(
                        DateTime.UtcNow,
                        driver,
                        steamId,
                        vehicle,
                        track,
                        Math.Max(0, lap + 1),
                        speed,
                        throttle,
                        brake,
                        steering,
                        gear,
                        fuel,
                        accelX,
                        accelY,
                        accelZ,
                        pitchRate,
                        yawRate,
                        rollRate,
                        frontRideHeight,
                        rearRideHeight,
                        frontDownforce,
                        rearDownforce,
                        rearBrakeBias,
                        absActive,
                        tcActive,
                        frontArb,
                        rearArb,
                        fl,
                        fr,
                        rl,
                        rr,
                        InGarage: inGarage,
                        InPitLane: inPitLane);
                }

                if (snapshot is not null)
                    SnapshotReceived?.Invoke(this, snapshot);

                await Task.Delay(20, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                view?.Dispose();
                map?.Dispose();
                view = null;
                map = null;
                SetConnected(false);
                await Task.Delay(500, token);
            }
        }

        view?.Dispose();
        map?.Dispose();
        SetConnected(false);
    }, token);

    private static WheelTelemetry ReadWheel(MemoryMappedViewAccessor view, long wheelBase)
    {
        static double KelvinToC(double value) => value > 100 ? value - 273.15 : value;

        return new WheelTelemetry(
            view.ReadDouble(wheelBase + WSuspensionDeflection),
            Math.Max(0d, view.ReadDouble(wheelBase + WRideHeight)),
            view.ReadDouble(wheelBase + WSuspensionForce),
            view.ReadDouble(wheelBase + WBrakeTemp),
            Clamp01(view.ReadDouble(wheelBase + WBrakePressure)),
            view.ReadDouble(wheelBase + WRotation),
            view.ReadDouble(wheelBase + WLateralPatchVel),
            view.ReadDouble(wheelBase + WLongitudinalPatchVel),
            view.ReadDouble(wheelBase + WLateralGroundVel),
            view.ReadDouble(wheelBase + WLongitudinalGroundVel),
            view.ReadDouble(wheelBase + WCamber),
            view.ReadDouble(wheelBase + WLateralForce),
            view.ReadDouble(wheelBase + WLongitudinalForce),
            Math.Max(0d, view.ReadDouble(wheelBase + WTireLoad)),
            Clamp01(view.ReadDouble(wheelBase + WGripFraction)),
            Math.Max(0d, view.ReadDouble(wheelBase + WPressure)),
            KelvinToC(view.ReadDouble(wheelBase + WTemperature)),
            KelvinToC(view.ReadDouble(wheelBase + WTemperature + 8)),
            KelvinToC(view.ReadDouble(wheelBase + WTemperature + 16)),
            Clamp01(view.ReadDouble(wheelBase + WWear)),
            KelvinToC(view.ReadDouble(wheelBase + WCarcassTemp)),
            view.ReadSingle(wheelBase + WOptimalTemp));
    }

    private static (string Driver, ulong SteamId, bool InPits, bool InGarage) ReadPlayerScoring(MemoryMappedViewAccessor view, int telemetryId)
    {
        string fallbackDriver = string.Empty;
        ulong fallbackSteamId = 0;
        bool fallbackInPits = false;
        bool fallbackInGarage = false;

        for (var i = 0; i < 104; i++)
        {
            var scoringBase = OffVehicleScoring + i * VehicleScoringSize;
            var scoringId = view.ReadInt32(scoringBase + SId);
            if (scoringId != telemetryId)
                continue;

            var driver = ReadFixedUtf8(view, scoringBase + SDriverName, 32);
            var isPlayer = view.ReadByte(scoringBase + SIsPlayer) != 0;
            var inPits = view.ReadByte(scoringBase + SInPits) != 0;
            var inGarage = view.ReadByte(scoringBase + SInGarageStall) != 0;
            var steamId = view.ReadUInt64(scoringBase + SSteamId);

            if (isPlayer)
                return (driver, steamId, inPits, inGarage);

            fallbackDriver = driver;
            fallbackSteamId = steamId;
            fallbackInPits = inPits;
            fallbackInGarage = inGarage;
        }

        return (fallbackDriver, fallbackSteamId, fallbackInPits, fallbackInGarage);
    }

    private void SetConnected(bool value)
    {
        if (value == IsConnected) return;
        IsConnected = value;
        ConnectionChanged?.Invoke(this, value);
    }

    private static double Clamp01(double value) => Math.Clamp(value, 0d, 1d);

    private static string ReadFixedUtf8(MemoryMappedViewAccessor view, long offset, int length)
    {
        var bytes = new byte[length];
        view.ReadArray(offset, bytes, 0, length);
        var end = Array.IndexOf(bytes, (byte)0);
        if (end < 0) end = bytes.Length;
        return Encoding.UTF8.GetString(bytes, 0, end).Trim();
    }
}
