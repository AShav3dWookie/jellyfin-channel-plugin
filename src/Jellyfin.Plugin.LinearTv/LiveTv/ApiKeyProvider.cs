using MediaBrowser.Controller.Security;

namespace Jellyfin.Plugin.LinearTv.LiveTv;

/// <summary>
/// Supplies the API key for the loopback stream request, creating one named "Linear TV" on first
/// use. Looked up on every tune rather than cached, so a key deleted from the dashboard is
/// simply recreated.
/// </summary>
internal sealed class ApiKeyProvider(IAuthenticationManager authenticationManager)
{
    private const string AppName = "Linear TV";

    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>The plugin's API key. Never log it.</summary>
    public async Task<string> GetAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var key = await FindAsync().ConfigureAwait(false);
            if (key is null)
            {
                await authenticationManager.CreateApiKey(AppName).ConfigureAwait(false);
                key = await FindAsync().ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Created the Linear TV API key but could not read it back.");
            }

            return key;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<string?> FindAsync()
        => (await authenticationManager.GetApiKeys().ConfigureAwait(false))
            .FirstOrDefault(k => k.AppName == AppName)?.AccessToken;
}
