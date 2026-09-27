using TavernTracker.Core.Games;
using TavernTracker.Core.Hearthstone;
using TavernTracker.Core.Leaderboard;
using TavernTracker.Core.Combat;

namespace TavernTracker.Core;

/// <summary>
/// Wires everything together: reads Power.log, records finished games, and keeps leaderboard
/// snapshots fresh. The UI reads state from here; events fire on background threads.
/// </summary>
public sealed class TrackerEngine : IDisposable
{
    private readonly object _parseGate = new();
    private LogTailer? _tailer;
    private PowerLogParser? _parser;
    private readonly LeaderboardClient _client;
    private readonly DateTime _startedAt = DateTime.Now;
    private DateTime _hsCheckedAt = DateTime.MinValue;
    private volatile bool _hsRunning;

    // Memory reading (optional: null when unavailable).
    private readonly IGameMemory? _memory;
    private readonly CancellationTokenSource _cts = new();
    private Task? _memoryLoop;
    private volatile MemorySnapshot? _lastMemory;
    private volatile int _lastSolo, _lastDuos;
    private readonly object _lobbyGate = new();
    private string? _liveGameId;
    private bool _liveDuos;
    private List<MemoryPlayer> _liveLobby = new();
    private IReadOnlyList<int> _liveRaces = Array.Empty<int>();
    private readonly Dictionary<string, int> _ratingBefore = new();
    private (string Id, bool Duos, int Before, DateTime Until)? _pendingAfter;

    // Opponents' boards from your last fight against each (per game), and the hovered leaderboard hero.
    private readonly object _boardsGate = new();
    private readonly Dictionary<string, SeenBoard> _boards = new(StringComparer.Ordinal);
    private volatile string? _hoveredHero;

    /// <summary>Hero card id of the leaderboard portrait under the mouse ("" none, null unknown: no memory reading).</summary>
    public string? HoveredLeaderboardHero => MemoryAvailable ? _hoveredHero : null;

    /// <summary>The board this hero had when you last fought them in the live game, or null.</summary>
    public SeenBoard? LastSeenBoard(string gameId, string heroCardId)
    {
        var key = Cards.NormalizeHero(heroCardId);
        lock (_boardsGate) return _boards.TryGetValue(gameId + "|" + key, out var b) ? b : null;
    }

    // Combat odds
    private readonly ICombatSimulator? _simulator;
    private volatile CombatState _combat = CombatState.None;
    private int _combatSeq;

    /// <summary>The odds for the current (or last) combat of the live game.</summary>
    public CombatState CurrentCombat => _combat;
    public string SimulatorStatus => _simulator?.Status ?? "Off";

    public AppSettings Settings { get; }
    public GameStore Games { get; }
    public SnapshotService Snapshots { get; }
    public Cards.CardDatabase Cards { get; } = new();
    public Cards.TileCache Tiles { get; } = new();
    public Stats.PickStatsService PickStats { get; } = new();
    private (string Key, Stats.ChoiceStats? Value) _choiceCache = ("", null);

    public string? HearthstoneDirectory { get; private set; }
    public string? CurrentLogFile => _tailer?.CurrentFile;
    /// <summary>True when we had to turn on logging: Hearthstone must be restarted once.</summary>
    public bool NeedsHearthstoneRestart { get; private set; }

    public event Action? GamesChanged;
    public event Action<BoardHistory>? BoardUpdated;
    public event Action<string>? Notice;

    public TrackerEngine(AppSettings settings, IGameMemory? memory = null, ICombatSimulator? simulator = null)
    {
        Settings = settings;
        _memory = memory;
        _simulator = simulator;
        Games = new GameStore(AppPaths.File("games.json"));
        Games.Changed += () => GamesChanged?.Invoke();
        _client = new LeaderboardClient();
        Snapshots = new SnapshotService(_client, () => Settings.Region, () => TimeSpan.FromMinutes(Settings.SnapshotMinutes));
        Snapshots.Updated += b => BoardUpdated?.Invoke(b);
        Snapshots.Failed += msg => Notice?.Invoke(msg);
    }

    public void Start()
    {
        NeedsHearthstoneRestart = HearthstoneSetup.EnsureLogConfig();
        Snapshots.Start();
        _ = Cards.LoadAsync();
        StartLogReading();
        if (_memory != null) _memoryLoop = Task.Run(() => MemoryLoopAsync(_cts.Token));
    }

    /// <summary>(Re)starts reading logs, e.g. after the Hearthstone folder was changed in settings.</summary>
    public bool StartLogReading()
    {
        _tailer?.Dispose();
        _tailer = null;

        HearthstoneDirectory = HearthstoneSetup.FindInstall(Settings.HearthstoneDirectory);
        if (HearthstoneDirectory == null)
        {
            Log.Warn("Hearthstone install not found");
            return false;
        }
        if (Settings.HearthstoneDirectory != HearthstoneDirectory)
        {
            Settings.HearthstoneDirectory = HearthstoneDirectory;
            Settings.Save();
        }

        _tailer = new LogTailer(HearthstoneSetup.LogsDirectory(HearthstoneDirectory), OnLine, OnNewFile);
        _tailer.Start();
        return true;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _memoryLoop?.Wait(2000); } catch { /* shutting down */ }
        _memory?.Dispose();
        _simulator?.Dispose();
        _tailer?.Dispose();
        Cards.Dispose();
        Tiles.Dispose();
        PickStats.Dispose();
        Snapshots.Dispose();
        _client.Dispose();
    }

    public BoardHistory HomeBoard(bool duos) => Snapshots.Board(Settings.Region, duos);

    /// <summary>A copy of the game in progress, or null when you're not in a Battlegrounds game.</summary>
    public BgGame? LiveGame()
    {
        // A game left unfinished in an old log (Hearthstone closed mid-game) isn't live.
        if (!HearthstoneRunning()) return null;
        lock (_parseGate)
        {
            var g = _parser?.Game;
            if (g == null || g.IsOver) return null;
            if (g.GameType.Length > 0 && !g.GameType.Contains("BATTLEGROUNDS", StringComparison.Ordinal)) return null;
            if (g.GameType.Length == 0 && g.Heroes.Count == 0) return null;
            return Clone(g);
        }
    }

    /// <summary>
    /// Stats for the choice the game is offering you right now (heroes, trinkets, quests), or null when
    /// there's no such choice. Cached until the offer or the lobby's tribes change.
    /// </summary>
    public Stats.ChoiceStats? CurrentChoiceStats()
    {
        if (!Settings.ShowPickStats) return null;
        BgChoice? choice;
        bool duos;
        lock (_parseGate)
        {
            var g = _parser?.Game;
            if (g == null || g.IsOver) return null;
            choice = _parser!.ActiveChoice();
            duos = g.IsDuos;
        }
        if (choice == null || choice.Kind == ChoiceKind.Other) return null;
        var tribes = LobbyRaces().Select(Races.Key).ToList();
        var key = choice.Signature + "|" + string.Join(",", tribes);
        var cached = _choiceCache;
        if (cached.Key == key && cached.Value is { Message: null }) return cached.Value;
        var built = PickStats.Build(choice, duos, tribes, Cards.NormalizeHero, Cards.IdOf);
        // Still loading with the same message: keep the same object so the overlay doesn't redraw.
        if (cached.Key == key && cached.Value != null && built?.Message != null && built.Message == cached.Value.Message) return cached.Value;
        _choiceCache = (key, built);
        return built;
    }

    private int _hsChecking;

    /// <summary>
    /// Is Hearthstone open? Answers from the last check; a new check (a slow process scan) runs in the
    /// background at most every 5 seconds, so callers on the UI thread never wait for it.
    /// </summary>
    public bool HearthstoneRunning()
    {
        if (DateTime.UtcNow - _hsCheckedAt >= TimeSpan.FromSeconds(5) && Interlocked.Exchange(ref _hsChecking, 1) == 0)
        {
            bool first = _hsCheckedAt == DateTime.MinValue;
            _hsCheckedAt = DateTime.UtcNow;
            var check = Task.Run(() =>
            {
                try
                {
                    var procs = System.Diagnostics.Process.GetProcessesByName("Hearthstone");
                    _hsRunning = procs.Length > 0;
                    foreach (var p in procs) p.Dispose();
                }
                catch
                {
                    _hsRunning = true; // can't tell: don't hide anything
                }
                finally
                {
                    Interlocked.Exchange(ref _hsChecking, 0);
                }
            });
            if (first) check.Wait(2000); // the very first answer shouldn't be a guess
        }
        return _hsRunning;
    }

    public SessionSummary Session(bool duos) =>
        Sessions.Current(Games.All(), duos, TimeSpan.FromHours(Settings.SessionGapHours), DateTime.Now,
            HomeBoard(duos), Settings.BattleTag, CurrentRating(duos));

    /// <summary>Is memory reading on and working?</summary>
    public bool MemoryAvailable => _memory != null && _lastMemory != null && DateTime.UtcNow - _lastMemory.ReadUtc < TimeSpan.FromSeconds(30);
    public string MemoryStatus => _memory == null ? "Off" : _memory.Status;

    /// <summary>True while you're on the Battlegrounds menu screen (needs memory reading).</summary>
    public bool InBattlegroundsMenu => MemoryAvailable && _lastMemory!.InBattlegroundsMenu;

    /// <summary>Your rating as the game reports it (0 if unknown).</summary>
    public int CurrentRating(bool duos) => duos ? _lastDuos : _lastSolo;

    /// <summary>
    /// Tribes in the current lobby (Hearthstone race numbers); empty if unknown. Read from the game's memory;
    /// without it, worked out from the minions Bob offers in the shop (known once all five tribes have shown up).
    /// </summary>
    public IReadOnlyList<int> LobbyRaces()
    {
        lock (_lobbyGate)
        {
            if (_liveGameId == null) return Array.Empty<int>();
            if (_liveRaces.Count > 0) return _liveRaces;
        }
        return InferredRaces();
    }

    private (string Key, IReadOnlyList<int> Races) _inferred = ("", Array.Empty<int>());

    private IReadOnlyList<int> InferredRaces()
    {
        IReadOnlyCollection<string> seen;
        string gameId;
        lock (_parseGate)
        {
            var g = _parser?.Game;
            if (g == null || g.IsOver) return Array.Empty<int>();
            seen = _parser!.ShopCardIds();
            gameId = g.Id;
        }
        var key = gameId + "|" + seen.Count;
        if (_inferred.Key == key) return _inferred.Races;
        var byId = Cards.All().ToDictionary(c => c.Id, StringComparer.Ordinal);
        var tribes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in seen)
        {
            // Golden copies share the tribe of their normal card; only single-tribe minions are proof.
            if (!byId.TryGetValue(id, out var card) || card.IsSpell || !card.InPool) continue;
            if (card.Races.Count == 1 && card.Races[0] != "ALL") tribes.Add(card.Races[0]);
        }
        IReadOnlyList<int> result = tribes.Count >= 5
            ? tribes.Select(Races.Number).Where(n => n > 0).OrderBy(n => n).ToList()
            : Array.Empty<int>();
        if (result.Count > 0 && _inferred.Races.Count == 0) Log.Info($"Lobby tribes worked out from the shop: {string.Join(", ", tribes)}");
        _inferred = (key, result);
        return result;
    }

    /// <summary>Everyone in the current lobby with their public rating and how often you've met them.</summary>
    public IReadOnlyList<LobbyEntry> Lobby()
    {
        List<MemoryPlayer> players;
        bool duos;
        lock (_lobbyGate)
        {
            if (_liveGameId == null) return Array.Empty<LobbyEntry>();
            players = _liveLobby.ToList();
            duos = _liveDuos;
        }
        var myKey = Names.Key(Settings.BattleTag);
        var board = HomeBoard(duos);
        var met = MetCounts();
        return players
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .Select(p =>
            {
                var key = Names.Key(p.Name);
                bool me = key.Length > 0 && key == myKey;
                var row = board.Find(p.Name, out var same);
                return new LobbyEntry
                {
                    Name = Names.Display(p.Name),
                    HeroCardId = p.HeroCardId,
                    Place = p.Place,
                    TavernTier = p.TavernTier,
                    IsMe = me,
                    Rating = me ? CurrentRating(duos) : row?.Rating ?? 0,
                    Rank = row?.Rank ?? 0,
                    SharedName = same > 1,
                    TimesMet = me ? 0 : met.TryGetValue(key, out var n) ? n : 0,
                    Cutoff = board.HasData ? board.Cutoff : 0,
                };
            })
            .OrderBy(e => e.Place <= 0 ? 99 : e.Place)
            .ToList();
    }

    private Dictionary<string, int> MetCounts()
    {
        var counts = new Dictionary<string, int>();
        string? live;
        lock (_lobbyGate) live = _liveGameId;
        foreach (var g in Games.All())
        {
            if (g.Id == live) continue;
            foreach (var p in g.Lobby)
            {
                if (p.IsMine || string.IsNullOrWhiteSpace(p.PlayerName)) continue;
                var k = Names.Key(p.PlayerName);
                counts[k] = counts.TryGetValue(k, out var n) ? n + 1 : 1;
            }
        }
        return counts;
    }

    /// <summary>Reads a saved Power.log (e.g. from an older session) and adds any games found.</summary>
    public int ImportLog(string path)
    {
        int before = Games.All().Count;
        var parser = new PowerLogParser(path, () => Settings.BattleTag);
        parser.SetBaseDate(LogTailer.SessionDate(path));
        parser.GameEnded += RecordGame;
        foreach (var line in File.ReadLines(path)) parser.Feed(line);
        return Games.All().Count - before;
    }

    // ------------------------------------------------------------------ log callbacks (tailer thread)

    private void OnNewFile(string path)
    {
        // A log folder created after we switched logging on means Hearthstone has been restarted.
        if (NeedsHearthstoneRestart && LogTailer.SessionDate(path) > _startedAt) NeedsHearthstoneRestart = false;
        lock (_parseGate)
        {
            _parser = new PowerLogParser(path, () => Settings.BattleTag);
            _parser.SetBaseDate(LogTailer.SessionDate(path));
            _parser.GameStarted += OnGameStarted;
            _parser.CombatStarted += OnCombatStarted;
            _parser.CombatEnded += () => { if (_combat.Phase == CombatPhase.Done) _combat = _combat.With(inCombat: false); };
            _parser.GameEnded += RecordGame;
        }
    }

    private void OnGameStarted(BgGame g)
    {
        // Older games replayed from the log at startup aren't live.
        if (DateTime.Now - g.StartedLocal > TimeSpan.FromMinutes(5)) return;
        lock (_lobbyGate)
        {
            _liveGameId = g.Id;
            _liveDuos = g.IsDuos;
            _liveLobby = new List<MemoryPlayer>();
            _liveRaces = Array.Empty<int>();
            int before = CurrentRating(g.IsDuos);
            if (before > 0) _ratingBefore[g.Id] = before;
        }
        _pendingAfter = null;
        _combat = CombatState.None;
        lock (_boardsGate)
            foreach (var k in _boards.Keys.Where(k => !k.StartsWith(g.Id + "|", StringComparison.Ordinal)).ToList()) _boards.Remove(k);
        // Hero stats are needed within seconds (hero selection): fetch them now.
        if (Settings.ShowPickStats) PickStats.Ensure(ChoiceKind.Hero, g.IsDuos);
        // Warm the simulator up during hero selection so the first combat isn't delayed.
        if (_simulator != null && Settings.ShowCombatOdds) _ = _simulator.PrepareAsync();
    }

    private void OnCombatStarted(CombatSnapshot snap)
    {
        try
        {
            var seen = SeenBoard.FromCombat(snap);
            if (seen != null)
                lock (_boardsGate) _boards[snap.GameId + "|" + Cards.NormalizeHero(seen.HeroCardId)] = seen;
        }
        catch (Exception ex)
        {
            Log.Error("Reading the opponent's board failed", ex);
        }

        Log.Info($"Combat {snap.CombatIndex}.{snap.SetupIndex} on turn {snap.Turn}: you ({BattleInputBuilder.HeroCard(snap, snap.LocalPlayerId)}) vs {BattleInputBuilder.HeroCard(snap, snap.OpponentPlayerId)}"
                 + $" · {snap.Entities.Count(e => e.IsMinion && e.InPlay && e.Controller == snap.OpponentPlayerId)} enemy minions{(snap.Duos ? " (duos)" : "")}");

        if (_simulator == null || !Settings.ShowCombatOdds) return;
        // Combats replayed from the log at startup aren't live.
        if (DateTime.Now - snap.TakenLocal > TimeSpan.FromMinutes(2)) return;

        if (snap.Duos)
        {
            OnDuosSetup(snap);
            return;
        }
        Simulate(snap, () => BattleInputBuilder.Build(snap, LobbyRaces().ToList()));
    }

    // Duos: the first set-up of a combat is the pair that fights first; later set-ups swap in a teammate.
    private readonly object _duosGate = new();
    private (string Key, CombatSnapshot First, CombatSnapshot? PlayerMate, CombatSnapshot? OpponentMate, bool Full)? _duos;

    private void OnDuosSetup(CombatSnapshot snap)
    {
        var key = $"{snap.GameId}|{snap.Turn}|{snap.CombatIndex}";
        bool runFull = false, startTimer = false;
        lock (_duosGate)
        {
            if (_duos == null || _duos.Value.Key != key)
            {
                _duos = (key, snap, null, null, false);
                startTimer = true;
            }
            else
            {
                var d = _duos.Value;
                string myFirst = BattleInputBuilder.HeroCard(d.First, d.First.LocalPlayerId);
                string theirFirst = BattleInputBuilder.HeroCard(d.First, d.First.OpponentPlayerId);
                string myNow = BattleInputBuilder.HeroCard(snap, snap.LocalPlayerId);
                string theirNow = BattleInputBuilder.HeroCard(snap, snap.OpponentPlayerId);
                if (d.PlayerMate == null && myNow.Length > 0 && myNow != myFirst) d.PlayerMate = snap;
                if (d.OpponentMate == null && theirNow.Length > 0 && theirNow != theirFirst) d.OpponentMate = snap;
                if (!d.Full && d.PlayerMate != null && d.OpponentMate != null)
                {
                    d.Full = true;
                    runFull = true;
                }
                _duos = d;
            }
        }

        if (startTimer)
        {
            // Show "simulating" right away; if the teammates' boards don't all show up, run with what we have.
            _combat = new CombatState { Phase = CombatPhase.Running, Turn = snap.Turn, GameId = snap.GameId, InCombat = true };
            _ = Task.Run(async () =>
            {
                await Task.Delay(2500).ConfigureAwait(false);
                (string Key, CombatSnapshot First, CombatSnapshot? PlayerMate, CombatSnapshot? OpponentMate, bool Full) d;
                lock (_duosGate)
                {
                    if (_duos == null || _duos.Value.Key != key || _duos.Value.Full) return;
                    d = _duos.Value;
                }
                Simulate(d.First, () => BattleInputBuilder.BuildDuos(d.First, d.PlayerMate, d.OpponentMate, LobbyRaces().ToList()));
            });
        }
        if (runFull)
        {
            var d = _duos!.Value;
            Simulate(d.First, () => BattleInputBuilder.BuildDuos(d.First, d.PlayerMate, d.OpponentMate, LobbyRaces().ToList()));
        }
    }

    private void Simulate(CombatSnapshot snap, Func<BattleInputBuilder.Result> build)
    {
        int seq = Interlocked.Increment(ref _combatSeq);
        _combat = new CombatState { Phase = CombatPhase.Running, Turn = snap.Turn, GameId = snap.GameId, InCombat = true };
        _ = Task.Run(async () =>
        {
            CombatState result;
            try
            {
                var input = build();
                if (input.Json == null)
                {
                    Log.Info($"No odds for turn {snap.Turn}: {input.Problem}");
                    result = new CombatState { Phase = CombatPhase.Unavailable, Message = input.Problem, Turn = snap.Turn, GameId = snap.GameId, InCombat = true };
                }
                else if (!await _simulator!.PrepareAsync().ConfigureAwait(false))
                {
                    result = new CombatState { Phase = CombatPhase.Unavailable, Message = _simulator.Status, Turn = snap.Turn, GameId = snap.GameId, InCombat = true };
                }
                else
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var odds = _simulator.Run(input.Json);
                    Log.Info($"Combat turn {snap.Turn}: win {odds.Won:0.#}% tie {odds.Tied:0.#}% loss {odds.Lost:0.#}% ({odds.Simulations} sims, {sw.ElapsedMilliseconds} ms{(input.Partial ? ", duos partial" : "")})");
                    result = new CombatState
                    {
                        Phase = CombatPhase.Done, Odds = odds, Turn = snap.Turn, GameId = snap.GameId, InCombat = true,
                        Message = input.Partial ? "Partial: a teammate's board wasn't visible yet" : null,
                    };
                }
            }
            catch (Exception ex)
            {
                Log.Error("Combat simulation failed", ex);
                result = new CombatState { Phase = CombatPhase.Unavailable, Message = "Simulation failed (see log.txt)", Turn = snap.Turn, GameId = snap.GameId, InCombat = true };
            }
            // Only publish if no newer combat started meanwhile.
            if (seq == _combatSeq) _combat = result;
        });
    }

    private void OnLine(string line, DateTime _)
    {
        lock (_parseGate) _parser?.Feed(line);
    }

    private void RecordGame(BgGame g)
    {
        if (string.IsNullOrWhiteSpace(Settings.BattleTag) && g.LocalPlayerName.Contains('#'))
        {
            Settings.BattleTag = g.LocalPlayerName;
            Settings.Save();
            Log.Info($"BattleTag detected: {g.LocalPlayerName}");
        }

        var tag = string.IsNullOrWhiteSpace(Settings.BattleTag) ? g.LocalPlayerName : Settings.BattleTag;
        var board = HomeBoard(g.IsDuos);
        var ended = g.EndedLocal ?? DateTime.Now;
        bool recent = DateTime.Now - ended < TimeSpan.FromMinutes(10);
        // Live games use the current board; games read from older logs use the rating we had saved then.
        int rating = recent
            ? board.Find(tag, out _)?.Rating ?? 0
            : board.RatingAt(tag, ended.ToUniversalTime()) ?? 0;
        var record = GameRecord.From(g, rating);

        List<MemoryPlayer> lobby;
        lock (_lobbyGate)
        {
            bool live = _liveGameId == g.Id;
            lobby = live ? _liveLobby.ToList() : new List<MemoryPlayer>();
            if (_ratingBefore.TryGetValue(g.Id, out var before)) record.RatingBefore = before;
            if (live) { _liveGameId = null; }
        }
        AttachNames(record, lobby);
        // The results screen already shows the new rating (ignore a value left over from the previous game).
        if (record.RatingBefore > 0 && _lastMemory is { NewRating: > 0 } m && recent && m.NewRating != record.RatingBefore) record.RatingAfter = m.NewRating;

        if (!Games.Add(record)) return;
        if (g.IsRanked && recent && record.RatingBefore > 0 && record.RatingAfter == 0)
            _pendingAfter = (record.Id, g.IsDuos, record.RatingBefore, DateTime.UtcNow.AddMinutes(3));
        Log.Info($"Game recorded: {record.HeroName} place {record.Place} ({record.GameType})");

        // Grab a fresh board a few minutes later so the rating change shows up sooner.
        if (g.IsRanked && recent)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMinutes(4)).ConfigureAwait(false);
                await Snapshots.RefreshAsync(Settings.Region, g.IsDuos).ConfigureAwait(false);
            });
        }
    }

    /// <summary>Puts player names (read from memory during the game) onto the recorded lobby heroes.</summary>
    private static void AttachNames(GameRecord record, List<MemoryPlayer> lobby)
    {
        if (lobby.Count == 0) return;
        var used = new HashSet<MemoryPlayer>();
        foreach (var hero in record.Lobby)
        {
            var match = lobby.FirstOrDefault(p => !used.Contains(p) && p.HeroCardId == hero.CardId)
                        ?? lobby.FirstOrDefault(p => !used.Contains(p) && p.Place == hero.Place && p.Place > 0);
            if (match == null) continue;
            used.Add(match);
            hero.PlayerName = Names.Display(match.Name);
        }
    }

    // ------------------------------------------------------------------ memory loop (background)

    private async Task MemoryLoopAsync(CancellationToken ct)
    {
        var nextFull = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            bool inGame;
            lock (_lobbyGate) inGame = _liveGameId != null;
            try
            {
                if (HearthstoneRunningFresh())
                {
                    if (DateTime.UtcNow >= nextFull)
                    {
                        var snap = _memory!.Read(includeLobby: inGame);
                        if (snap != null) OnMemory(snap, inGame);
                        nextFull = DateTime.UtcNow.AddMilliseconds(inGame ? 1500 : 3000);
                    }
                    // Hovering a leaderboard portrait needs a quick answer, so poll it often during a game.
                    _hoveredHero = inGame ? _memory!.HoveredLeaderboardHero() : "";
                }
            }
            catch (Exception ex)
            {
                Log.Error("Memory loop", ex);
            }

            try { await Task.Delay(inGame ? 120 : 1000, ct).ConfigureAwait(false); }
            catch (TaskCanceledException) { break; }
        }
    }

    private void OnMemory(MemorySnapshot snap, bool inGame)
    {
        _lastMemory = snap;
        if (snap.Rating > 0) _lastSolo = snap.Rating;
        if (snap.DuosRating > 0) _lastDuos = snap.DuosRating;

        if (inGame)
        {
            lock (_lobbyGate)
            {
                if (_liveGameId != null)
                {
                    // Names fill in over the first seconds; keep the read with the most names.
                    if (snap.Players.Count(p => p.Name.Length > 0) >= _liveLobby.Count(p => p.Name.Length > 0))
                        _liveLobby = snap.Players.ToList();
                    else
                    {
                        // Keep names but refresh places/tiers.
                        var byHero = snap.Players.GroupBy(p => p.HeroCardId).ToDictionary(x => x.Key, x => x.First());
                        _liveLobby = _liveLobby.Select(old => byHero.TryGetValue(old.HeroCardId, out var fresh)
                            ? new MemoryPlayer { PlayerId = old.PlayerId, Name = old.Name.Length > 0 ? old.Name : fresh.Name, HeroCardId = old.HeroCardId, Place = fresh.Place, TavernTier = fresh.TavernTier, TriplesCount = fresh.TriplesCount }
                            : old).ToList();
                    }
                    if (snap.AvailableRaces.Count > 0) _liveRaces = snap.AvailableRaces;
                    if (!_ratingBefore.ContainsKey(_liveGameId))
                    {
                        int before = _liveDuos ? _lastDuos : _lastSolo;
                        if (before > 0) _ratingBefore[_liveGameId] = before;
                    }
                }
            }
        }

        // After a ranked game: wait for the new rating to show up.
        if (_pendingAfter is { } p)
        {
            if (DateTime.UtcNow > p.Until) { _pendingAfter = null; return; }
            int now = snap.NewRating > 0 ? snap.NewRating : (p.Duos ? snap.DuosRating : snap.Rating);
            if (now > 0 && now != p.Before)
            {
                Games.Update(p.Id, r => r.RatingAfter = now);
                Log.Info($"Rating after game: {now} ({now - p.Before:+0;-0;0})");
                _pendingAfter = null;
            }
        }
    }

    private DateTime _hsFreshAt = DateTime.MinValue;
    private bool _hsFresh;

    /// <summary>Same as HearthstoneRunning but safe to call from the memory thread.</summary>
    private bool HearthstoneRunningFresh()
    {
        if (DateTime.UtcNow - _hsFreshAt < TimeSpan.FromSeconds(5)) return _hsFresh;
        _hsFreshAt = DateTime.UtcNow;
        try
        {
            var procs = System.Diagnostics.Process.GetProcessesByName("Hearthstone");
            _hsFresh = procs.Length > 0;
            foreach (var pr in procs) pr.Dispose();
        }
        catch { _hsFresh = false; }
        return _hsFresh;
    }

    private static BgGame Clone(BgGame g) => new()
    {
        Id = g.Id,
        StartedLocal = g.StartedLocal,
        EndedLocal = g.EndedLocal,
        GameType = g.GameType,
        LocalPlayerName = g.LocalPlayerName,
        LocalPlayerId = g.LocalPlayerId,
        Turn = g.Turn,
        AnomalyDbfId = g.AnomalyDbfId,
        Heroes = g.Heroes.Select(h => new BgHero
        {
            EntityId = h.EntityId,
            CardId = h.CardId,
            Name = h.Name,
            Place = h.Place,
            TavernTier = h.TavernTier,
            Health = h.Health,
            Damage = h.Damage,
            Armor = h.Armor,
            IsMine = h.IsMine,
        }).ToList(),
    };
}
