using System.Globalization;
using System.IO;
using RaceMind.Models;

namespace RaceMind.Services;

public sealed class SessionStore
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    public string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RaceMind", "Data", "Sessions");
    public SessionStore() => Directory.CreateDirectory(Root);

    public async Task AppendAsync(string sessionId, TelemetrySnapshot s)
    {
        await _writeLock.WaitAsync();
        try
        {
            var file = Path.Combine(Root, sessionId + ".csv");
            var exists = File.Exists(file);
            await using var sw = new StreamWriter(file, append: true);
            if (!exists)
                await sw.WriteLineAsync("timestamp,driver,driver_steam_id,vehicle,track,lap,speed_kph,throttle,brake,steering,gear,fuel_l,accel_x,accel_y,accel_z,pitch_rate,yaw_rate,roll_rate,front_ride_height_m,rear_ride_height_m,front_downforce_n,rear_downforce_n,rear_brake_bias,abs_active,tc_active,front_arb,rear_arb,fl_grip,fr_grip,rl_grip,rr_grip,fl_pressure_kpa,fr_pressure_kpa,rl_pressure_kpa,rr_pressure_kpa,fl_temp_c,fr_temp_c,rl_temp_c,rr_temp_c,fl_load_n,fr_load_n,rl_load_n,rr_load_n,fl_brake_temp_c,fr_brake_temp_c,rl_brake_temp_c,rr_brake_temp_c,in_garage,in_pit_lane");

            await sw.WriteLineAsync(string.Join(',',
                s.Timestamp.ToString("O", CultureInfo.InvariantCulture), Csv(s.Driver), s.DriverSteamId.ToString(CultureInfo.InvariantCulture),
                Csv(s.Vehicle), Csv(s.Track), s.Lap,
                N(s.SpeedKph), N(s.Throttle), N(s.Brake), N(s.Steering), s.Gear, N(s.FuelLitres),
                N(s.LocalAccelX), N(s.LocalAccelY), N(s.LocalAccelZ), N(s.PitchRateRadS), N(s.YawRateRadS), N(s.RollRateRadS),
                N(s.FrontRideHeightM), N(s.RearRideHeightM), N(s.FrontDownforceN), N(s.RearDownforceN), N(s.RearBrakeBias),
                s.AbsActive, s.TcActive, s.FrontAntiRollBar, s.RearAntiRollBar,
                N(s.FrontLeft.GripFraction), N(s.FrontRight.GripFraction), N(s.RearLeft.GripFraction), N(s.RearRight.GripFraction),
                N(s.FrontLeft.PressureKpa), N(s.FrontRight.PressureKpa), N(s.RearLeft.PressureKpa), N(s.RearRight.PressureKpa),
                N(AvgTemp(s.FrontLeft)), N(AvgTemp(s.FrontRight)), N(AvgTemp(s.RearLeft)), N(AvgTemp(s.RearRight)),
                N(s.FrontLeft.TireLoadN), N(s.FrontRight.TireLoadN), N(s.RearLeft.TireLoadN), N(s.RearRight.TireLoadN),
                N(s.FrontLeft.BrakeTempC), N(s.FrontRight.BrakeTempC), N(s.RearLeft.BrakeTempC), N(s.RearRight.BrakeTempC),
                s.InGarage, s.InPitLane));
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static double AvgTemp(WheelTelemetry w) => (w.TempLeftC + w.TempCenterC + w.TempRightC) / 3.0;
    private static string N(double value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
}
