using System.Globalization;
using System.Text;

namespace TavernTracker.Core.Leaderboard;

public readonly record struct RatingPoint(DateTime Utc, int Rank, int Rating);

public sealed class PlayerProfile
{
    public required string Name { get; init; }
    public required string Region { get; init; }
    public required bool Duos { get; init; }
    public int Rank { get; init; }          // 0 = not on the latest board
    public int Rating { get; init; }
    public int SameNameCount { get; init; } = 1;
    public int Peak { get; init; }
    public int? Change24h { get; init; }
    public int? Change7d { get; init; }
    public DateTime? FirstSeenUtc { get; init; }
    public IReadOnlyList<RatingPoint> History { get; init; } = Array.Empty<RatingPoint>();
}

public sealed class BoardRowView
{
    public int Rank { get; init; }
    public string Name { get; init; } = "";
    public int Rating { get; init; }
    public int? Change24h { get; init; }
}

/// <summary>
/// One leaderboard (region + solo/duos): the latest download plus every player's rating history,
/// built by saving a copy of the board every so often. Saved per season, because ratings reset.
///   latest_EU_solo.json         the most recent full board
///   history_EU_solo_s19.csv     unixSeconds,rank,rating,name   (a line only when a rating changes)
/// </summary>
public sealed class BoardHistory
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<RatingPoint>> _points = new();
    private readonly Dictionary<string, string> _displayNames = new();
    private List<LeaderboardRow> _latest = new();
    private Dictionary<string, (LeaderboardRow Row, int Count)> _latestByKey = new();

    public string Region { get; }
    public bool Duos { get; }
    public int SeasonId { get; private set; }
    public DateTime FetchedUtc { get; private set; }
    public bool HasData => _latest.Count > 0;
    public int Count { get { lock (_gate) return _latest.Count; } }

    private string Suffix => $"{Region}_{(Duos ? "duos" : "solo")}";
    private string LatestPath => AppPaths.File($"latest_{Suffix}.json");
    private string HistoryPath(int season) => AppPaths.File($"history_{Suffix}_s{season}.csv");

    public BoardHistory(string region, bool duos)
    {
        Region = region;
        Duos = duos;
    }

    /// <summary>Loads what's on disk. Safe to call once at startup.</summary>
    public void Load()
    {
        var latest = JsonFile.Load<LatestFile>(LatestPath);
        if (latest?.Rows == null) return;
        lock (_gate)
        {
            SeasonId = latest.SeasonId;
            FetchedUtc = new DateTime(latest.FetchedUtcTicks, DateTimeKind.Utc);
            SetLatest(latest.Rows.Select(r => new LeaderboardRow(r.Rank, r.Name, r.Rating)).ToList());
            LoadHistoryFile(HistoryPath(SeasonId));
        }
    }

    /// <summary>Adds a fresh download: updates the latest board and appends rating changes.</summary>
    public void Ingest(LeaderboardDownload d)
    {
        lock (_gate)
        {
            if (d.SeasonId != SeasonId)
            {
                // New season: ratings reset, start a fresh history file.
                _points.Clear();
                _displayNames.Clear();
                SeasonId = d.SeasonId;
                LoadHistoryFile(HistoryPath(SeasonId));
            }

            var ts = d.FetchedUtc;
            var sb = new StringBuilder();
            var seen = new HashSet<string>();
            foreach (var row in d.Rows)
            {
                var key = Names.Key(row.Name);
                if (!seen.Add(key)) continue; // same name twice: keep the best-ranked one
                if (!_points.TryGetValue(key, out var list))
                {
                    list = new List<RatingPoint>();
                    _points[key] = list;
                }
                _displayNames[key] = row.Name;
                if (list.Count == 0 || list[^1].Rating != row.Rating)
                {
                    list.Add(new RatingPoint(ts, row.Rank, row.Rating));
                    sb.Append(new DateTimeOffset(ts).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)).Append(',')
                      .Append(row.Rank.ToString(CultureInfo.InvariantCulture)).Append(',')
                      .Append(row.Rating.ToString(CultureInfo.InvariantCulture)).Append(',')
                      .Append(row.Name.Replace('\n', ' ').Replace('\r', ' ')).Append('\n');
                }
            }

            FetchedUtc = ts;
            SetLatest(d.Rows.ToList());

            try
            {
                if (sb.Length > 0) File.AppendAllText(HistoryPath(SeasonId), sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex) { Log.Error("Could not append rating history", ex); }

            JsonFile.Save(LatestPath, new LatestFile
            {
                SeasonId = SeasonId,
                FetchedUtcTicks = FetchedUtc.Ticks,
                Rows = d.Rows.Select(r => new LatestRow { Rank = r.Rank, Name = r.Name, Rating = r.Rating }).ToList(),
            });
        }
    }

    public LeaderboardRow? Find(string name, out int sameNameCount)
    {
        lock (_gate)
        {
            if (_latestByKey.TryGetValue(Names.Key(name), out var hit))
            {
                sameNameCount = hit.Count;
                return hit.Row;
            }
            sameNameCount = 0;
            return null;
        }
    }

    /// <summary>Lowest rating on the board: anyone not listed is below this.</summary>
    public int Cutoff
    {
        get { lock (_gate) return _latest.Count > 0 ? _latest.Min(r => r.Rating) : 0; }
    }

    public IReadOnlyList<LeaderboardRow> Search(string query, int limit = 25)
    {
        var q = Names.Key(query);
        if (q.Length == 0) return Array.Empty<LeaderboardRow>();
        lock (_gate)
        {
            return _latest
                .Where(r => Names.Key(r.Name).Contains(q, StringComparison.Ordinal))
                .OrderBy(r => Names.Key(r.Name) == q ? 0 : Names.Key(r.Name).StartsWith(q, StringComparison.Ordinal) ? 1 : 2)
                .ThenBy(r => r.Rank)
                .Take(limit)
                .ToList();
        }
    }

    public IReadOnlyList<BoardRowView> Rows(DateTime nowUtc, string? filter = null, int limit = 500)
    {
        var q = Names.Key(filter);
        lock (_gate)
        {
            return _latest
                .Where(r => q.Length == 0 || Names.Key(r.Name).Contains(q, StringComparison.Ordinal))
                .Take(limit)
                .Select(r => new BoardRowView
                {
                    Rank = r.Rank,
                    Name = r.Name,
                    Rating = r.Rating,
                    Change24h = ChangeSince(Names.Key(r.Name), r.Rating, nowUtc.AddHours(-24)),
                })
                .ToList();
        }
    }

    /// <summary>Profile for a player; works even if they've since dropped off the board.</summary>
    public PlayerProfile? Profile(string name, DateTime nowUtc)
    {
        var key = Names.Key(name);
        lock (_gate)
        {
            _latestByKey.TryGetValue(key, out var hit);
            _points.TryGetValue(key, out var pts);
            if (hit.Row == null && (pts == null || pts.Count == 0)) return null;

            var history = pts?.ToList() ?? new List<RatingPoint>();
            int rating = hit.Row?.Rating ?? 0;
            return new PlayerProfile
            {
                Name = hit.Row?.Name ?? (_displayNames.TryGetValue(key, out var dn) ? dn : name),
                Region = Region,
                Duos = Duos,
                Rank = hit.Row?.Rank ?? 0,
                Rating = rating,
                SameNameCount = Math.Max(1, hit.Count),
                Peak = history.Count > 0 ? Math.Max(history.Max(p => p.Rating), rating) : rating,
                Change24h = rating > 0 ? ChangeSince(key, rating, nowUtc.AddHours(-24)) : null,
                Change7d = rating > 0 ? ChangeSince(key, rating, nowUtc.AddDays(-7)) : null,
                FirstSeenUtc = history.Count > 0 ? history[0].Utc : null,
                History = history,
            };
        }
    }

    /// <summary>The player's rating at a moment (last recorded value at or before it), if known.</summary>
    public int? RatingAt(string name, DateTime utc)
    {
        lock (_gate)
        {
            if (!_points.TryGetValue(Names.Key(name), out var pts) || pts.Count == 0) return null;
            RatingPoint? best = null;
            foreach (var p in pts)
            {
                if (p.Utc <= utc) best = p;
                else break;
            }
            return best?.Rating;
        }
    }

    private int? ChangeSince(string key, int current, DateTime sinceUtc)
    {
        if (!_points.TryGetValue(key, out var pts) || pts.Count == 0) return null;
        // Need a data point from before the window, otherwise we don't know the change.
        if (pts[0].Utc > sinceUtc) return null;
        RatingPoint then = pts[0];
        foreach (var p in pts)
        {
            if (p.Utc <= sinceUtc) then = p;
            else break;
        }
        return current - then.Rating;
    }

    private void SetLatest(List<LeaderboardRow> rows)
    {
        _latest = rows.OrderBy(r => r.Rank).ToList();
        var map = new Dictionary<string, (LeaderboardRow, int)>();
        foreach (var r in _latest)
        {
            var key = Names.Key(r.Name);
            map[key] = map.TryGetValue(key, out var existing) ? (existing.Item1, existing.Item2 + 1) : (r, 1);
        }
        _latestByKey = map;
    }

    private void LoadHistoryFile(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                // unixSeconds,rank,rating,name  (the name may itself contain commas)
                var parts = line.Split(',', 4);
                if (parts.Length < 4) continue;
                if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var secs)) continue;
                if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rank)) continue;
                if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rating)) continue;
                var key = Names.Key(parts[3]);
                if (!_points.TryGetValue(key, out var list))
                {
                    list = new List<RatingPoint>();
                    _points[key] = list;
                }
                list.Add(new RatingPoint(DateTimeOffset.FromUnixTimeSeconds(secs).UtcDateTime, rank, rating));
                _displayNames[key] = parts[3];
            }
            foreach (var list in _points.Values) list.Sort((a, b) => a.Utc.CompareTo(b.Utc));
        }
        catch (Exception ex)
        {
            Log.Error($"Could not read {Path.GetFileName(path)}", ex);
        }
    }

    private sealed class LatestFile
    {
        public int SeasonId { get; set; }
        public long FetchedUtcTicks { get; set; }
        public List<LatestRow>? Rows { get; set; }
    }

    private sealed class LatestRow
    {
        public int Rank { get; set; }
        public string Name { get; set; } = "";
        public int Rating { get; set; }
    }
}
