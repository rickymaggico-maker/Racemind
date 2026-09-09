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

    // SharedMemoryObjectOut: generic (332) + paths (1300) + scoring (126832) + telemetry.
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
    private const int VGear = 352;
    private const int VThrottle = 388;
    private const int VBrake = 396;
    private const int VSteering = 404;
    private const int VFuel = 524;
    private const int VCurrentSector = 600;

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

                // LMU_Data has no begin/end version pair. Re-read the elapsed-time witness
                // and only accept a stable player telemetry record.
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
                    var gear = view.ReadInt32(playerBase + VGear);
                    var throttle = Clamp01(view.ReadDouble(playerBase + VThrottle));
                    var brake = Clamp01(view.ReadDouble(playerBase + VBrake));
                    var steering = Math.Clamp(view.ReadDouble(playerBase + VSteering), -1d, 1d);
                    var fuel = Math.Max(0d, view.ReadDouble(playerBase + VFuel));
                    var currentSector = view.ReadInt32(playerBase + VCurrentSector);
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
