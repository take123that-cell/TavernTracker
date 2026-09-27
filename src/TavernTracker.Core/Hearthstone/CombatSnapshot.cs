namespace TavernTracker.Core.Hearthstone;

/// <summary>One game entity copied at the moment combat starts.</summary>
public sealed class CombatEntity
{
    public int Id { get; init; }
    public string CardId { get; init; } = "";
    public string Name { get; init; } = "";
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Reads a tag by name or by number. The client logs tags it has a name for by name
    /// ("ATK") and the rest as numbers ("2022"), so callers pass both.
    /// </summary>
    public int Tag(string name, int number = -1)
    {
        if (Tags.TryGetValue(name, out var v) || (number >= 0 && Tags.TryGetValue(number.ToString(System.Globalization.CultureInfo.InvariantCulture), out v)))
            return int.TryParse(v, out var n) ? n : 0;
        return 0;
    }

    public bool Has(string name, int number = -1) => Tag(name, number) != 0;

    /// <summary>Enum-valued tags are logged as names ("MINION", "PLAY") or numbers.</summary>
    public bool Is(string tag, string valueName, int valueNumber)
    {
        if (!Tags.TryGetValue(tag, out var v)) return false;
        return v == valueName || v == valueNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public int Controller => Tag("CONTROLLER", 50);
    public int Zone(string zoneName, int zoneNumber) => Is("ZONE", zoneName, zoneNumber) ? 1 : 0;
    public bool InPlay => Is("ZONE", "PLAY", 1);
    public bool InHand => Is("ZONE", "HAND", 3);
    public bool InSecretZone => Is("ZONE", "SECRET", 7);
    public bool IsMinion => Is("CARDTYPE", "MINION", 4);
    public bool IsHero => Is("CARDTYPE", "HERO", 3);
    public bool IsHeroPower => Is("CARDTYPE", "HERO_POWER", 10);
    public bool IsEnchantment => Is("CARDTYPE", "ENCHANTMENT", 6);
    public bool IsTrinket => Is("CARDTYPE", "BATTLEGROUND_TRINKET", 44);
    public bool IsQuestReward => Is("CARDTYPE", "BATTLEGROUND_QUEST_REWARD", 40);
    public bool IsSpell => Is("CARDTYPE", "SPELL", 5) || Is("CARDTYPE", "BATTLEGROUND_SPELL", 42);
}

/// <summary>Everything on the table when a Battlegrounds combat begins.</summary>
public sealed class CombatSnapshot
{
    public string GameId { get; init; } = "";
    public int Turn { get; init; }
    public bool Duos { get; init; }
    public int LocalPlayerId { get; init; }
    /// <summary>PlayerID of the side you're fighting (the game's second player during combat).</summary>
    public int OpponentPlayerId { get; init; }
    public CombatEntity? GameEntity { get; init; }
    public IReadOnlyDictionary<int, CombatEntity> PlayerEntities { get; init; } = new Dictionary<int, CombatEntity>();
    public IReadOnlyList<CombatEntity> Entities { get; init; } = Array.Empty<CombatEntity>();
    public DateTime TakenLocal { get; init; }
}
