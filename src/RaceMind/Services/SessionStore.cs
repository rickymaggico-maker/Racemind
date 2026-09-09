using System.Globalization;
using RaceMind.Models;

namespace RaceMind.Services;

public sealed class SessionStore
{
    public string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RaceMind", "Data", "Sessions");
    public SessionStore() => Directory.CreateDirectory(Root);

    public async Task AppendAsync(string sessionId, TelemetrySnapshot s)
    {
        var file = Path.Combine(Root, sessionId + ".csv");
        var exists = File.Exists(file);
        await using var sw = new StreamWriter(file, append: true);
        if (!exists)
            await sw.WriteLineAsync("timestamp,vehicle,track,lap,speed_kph,throttle,brake,steering,gear,fuel_l,in_garage,in_pit_lane");
        await sw.WriteLineAsync(string.Join(',',
            s.Timestamp.ToString("O", CultureInfo.InvariantCulture), Csv(s.Vehicle), Csv(s.Track), s.Lap,
            s.SpeedKph.ToString(CultureInfo.InvariantCulture), s.Throttle.ToString(CultureInfo.InvariantCulture),
            s.Brake.ToString(CultureInfo.InvariantCulture), s.Steering.ToString(CultureInfo.InvariantCulture), s.Gear,
            s.FuelLitres.ToString(CultureInfo.InvariantCulture), s.InGarage, s.InPitLane));
    }

    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
}
