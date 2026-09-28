using System.Globalization;
using System.Text.RegularExpressions;

namespace TavernTracker.Core.Hearthstone;

/// <summary>
/// Reads Power.log lines and keeps track of the current Battlegrounds game.
/// Line formats follow HearthSim's open-source parsers (HDT, python-hslog):
///   D 19:01:02.1234567 GameState.DebugPrintGame() - GameType=GT_BATTLEGROUNDS
///   D 19:01:02.1234567 GameState.DebugPrintGame() - PlayerID=7, PlayerName=Name#1234
///   D 19:01:02.1234567 GameState.DebugPrintPower() -     FULL_ENTITY - Updating [entityName=X id=42 zone=PLAY zonePos=0 cardId=Y player=7] CardID=Y
///   D 19:01:02.1234567 GameState.DebugPrintPower() -         tag=CONTROLLER value=7
///   D 19:01:02.1234567 GameState.DebugPrintPower() -     TAG_CHANGE Entity=[... id=42 ...] tag=PLAYER_LEADERBOARD_PLACE value=3
/// Entities and tags are read from PowerTaskList.DebugPrintPower lines, which is what HDT does: they are
/// the game's actions in the order the client plays them out. The GameState.DebugPrintPower copy is
/// the raw server order, where combat can begin before the opponent's board has been created (so a
/// snapshot taken there sees no minions). GameState lines are only used for game info and choices,
/// and for very old logs without PowerTaskList lines.
/// </summary>
public sealed class PowerLogParser
{
    private static readonly Regex LinePrefix = new(@"^[DW] (?<ts>\d{1,2}:\d{2}:\d{2}(\.\d+)?) (?<body>.*)$", RegexOptions.Compiled);
    private static readonly Regex PlayerName = new(@"PlayerID=(?<id>\d+), PlayerName=(?<name>.+)$", RegexOptions.Compiled);
    private static readonly Regex PlayerEntity = new(@"Player EntityID=(?<eid>\d+) PlayerID=(?<pid>\d+) GameAccountId=\[hi=(?<hi>\d+) lo=(?<lo>\d+)\]", RegexOptions.Compiled);
    private static readonly Regex GameEntity = new(@"GameEntity EntityID=(?<id>\d+)", RegexOptions.Compiled);
    private static readonly Regex FullEntityUpdating = new(@"FULL_ENTITY - Updating (?<ent>\[.*\]) CardID=(?<card>\w*)", RegexOptions.Compiled);
    private static readonly Regex FullEntityCreating = new(@"FULL_ENTITY - Creating ID=(?<id>\d+) CardID=(?<card>\w*)", RegexOptions.Compiled);
    private static readonly Regex ShowChangeEntity = new(@"(SHOW_ENTITY|CHANGE_ENTITY) - Updating Entity=(?<ent>.+) CardID=(?<card>\w*)", RegexOptions.Compiled);
    private static readonly Regex TagLine = new(@"^tag=(?<tag>\w+) value=(?<value>\S+)", RegexOptions.Compiled);
    private static readonly Regex TagChange = new(@"TAG_CHANGE Entity=(?<ent>.+) tag=(?<tag>\w+) value=(?<value>\S+)", RegexOptions.Compiled);
    private static readonly Regex BracketId = new(@"\bid=(?<id>\d+)", RegexOptions.Compiled);
    private static readonly Regex BracketName = new(@"entityName=(?<name>.*?) id=\d+", RegexOptions.Compiled);
    private static readonly Regex BracketCard = new(@"cardId=(?<card>\w*)", RegexOptions.Compiled);
    private static readonly Regex BracketPlayer = new(@"player=(?<p>\d+)", RegexOptions.Compiled);
    // Choices (same formats as HDT's ChoicesHandler).
    private static readonly Regex ChoicesHeader = new(@"^id=(?<id>\d+) Player=(?<player>.+?) TaskList=(?<tl>\d*) ChoiceType=(?<type>\w+)", RegexOptions.Compiled);
    private static readonly Regex ChosenHeader = new(@"^id=(?<id>\d+) Player=(?<player>.+?) EntitiesCount=", RegexOptions.Compiled);
    private static readonly Regex ChoiceSource = new(@"^Source=(?<src>.+)$", RegexOptions.Compiled);
    private static readonly Regex ChoiceEntity = new(@"^Entities\[(?<i>\d+)\]=(?<ent>.+)$", RegexOptions.Compiled);

    private sealed class Entity
    {
        public int Id;
        public string CardId = "";
        public string Name = "";
        public readonly Dictionary<string, string> Tags = new(StringComparer.Ordinal);
        public long PlaceSeq; // when PLAYER_LEADERBOARD_PLACE last changed (to prefer the live copy)

        public int Int(string tag) => Tags.TryGetValue(tag, out var v) && int.TryParse(v, out var n) ? n : 0;
        public string Str(string tag) => Tags.TryGetValue(tag, out var v) ? v : "";
    }

    private readonly string _sourceKey;
    private readonly Func<string?> _preferredName;

    private Dictionary<int, Entity> _entities = new();
    private Dictionary<int, string> _playerNames = new();     // PlayerID -> name
    private Dictionary<int, int> _playerEntityToId = new();    // entity id -> PlayerID
    private Dictionary<int, long> _playerAccountHi = new();   // PlayerID -> account hi (0 for AI/innkeeper)
    private HashSet<int> _leaderboardEntities = new();
    private Entity? _current;
    private int _gameEntityId = 1;
    private long _seq;
    private DateTime _baseDate = DateTime.Today;
    private TimeSpan _lastTime = TimeSpan.Zero;
    private bool _inGame;
    private bool _dirty;

    // PowerTaskList mode (see the class summary) and game info that arrives before the game object.
    private bool _taskList;
    private string _pendingType = "";
    private Dictionary<int, string> _pendingNames = new();
    private bool _infoForNextGame;

    // Minions Bob offers in the shop (for working out the lobby's tribes without memory reading).
    private bool _inCombat;
    private bool _combatPending;
    private int _combatIndex;
    private int _setupIndex;
    private HashSet<int> _shopEntities = new();

    private sealed class OpenChoice
    {
        public int Id;
        public string Player = "";
        public string Type = "";
        public int SourceEntity;
        public readonly List<int> Entities = new();
        public int Turn;
        public int CombatIndex;
        public DateTime Offered;
    }
    private OpenChoice? _choice;          // the choice being offered to you, if any
    private OpenChoice? _choiceBuilding;  // header seen, entity lines still coming
    private bool _chosenBlock;            // inside a DebugPrintEntitiesChosen block

    public BgGame? Game { get; private set; }

    /// <summary>Raised when a Battlegrounds game is detected (after enough of it has been read).</summary>
    public event Action<BgGame>? GameStarted;
    /// <summary>Raised when something visible changed (placements, tiers, turn).</summary>
    public event Action<BgGame>? GameChanged;
    /// <summary>Raised when a Battlegrounds combat begins, with a copy of both boards (for the simulator).</summary>
    public event Action<CombatSnapshot>? CombatStarted;
    /// <summary>Raised when the shopping phase begins again (combat over).</summary>
    public event Action? CombatEnded;

    /// <summary>Raised once when a Battlegrounds game finishes.</summary>
    public event Action<BgGame>? GameEnded;

    /// <param name="sourceKey">Identifies the log file, used to build stable game ids.</param>
    /// <param name="preferredName">Your BattleTag if known (settings), used to pick your player.</param>
    public PowerLogParser(string sourceKey, Func<string?>? preferredName = null)
    {
        _sourceKey = sourceKey;
        _preferredName = preferredName ?? (() => null);
    }

    public void SetBaseDate(DateTime sessionStart)
    {
        _baseDate = sessionStart.Date;
        _lastTime = sessionStart.TimeOfDay;
    }

    /// <summary>Feed one raw line from Power.log.</summary>
    public void Feed(string raw)
    {
        var m = LinePrefix.Match(raw);
        if (!m.Success) return;
        var body = m.Groups["body"].Value;

        bool isGame = body.StartsWith("GameState.DebugPrintGame()", StringComparison.Ordinal);
        bool isGsPower = body.StartsWith("GameState.DebugPrintPower()", StringComparison.Ordinal);
        bool isTaskPower = body.StartsWith("PowerTaskList.DebugPrintPower()", StringComparison.Ordinal);
        if (isTaskPower && !_taskList) SwitchToTaskList();
        bool isPower = _taskList ? isTaskPower : isGsPower;
        bool isChoices = body.StartsWith("GameState.DebugPrintEntityChoices()", StringComparison.Ordinal);
        bool isChosen = body.StartsWith("GameState.DebugPrintEntitiesChosen()", StringComparison.Ordinal);
        if (!isGame && !isPower && !isChoices && !isChosen && !isGsPower) return;

        var time = ToLocalTime(m.Groups["ts"].Value);
        int dash = body.IndexOf(" - ", StringComparison.Ordinal);
        if (dash < 0) return;
        var data = body[(dash + 3)..].Trim();

        if (isGsPower && _taskList)
        {
            // The server announces a new game here a moment before the task list plays it out; the
            // game info lines that follow belong to that next game.
            if (data == "CREATE_GAME") ExpectNewGame();
            return;
        }

        if (isGame) HandleGameInfo(data);
        else if (isPower) HandlePower(data, time);
        else HandleChoice(data, isChosen, time);

        if (_dirty && Game != null)
        {
            _dirty = false;
            RefreshModel();
            if (Game != null) GameChanged?.Invoke(Game);
        }
    }

    /// <summary>Call after a batch of lines so the model is up to date.</summary>
    public void Flush()
    {
        if (Game == null) return;
        RefreshModel();
    }

    // ------------------------------------------------------------------ handlers

    private void SwitchToTaskList()
    {
        _taskList = true;
        // A game begun from GameState lines is replaced by the task-list copy that follows.
        if (Game != null && !Game.IsOver)
        {
            Game = null;
            _inGame = false;
            _choice = null;
            _choiceBuilding = null;
            _infoForNextGame = true;
        }
    }

    private void ExpectNewGame()
    {
        _pendingNames = new Dictionary<int, string>();
        _pendingType = "";
        _infoForNextGame = true;
    }

    private void HandleGameInfo(string data)
    {
        bool applyNow = _inGame && Game != null && !_infoForNextGame;
        var pm = PlayerName.Match(data);
        if (pm.Success)
        {
            int id = int.Parse(pm.Groups["id"].Value, CultureInfo.InvariantCulture);
            var name = pm.Groups["name"].Value.Trim();
            _pendingNames[id] = name;
            if (applyNow)
            {
                _playerNames[id] = name;
                _dirty = true;
            }
            return;
        }
        int eq = data.IndexOf('=');
        if (eq > 0 && data[..eq].Trim() == "GameType")
        {
            _pendingType = data[(eq + 1)..].Trim();
            if (!applyNow) return;
            Game!.GameType = _pendingType;
            _dirty = true;
        }
    }

    private void HandlePower(string data, DateTime time)
    {
        if (data == "CREATE_GAME")
        {
            if (!_taskList) ExpectNewGame(); // GameState mode: the info lines come right after
            StartNewGame(time);
            return;
        }
        if (!_inGame) return;

        if (data.StartsWith("tag=", StringComparison.Ordinal))
        {
            var tm = TagLine.Match(data);
            if (tm.Success && _current != null) SetTag(_current, tm.Groups["tag"].Value, tm.Groups["value"].Value, time);
            return;
        }

        _current = null;

        Match mm;
        if ((mm = GameEntity.Match(data)).Success && data.StartsWith("GameEntity", StringComparison.Ordinal))
        {
            _gameEntityId = int.Parse(mm.Groups["id"].Value, CultureInfo.InvariantCulture);
            _current = GetOrAdd(_gameEntityId);
            return;
        }
        if ((mm = PlayerEntity.Match(data)).Success)
        {
            int eid = int.Parse(mm.Groups["eid"].Value, CultureInfo.InvariantCulture);
            int pid = int.Parse(mm.Groups["pid"].Value, CultureInfo.InvariantCulture);
            _playerEntityToId[eid] = pid;
            _playerAccountHi[pid] = long.TryParse(mm.Groups["hi"].Value, out var hi) ? hi : 0;
            _current = GetOrAdd(eid);
            return;
        }
        if ((mm = FullEntityUpdating.Match(data)).Success)
        {
            var e = FromBracket(mm.Groups["ent"].Value);
            if (e != null)
            {
                e.CardId = mm.Groups["card"].Value;
                _current = e;
            }
            return;
        }
        if ((mm = FullEntityCreating.Match(data)).Success)
        {
            var e = GetOrAdd(int.Parse(mm.Groups["id"].Value, CultureInfo.InvariantCulture));
            e.CardId = mm.Groups["card"].Value;
            _current = e;
            return;
        }
        if ((mm = ShowChangeEntity.Match(data)).Success)
        {
            var e = Resolve(mm.Groups["ent"].Value);
            if (e != null)
            {
                if (mm.Groups["card"].Value.Length > 0) e.CardId = mm.Groups["card"].Value;
                _current = e;
            }
            return;
        }
        if ((mm = TagChange.Match(data)).Success)
        {
            var e = Resolve(mm.Groups["ent"].Value);
            if (e != null) SetTag(e, mm.Groups["tag"].Value, mm.Groups["value"].Value, time);
        }
    }

    private void StartNewGame(DateTime time)
    {
        // A CREATE_GAME while a game is open means the old one was abandoned (crash, reconnect).
        if (Game != null && !Game.IsOver && IsBattlegrounds()) FinishGame(time, abandoned: true);

        _entities = new Dictionary<int, Entity>();
        _playerNames = new Dictionary<int, string>(_pendingNames);
        _playerEntityToId = new Dictionary<int, int>();
        _playerAccountHi = new Dictionary<int, long>();
        _leaderboardEntities = new HashSet<int>();
        _shopEntities = new HashSet<int>();
        _inCombat = false;
        _combatPending = false;
        _current = null;
        _gameEntityId = 1;
        _inGame = true;
        _announced = false;
        _choice = null;
        _choiceBuilding = null;
        _infoForNextGame = false;
        Game = new BgGame
        {
            Id = MakeId(time),
            StartedLocal = time,
            GameType = _pendingType,
        };
    }

    private bool _announced;

    private void SetTag(Entity e, string tag, string value, DateTime time)
    {
        // The client logs a tag by name when it has one ("ATK") and by number otherwise ("2022"), and that
        // can change between game versions. Keep both spellings so every reader finds it, and switch on
        // the canonical name.
        string? previous;
        int number = GameTags.Number(tag);
        if (number >= 0)
        {
            var numberKey = number.ToString(CultureInfo.InvariantCulture);
            var name = GameTags.Name(number);
            if (!e.Tags.TryGetValue(numberKey, out previous) && name != null) e.Tags.TryGetValue(name, out previous);
            e.Tags[numberKey] = value;
            if (name != null) e.Tags[name] = value;
            if (!ReferenceEquals(name, tag) && name != tag) e.Tags[tag] = value;
            tag = name ?? numberKey;
        }
        else
        {
            e.Tags.TryGetValue(tag, out previous);
            e.Tags[tag] = value;
        }
        switch (tag)
        {
            // Same signals as HDT (TagChangeActions): tag 2022 going 1→0 means a battle is starting (shopping
            // is over); tag 3533 going 1→0 means the combat has been set up, both boards included. HDT takes
            // its board snapshot on 3533, so we snapshot (and simulate) there too. The client logs both as numbers.
            case "BG_BATTLE_STARTING": // 2022
                if (previous == "1" && value == "0" && IsBattlegrounds())
                {
                    _inCombat = true;
                    _combatPending = true;
                    _combatIndex++;
                    _setupIndex = 0;
                }
                else if (previous == "0" && value == "1")
                {
                    _inCombat = false;
                    _combatPending = false;
                    CombatEnded?.Invoke();
                }
                break;
            case "IGNORE_MODIFIER_HERO_POWER_CHECK": // 3533: combat set up
                if (previous == "1" && value == "0" && IsBattlegrounds())
                {
                    // Every set-up is a snapshot. In Duos the game sets up each pairing in turn (the players
                    // who fight first, then the teammates) and the engine combines them, like HDT's Bob's Buddy.
                    _inCombat = true;
                    _combatPending = true;
                    TryRaiseCombat(time);
                }
                break;
            case "PROPOSED_ATTACKER":
            case "ATTACKING":
                // Fallback if the set-up tag never shows up: by the first attack both boards are on the table.
                if (_combatPending && value != "0") TryRaiseCombat(time);
                break;
            case "ZONE":
                // Outside combat, minions appearing on the other side of the table are Bob's shop offers.
                if (!_inCombat && (value == "PLAY" || value == "1")) _shopEntities.Add(e.Id);
                break;
            case "PLAYER_LEADERBOARD_PLACE":
                e.PlaceSeq = ++_seq;
                _leaderboardEntities.Add(e.Id);
                _dirty = true;
                break;
            case "PLAYER_TECH_LEVEL":
            case "HEALTH":
            case "DAMAGE":
            case "ARMOR":
            case "CONTROLLER":
                if (_leaderboardEntities.Contains(e.Id)) _dirty = true;
                break;
            case "TURN":
                if (e.Id == _gameEntityId) _dirty = true;
                break;
            case "STATE":
                if (e.Id == _gameEntityId && value == "COMPLETE") FinishGame(time, abandoned: false);
                break;
        }
    }

    /// <summary>Raises CombatStarted once per combat.</summary>
    private void TryRaiseCombat(DateTime time)
    {
        if (!_combatPending) return;
        _combatPending = false;
        // The opponent's board is on the table now; it isn't Bob's shop.
        int me = Game?.LocalPlayerId ?? 0;
        _shopEntities.RemoveWhere(id => _entities.TryGetValue(id, out var x)
            && (x.Str("ZONE") is "PLAY" or "1") && x.Int("CONTROLLER") != me);
        RaiseCombat(time);
    }

    private void RaiseCombat(DateTime time)
    {
        if (CombatStarted == null || Game == null) return;
        RefreshModel();
        int me = Game.LocalPlayerId;
        int opponent = _playerEntityToId.Values.FirstOrDefault(pid => pid != me);
        var copies = _entities.Values.Select(Copy).ToList();
        var players = new Dictionary<int, CombatEntity>();
        foreach (var (eid, pid) in _playerEntityToId)
        {
            var c = copies.FirstOrDefault(x => x.Id == eid);
            if (c != null) players[pid] = c;
        }
        CombatStarted.Invoke(new CombatSnapshot
        {
            GameId = Game.Id,
            Turn = Game.Turn,
            Duos = Game.IsDuos,
            LocalPlayerId = me,
            OpponentPlayerId = opponent,
            GameEntity = copies.FirstOrDefault(x => x.Id == _gameEntityId),
            CombatIndex = _combatIndex,
            SetupIndex = _setupIndex++,
            PlayerEntities = players,
            Entities = copies,
            TakenLocal = time,
        });
    }

    private static CombatEntity Copy(Entity e) => new()
    {
        Id = e.Id,
        CardId = e.CardId,
        Name = e.Name,
        Tags = new Dictionary<string, string>(e.Tags, StringComparer.Ordinal),
    };

    private void FinishGame(DateTime time, bool abandoned)
    {
        if (Game == null || Game.IsOver) return;
        _inGame = false;
        RefreshModel();
        if (!IsBattlegrounds())
        {
            Game = null; // constructed/arena/etc: not ours
            return;
        }
        Game.EndedLocal = time;
        if (abandoned) Log.Warn($"Game {Game.Id} ended without a result (client restarted?)");
        if (!_announced) { _announced = true; GameStarted?.Invoke(Game); }
        GameEnded?.Invoke(Game);
    }

    /// <summary>
    /// Card ids of the minions Bob has offered you in the shop this game. Only cards from the lobby's pool
    /// appear there, so their tribes reveal the lobby's tribes when memory reading isn't available.
    /// </summary>
    public IReadOnlyCollection<string> ShopCardIds()
    {
        if (Game == null || Game.LocalPlayerId == 0) return Array.Empty<string>();
        int me = Game.LocalPlayerId;
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in _shopEntities)
        {
            if (!_entities.TryGetValue(id, out var e) || e.CardId.Length == 0) continue;
            if (e.Str("CARDTYPE") is not ("MINION" or "4")) continue;
            int controller = e.Int("CONTROLLER");
            if (controller == 0 || controller == me) continue;
            result.Add(e.CardId);
        }
        return result;
    }

    // ------------------------------------------------------------------ choices

    private void HandleChoice(string data, bool chosen, DateTime time)
    {
        if (!_inGame) return;
        Match mm;
        if (!chosen && (mm = ChoicesHeader.Match(data)).Success)
        {
            _chosenBlock = false;
            _choiceBuilding = new OpenChoice
            {
                Id = int.Parse(mm.Groups["id"].Value, CultureInfo.InvariantCulture),
                Player = mm.Groups["player"].Value.Trim(),
                Type = mm.Groups["type"].Value,
                Turn = Game?.Turn ?? 0,
                CombatIndex = _combatIndex,
                Offered = time,
            };
            // Only the latest offer is shown; a new one for the same player replaces the old.
            if (_choice != null && _choice.Player == _choiceBuilding.Player) _choice = null;
            if (IsLocalPlayerName(_choiceBuilding.Player)) _choice = _choiceBuilding;
            return;
        }
        if (chosen && (mm = ChosenHeader.Match(data)).Success)
        {
            _chosenBlock = true;
            _choiceBuilding = null;
            int id = int.Parse(mm.Groups["id"].Value, CultureInfo.InvariantCulture);
            if (_choice != null && _choice.Id == id) _choice = null;
            return;
        }
        if (_chosenBlock || _choiceBuilding == null) return;
        if ((mm = ChoiceSource.Match(data)).Success)
        {
            _choiceBuilding.SourceEntity = Resolve(mm.Groups["src"].Value)?.Id ?? 0;
            return;
        }
        if ((mm = ChoiceEntity.Match(data)).Success)
        {
            var e = Resolve(mm.Groups["ent"].Value);
            if (e != null && !_choiceBuilding.Entities.Contains(e.Id)) _choiceBuilding.Entities.Add(e.Id);
        }
    }

    private bool IsLocalPlayerName(string name)
    {
        var preferred = _preferredName();
        if (!string.IsNullOrWhiteSpace(preferred) && Names.Key(preferred) == Names.Key(name)) return true;
        if (Game == null) return false;
        if (Game.LocalPlayerName.Length == 0) RefreshModel();
        if (Game.LocalPlayerName.Length > 0) return Names.Key(Game.LocalPlayerName) == Names.Key(name);
        // Local player not known yet: any real BattleTag (AI players and Bob don't get choices logged).
        return name.Contains('#');
    }

    /// <summary>The choice currently offered to you, with the offered cards as they are now (rerolls included).</summary>
    public BgChoice? ActiveChoice()
    {
        var c = _choice;
        if (c == null || Game == null || Game.IsOver || c.Entities.Count == 0) return null;
        // An offer never outlives its shopping phase: once a fight starts or the turn moves on, it has
        // been answered (even if the "chosen" line was missed), so stale stats never linger on the board.
        if (_inCombat || _combatIndex > c.CombatIndex || Game.Turn > c.Turn) return null;
        var options = c.Entities
            .Select(id => _entities.TryGetValue(id, out var e) ? e : null)
            .Where(e => e != null && e.CardId.Length > 0)
            .Select(e => new ChoiceOption
            {
                EntityId = e!.Id,
                CardId = e.CardId,
                RewardDbfId = TagInt(e, "QUEST_REWARD_DATABASE_ID", 1089),
            })
            .ToList();
        if (options.Count == 0) return null;
        var source = c.SourceEntity != 0 && _entities.TryGetValue(c.SourceEntity, out var se) ? se.CardId : "";
        return new BgChoice
        {
            Id = c.Id,
            GameId = Game.Id,
            ChoiceType = c.Type,
            SourceCardId = source,
            Turn = c.Turn,
            Kind = Classify(c, source, options),
            Options = options,
        };
    }

    private ChoiceKind Classify(OpenChoice c, string source, List<ChoiceOption> options)
    {
        string TypeOf(ChoiceOption o) => _entities.TryGetValue(o.EntityId, out var e) ? e.Str("CARDTYPE") : "";
        if (c.Type == "MULLIGAN" || options.All(o => TypeOf(o) is "HERO" or "3")) return ChoiceKind.Hero;
        if (source is "BG30_Trinket_1st" or "BG30_Trinket_2nd" || options.All(o => TypeOf(o) is "BATTLEGROUND_TRINKET" or "44")) return ChoiceKind.Trinket;
        if (source == "BG24_QuestsPlayerEnch_t" || options.All(o => o.RewardDbfId > 0)) return ChoiceKind.Quest;
        return ChoiceKind.Other;
    }

    /// <summary>Tags are logged by name when the client knows it, otherwise by number.</summary>
    private static int TagInt(Entity e, string name, int number)
    {
        int v = e.Int(name);
        return v != 0 ? v : e.Int(number.ToString(CultureInfo.InvariantCulture));
    }

    // ------------------------------------------------------------------ model

    private bool IsBattlegrounds()
    {
        if (Game == null) return false;
        if (Game.GameType.Length > 0) return Game.GameType.Contains("BATTLEGROUNDS", StringComparison.Ordinal);
        // No GameType line (very old logs): Battlegrounds is the mode with a player leaderboard.
        return _leaderboardEntities.Count > 0;
    }

    private void RefreshModel()
    {
        if (Game == null) return;

        // Which player is you: the configured BattleTag, else the only real BattleTag in the game.
        int myId = 0;
        var preferred = _preferredName();
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var key = Names.Key(preferred);
            myId = _playerNames.FirstOrDefault(p => Names.Key(p.Value) == key).Key;
        }
        if (myId == 0)
        {
            var humans = _playerNames
                .Where(p => p.Value.Contains('#') && p.Value != "UNKNOWN HUMAN PLAYER")
                .Where(p => !_playerAccountHi.TryGetValue(p.Key, out var hi) || hi != 0)
                .ToList();
            if (humans.Count >= 1) myId = humans[0].Key;
        }
        Game.LocalPlayerId = myId;
        Game.LocalPlayerName = myId != 0 && _playerNames.TryGetValue(myId, out var n) ? n : "";

        if (_entities.TryGetValue(_gameEntityId, out var ge))
        {
            // The game entity's TURN counts both halves of a round (shop + combat).
            int turn = ge.Int("TURN");
            Game.Turn = turn > 0 ? (turn + 1) / 2 : 0;
            Game.AnomalyDbfId = TagInt(ge, "BACON_GLOBAL_ANOMALY_DBID", 2897);
        }

        var heroes = _leaderboardEntities
            .Select(id => _entities[id])
            .Where(e => e.Int("PLAYER_LEADERBOARD_PLACE") > 0)
            .Where(e => e.Str("CARDTYPE") is "HERO" or "")
            // Combat copies share the card and controller; keep the one whose place changed last.
            .GroupBy(e => (e.CardId, Controller(e)))
            .Select(g => g.OrderByDescending(x => x.PlaceSeq).ThenBy(x => x.Id).First())
            .Select(e => new BgHero
            {
                EntityId = e.Id,
                CardId = e.CardId,
                Name = e.Name,
                Place = e.Int("PLAYER_LEADERBOARD_PLACE"),
                TavernTier = e.Int("PLAYER_TECH_LEVEL"),
                Health = e.Int("HEALTH"),
                Damage = e.Int("DAMAGE"),
                Armor = e.Int("ARMOR"),
                IsMine = myId != 0 && Controller(e) == myId,
            })
            .OrderBy(h => h.Place)
            .ToList();
        Game.Heroes = heroes;

        if (!_announced && IsBattlegrounds() && (Game.GameType.Length > 0 || heroes.Count > 0))
        {
            _announced = true;
            GameStarted?.Invoke(Game);
        }
    }

    private static int Controller(Entity e)
    {
        var c = e.Int("CONTROLLER");
        return c;
    }

    // ------------------------------------------------------------------ helpers

    private Entity GetOrAdd(int id)
    {
        if (!_entities.TryGetValue(id, out var e))
        {
            e = new Entity { Id = id };
            _entities[id] = e;
        }
        return e;
    }

    /// <summary>Entity references come as [entityName=.. id=N ...], GameEntity, a player name, or a bare id.</summary>
    private Entity? Resolve(string reference)
    {
        reference = reference.Trim();
        if (reference.StartsWith('[')) return FromBracket(reference);
        if (reference == "GameEntity") return GetOrAdd(_gameEntityId);
        if (int.TryParse(reference, out var id)) return GetOrAdd(id);

        // A player name: map it to that player's entity.
        foreach (var (pid, name) in _playerNames)
        {
            if (name == reference)
            {
                var eid = _playerEntityToId.FirstOrDefault(p => p.Value == pid).Key;
                return eid != 0 ? GetOrAdd(eid) : null;
            }
        }
        return null;
    }

    private Entity? FromBracket(string bracket)
    {
        var idm = BracketId.Match(bracket);
        if (!idm.Success) return null;
        var e = GetOrAdd(int.Parse(idm.Groups["id"].Value, CultureInfo.InvariantCulture));
        var nm = BracketName.Match(bracket);
        if (nm.Success && nm.Groups["name"].Value.Length > 0 && !nm.Groups["name"].Value.StartsWith("UNKNOWN", StringComparison.Ordinal))
            e.Name = nm.Groups["name"].Value;
        var cm = BracketCard.Match(bracket);
        if (cm.Success && cm.Groups["card"].Value.Length > 0 && e.CardId.Length == 0) e.CardId = cm.Groups["card"].Value;
        var pm = BracketPlayer.Match(bracket);
        if (pm.Success && !e.Tags.ContainsKey("CONTROLLER")) e.Tags["CONTROLLER"] = pm.Groups["p"].Value;
        return e;
    }

    private DateTime ToLocalTime(string ts)
    {
        if (!TimeSpan.TryParse(ts, CultureInfo.InvariantCulture, out var t)) return _baseDate.Add(_lastTime);
        // Log lines only carry the time of day; roll the date over at midnight.
        if (t < _lastTime - TimeSpan.FromHours(1)) _baseDate = _baseDate.AddDays(1);
        _lastTime = t;
        return _baseDate.Add(t);
    }

    private string MakeId(DateTime time)
    {
        var key = $"{_sourceKey}|{time:yyyyMMddHHmmssfff}";
        var hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}
