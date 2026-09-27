namespace TavernTracker.Core.Leaderboard;

/// <summary>
/// Keeps leaderboard copies fresh. Your own region (solo + duos) is refreshed on a timer, which is
/// what builds up rating history; other regions are only downloaded when you look at them.
/// </summary>
public sealed class SnapshotService : IDisposable
{
    private readonly LeaderboardClient _client;
    private readonly Func<string> _homeRegion;
    private readonly Func<TimeSpan> _interval;
    private readonly Dictionary<string, BoardHistory> _boards = new();
    private readonly Dictionary<string, Task> _running = new();
    private readonly Dictionary<string, DateTime> _lastFailure = new();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>Raised (on a background thread) after a board was refreshed.</summary>
    public event Action<BoardHistory>? Updated;
    /// <summary>Raised (on a background thread) when a download fails.</summary>
    public event Action<string>? Failed;

    public SnapshotService(LeaderboardClient client, Func<string> homeRegion, Func<TimeSpan> interval)
    {
        _client = client;
        _homeRegion = homeRegion;
        _interval = interval;
    }

    public BoardHistory Board(string region, bool duos)
    {
        var key = $"{region}_{duos}";
        lock (_gate)
        {
            if (!_boards.TryGetValue(key, out var b))
            {
                b = new BoardHistory(region, duos);
                b.Load();
                _boards[key] = b;
            }
            return b;
        }
    }

    public void Start() => _loop ??= Task.Run(() => LoopAsync(_cts.Token));

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(2000); } catch { /* shutting down */ }
        _cts.Dispose();
    }

    /// <summary>Downloads a board now unless one is already downloading. Returns when done.</summary>
    public Task RefreshAsync(string region, bool duos)
    {
        var key = $"{region}_{duos}";
        lock (_gate)
        {
            if (_running.TryGetValue(key, out var t) && !t.IsCompleted) return t;
            var task = Task.Run(async () =>
            {
                var board = Board(region, duos);
                try
                {
                    var d = await _client.DownloadAsync(region, duos, _cts.Token).ConfigureAwait(false);
                    board.Ingest(d);
                    Log.Info($"Leaderboard {region} {(duos ? "duos" : "solo")}: {d.Rows.Count} players (season {d.SeasonId})");
                    Updated?.Invoke(board);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Log.Error($"Leaderboard {region} {(duos ? "duos" : "solo")} download failed", ex);
                    lock (_gate) _lastFailure[key] = DateTime.UtcNow;
                    Failed?.Invoke($"Couldn't download the {region} leaderboard. Using the last saved copy.");
                }
            });
            _running[key] = task;
            return task;
        }
    }

    /// <summary>Refresh only if the saved copy is older than <paramref name="maxAge"/>.</summary>
    public Task EnsureFreshAsync(string region, bool duos, TimeSpan maxAge)
    {
        var b = Board(region, duos);
        lock (_gate)
        {
            // After a failure, wait 5 minutes before trying again on our own.
            if (_lastFailure.TryGetValue($"{region}_{duos}", out var failed) && DateTime.UtcNow - failed < TimeSpan.FromMinutes(5))
                return Task.CompletedTask;
        }
        return !b.HasData || DateTime.UtcNow - b.FetchedUtc > maxAge ? RefreshAsync(region, duos) : Task.CompletedTask;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var region = _homeRegion();
            var interval = _interval();
            try
            {
                await Task.WhenAll(
                    EnsureFreshAsync(region, false, interval),
                    EnsureFreshAsync(region, true, interval)).ConfigureAwait(false);
            }
            catch (Exception ex) { Log.Error("Snapshot loop", ex); }

            try { await Task.Delay(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false); }
            catch (TaskCanceledException) { break; }
        }
    }
}
