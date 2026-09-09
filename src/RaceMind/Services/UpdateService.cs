using Velopack;
using Velopack.Sources;

namespace RaceMind.Services;

public sealed class UpdateService
{
    private const string RepoUrl = "https://github.com/rickymaggico-maker/Racemind";

    public async Task<bool> CheckAndApplyAsync()
    {
        try
        {
            var mgr = new UpdateManager(new GithubSource(RepoUrl, null, false));
            var update = await mgr.CheckForUpdatesAsync();
            if (update is null) return false;
            await mgr.DownloadUpdatesAsync(update);
            mgr.ApplyUpdatesAndRestart(update);
            return true;
        }
        catch { return false; }
    }
}
