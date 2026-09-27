using System.Text;
using System.Text.Json;
using TavernTracker.Core.Hearthstone;

namespace TavernTracker.Core.Stats;

/// <summary>One row under a choice: a tribe and how the option does with it.</summary>
public sealed class TribeRow
{
    public string Tribe { get; init; } = "";
    public string Label { get; init; } = "";
    /// <summary>Text on the right, e.g. "3.95".</summary>
    public string Value { get; init; } = "";
    /// <summary>Bar length, 0..1.</summary>
    public double Bar { get; init; }
    /// <summary>False when the tribe isn't in this lobby (drawn faded with an X, like HDT).</summary>
    public bool InLobby { get; init; } = true;
}

/// <summary>The numbers shown above one offered card.</summary>
public sealed class OptionStats
{
    public string CardId { get; init; } = "";
    public double? AvgPlacement { get; init; }
    public string? Tier { get; init; }
    /// <summary>Label of the third box: "Pick Rate" or "Completion".</summary>
    public string ThirdLabel { get; init; } = "Pick Rate";
    /// <summary>Percent (0..100).</summary>
    public double? Third { get; init; }
    public IReadOnlyList<TribeRow> Tribes { get; init; } = Array.Empty<TribeRow>();
    /// <summary>Heroes: % of games finishing 1st..8th.</summary>
    public IReadOnlyList<double>? Placements { get; init; }
    public string? Note { get; init; }
    public int DataPoints { get; init; }
}

public sealed class ChoiceStats
{
    public ChoiceKind Kind { get; init; }
    public string Signature { get; init; } = "";
    public bool Duos { get; init; }
    /// <summary>Same order as the options in the game (null where there's no data).</summary>
    public IReadOnlyList<OptionStats?> Options { get; init; } = Array.Empty<OptionStats?>();
    /// <summary>Why nothing is shown, when the stats aren't there (still downloading, say).</summary>
    public string? Message { get; init; }
    public string Caption { get; init; } = "";
}

/// <summary>
/// Hero, trinket and quest statistics from Firestone's free public data (the same files the Firestone
/// app reads: static.zerotoheroes.com/api/bgs/...). Downloaded when first needed and kept for 6 hours.
/// Numbers are for players in the top 50% of MMR over the current patch, like Firestone's in-game overlays.
/// </summary>
public sealed class PickStatsService : IDisposable
{
    public const int MmrPercentile = 50;
    private const string Base = "https://static.zerotoheroes.com/api/bgs/";

    private readonly HttpClient _http;
    private readonly object _gate = new();
    private readonly Dictionary<string, Task> _loading = new();
    private HeroData? _heroesSolo, _heroesDuo;
    private Dictionary<string, (double Avg, double PickRate, int Points)>? _trinkets;
    private QuestData? _quests;

    public PickStatsService()
    {
        _http = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromMinutes(2),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) TavernTracker/1.1");
    }

    public void Dispose() => _http.Dispose();

    public static string HeroUrl(bool duos) => $"{Base}{(duos ? "duo/" : "")}hero-stats/mmr-{MmrPercentile}/last-patch/overview-from-hourly.gz.json";
    public static string TrinketUrl => $"{Base}trinket-stats/last-patch/overview-from-hourly.gz.json";
    public static string QuestUrl => $"{Base}quest-stats/mmr-{MmrPercentile}/last-patch/overview-from-hourly.gz.json";

    /// <summary>Starts downloading what this kind of choice needs. Returns true when it's ready.</summary>
    public bool Ensure(ChoiceKind kind, bool duos)
    {
        lock (_gate)
        {
            switch (kind)
            {
                case ChoiceKind.Hero:
                    if ((duos ? _heroesDuo : _heroesSolo) != null) return true;
                    Start(duos ? "heroes_duo" : "heroes_solo", HeroUrl(duos), json =>
                    {
                        var d = ParseHeroes(json);
                        lock (_gate) { if (duos) _heroesDuo = d; else _heroesSolo = d; }
                    });
                    return false;
                case ChoiceKind.Trinket:
                    if (_trinkets != null) return true;
                    Start("trinkets", TrinketUrl, json => { var d = ParseTrinkets(json); lock (_gate) _trinkets = d; });
                    return false;
                case ChoiceKind.Quest:
                    if (_quests != null) return true;
                    Start("quests", QuestUrl, json => { var d = ParseQuests(json); lock (_gate) _quests = d; });
                    return false;
                default:
                    return true;
            }
        }
    }

    /// <summary>Did the last download for this kind fail? (So the overlay can say so instead of "loading".)</summary>
    public bool Failed(ChoiceKind kind, bool duos)
    {
        var key = kind switch { ChoiceKind.Hero => duos ? "heroes_duo" : "heroes_solo", ChoiceKind.Trinket => "trinkets", ChoiceKind.Quest => "quests", _ => "" };
        lock (_gate) return _loading.TryGetValue(key, out var t) && t.IsCompleted && !Ready(kind, duos);
    }

    private bool Ready(ChoiceKind kind, bool duos) => kind switch
    {
        ChoiceKind.Hero => (duos ? _heroesDuo : _heroesSolo) != null,
        ChoiceKind.Trinket => _trinkets != null,
        ChoiceKind.Quest => _quests != null,
        _ => true,
    };

    private void Start(string key, string url, Action<string> apply)
    {
        if (_loading.TryGetValue(key, out var running) && (!running.IsCompleted || DateTime.UtcNow < _retryAfter.GetValueOrDefault(key))) return;
        _loading[key] = Task.Run(async () =>
        {
            var path = AppPaths.File($"stats_{key}.json");
            try
            {
                string? json = null;
                bool fresh = File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromHours(6);
                if (!fresh)
                {
                    try
                    {
                        var bytes = await _http.GetByteArrayAsync(url).ConfigureAwait(false);
                        json = Combat.SimCardData.Decode(bytes);
                        await File.WriteAllTextAsync(path, json, Encoding.UTF8).ConfigureAwait(false);
                        Log.Info($"Firestone {key} stats downloaded ({json.Length / 1024} KB)");
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Couldn't download Firestone {key} stats ({ex.Message})");
                    }
                }
                if (json == null && File.Exists(path)) json = await File.ReadAllTextAsync(path, Encoding.UTF8).ConfigureAwait(false);
                if (json != null) apply(json);
                else lock (_gate) _retryAfter[key] = DateTime.UtcNow.AddMinutes(2);
            }
            catch (Exception ex)
            {
                Log.Error($"Reading {key} stats failed", ex);
                lock (_gate) _retryAfter[key] = DateTime.UtcNow.AddMinutes(2);
            }
        });
    }

    private readonly Dictionary<string, DateTime> _retryAfter = new();

    // ------------------------------------------------------------------ building the overlay data

    /// <param name="lobbyTribes">Tribe keys ("BEAST"…) in this lobby, empty if unknown.</param>
    /// <param name="normalizeHero">Maps a hero skin to its base hero.</param>
    public ChoiceStats? Build(BgChoice choice, bool duos, IReadOnlyCollection<string> lobbyTribes, Func<string, string> normalizeHero, Func<int, string?> idOfDbf)
    {
        if (choice.Kind == ChoiceKind.Other) return null;
        string caption = $"Firestone · top {MmrPercentile}% MMR · this patch";
        if (!Ensure(choice.Kind, duos))
        {
            return new ChoiceStats
            {
                Kind = choice.Kind,
                Signature = choice.Signature,
                Duos = duos,
                Message = Failed(choice.Kind, duos) ? "Stats unavailable (couldn't reach Firestone)" : "Loading stats…",
                Caption = caption,
            };
        }
        lock (_gate)
        {
            var options = choice.Kind switch
            {
                ChoiceKind.Hero => choice.Options.Select(o => HeroOption(duos ? _heroesDuo! : _heroesSolo!, normalizeHero(o.CardId), lobbyTribes, duos)).ToList(),
                ChoiceKind.Trinket => choice.Options.Select(o => TrinketOption(o.CardId)).ToList(),
                ChoiceKind.Quest => choice.Options.Select(o => QuestOption(o, lobbyTribes, idOfDbf)).ToList(),
                _ => new List<OptionStats?>(),
            };
            return new ChoiceStats { Kind = choice.Kind, Signature = choice.Signature, Duos = duos, Options = options, Caption = caption };
        }
    }

    // ------------------------------------------------------------------ heroes

    public sealed class HeroStat
    {
        public string HeroCardId = "";
        public int DataPoints;
        public int TotalOffered, TotalPicked;
        public double AveragePosition;
        public double[] Placements = new double[8];
        public List<(string Tribe, int Points, int PointsMissing, double Impact)> Tribes = new();
    }

    public sealed class HeroData
    {
        public Dictionary<string, HeroStat> Heroes = new(StringComparer.Ordinal);
    }

    public static HeroData ParseHeroes(string json)
    {
        var data = new HeroData();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("heroStats", out var arr) || arr.ValueKind != JsonValueKind.Array) return data;
        foreach (var h in arr.EnumerateArray())
        {
            var s = new HeroStat
            {
                HeroCardId = Str(h, "heroCardId"),
                DataPoints = Int(h, "dataPoints"),
                TotalOffered = Int(h, "totalOffered"),
                TotalPicked = Int(h, "totalPicked"),
                AveragePosition = Num(h, "averagePosition"),
            };
            if (s.HeroCardId.Length == 0 || s.DataPoints <= 0) continue;
            if (h.TryGetProperty("placementDistribution", out var pd) && pd.ValueKind == JsonValueKind.Array)
            {
                var rows = pd.EnumerateArray().Select(p => (Rank: Int(p, "rank"), Pct: Num(p, "percentage"), N: Int(p, "totalMatches"))).ToList();
                int total = rows.Sum(r => r.N);
                foreach (var r in rows.Where(r => r.Rank is >= 1 and <= 8))
                    s.Placements[r.Rank - 1] = r.Pct > 0 ? r.Pct : total > 0 ? 100.0 * r.N / total : 0;
            }
            if (h.TryGetProperty("tribeStats", out var ts) && ts.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in ts.EnumerateArray())
                    s.Tribes.Add((TribeKey(t), Int(t, "dataPoints"), Int(t, "dataPointsOnMissingTribe"), Num(t, "impactAveragePosition")));
            }
            // A file can hold several MMR/time slices of the same hero; keep the biggest.
            if (!data.Heroes.TryGetValue(s.HeroCardId, out var old) || old.DataPoints < s.DataPoints) data.Heroes[s.HeroCardId] = s;
        }
        return data;
    }

    /// <summary>Firestone's lobby-adjusted average: base average plus each lobby tribe's impact (solo only).</summary>
    public static double AdjustedPosition(HeroStat s, IReadOnlyCollection<string> lobby, bool duos)
    {
        if (duos || lobby.Count == 0) return s.AveragePosition;
        var impacts = s.Tribes
            .Where(t => t.Points > s.DataPoints / 20 && t.PointsMissing > t.Points / 20)
            .Where(t => lobby.Contains(t.Tribe))
            .Sum(t => t.Impact);
        return s.AveragePosition + impacts;
    }

    private static OptionStats? HeroOption(HeroData data, string heroId, IReadOnlyCollection<string> lobby, bool duos)
    {
        if (!data.Heroes.TryGetValue(heroId, out var s)) return null;
        var all = data.Heroes.Values.Where(h => h.DataPoints >= 30).Select(h => AdjustedPosition(h, lobby, duos)).ToList();
        double avg = AdjustedPosition(s, lobby, duos);
        return new OptionStats
        {
            CardId = heroId,
            AvgPlacement = avg,
            Tier = TierOf(avg, all),
            ThirdLabel = "Pick Rate",
            Third = s.TotalOffered > 0 ? 100.0 * s.TotalPicked / s.TotalOffered : null,
            Placements = s.Placements,
            DataPoints = s.DataPoints,
        };
    }

    // ------------------------------------------------------------------ trinkets

    public static Dictionary<string, (double Avg, double PickRate, int Points)> ParseTrinkets(string json)
    {
        var result = new Dictionary<string, (double, double, int)>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("trinketStats", out var arr) || arr.ValueKind != JsonValueKind.Array) return result;
        foreach (var t in arr.EnumerateArray())
        {
            var id = Str(t, "trinketCardId");
            if (id.Length == 0) continue;
            result[id] = (Num(t, "averagePlacement"), Pct(Num(t, "pickRate")), Int(t, "dataPoints"));
        }
        return result;
    }

    private OptionStats? TrinketOption(string cardId)
    {
        if (_trinkets == null || !_trinkets.TryGetValue(cardId, out var s)) return null;
        var peers = _trinkets.Values.Where(v => v.Points >= 30).Select(v => v.Avg).ToList();
        return new OptionStats
        {
            CardId = cardId,
            AvgPlacement = s.Avg,
            Tier = TierOf(s.Avg, peers),
            ThirdLabel = "Pick Rate",
            Third = s.PickRate,
            DataPoints = s.Points,
        };
    }

    // ------------------------------------------------------------------ quests

    public sealed class QuestData
    {
        public Dictionary<string, (double TurnToComplete, double Completion, int Points)> Quests = new(StringComparer.Ordinal);
        public Dictionary<string, (double Avg, int Points, List<(string Tribe, int Points, double Avg, double Impact)> Tribes)> Rewards = new(StringComparer.Ordinal);
    }

    public static QuestData ParseQuests(string json)
    {
        var d = new QuestData();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("questStats", out var qs) && qs.ValueKind == JsonValueKind.Array)
        {
            foreach (var q in qs.EnumerateArray())
            {
                var id = Str(q, "questCardId");
                if (id.Length == 0) continue;
                var v = (Num(q, "averageTurnToComplete"), Pct(Num(q, "completionRate")), Int(q, "dataPoints"));
                if (!d.Quests.TryGetValue(id, out var old) || old.Points < v.Item3) d.Quests[id] = v;
            }
        }
        if (root.TryGetProperty("rewardStats", out var rs) && rs.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in rs.EnumerateArray())
            {
                var id = Str(r, "rewardCardId");
                if (id.Length == 0) continue;
                var tribes = new List<(string, int, double, double)>();
                if (r.TryGetProperty("tribeStats", out var ts) && ts.ValueKind == JsonValueKind.Array)
                    foreach (var t in ts.EnumerateArray())
                        tribes.Add((TribeKey(t), Int(t, "dataPoints"), Num(t, "averagePlacement"), Num(t, "impactPlacement")));
                var v = (Num(r, "averagePlacement"), Int(r, "dataPoints"), tribes);
                if (!d.Rewards.TryGetValue(id, out var old) || old.Points < v.Item2) d.Rewards[id] = v;
            }
        }
        return d;
    }

    private OptionStats? QuestOption(ChoiceOption o, IReadOnlyCollection<string> lobby, Func<int, string?> idOfDbf)
    {
        if (_quests == null) return null;
        var rewardId = o.RewardDbfId > 0 ? idOfDbf(o.RewardDbfId) : null;
        _quests.Quests.TryGetValue(o.CardId, out var quest);
        if (rewardId == null || !_quests.Rewards.TryGetValue(rewardId, out var reward))
        {
            if (quest.Points == 0) return null;
            return new OptionStats { CardId = o.CardId, ThirdLabel = "Completion", Third = quest.Completion, Note = TurnNote(quest.TurnToComplete), DataPoints = quest.Points };
        }
        var peers = _quests.Rewards.Values.Where(r => r.Points >= 30).Select(r => r.Avg).ToList();
        var usable = reward.Tribes.Where(t => t.Points >= Math.Max(20, reward.Points / 25) && t.Tribe.Length > 0).ToList();
        // Like HDT's composition list: the three best tribes for this reward, with an X on the ones this lobby doesn't have.
        var best = usable.OrderBy(t => t.Impact).Take(3).ToList();
        double worst = best.Count > 0 ? best.Max(t => t.Avg) : 0, bestAvg = best.Count > 0 ? best.Min(t => t.Avg) : 0;
        var rows = best.Select(t => new TribeRow
        {
            Tribe = t.Tribe,
            Label = Races.PluralForKey(t.Tribe),
            Value = t.Avg.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            // Longer bar = better average place (8th = empty, 1st = full).
            Bar = Math.Clamp((8.0 - t.Avg) / 7.0, 0.05, 1.0),
            InLobby = lobby.Count == 0 || lobby.Contains(t.Tribe),
        }).ToList();
        return new OptionStats
        {
            CardId = o.CardId,
            AvgPlacement = reward.Avg,
            Tier = TierOf(reward.Avg, peers),
            ThirdLabel = "Completion",
            Third = quest.Points > 0 ? quest.Completion : null,
            Tribes = rows,
            Note = quest.Points > 0 ? TurnNote(quest.TurnToComplete) : null,
            DataPoints = reward.Points,
        };
    }

    /// <summary>Firestone stores the completion turn in half-turns.</summary>
    private static string? TurnNote(double halfTurns) => halfTurns > 0 ? $"Done by turn {halfTurns / 2 + 1:0.#} on average" : null;

    // ------------------------------------------------------------------ shared

    /// <summary>
    /// Tier letter from where a value sits among its peers (lower average place = better). Same cut-offs as
    /// Firestone's tier lists (mean and standard deviation); letters as HDT shows them (S, A, B, C, D, F).
    /// </summary>
    public static string? TierOf(double value, IReadOnlyCollection<double> peers)
    {
        if (peers.Count < 3) return null;
        double mean = peers.Average();
        double sd = Math.Sqrt(peers.Sum(p => (p - mean) * (p - mean)) / peers.Count);
        if (sd <= 0) return "B";
        if (value < mean - 3 * sd) return "S";
        if (value < mean - 1.5 * sd) return "A";
        if (value < mean) return "B";
        if (value < mean + sd) return "C";
        if (value < mean + 2 * sd) return "D";
        return "F";
    }

    private static double Pct(double v) => v <= 1.0 ? v * 100 : v;

    private static string TribeKey(JsonElement t)
    {
        if (!t.TryGetProperty("tribe", out var v)) return "";
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return Races.Key(n);
        if (v.ValueKind == JsonValueKind.String) return Cards.CardDatabase.NormalizeRace(v.GetString() ?? "");
        return "";
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (int)Math.Round(v.GetDouble()) : 0;

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
}
