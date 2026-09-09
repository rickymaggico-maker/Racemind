using System.IO;
using Velopack;
using Velopack.Sources;

namespace RaceMind.Services;

public enum UpdateCheckResult
{
    UpToDate,
    UpdateAvailable,
    Error
}

public sealed class UpdateService
{
    private const string RepoUrl = "https://github.com/rickymaggico-maker/Racemind";
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RaceMind", "Data", "Logs", "updater.log");

    public string? LastError { get; private set; }

    private static UpdateManager CreateManager() =>
        new(new GithubSource(RepoUrl, null, false));

    public async Task<bool> CheckAndApplyAsync()
    {
        if (!await _gate.WaitAsync(0))
            return false;

        try
        {
            LastError = null;
            await LogAsync("Automatic update check started.");
            var mgr = CreateManager();
            var update = await mgr.CheckForUpdatesAsync();
            if (update is null)
            {
                await LogAsync("No update available.");
                return false;
            }

            await LogAsync("Update found. Download starting.");
            await mgr.DownloadUpdatesAsync(update);
            await LogAsync("Update downloaded. Applying and restarting.");
            mgr.ApplyUpdatesAndRestart(update);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            await LogAsync($"Automatic update failed: {ex}");
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<UpdateCheckResult> CheckManualAsync(bool installWhenAvailable = true)
    {
        await _gate.WaitAsync();
        try
        {
            LastError = null;
            await LogAsync("Manual update check started.");
            var mgr = CreateManager();
            var update = await mgr.CheckForUpdatesAsync();
            if (update is null)
            {
                await LogAsync("Manual check: app is up to date.");
                return UpdateCheckResult.UpToDate;
            }

            await LogAsync("Manual check: update available.");
            if (installWhenAvailable)
            {
                await mgr.DownloadUpdatesAsync(update);
                await LogAsync("Manual check: update downloaded. Applying and restarting.");
                mgr.ApplyUpdatesAndRestart(update);
            }

            return UpdateCheckResult.UpdateAvailable;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            await LogAsync($"Manual update failed: {ex}");
            return UpdateCheckResult.Error;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task LogAsync(string message)
    {
        try
        {
            var directory = Path.GetDirectoryName(LogPath)!;
            Directory.CreateDirectory(directory);
            await File.AppendAllTextAsync(LogPath, $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never break the updater.
        }
    }
}
