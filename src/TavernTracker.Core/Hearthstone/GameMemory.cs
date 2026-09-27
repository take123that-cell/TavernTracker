namespace TavernTracker.Core.Hearthstone;

/// <summary>A lobby player as the game client knows them (from memory).</summary>
public sealed class MemoryPlayer
{
    public int PlayerId { get; init; }
    public string Name { get; init; } = "";
    public string HeroCardId { get; init; } = "";
    public int Place { get; init; }
    public int TavernTier { get; init; }
    public int TriplesCount { get; init; }
    public int WinStreak { get; init; }
}

/// <summary>What we read from the game's memory for Battlegrounds.</summary>
public sealed class MemorySnapshot
{
    public DateTime ReadUtc { get; init; } = DateTime.UtcNow;
    /// <summary>Your solo rating, -1 if unknown.</summary>
    public int Rating { get; init; } = -1;
    public int DuosRating { get; init; } = -1;
    /// <summary>Rating after the game that just ended (shown on the results screen), -1 if not available.</summary>
    public int NewRating { get; init; } = -1;
    /// <summary>Tribes in this lobby, as Hearthstone race numbers.</summary>
    public IReadOnlyList<int> AvailableRaces { get; init; } = Array.Empty<int>();
    public IReadOnlyList<MemoryPlayer> Players { get; init; } = Array.Empty<MemoryPlayer>();
    /// <summary>Which screen the game is on (SceneMgr mode): 15 = Battlegrounds menu, 4 = in a game, -1 unknown.</summary>
    public int Scene { get; init; } = -1;
    public bool InBattlegroundsMenu => Scene == 15;
}

/// <summary>Reads from the running game. Implemented in TavernTracker.Memory; null-safe everywhere.</summary>
public interface IGameMemory : IDisposable
{
    /// <summary>Returns null when the game isn't running or can't be read right now.</summary>
    MemorySnapshot? Read(bool includeLobby);
    /// <summary>
    /// The hero card id of the leaderboard portrait under the mouse: "" when none is, null when it can't be
    /// read. Quick; called several times a second from the same thread as Read.
    /// </summary>
    string? HoveredLeaderboardHero();
    string Status { get; }
}

/// <summary>Hearthstone's race numbers (from HearthSim's python-hearthstone) mapped to card-data names.</summary>
public static class Races
{
    private static readonly Dictionary<int, (string Key, string Label)> Map = new()
    {
        [11] = ("UNDEAD", "Undead"),
        [14] = ("MURLOC", "Murloc"),
        [15] = ("DEMON", "Demon"),
        [17] = ("MECHANICAL", "Mech"),
        [18] = ("ELEMENTAL", "Elemental"),
        [20] = ("BEAST", "Beast"),
        [23] = ("PIRATE", "Pirate"),
        [24] = ("DRAGON", "Dragon"),
        [26] = ("ALL", "All"),
        [43] = ("QUILBOAR", "Quilboar"),
        [92] = ("NAGA", "Naga"),
        [126] = ("ABERRATION", "Aberration"),
    };

    /// <summary>Race number for a card-data name ("BEAST" → 20), or 0.</summary>
    public static int Number(string key)
    {
        foreach (var (n, v) in Map) if (v.Key == key) return n;
        return 0;
    }

    public static string Key(int race) => Map.TryGetValue(race, out var v) ? v.Key : $"RACE_{race}";
    public static string Label(int race) => Map.TryGetValue(race, out var v) ? v.Label : $"Tribe {race}";

    /// <summary>Card-data race name ("MECHANICAL") to a short label ("Mech").</summary>
    public static string LabelForKey(string key)
    {
        foreach (var (_, v) in Map) if (v.Key == key) return v.Label;
        return key.Length > 1 ? key[0] + key[1..].ToLowerInvariant() : key;
    }

    /// <summary>Plural label for group titles ("Mechs", "Undead", "Naga").</summary>
    public static string PluralForKey(string key) => key switch
    {
        "ALL" => "All Tribes",
        "MECHANICAL" => "Mechs",
        "UNDEAD" => "Undead",
        "NAGA" => "Naga",
        "QUILBOAR" => "Quilboar",
        _ => LabelForKey(key) + "s",
    };

    /// <summary>All Battlegrounds tribes in display order.</summary>
    public static readonly string[] BattlegroundsKeys =
        { "ABERRATION", "BEAST", "DEMON", "DRAGON", "ELEMENTAL", "MECHANICAL", "MURLOC", "NAGA", "PIRATE", "QUILBOAR", "UNDEAD" };
}
