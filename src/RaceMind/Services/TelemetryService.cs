using System.IO.MemoryMappedFiles;
using System.Text;
using RaceMind.Models;

namespace RaceMind.Services;

/// <summary>
/// Reads LMU's native LMU_Data shared-memory mapping. It never emits demo data.
/// The offsets below follow LMU's SharedMemoryInterface layout (pack=4):
/// 332 bytes generic + 1300 bytes paths + 126832 bytes scoring, then telemetry.
/// Each player telemetry record is 1888 bytes and LMU exposes 104 slots.
/// </summary>
public sealed class TelemetryService
{
    private const string MapName = "LMU_Data";
    private const long MappingSize = 324_820;
    private const long OffActiveVehicles = 128_464;
    private const long OffPlayerVehicleIdx = 128_465;
    private const long OffPlayerHasVehicle = 128_466;
    private const long OffTelemetryInfo = 128_468;
    private const int VehicleTelemetrySize = 1_888;

    // rF2VehicleTelemetry offsets, pack=4.
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

                // LMU_Data has no begin/end version pair. Re-read the 100 Hz
                // elapsed-time witness and only accept a stable record.
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

                    var speed = Math.Sqrt(vx * vx + vy * vy + vz * vz) * 3.6;
                    var inPitLane = currentSector < 0; // sign bit is LMU/rF2 pit-lane flag

                    snapshot = new TelemetrySnapshot(
                        DateTime.UtcNow,
                        vehicle,
                        track,
                        Math.Max(0, lap + 1),
                        speed,
                        throttle,
                        brake,
                        steering,
                        gear,
                        fuel,
                        InGarage: false,
                        InPitLane: inPitLane);
                }

                if (snapshot is not null)
                    SnapshotReceived?.Invoke(this, snapshot);

                await Task.Delay(20, token); // UI/storage sampling target: ~50 Hz
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
