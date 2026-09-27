using System.Text.Json;
using System.Text.Json.Nodes;
using TavernTracker.Core.Hearthstone;

namespace TavernTracker.Core.Combat;

/// <summary>
/// Turns a <see cref="CombatSnapshot"/> into the input of Firestone's simulator (BgsBattleInfo).
/// Field mapping follows Firestone's own board builder (MIT) and HDT's Bob's Buddy input setup.
/// </summary>
public static class BattleInputBuilder
{
    // Player-counter enchantments attached to the player entity (card ids from the game data).
    private const string TagTransfer = "Bacon_TagTransferPlayerE";   // carries the opponent's counters
    private const string EternalKnight = "BG25_008pe";
    private const string UndeadBonus = "BG25_011pe";
    private const string BloodGem = "BG26_159pe";
    private const string Beetle = "BG31_808pe";
    private const string Haunted = "BG33_112pe";
    private const string Whelp = "BG34_402pe";
    private const string TimewarpedGoldrinn = "BG34_Giant_362pe";
    private const string Goldrinn = "BGS_018pe";
    private const string AncestralAutomaton = "BG_TTN_401pe";

    public sealed class Result
    {
        public string? Json { get; init; }
        /// <summary>Why no simulation is possible (shown instead of odds).</summary>
        public string? Problem { get; init; }
        public int PlayerMinions { get; init; }
        /// <summary>Duos odds without one or both teammates' boards.</summary>
        public bool Partial { get; init; }
        public int OpponentMinions { get; init; }
    }

    public static Result Build(CombatSnapshot snap, IReadOnlyCollection<int> tribes, int simulations = 5000)
    {
        if (snap.Duos) return BuildDuos(snap, null, null, tribes, simulations);
        if (snap.LocalPlayerId == 0 || snap.OpponentPlayerId == 0) return new Result { Problem = "Couldn't identify the two sides" };
        var (byId, attached) = Index(snap);
        var player = Side(snap, snap.LocalPlayerId, friendly: true, byId, attached, out var playerProblem);
        if (player == null) return new Result { Problem = playerProblem };
        var opponent = Side(snap, snap.OpponentPlayerId, friendly: false, byId, attached, out var opponentProblem);
        if (opponent == null) return new Result { Problem = opponentProblem };
        return Assemble(snap, player, opponent, null, null, tribes, simulations);
    }

    /// <summary>
    /// Duos: the pair that fights first comes from the first set-up; the teammates (who step in when a board
    /// is wiped out) from the later set-ups, when they've been seen. Missing teammates are left out.
    /// </summary>
    public static Result BuildDuos(CombatSnapshot first, CombatSnapshot? playerTeammate, CombatSnapshot? opponentTeammate,
        IReadOnlyCollection<int> tribes, int simulations = 4000)
    {
        if (first.LocalPlayerId == 0 || first.OpponentPlayerId == 0) return new Result { Problem = "Couldn't identify the two sides" };
        var (byId, attached) = Index(first);
        var player = Side(first, first.LocalPlayerId, friendly: true, byId, attached, out var p1);
        if (player == null) return new Result { Problem = p1 };
        var opponent = Side(first, first.OpponentPlayerId, friendly: false, byId, attached, out var p2);
        if (opponent == null) return new Result { Problem = p2 };

        JsonObject? pt = null, ot = null;
        if (playerTeammate != null)
        {
            var (b, a) = Index(playerTeammate);
            pt = Side(playerTeammate, playerTeammate.LocalPlayerId, friendly: true, b, a, out _);
        }
        if (opponentTeammate != null)
        {
            var (b, a) = Index(opponentTeammate);
            ot = Side(opponentTeammate, opponentTeammate.OpponentPlayerId, friendly: false, b, a, out _);
        }
        return Assemble(first, player, opponent, pt, ot, tribes, simulations);
    }

    /// <summary>Hero card id on one side of a snapshot ("" if unknown).</summary>
    public static string HeroCard(CombatSnapshot snap, int playerId) => snap.HeroOf(playerId)?.CardId ?? "";

    private static (Dictionary<int, CombatEntity> ById, Dictionary<int, List<CombatEntity>> Attached) Index(CombatSnapshot snap)
    {
        var byId = new Dictionary<int, CombatEntity>();
        foreach (var e in snap.Entities) byId[e.Id] = e;
        var attached = snap.Entities
            .Where(e => e.IsEnchantment && e.Tag("ATTACHED", 40) > 0 && (e.InPlay || e.Tags.ContainsKey("ZONE") == false))
            .GroupBy(e => e.Tag("ATTACHED", 40))
            .ToDictionary(g => g.Key, g => g.ToList());
        return (byId, attached);
    }

    private static Result Assemble(CombatSnapshot snap, JsonObject player, JsonObject opponent, JsonObject? playerTeammate,
        JsonObject? opponentTeammate, IReadOnlyCollection<int> tribes, int simulations)
    {
        var gameState = new JsonObject
        {
            ["currentTurn"] = Math.Max(1, snap.Turn),
            ["validTribes"] = new JsonArray(tribes.Select(t => (JsonNode)t).ToArray()),
            ["anomalies"] = new JsonArray(),
        };
        int anomalyDbf = snap.GameEntity?.Tag("BACON_GLOBAL_ANOMALY_DBID", 2897) ?? 0;
        // The bridge converts dbf ids to card ids with the simulator's card data.
        if (anomalyDbf > 0) gameState["anomalyDbfIds"] = new JsonArray(anomalyDbf);

        var input = new JsonObject
        {
            ["playerBoard"] = player,
            ["opponentBoard"] = opponent,
            ["options"] = new JsonObject
            {
                ["numberOfSimulations"] = simulations,
                ["maxAcceptableDuration"] = 6000,
                ["skipInfoLogs"] = true,
                ["hideMaxSimulationDurationWarning"] = true,
            },
            ["gameState"] = gameState,
        };
        if (playerTeammate != null) input["playerTeammateBoard"] = playerTeammate;
        if (opponentTeammate != null) input["opponentTeammateBoard"] = opponentTeammate;
        return new Result
        {
            Json = input.ToJsonString(),
            PlayerMinions = ((JsonArray)player["board"]!).Count,
            OpponentMinions = ((JsonArray)opponent["board"]!).Count,
            Partial = snap.Duos && (playerTeammate == null || opponentTeammate == null),
        };
    }

    private static JsonObject? Side(CombatSnapshot snap, int playerId, bool friendly,
        Dictionary<int, CombatEntity> byId, Dictionary<int, List<CombatEntity>> attached, out string? problem)
    {
        problem = null;
        if (!snap.PlayerEntities.TryGetValue(playerId, out var playerEntity))
        {
            problem = "Player data missing from the log";
            return null;
        }

        var hero = snap.HeroOf(playerId);
        if (hero == null || string.IsNullOrEmpty(hero.CardId))
        {
            problem = friendly ? "Your hero wasn't found" : "Opponent's hero wasn't found";
            return null;
        }

        var mine = snap.Entities.Where(e => e.Controller == playerId).ToList();
        var board = mine.Where(e => e.IsMinion && e.InPlay && e.CardId.Length > 0)
            .OrderBy(e => e.Tag("ZONE_POSITION", 263))
            .Select(e => Minion(e, friendly, attached))
            .ToList();
        if (board.Count > 7)
        {
            problem = "Board read incorrectly (more than 7 minions)";
            return null;
        }
        if (mine.Any(e => e.IsMinion && e.InPlay && e.CardId.Length == 0))
        {
            problem = "Some minions are hidden";
            return null;
        }

        var heroPowers = new JsonArray();
        foreach (var hp in mine.Where(e => e.IsHeroPower && e.InPlay).Take(2))
        {
            heroPowers.Add(new JsonObject
            {
                ["cardId"] = hp.CardId,
                ["entityId"] = hp.Id,
                ["used"] = hp.Has("EXHAUSTED", 43),
                ["activated"] = hp.Has("BACON_HERO_POWER_ACTIVATED", 1398),
                ["info"] = hp.Tag("TAG_SCRIPT_DATA_NUM_1", 2),
                ["info2"] = hp.Tag("TAG_SCRIPT_DATA_NUM_2", 3),
                ["info3"] = 0, ["info4"] = 0, ["info5"] = 0, ["info6"] = 0,
            });
        }

        var trinkets = new JsonArray();
        foreach (var t in mine.Where(e => e.IsTrinket && e.InPlay))
        {
            trinkets.Add(new JsonObject
            {
                ["cardId"] = t.CardId,
                ["entityId"] = t.Id,
                ["scriptDataNum1"] = t.Tag("TAG_SCRIPT_DATA_NUM_1", 2),
                ["scriptDataNum2"] = t.Tag("TAG_SCRIPT_DATA_NUM_2", 3),
            });
        }

        var questRewards = new JsonArray();
        var questRewardEntities = new JsonArray();
        foreach (var r in mine.Where(e => e.IsQuestReward && e.InPlay))
        {
            questRewards.Add(r.CardId);
            questRewardEntities.Add(new JsonObject
            {
                ["cardId"] = r.CardId,
                ["entityId"] = r.Id,
                ["scriptDataNum1"] = r.Tag("TAG_SCRIPT_DATA_NUM_1", 2),
            });
        }

        var secrets = new JsonArray();
        foreach (var s in mine.Where(e => e.InSecretZone && e.CardId.Length > 0))
            secrets.Add(new JsonObject { ["entityId"] = s.Id, ["cardId"] = s.CardId, ["triggered"] = false });

        var hand = new JsonArray();
        if (friendly)
        {
            foreach (var h in mine.Where(e => e.InHand && e.IsMinion && e.CardId.Length > 0).OrderBy(e => e.Tag("ZONE_POSITION", 263)))
                hand.Add(Minion(h, true, attached));
        }

        int health = hero.Tag("HEALTH", 45);
        if (health == 0) health = 30;
        int hpLeft = health + hero.Tag("ARMOR", 292) - hero.Tag("DAMAGE", 44);
        int tier = hero.Tag("PLAYER_TECH_LEVEL", 1377);
        if (tier == 0) tier = playerEntity.Tag("PLAYER_TECH_LEVEL", 1377);

        return new JsonObject
        {
            ["player"] = new JsonObject
            {
                ["cardId"] = hero.CardId,
                ["entityId"] = hero.Id,
                ["hpLeft"] = Math.Max(1, hpLeft),
                ["tavernTier"] = Math.Max(1, tier),
                ["heroPowers"] = heroPowers,
                ["questEntities"] = new JsonArray(),
                ["questRewards"] = questRewards,
                ["questRewardEntities"] = questRewardEntities,
                ["trinkets"] = trinkets,
                ["secrets"] = secrets,
                ["hand"] = hand,
                ["friendly"] = friendly,
                ["globalInfo"] = GlobalInfo(playerEntity, attached, friendly),
            },
            ["board"] = new JsonArray(board.Select(b => (JsonNode)b).ToArray()),
            ["secrets"] = secrets.DeepClone(),
        };
    }

    /// <summary>Minion as Firestone's builder makes it (bgs-player.ts buildBgsEntity).</summary>
    private static JsonObject Minion(CombatEntity e, bool friendly, Dictionary<int, List<CombatEntity>> attached)
    {
        int health = e.Tag("HEALTH", 45);
        var enchantments = new JsonArray();
        if (attached.TryGetValue(e.Id, out var list))
        {
            foreach (var en in list.Where(x => x.CardId.Length > 0))
            {
                enchantments.Add(new JsonObject
                {
                    ["cardId"] = en.CardId,
                    ["originEntityId"] = en.Tag("CREATOR", 313),
                    ["tagScriptDataNum1"] = en.Tag("TAG_SCRIPT_DATA_NUM_1", 2),
                    ["tagScriptDataNum2"] = en.Tag("TAG_SCRIPT_DATA_NUM_2", 3),
                    ["timing"] = 0,
                });
            }
        }
        return new JsonObject
        {
            ["entityId"] = e.Id,
            ["cardId"] = e.CardId,
            ["attack"] = e.Tag("ATK", 47),
            ["health"] = health - e.Tag("DAMAGE", 44),
            ["maxHealth"] = health,
            ["taunt"] = e.Has("TAUNT", 190),
            ["divineShield"] = e.Tag("DIVINE_SHIELD", 194) == 1,
            ["poisonous"] = e.Tag("POISONOUS", 363) == 1,
            ["venomous"] = e.Tag("VENOMOUS", 2853) == 1,
            ["reborn"] = e.Tag("REBORN", 1085) == 1,
            ["stealth"] = e.Tag("STEALTH", 191) == 1,
            ["windfury"] = e.Tag("WINDFURY", 189) is 1 or 3 || e.Tag("MEGA_WINDFURY", 1207) == 1,
            ["scriptDataNum1"] = e.Tag("TAG_SCRIPT_DATA_NUM_1", 2),
            ["scriptDataNum2"] = e.Tag("TAG_SCRIPT_DATA_NUM_2", 3),
            ["scriptDataNum3"] = e.Tag("TAG_SCRIPT_DATA_NUM_3", 2889),
            ["scriptDataNum4"] = e.Tag("TAG_SCRIPT_DATA_NUM_4", 2919),
            ["scriptDataNum5"] = e.Tag("TAG_SCRIPT_DATA_NUM_5", 2920),
            ["scriptDataNum6"] = e.Tag("TAG_SCRIPT_DATA_NUM_6", 2921),
            ["locked"] = e.Has("UNPLAYABLE_VISUALS", 2798) || e.Has("LITERALLY_UNPLAYABLE", 1020),
            ["friendly"] = friendly,
            ["definitelyDead"] = false,
            ["immuneWhenAttackCharges"] = 0,
            ["enchantments"] = enchantments,
        };
    }

    /// <summary>Per-player counters (HDT BobsBuddyInvoker mapping, named as Firestone's BgsPlayerGlobalInfo).</summary>
    private static JsonObject GlobalInfo(CombatEntity playerEntity, Dictionary<int, List<CombatEntity>> attached, bool friendly)
    {
        attached.TryGetValue(playerEntity.Id, out var onPlayer);
        onPlayer ??= new List<CombatEntity>();
        CombatEntity? Find(string cardId) => onPlayer.FirstOrDefault(x => x.CardId == cardId);

        // The opponent's counters are copied onto a "tag transfer" enchantment during combat.
        var transfer = friendly ? null : onPlayer.FirstOrDefault(x => x.CardId == TagTransfer && x.InPlay);
        int Counter(string name, int number) => transfer != null ? transfer.Tag(name, number) : playerEntity.Tag(name, number);
        int S1(CombatEntity? e) => e?.Tag("TAG_SCRIPT_DATA_NUM_1", 2) ?? 0;
        int S2(CombatEntity? e) => e?.Tag("TAG_SCRIPT_DATA_NUM_2", 3) ?? 0;

        var goldrinn = onPlayer.Where(x => x.CardId == Goldrinn && x.InPlay).ToList();
        var twGoldrinn = Find(TimewarpedGoldrinn);

        var info = new JsonObject
        {
            ["EternalKnightsDeadThisGame"] = S1(Find(EternalKnight)),
            ["UndeadAttackBonus"] = S1(Find(UndeadBonus)),
            ["UndeadHealthBonus"] = S2(Find(UndeadBonus)),
            ["BeetleAttackBuff"] = S1(Find(Beetle)),
            ["BeetleHealthBuff"] = S2(Find(Beetle)),
            ["WhelpAttackBuff"] = S1(Find(Whelp)),
            ["WhelpHealthBuff"] = S2(Find(Whelp)),
            ["HauntedCarapaceAttackBonus"] = S1(Find(Haunted)),
            ["HauntedCarapaceHealthBonus"] = S2(Find(Haunted)),
            ["GoldrinnBuffAtk"] = goldrinn.Sum(S1) + S1(twGoldrinn),
            ["GoldrinnBuffHealth"] = goldrinn.Sum(S2) + S2(twGoldrinn),
            ["AstralAutomatonsSummonedThisGame"] = S1(Find(AncestralAutomaton)),
            ["ElementalAttackBuff"] = Counter("BACON_ELEMENTAL_BUFFATKVALUE", 4002),
            ["ElementalHealthBuff"] = Counter("BACON_ELEMENTAL_BUFFHEALTHVALUE", 4001),
            ["PiratesSummonedThisGame"] = Counter("_", 2358),
            ["MagnetizedThisGame"] = Counter("_", 3670),
            ["BeastsSummonedThisGame"] = Counter("_", 3962),
            ["TastyLobstersBuff"] = Counter("_", 4803),
            ["GoldenMinionsPlayedThisGame"] = Counter("_", 4799),
            ["FriendlyMinionsDeadLastCombat"] = Counter("_", 2717),
            ["BattlecriesTriggeredThisGame"] = Counter("_", 3236),
            ["TavernSpellsCastThisGame"] = Counter("_", 3088),
            ["DeathrattlesTriggeredThisGame"] = Counter("_", 4639),
            ["VolumizerAttackBuff"] = Counter("_", 4468),
            ["VolumizerHealthBuff"] = Counter("_", 4469),
            ["BloodGemAttackBonus"] = Math.Max(S1(Find(BloodGem)), playerEntity.Tag("BACON_BLOODGEMBUFFATKVALUE", 1844)),
            ["BloodGemHealthBonus"] = Math.Max(S2(Find(BloodGem)), playerEntity.Tag("BACON_BLOODGEMBUFFHEALTHVALUE", 2827)),
            ["TavernSpellAttackBuff"] = playerEntity.Has("TAVERN_SPELL_ATTACK_INCREASE", 3989)
                ? playerEntity.Tag("TAVERN_SPELL_ATTACK_INCREASE", 3989) : transfer?.Tag("TAVERN_SPELL_ATTACK_INCREASE", 3989) ?? 0,
            ["TavernSpellHealthBuff"] = playerEntity.Has("TAVERN_SPELL_HEALTH_INCREASE", 3990)
                ? playerEntity.Tag("TAVERN_SPELL_HEALTH_INCREASE", 3990) : transfer?.Tag("TAVERN_SPELL_HEALTH_INCREASE", 3990) ?? 0,
            ["GoldSpentThisGame"] = playerEntity.Tag("NUM_RESOURCES_SPENT_THIS_GAME", 418),
        };
        return info;
    }
}
