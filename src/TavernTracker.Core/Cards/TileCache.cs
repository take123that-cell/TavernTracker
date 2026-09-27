using System.Collections.Concurrent;

namespace TavernTracker.Core.Cards;

/// <summary>
/// Downloads card art once and keeps it in %AppData%\TavernTracker\tiles: the art strips used in card lists
/// and the full Battlegrounds card renders shown on hover (both from art.hearthstonejson.com).
/// </summary>
public sealed class TileCache : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly ConcurrentDictionary<string, Task<string?>> _jobs = new();
    private readonly SemaphoreSlim _parallel = new(6);
    private readonly string _dir;

    public TileCache()
    {
        _dir = Path.Combine(AppPaths.Root, "tiles");
        Directory.CreateDirectory(_dir);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TavernTracker/1.1");
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Path of the downloaded art strip, or null if it can't be fetched.</summary>
    public Task<string?> GetAsync(string cardId) =>
        Fetch(cardId + ".png", $"https://art.hearthstonejson.com/v1/tiles/{cardId}.png");

    /// <summary>Path of the full card picture (Battlegrounds version), or null.</summary>
    public Task<string?> GetRenderAsync(string cardId) =>
        Fetch("render_" + cardId + ".png", $"https://art.hearthstonejson.com/v1/bgs/latest/enUS/256x/{cardId}.png");

    /// <summary>Path of the card's square art (for minion portraits), or null.</summary>
    public Task<string?> GetArtAsync(string cardId) =>
        Fetch("art_" + cardId + ".jpg", $"https://art.hearthstonejson.com/v1/256x/{cardId}.jpg");

    /// <summary>Starts downloading art strips in the background so lists open instantly later.</summary>
    public void Prefetch(IEnumerable<string> cardIds)
    {
        foreach (var id in cardIds) _ = GetAsync(id);
    }

    private Task<string?> Fetch(string file, string url) => _jobs.GetOrAdd(file, key => Task.Run(async () =>
    {
        var path = Path.Combine(_dir, key);
        if (File.Exists(path) && new FileInfo(path).Length > 0) return path;
        await _parallel.WaitAsync().ConfigureAwait(false);
        try
        {
            var bytes = await _http.GetByteArrayAsync(url).ConfigureAwait(false);
            var tmp = path + "." + Guid.NewGuid().ToString("N")[..6] + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
            return path;
        }
        catch
        {
            _jobs.TryRemove(key, out _); // allow a retry later
            return (string?)null;
        }
        finally
        {
            _parallel.Release();
        }
    }));
}
