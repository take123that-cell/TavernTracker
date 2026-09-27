using TavernTracker.Core.Leaderboard;

namespace TavernTracker.Core.Games;

public sealed class SessionSummary
{
    public bool Duos { get; init; }
    public DateTime StartedLocal { get; init; }
    public IReadOnlyList<GameRecord> Games { get; init; } = Array.Empty<GameRecord>();
    public int Count => Games.Count;
    public IReadOnlyList<int> Placements => Games.Where(g => g.Place > 0).Select(g => g.Place).ToList();
    public double? AvgPlace => Placements.Count > 0 ? Placements.Average() : null;
    public int Top4 => Games.Count(g => g.Top4);
    public int Firsts => Games.Count(g => g.Place == 1);
    public int RatingStart { get; init; }
    public int RatingNow { get; init; }
    public int? Delta => RatingStart > 0 && RatingNow > 0 ? RatingNow - RatingStart : null;
}

public static class Sessions
{
    /// <summary>
    /// Your current session: ranked games in one mode with no break longer than <paramref name="gap"/>.
    /// Rating comes from the public leaderboard, so it's only known while you're on it.
    /// </summary>
    public static SessionSummary Current(IReadOnlyList<GameRecord> all, bool duos, TimeSpan gap,
        DateTime nowLocal, BoardHistory? board, string battleTag, int liveRating = 0)
    {
        var games = all.Where(g => g.Duos == duos && g.Ranked).OrderBy(g => g.StartedLocal).ToList();
        var session = new List<GameRecord>();
        var cursor = nowLocal;
        for (int i = games.Count - 1; i >= 0; i--)
        {
            var g = games[i];
            if (cursor - g.EndedLocal > gap) break;
            session.Insert(0, g);
            cursor = g.StartedLocal;
        }

        var started = session.Count > 0 ? session[0].StartedLocal : nowLocal;
        int ratingNow = 0, ratingStart = 0;

        // Best source: ratings read from the game itself.
        ratingStart = session.FirstOrDefault(g => g.RatingBefore > 0)?.RatingBefore ?? 0;
        ratingNow = liveRating > 0 ? liveRating : session.LastOrDefault(g => g.RatingAfter > 0)?.RatingAfter ?? 0;
        if (ratingStart == 0 && session.Count == 0) ratingStart = ratingNow;

        if ((ratingStart == 0 || ratingNow == 0) && board != null && !string.IsNullOrWhiteSpace(battleTag))
        {
            if (ratingNow == 0) ratingNow = board.Find(battleTag, out _)?.Rating ?? 0;
            if (ratingStart == 0) ratingStart = board.RatingAt(battleTag, started.ToUniversalTime()) ?? 0;
        }
        if (ratingStart == 0)
        {
            // Fall back to what we recorded at the end of the first game (the board may lag behind).
            ratingStart = session.FirstOrDefault(g => g.BoardRating > 0)?.BoardRating ?? 0;
        }

        return new SessionSummary
        {
            Duos = duos,
            StartedLocal = started,
            Games = session,
            RatingStart = ratingStart,
            RatingNow = ratingNow,
        };
    }
}

/// <summary>One player in the current lobby, for the lobby panel.</summary>
public sealed class LobbyEntry
{
    public string Name { get; init; } = "";
    public string HeroCardId { get; init; } = "";
    public int Place { get; init; }
    public int TavernTier { get; init; }
    public bool IsMe { get; init; }
    /// <summary>Public leaderboard rating (yours comes from the game), 0 if not listed.</summary>
    public int Rating { get; init; }
    public int Rank { get; init; }
    public bool SharedName { get; init; }
    public int TimesMet { get; init; }
    /// <summary>Lowest rating on the board, to show "below X" for unlisted players.</summary>
    public int Cutoff { get; init; }
}
