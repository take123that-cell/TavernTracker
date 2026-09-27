namespace TavernTracker.Core.Combat;

public enum CombatPhase { None, Running, Done, Unavailable }

/// <summary>What the combat-odds bar shows.</summary>
public sealed class CombatState
{
    public static readonly CombatState None = new();

    public CombatPhase Phase { get; init; }
    public CombatOdds? Odds { get; init; }
    public string? Message { get; init; }
    public int Turn { get; init; }
    public string GameId { get; init; } = "";
    /// <summary>True during the fight; false once shopping starts again (the last result stays visible).</summary>
    public bool InCombat { get; init; }

    public CombatState With(bool inCombat) => new()
    {
        Phase = Phase, Odds = Odds, Message = Message, Turn = Turn, GameId = GameId, InCombat = inCombat,
    };
}
