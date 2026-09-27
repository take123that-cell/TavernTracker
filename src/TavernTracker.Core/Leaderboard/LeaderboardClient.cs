using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace TavernTracker.Core.Leaderboard;

public sealed record LeaderboardRow(int Rank, string Name, int Rating);

public sealed class LeaderboardDownload
{
    public required string Region { get; init; }
    public required bool Duos { get; init; }
    public int SeasonId { get; init; }
    public DateTime FetchedUtc { get; init; }
    public required IReadOnlyList<LeaderboardRow> Rows { get; init; }
}

/// <summary>Downloads Blizzard's public Battlegrounds leaderboard (hearthstone.blizzard.com), page by page.</summary>
public sealed class LeaderboardClient : IDisposable
{
    public static readonly string[] Regions = { "US", "EU", "AP" };

    private const string UrlFormat =
        "https://hearthstone.blizzard.com/en-us/api/community/leaderboardsData?region={0}&leaderboardId={1}&page={2}";
    private const int Parallel = 6;
    private const int MaxPages = 800; // 25 rows each: 20,000 players

    private readonly Func<string, CancellationToken, Task<Response?>> _getPage;
    private readonly HttpClient? _http;

    public LeaderboardClient()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TavernTracker/1.0");
        _getPage = (url, ct) => _http.GetFromJsonAsync<Response>(url, ct);
    }

    /// <summary>For tests: supply the page fetcher.</summary>
    public LeaderboardClient(Func<string, CancellationToken, Task<Response?>> getPage) => _getPage = getPage;

    public void Dispose() => _http?.Dispose();

    public static string BoardId(bool duos) => duos ? "battlegroundsduo" : "battlegrounds";

    public async Task<LeaderboardDownload> DownloadAsync(string region, bool duos, CancellationToken ct = default)
    {
        var first = await FetchAsync(region, duos, 1, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Empty leaderboard response");
        int total = Math.Clamp(first.Leaderboard?.Pagination?.TotalPages ?? 1, 1, MaxPages);

        var pages = new Response?[total];
        pages[0] = first;
        using var gate = new SemaphoreSlim(Parallel);
        await Task.WhenAll(Enumerable.Range(2, Math.Max(0, total - 1)).Select(async p =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { pages[p - 1] = await FetchAsync(region, duos, p, ct).ConfigureAwait(false); }
            finally { gate.Release(); }
        })).ConfigureAwait(false);

        var rows = pages
            .Where(p => p?.Leaderboard?.Rows != null)
            .SelectMany(p => p!.Leaderboard!.Rows!)
            .Where(r => !string.IsNullOrWhiteSpace(r.AccountId))
            .Select(r => new LeaderboardRow(r.Rank, r.AccountId!.Trim(), r.Rating))
            .OrderBy(r => r.Rank)
            .ToList();

        return new LeaderboardDownload
        {
            Region = region,
            Duos = duos,
            SeasonId = first.SeasonId,
            FetchedUtc = DateTime.UtcNow,
            Rows = rows,
        };
    }

    private async Task<Response?> FetchAsync(string region, bool duos, int page, CancellationToken ct)
    {
        var url = string.Format(UrlFormat, region, BoardId(duos), page);
        Exception? last = null;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try { return await _getPage(url, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(700 * attempt, ct).ConfigureAwait(false);
            }
        }
        throw new InvalidOperationException($"Leaderboard page {page} failed", last);
    }

    // JSON shapes (names match Blizzard's API).
    public sealed class Response
    {
        [JsonPropertyName("seasonId")] public int SeasonId { get; set; }
        [JsonPropertyName("leaderboard")] public Board? Leaderboard { get; set; }
    }

    public sealed class Board
    {
        [JsonPropertyName("rows")] public List<Row>? Rows { get; set; }
        [JsonPropertyName("pagination")] public Pagination? Pagination { get; set; }
    }

    public sealed class Pagination
    {
        [JsonPropertyName("totalPages")] public int TotalPages { get; set; }
        [JsonPropertyName("totalSize")] public int TotalSize { get; set; }
    }

    public sealed class Row
    {
        [JsonPropertyName("rank")] public int Rank { get; set; }
        [JsonPropertyName("accountid")] public string? AccountId { get; set; }
        [JsonPropertyName("rating")] public int Rating { get; set; }
    }
}
