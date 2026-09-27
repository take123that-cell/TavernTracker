namespace TavernTracker.Core.Hearthstone;

/// <summary>One hero on the in-game Battlegrounds leaderboard.</summary>
public sealed class BgHero
{
    public int EntityId { get; set; }
    public string CardId { get; set; } = "";
    /// <summary>Hero name as the client logs it (in your game language).</summary>
    public string Name { get; set; } = "";
    public int Place { get; set; }
    public int TavernTier { get; set; }
    public int Health { get; set; }
    public int Damage { get; set; }
    public int Armor { get; set; }
    public bool IsMine { get; set; }
    public bool IsDead => Health > 0 && Health - Damage <= 0;
    public int HealthLeft => Math.Max(0, Health - Damage) + Armor;
}

/// <summary>The Battlegrounds game currently being played (or just finished), built from Power.log.</summary>
public sealed class BgGame
{
    /// <summary>Stable id: log file + CREATE_GAME time, so re-reading a log never duplicates games.</summary>
    public string Id { get; set; } = "";
    public DateTime StartedLocal { get; set; }
    public DateTime? EndedLocal { get; set; }
    public string GameType { get; set; } = "";
    public string LocalPlayerName { get; set; } = "";
    public int LocalPlayerId { get; set; }
    public int Turn { get; set; }
    public List<BgHero> Heroes { get; set; } = new();
    /// <summary>The lobby's anomaly (card database id), 0 if none.</summary>
    public int AnomalyDbfId { get; set; }

    public bool IsDuos => GameType.Contains("DUO", StringComparison.OrdinalIgnoreCase);
    /// <summary>Ranked lobbies only (not friendly/AI/tutorial), which are the ones that move your rating.</summary>
    public bool IsRanked => GameType is "GT_BATTLEGROUNDS" or "GT_BATTLEGROUNDS_DUO";
    public bool IsOver => EndedLocal.HasValue;

    public BgHero? MyHero => Heroes.FirstOrDefault(h => h.IsMine);
    public int MyPlace => MyHero?.Place ?? 0;
}
