namespace TavernTracker.Core.Hearthstone;

public enum ChoiceKind { Other, Hero, Trinket, Quest }

/// <summary>One card offered in a choice (hero, trinket, quest…).</summary>
public sealed class ChoiceOption
{
    public int EntityId { get; init; }
    public string CardId { get; init; } = "";
    /// <summary>For quests: the reward's card database id (QUEST_REWARD_DATABASE_ID).</summary>
    public int RewardDbfId { get; init; }
}

/// <summary>A choice the game is currently offering you (read from Power.log's DebugPrintEntityChoices).</summary>
public sealed class BgChoice
{
    public int Id { get; init; }
    public string GameId { get; init; } = "";
    public ChoiceKind Kind { get; init; }
    public string ChoiceType { get; init; } = "";
    public string SourceCardId { get; init; } = "";
    public int Turn { get; init; }
    public IReadOnlyList<ChoiceOption> Options { get; init; } = Array.Empty<ChoiceOption>();

    /// <summary>Changes whenever the offered cards change (a reroll, say).</summary>
    public string Signature => $"{GameId}|{Id}|{Kind}|{string.Join(",", Options.Select(o => o.CardId + ":" + o.RewardDbfId))}";
}
