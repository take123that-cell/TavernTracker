namespace TavernTracker.Core.Hearthstone;

/// <summary>A minion as it was when you last fought its owner.</summary>
public sealed class SeenMinion
{
    public string CardId { get; init; } = "";
    public string Name { get; init; } = "";
    public int Attack { get; init; }
    public int Health { get; init; }
    public bool Golden { get; init; }
    public bool Taunt { get; init; }
    public bool DivineShield { get; init; }
    public bool Reborn { get; init; }
    public bool Venomous { get; init; }
    public bool Windfury { get; init; }
    public bool Stealth { get; init; }
}

/// <summary>An opponent's board from the start of your last combat against them (what HDT shows on hover).</summary>
public sealed class SeenBoard
{
    public string GameId { get; init; } = "";
    public string HeroCardId { get; init; } = "";
    public string HeroName { get; init; } = "";
    public int Turn { get; init; }
    public int TavernTier { get; init; }
    public IReadOnlyList<SeenMinion> Minions { get; init; } = Array.Empty<SeenMinion>();

    /// <summary>Reads the opponent's side from a combat snapshot. Null if it can't be identified.</summary>
    public static SeenBoard? FromCombat(CombatSnapshot snap)
    {
        if (snap.OpponentPlayerId == 0) return null;
        int pid = snap.OpponentPlayerId;
        var hero = snap.HeroOf(pid);
        if (hero == null || hero.CardId.Length == 0) return null;

        var minions = snap.Entities
            .Where(e => e.Controller == pid && e.IsMinion && e.InPlay && e.CardId.Length > 0)
            .OrderBy(e => e.Tag("ZONE_POSITION", 263))
            .Take(7)
            .Select(e =>
            {
                int health = e.Tag("HEALTH", 45);
                return new SeenMinion
                {
                    CardId = e.CardId,
                    Name = e.Name,
                    Attack = e.Tag("ATK", 47),
                    Health = Math.Max(0, health - e.Tag("DAMAGE", 44)),
                    Golden = e.Has("PREMIUM", 12),
                    Taunt = e.Has("TAUNT", 190),
                    DivineShield = e.Tag("DIVINE_SHIELD", 194) == 1,
                    Reborn = e.Tag("REBORN", 1085) == 1,
                    Venomous = e.Tag("VENOMOUS", 2853) == 1 || e.Tag("POISONOUS", 363) == 1,
                    Windfury = e.Tag("WINDFURY", 189) is 1 or 3 || e.Tag("MEGA_WINDFURY", 1207) == 1,
                    Stealth = e.Tag("STEALTH", 191) == 1,
                };
            })
            .ToList();

        return new SeenBoard
        {
            GameId = snap.GameId,
            HeroCardId = hero.CardId,
            HeroName = hero.Name,
            Turn = snap.Turn,
            TavernTier = hero.Tag("PLAYER_TECH_LEVEL", 1037),
            Minions = minions,
        };
    }
}
