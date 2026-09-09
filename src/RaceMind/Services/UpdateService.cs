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

    private static UpdateManager CreateManager() =>
        new(new GithubSource(RepoUrl, null, false));

    public async Task<bool> CheckAndApplyAsync()
    {
        try
        {
            var mgr = CreateManager();
            var update = await mgr.CheckForUpdatesAsync();
            if (update is null) return false;
            await mgr.DownloadUpdatesAsync(update);
            mgr.ApplyUpdatesAndRestart(update);
            return true;
        }
        catch { return false; }
    }

    public async Task<UpdateCheckResult> CheckManualAsync(bool installWhenAvailable = true)
    {
        try
        {
            var mgr = CreateManager();
            var update = await mgr.CheckForUpdatesAsync();
            if (update is null) return UpdateCheckResult.UpToDate;

            if (installWhenAvailable)
            {
                await mgr.DownloadUpdatesAsync(update);
                mgr.ApplyUpdatesAndRestart(update);
            }

            return UpdateCheckResult.UpdateAvailable;
        }
        catch
        {
            return UpdateCheckResult.Error;
        }
    }
}
