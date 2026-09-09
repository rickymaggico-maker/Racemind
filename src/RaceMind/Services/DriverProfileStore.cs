using System.Text.Json;
using RaceMind.Models;

namespace RaceMind.Services;

public sealed class DriverProfileStore
{
    private sealed record StoredStint(DateTime Timestamp, string Vehicle, string Track, double BestLapSeconds, double HandlingScore);
    private sealed record StoredDriver(string Driver, ulong SteamId, List<StoredStint> Stints);

    private readonly object _sync = new();
    private readonly string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RaceMind", "Data", "Drivers");

    public DriverProfileStore() => Directory.CreateDirectory(_root);

    public DriverComparisonSummary CompareAndStore(
        string driver,
        ulong steamId,
        string vehicle,
        string track,
        double bestLapSeconds,
        double handlingScore)
    {
        lock (_sync)
        {
            var path = Path.Combine(_root, DriverKey(driver, steamId) + ".json");
            var profile = Load(path, driver, steamId);
            var comparable = profile.Stints
                .Where(x => string.Equals(x.Vehicle, vehicle, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(x.Track, track, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.Timestamp)
                .ToList();

            var previousCount = comparable.Count;
            var lapDelta = 0d;
            var handlingDelta = 0d;
            var paceText = "Primo stint comparabile";
            var balanceText = "Profilo in costruzione";

            if (comparable.Count > 0)
            {
                var previousBest = comparable.Where(x => x.BestLapSeconds > 0).Select(x => x.BestLapSeconds).DefaultIfEmpty(0).Min();
                if (bestLapSeconds > 0 && previousBest > 0)
                {
                    lapDelta = bestLapSeconds - previousBest;
                    paceText = lapDelta < -0.05
                        ? $"Migliorato di {Math.Abs(lapDelta):0.00}s rispetto al miglior stint precedente"
                        : lapDelta > 0.05
                            ? $"Più lento di {lapDelta:0.00}s rispetto al miglior stint precedente"
                            : "Passo in linea con il miglior stint precedente";
                }

                var previousHandling = comparable.Average(x => x.HandlingScore);
                handlingDelta = handlingScore - previousHandling;
                balanceText = Math.Abs(handlingDelta) < 0.05
                    ? "Bilanciamento simile agli stint precedenti"
                    : handlingDelta > 0
                        ? "Più tendenza al sottosterzo rispetto agli stint precedenti"
                        : "Più tendenza al sovrasterzo rispetto agli stint precedenti";
            }

            profile.Stints.Add(new StoredStint(DateTime.UtcNow, vehicle, track, bestLapSeconds, handlingScore));
            if (profile.Stints.Count > 100)
                profile.Stints.RemoveRange(0, profile.Stints.Count - 100);
            Save(path, profile);

            return new DriverComparisonSummary(previousCount, paceText, balanceText, lapDelta, handlingDelta);
        }
    }

    private static StoredDriver Load(string path, string driver, ulong steamId)
    {
        try
        {
            if (File.Exists(path))
            {
                var parsed = JsonSerializer.Deserialize<StoredDriver>(File.ReadAllText(path));
                if (parsed is not null)
                    return parsed;
            }
        }
        catch
        {
            // A damaged profile must never interrupt telemetry analysis.
        }
        return new StoredDriver(driver, steamId, new List<StoredStint>());
    }

    private static void Save(string path, StoredDriver profile)
    {
        try
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, true);
        }
        catch
        {
            // Persistence is secondary to live analysis.
        }
    }

    private static string DriverKey(string driver, ulong steamId)
    {
        if (steamId != 0) return $"steam_{steamId}";
        var chars = (driver ?? "driver").Where(char.IsLetterOrDigit).ToArray();
        return chars.Length == 0 ? "driver" : new string(chars).ToLowerInvariant();
    }
}
