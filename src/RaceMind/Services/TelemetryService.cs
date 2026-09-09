using RaceMind.Models;

namespace RaceMind.Services;

/// <summary>
/// Permanent LMU telemetry boundary. No demo values are emitted.
/// v0.0.1 exposes connection state and is the only class that will know
/// the native LMU shared-memory layout. Future analyzers depend only on snapshots.
/// </summary>
public sealed class TelemetryService
{
    public bool IsConnected { get; private set; }
    public event EventHandler<TelemetrySnapshot>? SnapshotReceived;
    public event EventHandler<bool>? ConnectionChanged;

    public Task StartAsync(CancellationToken token)
    {
        return Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                var available = NativeLmuSharedMemoryProbe.IsAvailable();
                if (available != IsConnected)
                {
                    IsConnected = available;
                    ConnectionChanged?.Invoke(this, IsConnected);
                }
                await Task.Delay(500, token);
            }
        }, token);
    }
}

internal static class NativeLmuSharedMemoryProbe
{
    public static bool IsAvailable()
    {
        try
        {
            using var map = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting("LMU_Data", System.IO.MemoryMappedFiles.MemoryMappedFileRights.Read);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
