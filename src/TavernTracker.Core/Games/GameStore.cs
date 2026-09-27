using TavernTracker.Core.Hearthstone;

namespace TavernTracker.Core.Games;

public sealed class LobbyHero
{
    /// <summary>Hero name.</summary>
    public string Name { get; set; } = "";
    /// <summary>The player's name, when it could be read from the game (empty otherwise).</summary>
    public string PlayerName { get; set; } = "";
    public string CardId { get; set; } = "";
    public int Place { get; set; }
    public int TavernTier { get; set; }
    public bool IsMine { get; set; }
}

public sealed class GameRecord
{
    public string Id { get; set; } = "";
    public DateTime StartedLocal { get; set; }
    public DateTime EndedLocal { get; set; }
    public string GameType { get; set; } = "";
    public bool Duos { get; set; }
    public bool Ranked { get; set; }
    public string BattleTag { get; set; } = "";
    public string HeroName { get; set; } = "";
    public string HeroCardId { get; set; } = "";
    public int Place { get; set; }
    public int TavernTier { get; set; }
    public int Turns { get; set; }
    /// <summary>Your leaderboard rating when the game ended (0 if you weren't on the board).</summary>
    public int BoardRating { get; set; }
    /// <summary>Your rating before and after, read from the game (0 if unknown).</summary>
    public int RatingBefore { get; set; }
    public int RatingAfter { get; set; }
    public int? RatingDelta => RatingBefore > 0 && RatingAfter > 0 ? RatingAfter - RatingBefore : null;
    public List<LobbyHero> Lobby { get; set; } = new();

    public TimeSpan Duration => EndedLocal > StartedLocal ? EndedLocal - StartedLocal : TimeSpan.Zero;
    public bool Top4 => Place is > 0 and <= 4;

    public static GameRecord From(BgGame g, int boardRating) => new()
    {
        Id = g.Id,
        StartedLocal = g.StartedLocal,
        EndedLocal = g.EndedLocal ?? DateTime.Now,
        GameType = g.GameType,
        Duos = g.IsDuos,
        Ranked = g.IsRanked,
        BattleTag = g.LocalPlayerName,
        HeroName = g.MyHero?.Name ?? "",
        HeroCardId = g.MyHero?.CardId ?? "",
        Place = g.MyPlace,
        TavernTier = g.MyHero?.TavernTier ?? 0,
        Turns = g.Turn,
        BoardRating = boardRating,
        Lobby = g.Heroes.Select(h => new LobbyHero
        {
            Name = h.Name,
            CardId = h.CardId,
            Place = h.Place,
            TavernTier = h.TavernTier,
            IsMine = h.IsMine,
        }).ToList(),
    };
}

/// <summary>Your finished Battlegrounds games, saved to games.json.</summary>
public sealed class GameStore
{
    private const int MaxGames = 5000;
    private readonly object _gate = new();
    private readonly string _path;
    private readonly List<GameRecord> _games;

    public event Action? Changed;

    public GameStore(string path)
    {
        _path = path;
        _games = JsonFile.Load<List<GameRecord>>(path) ?? new List<GameRecord>();
        _games.Sort((a, b) => a.StartedLocal.CompareTo(b.StartedLocal));
    }

    public IReadOnlyList<GameRecord> All()
    {
        lock (_gate) return _games.ToList();
    }

    /// <summary>Adds a game unless we already have it (re-reading an old log is harmless).</summary>
    public bool Add(GameRecord g)
    {
        lock (_gate)
        {
            if (_games.Any(x => x.Id == g.Id)) return false;
            _games.Add(g);
            _games.Sort((a, b) => a.StartedLocal.CompareTo(b.StartedLocal));
            if (_games.Count > MaxGames) _games.RemoveRange(0, _games.Count - MaxGames);
            JsonFile.Save(_path, _games);
        }
        Changed?.Invoke();
        return true;
    }

    public void Update(string id, Action<GameRecord> change)
    {
        lock (_gate)
        {
            var g = _games.FirstOrDefault(x => x.Id == id);
            if (g == null) return;
            change(g);
            JsonFile.Save(_path, _games);
        }
        Changed?.Invoke();
    }
}
