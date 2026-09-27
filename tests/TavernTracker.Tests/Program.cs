using TavernTracker.Core;
using TavernTracker.Core.Games;
using TavernTracker.Core.Hearthstone;
using TavernTracker.Core.Leaderboard;
using Races = TavernTracker.Core.Hearthstone.Races;

int fails = 0;
void Check(bool ok, string what)
{
    Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
    if (!ok) fails++;
}

var dir = Path.Combine(Path.GetTempPath(), "tt-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
AppPaths.Root = dir;

// ---------------------------------------------------------------- log.config
{
    var (fresh, changed) = HearthstoneSetup.WithPowerSection("");
    Check(changed && fresh.Contains("[Power]") && fresh.Contains("Verbose=True"), "log.config: adds [Power] to empty file");

    var hdt = "[Achievements]\r\nLogLevel=1\r\nFilePrinting=True\r\n\r\n[Power]\r\nLogLevel=1\r\nFilePrinting=true\r\nConsolePrinting=False\r\nScreenPrinting=False\r\nVerbose=True\r\n";
    var (_, unchanged) = HearthstoneSetup.WithPowerSection(hdt);
    Check(!unchanged, "log.config: HDT's existing config needs no change (no restart nag)");

    var (fixedText, fixedChanged) = HearthstoneSetup.WithPowerSection("[Power]\nLogLevel=1\nVerbose=False\n[Zone]\nLogLevel=1\n");
    Check(fixedChanged && fixedText.Contains("Verbose=True") && fixedText.Contains("[Zone]") && fixedText.Contains("FilePrinting=True"),
        "log.config: fixes Verbose, adds missing keys, keeps other sections");
    var zoneIdx = fixedText.IndexOf("[Zone]", StringComparison.Ordinal);
    Check(fixedText.IndexOf("FilePrinting", StringComparison.Ordinal) < zoneIdx, "log.config: new keys stay inside [Power]");
}

// ---------------------------------------------------------------- Power.log parsing
string L(string t, string s) => $"D {t} GameState.{s}";
string P(string t, string s) => L(t, "DebugPrintPower() - " + s);
string G(string t, string s) => L(t, "DebugPrintGame() - " + s);
string Hero(int id, string name, string card, int player) =>
    $"[entityName={name} id={id} zone=PLAY zonePos=0 cardId={card} player={player}]";

var log = new List<string>
{
    // A constructed game first: must be ignored.
    P("18:59:00.0000001", "CREATE_GAME"),
    P("18:59:00.0000002", "    GameEntity EntityID=1"),
    G("18:59:00.0000003", "GameType=GT_RANKED"),
    G("18:59:00.0000004", "PlayerID=1, PlayerName=Tester#1234"),
    P("18:59:30.0000000", "TAG_CHANGE Entity=GameEntity tag=STATE value=COMPLETE"),

    // Battlegrounds game 1
    P("19:00:00.0000001", "CREATE_GAME"),
    P("19:00:00.0000002", "    GameEntity EntityID=1"),
    P("19:00:00.0000003", "        tag=TURN value=0"),
    P("19:00:00.0000004", "    Player EntityID=2 PlayerID=5 GameAccountId=[hi=144115198130930503 lo=12345]"),
    P("19:00:00.0000005", "        tag=PLAYER_ID value=5"),
    P("19:00:00.0000006", "    Player EntityID=3 PlayerID=13 GameAccountId=[hi=0 lo=0]"),
    G("19:00:00.0000007", "GameType=GT_BATTLEGROUNDS"),
    G("19:00:00.0000008", "FormatType=FT_WILD"),
    G("19:00:00.0000009", "PlayerID=5, PlayerName=Tester#1234"),
    G("19:00:00.0000010", "PlayerID=13, PlayerName=The Innkeeper"),
    // My hero
    P("19:00:05.0000000", "    FULL_ENTITY - Updating " + Hero(40, "Tess Greymane", "TB_BaconShop_HERO_50", 5) + " CardID=TB_BaconShop_HERO_50"),
    P("19:00:05.0000001", "        tag=CONTROLLER value=5"),
    P("19:00:05.0000002", "        tag=CARDTYPE value=HERO"),
    P("19:00:05.0000003", "        tag=HEALTH value=30"),
    P("19:00:05.0000004", "        tag=PLAYER_LEADERBOARD_PLACE value=1"),
    P("19:00:05.0000005", "        tag=PLAYER_TECH_LEVEL value=1"),
    // Opponents
    P("19:00:05.0000010", "    FULL_ENTITY - Updating " + Hero(41, "Rakanishu", "TB_BaconShop_HERO_75", 13) + " CardID=TB_BaconShop_HERO_75"),
    P("19:00:05.0000011", "        tag=CONTROLLER value=13"),
    P("19:00:05.0000012", "        tag=CARDTYPE value=HERO"),
    P("19:00:05.0000013", "        tag=HEALTH value=30"),
    P("19:00:05.0000014", "        tag=PLAYER_LEADERBOARD_PLACE value=2"),
    P("19:00:05.0000020", "    FULL_ENTITY - Updating " + Hero(42, "Ragnaros the Firelord", "TB_BaconShop_HERO_11", 16) + " CardID=TB_BaconShop_HERO_11"),
    P("19:00:05.0000021", "        tag=CONTROLLER value=16"),
    P("19:00:05.0000022", "        tag=CARDTYPE value=HERO"),
    P("19:00:05.0000023", "        tag=HEALTH value=30"),
    P("19:00:05.0000024", "        tag=PLAYER_LEADERBOARD_PLACE value=3"),
    P("19:01:00.0000000", "TAG_CHANGE Entity=GameEntity tag=TURN value=5"),
    P("19:01:00.0000001", "TAG_CHANGE Entity=" + Hero(40, "Tess Greymane", "TB_BaconShop_HERO_50", 5) + " tag=PLAYER_TECH_LEVEL value=4"),
    // A combat copy of Ragnaros (same card, same controller), created later with a stale place
    P("19:02:00.0000000", "    FULL_ENTITY - Updating " + Hero(900, "Ragnaros the Firelord", "TB_BaconShop_HERO_11", 16) + " CardID=TB_BaconShop_HERO_11"),
    P("19:02:00.0000001", "        tag=CONTROLLER value=16"),
    P("19:02:00.0000002", "        tag=CARDTYPE value=HERO"),
    P("19:02:00.0000003", "        tag=PLAYER_LEADERBOARD_PLACE value=3"),
    // Places shuffle; Ragnaros dies
    P("19:03:00.0000000", "TAG_CHANGE Entity=" + Hero(42, "Ragnaros the Firelord", "TB_BaconShop_HERO_11", 16) + " tag=PLAYER_LEADERBOARD_PLACE value=8"),
    P("19:03:00.0000001", "TAG_CHANGE Entity=" + Hero(42, "Ragnaros the Firelord", "TB_BaconShop_HERO_11", 16) + " tag=DAMAGE value=30"),
    P("19:03:00.0000002", "TAG_CHANGE Entity=" + Hero(41, "Rakanishu", "TB_BaconShop_HERO_75", 13) + " tag=PLAYER_LEADERBOARD_PLACE value=1"),
    P("19:03:00.0000003", "TAG_CHANGE Entity=" + Hero(40, "Tess Greymane", "TB_BaconShop_HERO_50", 5) + " tag=PLAYER_LEADERBOARD_PLACE value=2"),
    // Noise that must be ignored
    "D 19:03:01.0000000 PowerProcessor.DoTaskListForCard() - TAG_CHANGE Entity=GameEntity tag=STATE value=COMPLETE",
    "garbage line without prefix",
};
var endOfGame1 = new List<string>
{
    P("19:30:00.0000000", "TAG_CHANGE Entity=Tester#1234 tag=PLAYSTATE value=WON"),
    P("19:30:00.0000001", "TAG_CHANGE Entity=GameEntity tag=STATE value=COMPLETE"),
};
var game2 = new List<string>
{
    // Game 2 (duos) after midnight, never completes: next CREATE_GAME finishes it as abandoned
    P("23:59:00.0000000", "CREATE_GAME"),
    P("23:59:00.0000001", "    GameEntity EntityID=1"),
    P("23:59:00.0000002", "    Player EntityID=2 PlayerID=3 GameAccountId=[hi=144115198130930503 lo=12345]"),
    G("23:59:00.0000003", "GameType=GT_BATTLEGROUNDS_DUO"),
    G("23:59:00.0000004", "PlayerID=3, PlayerName=Tester#1234"),
    P("00:10:00.0000000", "    FULL_ENTITY - Creating ID=77 CardID=TB_BaconShop_HERO_22"),
    P("00:10:00.0000001", "        tag=CONTROLLER value=3"),
    P("00:10:00.0000002", "        tag=CARDTYPE value=HERO"),
    P("00:10:00.0000003", "        tag=PLAYER_LEADERBOARD_PLACE value=6"),
    P("00:20:00.0000000", "CREATE_GAME"),
};

var logFile = Path.Combine(dir, "Hearthstone_2026_09_26_18_55_00", "Power.log");
Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);

{
    var parser = new PowerLogParser(logFile);
    parser.SetBaseDate(LogTailer.SessionDate(logFile));
    var started = new List<BgGame>();
    var ended = new List<BgGame>();
    parser.GameStarted += started.Add;
    parser.GameEnded += ended.Add;

    foreach (var line in log) parser.Feed(line);
    var g = parser.Game!;
    Check(started.Count == 1 && started[0].GameType == "GT_BATTLEGROUNDS", "BG game detected, constructed game ignored");
    Check(g.LocalPlayerName == "Tester#1234" && g.LocalPlayerId == 5, "local player picked (not the Innkeeper)");
    Check(g.Heroes.Count == 3, $"3 heroes, combat copy de-duplicated (got {g.Heroes.Count})");
    Check(g.MyHero?.Name == "Tess Greymane" && g.MyPlace == 2 && g.MyHero.TavernTier == 4, "my hero, place 2, tier 4");
    var rag = g.Heroes.First(h => h.CardId == "TB_BaconShop_HERO_11");
    Check(rag.Place == 8 && rag.IsDead && rag.EntityId == 42, "dead opponent tracked on the live entity");
    Check(g.Turn == 3, $"turn number from game entity (got {g.Turn})");
    Check(!g.IsOver && ended.Count == 0, "PowerTaskList lines ignored (game still running)");

    foreach (var line in endOfGame1) parser.Feed(line);
    Check(ended.Count == 1 && ended[0].MyPlace == 2 && ended[0].IsRanked && !ended[0].IsDuos, "game ends: ranked solo, place 2");
    Check(ended[0].StartedLocal == new DateTime(2026, 9, 26, 19, 0, 0).AddTicks(1), "start time from folder date + line time");

    foreach (var line in game2) parser.Feed(line);
    Check(ended.Count == 2 && ended[1].IsDuos && ended[1].MyPlace == 6, "abandoned duos game finished by next CREATE_GAME");
    Check(ended[1].StartedLocal.Date == new DateTime(2026, 9, 26) && ended[1].EndedLocal!.Value.Date == new DateTime(2026, 9, 27),
        "date rolls over at midnight");

    var p2 = new PowerLogParser(logFile);
    p2.SetBaseDate(LogTailer.SessionDate(logFile));
    var again = new List<BgGame>();
    p2.GameEnded += again.Add;
    foreach (var line in log.Concat(endOfGame1)) p2.Feed(line);
    Check(again.Count == 1 && again[0].Id == ended[0].Id, "re-reading the same log gives the same game id");
}

// ---------------------------------------------------------------- tailer
{
    File.WriteAllText(logFile, "");
    var got = new List<string>();
    var t = new LogTailer(Path.Combine(dir), (l, _) => got.Add(l), _ => { });
    Check(LogTailer.FindNewestPowerLog(dir) == logFile, "finds newest Hearthstone_* log folder");
    t.OpenFile(logFile);
    File.AppendAllText(logFile, "line one\r\nline tw");
    t.ReadNew();
    File.AppendAllText(logFile, "o\nÜñíçødé #3\n");
    t.ReadNew();
    Check(got.SequenceEqual(new[] { "line one", "line two", "Üñíçødé #3" }), "tailer: partial lines and UTF-8 handled");
}

// ---------------------------------------------------------------- leaderboard + history
static LeaderboardClient.Response Page(int total, params (int rank, string name, int rating)[] rows) => new()
{
    SeasonId = 19,
    Leaderboard = new LeaderboardClient.Board
    {
        Pagination = new LeaderboardClient.Pagination { TotalPages = total },
        Rows = rows.Select(r => new LeaderboardClient.Row { Rank = r.rank, AccountId = r.name, Rating = r.rating }).ToList(),
    },
};

{
    var ratings = new Dictionary<string, int> { ["Top"] = 15000, ["Tester"] = 9000, ["Bob"] = 8000 };
    var client = new LeaderboardClient((url, _) =>
    {
        if (!url.Contains("leaderboardId=battlegrounds&")) throw new Exception("wrong board " + url);
        return Task.FromResult<LeaderboardClient.Response?>(url.EndsWith("page=1")
            ? Page(2, (1, "Top", ratings["Top"]), (2, "Tester", ratings["Tester"]))
            : Page(2, (3, "Bob", ratings["Bob"]), (4, "bob", 7000)));
    });

    var board = new BoardHistory("EU", false);
    var t0 = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
    var d1 = await client.DownloadAsync("EU", false);
    board.Ingest(new LeaderboardDownload { Region = "EU", Duos = false, SeasonId = d1.SeasonId, FetchedUtc = t0, Rows = d1.Rows });
    Check(board.Find("tester#1234", out _)?.Rating == 9000, "board lookup by BattleTag");
    board.Find("BOB", out var same);
    Check(same == 2 && board.Cutoff == 7000, "duplicate names counted, cutoff = lowest rating");

    ratings["Tester"] = 9200;
    var d2 = await client.DownloadAsync("EU", false);
    board.Ingest(new LeaderboardDownload { Region = "EU", Duos = false, SeasonId = 19, FetchedUtc = t0.AddDays(2), Rows = d2.Rows });
    ratings["Tester"] = 9150;
    var d3 = await client.DownloadAsync("EU", false);
    board.Ingest(new LeaderboardDownload { Region = "EU", Duos = false, SeasonId = 19, FetchedUtc = t0.AddDays(6), Rows = d3.Rows });

    var prof = board.Profile("Tester", t0.AddDays(6))!;
    Check(prof.History.Count == 3 && prof.Peak == 9200 && prof.Rating == 9150, "profile: 3 rating changes, peak 9200");
    Check(prof.Change7d == null, "7-day change unknown when history is shorter than 7 days");
    Check(prof.Change24h == -50, $"24h change -50 (got {prof.Change24h})");
    Check(board.Profile("Top", t0.AddDays(6))!.History.Count == 1, "unchanged rating is stored once");
    Check(board.Search("te").First().Name == "Tester" && board.Search("o").Count == 3, "search: prefix first, contains");
    Check(board.RatingAt("Tester", t0.AddDays(3)) == 9200, "rating at a past moment");

    var reloaded = new BoardHistory("EU", false);
    reloaded.Load();
    Check(reloaded.Profile("Tester", t0.AddDays(6))!.History.Count == 3 && reloaded.SeasonId == 19, "history reloads from disk");

    // new season resets history
    var d4 = new LeaderboardDownload { Region = "EU", Duos = false, SeasonId = 20, FetchedUtc = t0.AddDays(30), Rows = new[] { new LeaderboardRow(1, "Tester", 6000) } };
    reloaded.Ingest(d4);
    Check(reloaded.Profile("Tester", t0.AddDays(30))!.History.Count == 1 && reloaded.Profile("Top", t0.AddDays(30)) == null, "new season starts fresh history");

    var failing = new LeaderboardClient((_, _) => throw new HttpRequestException("offline"));
    bool threw = false;
    try { await failing.DownloadAsync("EU", false); } catch (InvalidOperationException) { threw = true; }
    Check(threw, "download failure surfaces as an error (after retries)");

    // ---------------------------------------------------------------- games + sessions
    var store = new GameStore(Path.Combine(dir, "games.json"));
    var baseTime = new DateTime(2026, 9, 26, 18, 0, 0);
    GameRecord Game(string id, int minutesAfter, int place, bool ranked = true) => new()
    {
        Id = id, StartedLocal = baseTime.AddMinutes(minutesAfter), EndedLocal = baseTime.AddMinutes(minutesAfter + 15),
        GameType = ranked ? "GT_BATTLEGROUNDS" : "GT_BATTLEGROUNDS_FRIENDLY", Ranked = ranked, Place = place,
    };
    store.Add(Game("old", -600, 8));        // yesterday-ish: separate session
    store.Add(Game("a", 0, 3));
    store.Add(Game("b", 20, 1));
    store.Add(Game("c", 40, 5, ranked: false)); // friendly: not counted
    Check(!store.Add(Game("a", 0, 3)), "duplicate game ignored");
    var s = Sessions.Current(store.All(), false, TimeSpan.FromHours(3), baseTime.AddMinutes(70), null, "");
    Check(s.Count == 2 && s.AvgPlace == 2.0 && s.Top4 == 2 && s.Firsts == 1, "session: 2 ranked games, avg 2.0");
    var s2 = Sessions.Current(store.All(), false, TimeSpan.FromHours(3), baseTime.AddHours(9), null, "");
    Check(s2.Count == 0, "long break starts a new session");
    Check(new GameStore(Path.Combine(dir, "games.json")).All().Count == 4, "games reload from disk");
}

// ---------------------------------------------------------------- card data
{
    var raw = new List<TavernTracker.Core.Cards.CardDatabase.RawCard>
    {
        new() { Id = "BG_1", Name = "Beasty", Type = "MINION", TechLevel = 1, IsBattlegroundsPoolMinion = true, Races = new[] { "BEAST" }, Text = "<b>Battlecry:</b> Gain +1/+1." , Attack = 1, Health = 1 },
        new() { Id = "BG_2", Name = "Clanky", Type = "MINION", TechLevel = 2, IsBattlegroundsPoolMinion = true, Races = new[] { "MECHANICAL" }, Mechanics = new[] { "DEATHRATTLE" } },
        new() { Id = "BG_3", Name = "Plain", Type = "MINION", TechLevel = 1, IsBattlegroundsPoolMinion = true, Text = "<b>Avenge (2):</b> Something." },
        new() { Id = "BG_4", Name = "Amalgam", Type = "MINION", TechLevel = 3, IsBattlegroundsPoolMinion = true, Races = new[] { "ALL" } },
        new() { Id = "BG_5", Name = "DuoGuy", Type = "MINION", TechLevel = 2, IsBattlegroundsPoolMinion = true, Races = new[] { "BEAST" }, IsBattlegroundsDuosExclusive = true },
        new() { Id = "BG_S", Name = "Coin Spell", Type = "BATTLEGROUND_SPELL", TechLevel = 1, IsBattlegroundsPoolSpell = true },
        new() { Id = "HERO_1", Name = "Some Hero", Type = "HERO", BattlegroundsHero = true },
        new() { Id = "CS_1", Name = "Constructed", Type = "MINION", Races = new[] { "BEAST" } },
    };
    var (cards, names) = TavernTracker.Core.Cards.CardDatabase.Filter(raw);
    Check(cards.Count == 6 && names["HERO_1"] == "Some Hero", "card data: only Battlegrounds pool cards kept, hero names kept");
    Check(cards.Single(c => c.Id == "BG_S").IsSpell, "tavern spells recognised");
    Check(TavernTracker.Core.Cards.Keywords.Has(cards.Single(c => c.Id == "BG_1"), "Battlecry"), "keyword from card text");
    Check(TavernTracker.Core.Cards.Keywords.Has(cards.Single(c => c.Id == "BG_2"), "Deathrattle"), "keyword from mechanics");
    Check(TavernTracker.Core.Cards.Keywords.Has(cards.Single(c => c.Id == "BG_3"), "Avenge"), "Avenge (N) matched");
    Check(cards.Single(c => c.Id == "BG_1").PlainText == "Battlecry: Gain +1/+1.", "card text markup removed");
    Check(Races.Key(126) == "ABERRATION" && Races.Label(17) == "Mech", "race numbers mapped to tribes");

    // Live pool corrections: rotated-out minions still flagged in the game data must disappear.
    var pool = new List<TavernTracker.Core.Cards.BgCard>
    {
        new() { Id = "BG29_810", DbfId = 108116, Name = "Thousandth Paper Drake", Tier = 2, Races = new() { "DRAGON" }, InPool = true },
        new() { Id = "D1", DbfId = 1, Name = "Glim Guardian", Tier = 1, Races = new() { "DRAGON" }, InPool = true },
        new() { Id = "N1", DbfId = 2, Name = "Mini-Myrmidon", Tier = 1, Races = new() { "NAGA" }, InPool = true },
        new() { Id = "B1", DbfId = 3, Name = "Back in pool", Tier = 1, Races = new() { "BEAST" }, InPool = false },
    };
    using var cdb = new TavernTracker.Core.Cards.CardDatabase();
    cdb.SetForTests(pool, new Dictionary<int, bool> { [108116] = false, [3] = true }, new List<string> { "DRAGON", "BEAST" });
    var poolNames = cdb.Pool(null, false).Select(c => c.Name).ToList();
    Check(!poolNames.Contains("Thousandth Paper Drake"), "rotated-out minion removed by live correction");
    Check(poolNames.Contains("Back in pool") && poolNames.Contains("Glim Guardian"), "minions added back by live correction");
    Check(!poolNames.Contains("Mini-Myrmidon"), "tribes not in rotation hidden before the lobby is known");
    Check(cdb.Pool(new[] { "BEAST" }, false).All(c => c.Races.Contains("BEAST")), "lobby tribes filter the list");
}

// ---------------------------------------------------------------- sessions with the game's rating
{
    var t = new DateTime(2026, 9, 26, 20, 0, 0);
    var games = new List<GameRecord>
    {
        new() { Id = "x1", StartedLocal = t, EndedLocal = t.AddMinutes(15), Ranked = true, GameType = "GT_BATTLEGROUNDS", Place = 2, RatingBefore = 9374, RatingAfter = 9398 },
        new() { Id = "x2", StartedLocal = t.AddMinutes(20), EndedLocal = t.AddMinutes(35), Ranked = true, GameType = "GT_BATTLEGROUNDS", Place = 1, RatingBefore = 9398, RatingAfter = 9464 },
    };
    var s = Sessions.Current(games, false, TimeSpan.FromHours(3), t.AddMinutes(40), null, "", liveRating: 9464);
    Check(s.RatingStart == 9374 && s.RatingNow == 9464 && s.Delta == 90, "session MMR from the game: 9,374 → 9,464 (+90)");
    Check(games[1].RatingDelta == 66, "per-game MMR change");
    var fresh = Sessions.Current(new List<GameRecord>(), false, TimeSpan.FromHours(3), t, null, "", liveRating: 9464);
    Check(fresh.RatingStart == 9464 && fresh.RatingNow == 9464, "no games yet: start = current rating");
}

// ---------------------------------------------------------------- combat snapshot -> simulator input
{
    string Ent(int id, string name, string card, int player) => $"[entityName={name} id={id} zone=PLAY zonePos=0 cardId={card} player={player}]";
    var combatLog = new List<string>
    {
        P("21:00:00.0000001", "CREATE_GAME"),
        P("21:00:00.0000002", "    GameEntity EntityID=1"),
        P("21:00:00.0000003", "        tag=2022 value=1"),
        P("21:00:00.0000003", "        tag=3533 value=1"),
        P("21:00:00.0000004", "    Player EntityID=2 PlayerID=5 GameAccountId=[hi=144115198130930503 lo=12345]"),
        P("21:00:00.0000005", "        tag=HERO_ENTITY value=40"),
        P("21:00:00.0000006", "        tag=BACON_ELEMENTAL_BUFFATKVALUE value=2"),
        P("21:00:00.0000007", "    Player EntityID=3 PlayerID=13 GameAccountId=[hi=0 lo=0]"),
        P("21:00:00.0000008", "        tag=HERO_ENTITY value=41"),
        G("21:00:00.0000009", "GameType=GT_BATTLEGROUNDS"),
        G("21:00:00.0000010", "PlayerID=5, PlayerName=Tester#1234"),
        G("21:00:00.0000011", "PlayerID=13, PlayerName=The Innkeeper"),
        // Heroes
        P("21:00:01.0000000", "    FULL_ENTITY - Updating " + Ent(40, "Tess Greymane", "TB_BaconShop_HERO_50", 5) + " CardID=TB_BaconShop_HERO_50"),
        P("21:00:01.0000001", "        tag=CONTROLLER value=5"),
        P("21:00:01.0000002", "        tag=CARDTYPE value=HERO"),
        P("21:00:01.0000003", "        tag=ZONE value=PLAY"),
        P("21:00:01.0000004", "        tag=HEALTH value=30"),
        P("21:00:01.0000005", "        tag=DAMAGE value=5"),
        P("21:00:01.0000006", "        tag=ARMOR value=2"),
        P("21:00:01.0000007", "        tag=PLAYER_TECH_LEVEL value=3"),
        P("21:00:01.0000008", "        tag=PLAYER_LEADERBOARD_PLACE value=1"),
        P("21:00:01.0000010", "    FULL_ENTITY - Updating " + Ent(41, "Rakanishu", "TB_BaconShop_HERO_75", 13) + " CardID=TB_BaconShop_HERO_75"),
        P("21:00:01.0000011", "        tag=CONTROLLER value=13"),
        P("21:00:01.0000012", "        tag=CARDTYPE value=HERO"),
        P("21:00:01.0000013", "        tag=ZONE value=PLAY"),
        P("21:00:01.0000014", "        tag=HEALTH value=30"),
        P("21:00:01.0000015", "        tag=PLAYER_TECH_LEVEL value=2"),
        // My board: a 10/10 Busker (position 2) with divine shield and a 1/1 Bonehead (position 1), plus an enchantment
        P("21:00:02.0000000", "    FULL_ENTITY - Updating " + Ent(50, "Southsea Busker", "BG26_135", 5) + " CardID=BG26_135"),
        P("21:00:02.0000001", "        tag=CONTROLLER value=5"),
        P("21:00:02.0000002", "        tag=CARDTYPE value=MINION"),
        P("21:00:02.0000003", "        tag=ZONE value=PLAY"),
        P("21:00:02.0000004", "        tag=ZONE_POSITION value=2"),
        P("21:00:02.0000005", "        tag=ATK value=10"),
        P("21:00:02.0000006", "        tag=HEALTH value=10"),
        P("21:00:02.0000007", "        tag=DIVINE_SHIELD value=1"),
        P("21:00:02.0000010", "    FULL_ENTITY - Updating " + Ent(51, "Harmless Bonehead", "BG28_300", 5) + " CardID=BG28_300"),
        P("21:00:02.0000011", "        tag=CONTROLLER value=5"),
        P("21:00:02.0000012", "        tag=CARDTYPE value=MINION"),
        P("21:00:02.0000013", "        tag=ZONE value=PLAY"),
        P("21:00:02.0000014", "        tag=ZONE_POSITION value=1"),
        P("21:00:02.0000015", "        tag=ATK value=1"),
        P("21:00:02.0000016", "        tag=HEALTH value=1"),
        P("21:00:02.0000020", "    FULL_ENTITY - Updating " + Ent(60, "Some Buff", "BG_ENCH_X", 5) + " CardID=BG_ENCH_X"),
        P("21:00:02.0000021", "        tag=CONTROLLER value=5"),
        P("21:00:02.0000022", "        tag=CARDTYPE value=ENCHANTMENT"),
        P("21:00:02.0000023", "        tag=ZONE value=PLAY"),
        P("21:00:02.0000024", "        tag=ATTACHED value=50"),
        P("21:00:02.0000025", "        tag=CREATOR value=51"),
        // A minion in the shop (controlled by the other player, zone SETASIDE) must be ignored
        P("21:00:02.0000030", "    FULL_ENTITY - Updating " + Ent(70, "Shop Minion", "BG26_135", 13) + " CardID=BG26_135"),
        P("21:00:02.0000031", "        tag=CONTROLLER value=13"),
        P("21:00:02.0000032", "        tag=CARDTYPE value=MINION"),
        P("21:00:02.0000033", "        tag=ZONE value=SETASIDE"),
        // Opponent board: one 2/2 (with 1 damage taken earlier -> health 1)
        P("21:00:03.0000000", "    FULL_ENTITY - Updating " + Ent(80, "Southsea Busker", "BG26_135", 13) + " CardID=BG26_135"),
        P("21:00:03.0000001", "        tag=CONTROLLER value=13"),
        P("21:00:03.0000002", "        tag=CARDTYPE value=MINION"),
        P("21:00:03.0000003", "        tag=ZONE value=PLAY"),
        P("21:00:03.0000004", "        tag=ZONE_POSITION value=1"),
        P("21:00:03.0000005", "        tag=ATK value=2"),
        P("21:00:03.0000006", "        tag=HEALTH value=2"),
        P("21:00:03.0000007", "        tag=DAMAGE value=1"),
        P("21:00:03.0000008", "        tag=TAUNT value=1"),
        P("21:00:04.0000000", "TAG_CHANGE Entity=GameEntity tag=TURN value=6"),
        P("21:00:04.0000001", "TAG_CHANGE Entity=GameEntity tag=2022 value=0"),
        P("21:00:04.0000002", "TAG_CHANGE Entity=GameEntity tag=3533 value=0"),
    };
    var cp = new PowerLogParser(logFile);
    cp.SetBaseDate(new DateTime(2026, 9, 26, 20, 59, 0));
    CombatSnapshot? snap = null;
    cp.CombatStarted += x => snap = x;
    foreach (var line in combatLog) cp.Feed(line);
    Check(snap != null && snap.LocalPlayerId == 5 && snap.OpponentPlayerId == 13 && snap.Turn == 3, "combat start detected (tag 2022 1→0), sides identified");

    var seen = SeenBoard.FromCombat(snap!);
    Check(seen != null && seen.HeroCardId == "TB_BaconShop_HERO_75" && seen.Turn == 3 && seen.Minions.Count == 1
          && seen.Minions[0].Attack == 2 && seen.Minions[0].Health == 1 && seen.Minions[0].Taunt,
        "opponent's board remembered from the combat (hero, turn, 2/1 taunt)");
    var built = TavernTracker.Core.Combat.BattleInputBuilder.Build(snap!, new[] { 20, 24, 23, 11, 126 }, 3000);
    Check(built.Json != null && built.PlayerMinions == 2 && built.OpponentMinions == 1, $"input built: 2 vs 1 minions ({built.Problem})");
    var root = System.Text.Json.Nodes.JsonNode.Parse(built.Json!)!;
    var pb = root["playerBoard"]!;
    Check((int)pb["player"]!["hpLeft"]! == 27 && (int)pb["player"]!["tavernTier"]! == 3, "hero health (30+2-5) and tier");
    Check((string)pb["board"]![0]!["cardId"]! == "BG28_300" && (bool)pb["board"]![1]!["divineShield"]!, "board ordered by position, divine shield kept");
    Check((string)pb["board"]![1]!["enchantments"]![0]!["cardId"]! == "BG_ENCH_X", "enchantments attached to the right minion");
    Check((int)pb["player"]!["globalInfo"]!["ElementalAttackBuff"]! == 2, "player counters read");
    var ob = root["opponentBoard"]!["board"]![0]!;
    Check((int)ob["health"]! == 1 && (bool)ob["taunt"]!, "opponent minion damage and taunt");

    var raw = System.Text.Encoding.UTF8.GetBytes("[{\"id\":\"X\"}]");
    using (var ms = new MemoryStream())
    {
        using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest, true)) gz.Write(raw);
        Check(TavernTracker.Core.Combat.SimCardData.Decode(ms.ToArray()) == "[{\"id\":\"X\"}]" &&
              TavernTracker.Core.Combat.SimCardData.Decode(raw) == "[{\"id\":\"X\"}]", "simulator card file read gzipped or plain");
    }
    var duo = TavernTracker.Core.Combat.BattleInputBuilder.Build(new CombatSnapshot { Duos = true }, Array.Empty<int>());
    Check(duo.Json == null && duo.Problem != null, "a duos snapshot without sides gives a reason, not bad odds");

    var export = Environment.GetEnvironmentVariable("TT_EXPORT_COMBAT");
    if (!string.IsNullOrEmpty(export)) File.WriteAllText(export, built.Json);
}

// ---------------------------------------------------------------- PowerTaskList lines (HDT's source of truth)
{
    string T(string t, string s) => $"D {t} PowerTaskList.DebugPrintPower() - {s}";
    string Ent(int id, string name, string card, int player) => $"[entityName={name} id={id} zone=PLAY zonePos=0 cardId={card} player={player}]";
    // The server copy (GameState) flips the combat tag before the opponent's minions exist; the task list
    // plays the minions out first. Snapshots must follow the task list, like HDT.
    var lines = new List<string>
    {
        P("23:00:00.0000001", "CREATE_GAME"),
        G("23:00:00.0000002", "GameType=GT_BATTLEGROUNDS"),
        G("23:00:00.0000003", "PlayerID=5, PlayerName=Tester#1234"),
        G("23:00:00.0000004", "PlayerID=13, PlayerName=The Innkeeper"),
        P("23:00:00.0000005", "    GameEntity EntityID=1"),
        P("23:00:00.0000006", "        tag=2022 value=1"),
        T("23:00:00.1000001", "CREATE_GAME"),
        T("23:00:00.1000002", "    GameEntity EntityID=1"),
        T("23:00:00.1000003", "        tag=2022 value=1"),
        T("23:00:00.1000003", "        tag=3533 value=1"),
        T("23:00:00.1000004", "    Player EntityID=2 PlayerID=5 GameAccountId=[hi=144115198130930503 lo=12345]"),
        T("23:00:00.1000005", "    Player EntityID=3 PlayerID=13 GameAccountId=[hi=0 lo=0]"),
        T("23:00:00.1000006", "        tag=HERO_ENTITY value=45"),
        // Bob sits on the opponent's side between fights; the snapshot must not take him for the opponent.
        T("23:00:00.2000000", "    FULL_ENTITY - Updating " + Ent(45, "Bob", "TB_BaconShopBob", 13) + " CardID=TB_BaconShopBob"),
        T("23:00:00.2000001", "        tag=CONTROLLER value=13"),
        T("23:00:00.2000002", "        tag=CARDTYPE value=HERO"),
        T("23:00:00.2000003", "        tag=ZONE value=PLAY"),
        T("23:00:01.0000000", "    FULL_ENTITY - Updating " + Ent(40, "Me", "TB_BaconShop_HERO_50", 5) + " CardID=TB_BaconShop_HERO_50"),
        T("23:00:01.0000001", "        tag=CONTROLLER value=5"),
        T("23:00:01.0000002", "        tag=CARDTYPE value=HERO"),
        T("23:00:01.0000003", "        tag=ZONE value=PLAY"),
        T("23:00:01.0000004", "        tag=PLAYER_LEADERBOARD_PLACE value=1"),
        T("23:00:01.0000010", "    FULL_ENTITY - Updating " + Ent(41, "Rakanishu", "TB_BaconShop_HERO_75", 13) + " CardID=TB_BaconShop_HERO_75"),
        T("23:00:01.0000011", "        tag=CONTROLLER value=13"),
        T("23:00:01.0000012", "        tag=CARDTYPE value=HERO"),
        T("23:00:01.0000013", "        tag=ZONE value=PLAY"),
        T("23:00:01.0000014", "        tag=PLAYER_LEADERBOARD_PLACE value=2"),
        // Server copy flips first...
        P("23:00:02.0000000", "TAG_CHANGE Entity=GameEntity tag=2022 value=0"),
        // ...the task list: "battle starting", then the opponent's board, then "combat set up".
        T("23:00:02.0500000", "TAG_CHANGE Entity=GameEntity tag=2022 value=0"),
        T("23:00:02.1000000", "    FULL_ENTITY - Updating " + Ent(80, "Southsea Busker", "BG26_135", 13) + " CardID=BG26_135"),
        T("23:00:02.1000001", "        tag=CONTROLLER value=13"),
        T("23:00:02.1000002", "        tag=CARDTYPE value=MINION"),
        T("23:00:02.1000003", "        tag=ZONE value=PLAY"),
        T("23:00:02.1000004", "        tag=ATK value=5"),
        T("23:00:02.1000005", "        tag=HEALTH value=4"),
        T("23:00:02.2000000", "TAG_CHANGE Entity=GameEntity tag=TURN value=4"),
        T("23:00:02.2000001", "TAG_CHANGE Entity=GameEntity tag=3533 value=0"),
    };
    var tp = new PowerLogParser("tasklist.log");
    tp.SetBaseDate(new DateTime(2026, 9, 26, 22, 59, 0));
    var combats = new List<CombatSnapshot>();
    tp.CombatStarted += c => combats.Add(c);
    foreach (var l in lines) tp.Feed(l);
    Check(tp.Game != null && tp.Game.GameType == "GT_BATTLEGROUNDS" && tp.Game.LocalPlayerName == "Tester#1234", "task-list mode keeps the game type and names from GameState lines");
    // Shop offers (Bob's side, outside combat) are remembered; the combat board isn't.
    foreach (var l in new[]
    {
        T("23:00:05.0000000", "TAG_CHANGE Entity=GameEntity tag=2022 value=1"),
        T("23:00:05.1000000", "    FULL_ENTITY - Updating " + Ent(90, "Shop Murloc", "BG_MURLOC_1", 13) + " CardID=BG_MURLOC_1"),
        T("23:00:05.1000001", "        tag=CONTROLLER value=13"),
        T("23:00:05.1000002", "        tag=CARDTYPE value=MINION"),
        T("23:00:05.1000003", "        tag=ZONE value=PLAY"),
    }) tp.Feed(l);
    var shop = tp.ShopCardIds();
    Check(shop.Contains("BG_MURLOC_1") && !shop.Contains("BG26_135"), "shop offers tracked for working out the lobby's tribes (combat boards excluded)");
    var sb = combats.Count == 1 ? SeenBoard.FromCombat(combats[0]) : null;
    Check(combats.Count == 1 && sb != null && sb.HeroCardId == "TB_BaconShop_HERO_75" && sb.Minions.Count == 1 && sb.Minions[0].Attack == 5,
        "combat snapshot taken from the task list: opponent's board is there (one combat, 5/4 Busker)");
}

// ---------------------------------------------------------------- tag names vs numbers, duos combats
{
    string T(string t, string s) => $"D {t} PowerTaskList.DebugPrintPower() - {s}";
    string Ent(int id, string card, int player) => $"[entityName=x id={id} zone=PLAY zonePos=0 cardId={card} player={player}]";
    IEnumerable<string> Minion(string t, int id, string card, int player, int atk, int hp) => new[]
    {
        T(t + "0", "    FULL_ENTITY - Updating " + Ent(id, card, player) + " CardID=" + card),
        T(t + "1", "        tag=CONTROLLER value=" + player),
        T(t + "2", "        tag=CARDTYPE value=MINION"),
        T(t + "3", "        tag=ZONE value=PLAY"),
        T(t + "4", "        tag=ATK value=" + atk),
        T(t + "5", "        tag=HEALTH value=" + hp),
    };
    IEnumerable<string> HeroE(string t, int id, string card, int player) => new[]
    {
        T(t + "0", "    FULL_ENTITY - Updating " + Ent(id, card, player) + " CardID=" + card),
        T(t + "1", "        tag=CONTROLLER value=" + player),
        T(t + "2", "        tag=CARDTYPE value=HERO"),
        T(t + "3", "        tag=ZONE value=PLAY"),
        T(t + "4", "        tag=HEALTH value=30"),
    };
    var lines = new List<string>
    {
        P("20:00:00.0000001", "CREATE_GAME"),
        G("20:00:00.0000002", "GameType=GT_BATTLEGROUNDS_DUO"),
        G("20:00:00.0000003", "PlayerID=5, PlayerName=Tester#1234"),
        T("20:00:00.1000001", "CREATE_GAME"),
        T("20:00:00.1000002", "    GameEntity EntityID=1"),
        // Named the way a newer client might log them.
        T("20:00:00.1000003", "        tag=BG_BATTLE_STARTING value=1"),
        T("20:00:00.1000004", "        tag=IGNORE_MODIFIER_HERO_POWER_CHECK value=1"),
        T("20:00:00.1000005", "    Player EntityID=2 PlayerID=5 GameAccountId=[hi=144115198130930503 lo=12345]"),
        T("20:00:00.1000006", "    Player EntityID=3 PlayerID=13 GameAccountId=[hi=0 lo=0]"),
    };
    lines.AddRange(HeroE("20:00:01.000000", 40, "HERO_ME", 5));
    lines.AddRange(HeroE("20:00:01.100000", 41, "HERO_THEM", 13));
    lines.AddRange(Minion("20:00:01.200000", 60, "BG_M1", 5, 3, 3));
    lines.AddRange(Minion("20:00:01.300000", 61, "BG_M2", 13, 4, 4));
    lines.Add(T("20:00:02.0000000", "TAG_CHANGE Entity=GameEntity tag=TURN value=4"));
    lines.Add(T("20:00:02.0000001", "TAG_CHANGE Entity=GameEntity tag=2022 value=0"));
    lines.Add(T("20:00:02.0000002", "TAG_CHANGE Entity=GameEntity tag=3533 value=0"));
    // Teammates step in: fresh hero copies and boards on both sides, then the set-up tag flips again.
    lines.Add(T("20:00:03.0000000", "TAG_CHANGE Entity=GameEntity tag=3533 value=1"));
    lines.AddRange(HeroE("20:00:03.100000", 70, "HERO_MATE", 5));
    lines.AddRange(HeroE("20:00:03.200000", 71, "HERO_THEIR_MATE", 13));
    lines.AddRange(Minion("20:00:03.300000", 80, "BG_M3", 5, 5, 5));
    lines.Add(T("20:00:03.4000000", "TAG_CHANGE Entity=GameEntity tag=IGNORE_MODIFIER_HERO_POWER_CHECK value=0"));
    var dp = new PowerLogParser("duos.log");
    dp.SetBaseDate(new DateTime(2026, 9, 27, 19, 59, 0));
    var snaps = new List<CombatSnapshot>();
    dp.CombatStarted += snaps.Add;
    foreach (var l in lines) dp.Feed(l);
    Check(snaps.Count == 2 && snaps[0].SetupIndex == 0 && snaps[1].SetupIndex == 1 && snaps[0].CombatIndex == snaps[1].CombatIndex,
        $"combat tags read by name or number; duos gives one snapshot per set-up (got {snaps.Count})");
    if (snaps.Count == 2)
    {
        Check(TavernTracker.Core.Combat.BattleInputBuilder.HeroCard(snaps[0], 13) == "HERO_THEM" && TavernTracker.Core.Combat.BattleInputBuilder.HeroCard(snaps[1], 13) == "HERO_THEIR_MATE",
            "teammate set-up shows the teammates' heroes");
        var duo = TavernTracker.Core.Combat.BattleInputBuilder.BuildDuos(snaps[0], snaps[1], snaps[1], new[] { 20, 14 }, 100);
        var root = System.Text.Json.Nodes.JsonNode.Parse(duo.Json!)!;
        Check(!duo.Partial && (string)root["playerBoard"]!["player"]!["cardId"]! == "HERO_ME" && (string)root["playerTeammateBoard"]!["player"]!["cardId"]! == "HERO_MATE"
              && (string)root["opponentTeammateBoard"]!["player"]!["cardId"]! == "HERO_THEIR_MATE",
            "duos simulator input has both pairs");
        var partial = TavernTracker.Core.Combat.BattleInputBuilder.BuildDuos(snaps[0], null, null, new[] { 20 }, 100);
        Check(partial.Json != null && partial.Partial, "duos odds still run without the teammates (marked partial)");
    }
}

// ---------------------------------------------------------------- Firestone card data and pool rules
{
    var json = """
    [
      {"id":"BG_A","dbfId":10,"name":"Alpha Beast","type":"Minion","techLevel":1,"isBaconPool":true,"races":["BEAST"],"mechanics":["BATTLECRY"],"attack":2,"health":2,"set":"Battlegrounds"},
      {"id":"BG_A_G","dbfId":11,"name":"Alpha Beast","type":"Minion","techLevel":1,"isBaconPool":false,"premium":true,"battlegroundsNormalDbfId":10,"races":["BEAST"]},
      {"id":"BG_T","dbfId":12,"name":"Timewarped Guy","type":"Minion","techLevel":3,"isBaconPool":true,"races":["DRAGON"],"mechanics":["BACON_TIMEWARPED"]},
      {"id":"BG_M","dbfId":13,"name":"Clank","type":"Minion","techLevel":2,"isBaconPool":true,"races":["MECH","UNDEAD"]},
      {"id":"BG_D","dbfId":14,"name":"Duo Pal","type":"Minion","techLevel":2,"isBaconPool":true,"races":["BEAST"],"mechanics":["BG_DUO_EXCLUSIVE"]},
      {"id":"BG_N","dbfId":15,"name":"Needs Pirates","type":"Minion","techLevel":4,"isBaconPool":true,"races":[]},
      {"id":"BG_B","dbfId":16,"name":"Buddy","type":"Minion","techLevel":2,"isBaconPool":true,"mechanics":["BACON_BUDDY"]},
      {"id":"BG_S","dbfId":17,"name":"Gold Rush","type":"Battleground_spell","techLevel":1,"isBaconPool":true,"cost":1},
      {"id":"BG_H","dbfId":18,"name":"Hero","type":"Hero","battlegroundsHero":true},
      {"id":"BG_H_SKIN_A","dbfId":19,"name":"Hero","type":"Hero","battlegroundsHero":true,"battlegroundsHeroParentDbfId":18},
      {"id":"BG24_Reward_1","dbfId":20,"name":"Some Reward","type":"Battleground_quest_reward"},
      {"id":"CS2_001","dbfId":1,"name":"Constructed","type":"Minion"}
    ]
    """;
    using var doc = System.Text.Json.JsonDocument.Parse(json);
    var parsed = TavernTracker.Core.Cards.CardDatabase.ParseFirestone(doc.RootElement);
    var byId = parsed.Cards.ToDictionary(c => c.Id);
    Check(!byId.ContainsKey("BG_A_G") && !byId.ContainsKey("CS2_001") && byId.Count == 7, "Firestone data: golden and non-Battlegrounds cards dropped");
    Check(!byId["BG_T"].InPool && !byId["BG_B"].InPool && byId["BG_A"].InPool, "Timewarped and buddy minions are not in the tavern pool");
    Check(byId["BG_M"].Races.SequenceEqual(new[] { "MECHANICAL", "UNDEAD" }), "Firestone's MECH read as MECHANICAL");
    Check(byId["BG_D"].DuosOnly && byId["BG_S"].IsSpell && byId["BG_S"].Cost == 1, "duo-only and spell flags read");
    Check(parsed.HeroParents["BG_H_SKIN_A"] == "BG_H" && parsed.DbfToId[20] == "BG24_Reward_1", "hero skins map to their hero; reward ids by dbf");

    var rules = TavernTracker.Core.Cards.CardDatabase.ParseRules("""{"BG_N":{"bgsMinionTypesRules":{"needTypesInLobby":["PIRATE"]}},"BG_A":{"bgsMinionTypesRules":{"bannedWithTypesInLobby":["MECH"]}},"X":{"other":1}}""");
    Check(rules.Count == 2 && rules["BG_A"].Banned[0] == "MECHANICAL", "card rules parsed");

    using var db = new TavernTracker.Core.Cards.CardDatabase();
    db.SetForTests(parsed.Cards, new Dictionary<int, bool>(), new List<string> { "BEAST", "MECHANICAL", "UNDEAD", "DRAGON", "PIRATE" }, rules, parsed.DbfToId);
    var noPirates = db.Pool(new[] { "BEAST", "UNDEAD", "DRAGON" }, false).Select(c => c.Id).ToList();
    Check(!noPirates.Contains("BG_N") && noPirates.Contains("BG_A") && noPirates.Contains("BG_M"), "tribe rule: pirate-only card hidden without pirates; dual-tribe card shown for either tribe");
    var withMechs = db.Pool(new[] { "BEAST", "MECHANICAL", "PIRATE" }, false).Select(c => c.Id).ToList();
    Check(!withMechs.Contains("BG_A") && withMechs.Contains("BG_N"), "tribe rule: banned-with-mechs card hidden; pirate card shown");
    Check(!db.Pool(new[] { "BEAST" }, false).Any(c => c.Id == "BG_D") && db.Pool(new[] { "BEAST" }, true).Any(c => c.Id == "BG_D"), "duo-only minion only in Duos");
    Check(db.NormalizeHero("BG_H_SKIN_A") == "BG_H" && db.NormalizeHero("TB_BaconShop_HERO_49_SKIN_B") == "TB_BaconShop_HERO_49" && db.IdOf(20) == "BG24_Reward_1", "hero skin normalising and dbf lookup");
}

// ---------------------------------------------------------------- choices (heroes, quests, trinkets)
{
    string C(string t, string s) => L(t, "DebugPrintEntityChoices() - " + s);
    string X(string t, string s) => L(t, "DebugPrintEntitiesChosen() - " + s);
    string E(int id, string card) => $"[entityName=x id={id} zone=HAND zonePos=1 cardId={card} player=5]";
    var choiceLog = new List<string>
    {
        P("22:00:00.0000001", "CREATE_GAME"),
        P("22:00:00.0000002", "    GameEntity EntityID=1"),
        P("22:00:00.0000003", "        tag=BACON_GLOBAL_ANOMALY_DBID value=555"),
        P("22:00:00.0000004", "    Player EntityID=2 PlayerID=5 GameAccountId=[hi=144115198130930503 lo=12345]"),
        G("22:00:00.0000005", "GameType=GT_BATTLEGROUNDS"),
        G("22:00:00.0000006", "PlayerID=5, PlayerName=Tester#1234"),
        P("22:00:00.0000010", "    FULL_ENTITY - Updating " + E(30, "BG20_HERO_242_SKIN_A") + " CardID=BG20_HERO_242_SKIN_A"),
        P("22:00:00.0000011", "        tag=CARDTYPE value=HERO"),
        P("22:00:00.0000012", "    FULL_ENTITY - Updating " + E(31, "TB_BaconShop_HERO_49") + " CardID=TB_BaconShop_HERO_49"),
        P("22:00:00.0000013", "        tag=CARDTYPE value=HERO"),
        C("22:00:01.0000000", "id=1 Player=Tester#1234 TaskList= ChoiceType=MULLIGAN CountMin=1 CountMax=1"),
        C("22:00:01.0000001", "  Source=GameEntity"),
        C("22:00:01.0000002", "  Entities[0]=" + E(30, "BG20_HERO_242_SKIN_A")),
        C("22:00:01.0000003", "  Entities[1]=" + E(31, "TB_BaconShop_HERO_49")),
    };
    var cp = new PowerLogParser("choices.log");
    cp.SetBaseDate(new DateTime(2026, 9, 26, 21, 59, 0));
    foreach (var l in choiceLog) cp.Feed(l);
    var hc = cp.ActiveChoice();
    Check(hc != null && hc.Kind == ChoiceKind.Hero && hc.Options.Count == 2 && hc.Options[0].CardId == "BG20_HERO_242_SKIN_A", "hero choice read from the log");
    Check(cp.Game!.AnomalyDbfId == 555, "anomaly read from the game entity");
    // Hero reroll: the offered entity changes card.
    cp.Feed(P("22:00:02.0000000", "CHANGE_ENTITY - Updating Entity=" + E(31, "TB_BaconShop_HERO_49") + " CardID=BG22_HERO_000"));
    Check(cp.ActiveChoice()!.Options[1].CardId == "BG22_HERO_000", "rerolled hero shows up in the choice");
    cp.Feed(X("22:00:03.0000000", "id=1 Player=Tester#1234 EntitiesCount=1"));
    cp.Feed(X("22:00:03.0000001", "  Entities[0]=" + E(30, "BG20_HERO_242_SKIN_A")));
    Check(cp.ActiveChoice() == null, "choice closes once picked");

    foreach (var l in new[]
    {
        P("22:05:00.0000000", "    FULL_ENTITY - Updating " + E(90, "BG24_QuestsPlayerEnch_t") + " CardID=BG24_QuestsPlayerEnch_t"),
        P("22:05:00.0000001", "    FULL_ENTITY - Updating " + E(91, "BG24_Quest_111") + " CardID=BG24_Quest_111"),
        P("22:05:00.0000002", "        tag=QUEST_REWARD_DATABASE_ID value=20"),
        P("22:05:00.0000003", "    FULL_ENTITY - Updating " + E(92, "BG24_Quest_222") + " CardID=BG24_Quest_222"),
        P("22:05:00.0000004", "        tag=1089 value=21"),
        C("22:05:01.0000000", "id=7 Player=Tester#1234 TaskList=12 ChoiceType=GENERAL CountMin=1 CountMax=1"),
        C("22:05:01.0000001", "  Source=" + E(90, "BG24_QuestsPlayerEnch_t")),
        C("22:05:01.0000002", "  Entities[0]=" + E(91, "BG24_Quest_111")),
        C("22:05:01.0000003", "  Entities[1]=" + E(92, "BG24_Quest_222")),
    }) cp.Feed(l);
    var qc = cp.ActiveChoice();
    Check(qc != null && qc.Kind == ChoiceKind.Quest && qc.Options[0].RewardDbfId == 20 && qc.Options[1].RewardDbfId == 21, "quest choice with rewards (tag by name or number)");

    foreach (var l in new[]
    {
        P("22:09:00.0000000", "    FULL_ENTITY - Updating " + E(95, "BG30_Trinket_1st") + " CardID=BG30_Trinket_1st"),
        P("22:09:00.0000001", "    FULL_ENTITY - Updating " + E(96, "BG30_MagicItem_001") + " CardID=BG30_MagicItem_001"),
        C("22:09:01.0000000", "id=9 Player=Tester#1234 TaskList=20 ChoiceType=GENERAL CountMin=1 CountMax=1"),
        C("22:09:01.0000001", "  Source=" + E(95, "BG30_Trinket_1st")),
        C("22:09:01.0000002", "  Entities[0]=" + E(96, "BG30_MagicItem_001")),
    }) cp.Feed(l);
    Check(cp.ActiveChoice() is { Kind: ChoiceKind.Trinket, Id: 9 }, "a new offer replaces the old one; trinket choice recognised");
    cp.Feed(C("22:09:05.0000000", "id=10 Player=Bob's Tavern TaskList=21 ChoiceType=GENERAL CountMin=1 CountMax=1"));
    Check(cp.ActiveChoice() is { Id: 9 }, "someone else's choice ignored");
}

// ---------------------------------------------------------------- pick stats (Firestone format)
{
    var heroesJson = """
    {"heroStats":[
      {"heroCardId":"BG20_HERO_242","dataPoints":5000,"totalOffered":10000,"totalPicked":3000,"averagePosition":3.9,
       "placementDistribution":[{"rank":1,"totalMatches":1000},{"rank":2,"totalMatches":1000},{"rank":3,"totalMatches":500},{"rank":4,"totalMatches":500},{"rank":5,"totalMatches":500},{"rank":6,"totalMatches":500},{"rank":7,"totalMatches":500},{"rank":8,"totalMatches":500}],
       "tribeStats":[{"tribe":20,"dataPoints":2500,"dataPointsOnMissingTribe":2500,"impactAveragePosition":-0.3},{"tribe":14,"dataPoints":2500,"dataPointsOnMissingTribe":2500,"impactAveragePosition":0.2}]},
      {"heroCardId":"H2","dataPoints":5000,"totalOffered":10000,"totalPicked":1000,"averagePosition":4.6,"tribeStats":[]},
      {"heroCardId":"H3","dataPoints":5000,"totalOffered":10000,"totalPicked":1000,"averagePosition":4.5,"tribeStats":[]},
      {"heroCardId":"H4","dataPoints":5000,"totalOffered":10000,"totalPicked":1000,"averagePosition":4.8,"tribeStats":[]},
      {"heroCardId":"H5","dataPoints":5000,"totalOffered":10000,"totalPicked":1000,"averagePosition":5.4,"tribeStats":[]}
    ]}
    """;
    var heroes = TavernTracker.Core.Stats.PickStatsService.ParseHeroes(heroesJson);
    var h = heroes.Heroes["BG20_HERO_242"];
    Check(Math.Abs(h.Placements[0] - 20) < 0.01 && h.Tribes.Count == 2 && h.Tribes[0].Tribe == "BEAST", "hero stats parsed (placements, tribes)");
    Check(Math.Abs(TavernTracker.Core.Stats.PickStatsService.AdjustedPosition(h, new[] { "BEAST", "PIRATE" }, false) - 3.6) < 1e-9, "hero average adjusted for the lobby's tribes (Firestone's method)");
    var peers = new[] { 3.9, 4.6, 4.5, 4.8, 5.4 };
    Check(TavernTracker.Core.Stats.PickStatsService.TierOf(3.9, peers) == "A" && TavernTracker.Core.Stats.PickStatsService.TierOf(5.4, peers) == "D", "tier letters from mean and spread");

    var questJson = """
    {"questStats":[{"questCardId":"BG24_Quest_111","dataPoints":900,"averageTurnToComplete":10,"completionRate":0.8}],
     "rewardStats":[{"rewardCardId":"BG24_Reward_1","dataPoints":900,"averagePlacement":4.2,
        "tribeStats":[{"tribe":14,"dataPoints":300,"averagePlacement":3.8,"impactPlacement":-0.4},{"tribe":23,"dataPoints":300,"averagePlacement":4.0,"impactPlacement":-0.2},{"tribe":18,"dataPoints":300,"averagePlacement":4.6,"impactPlacement":0.4},{"tribe":20,"dataPoints":300,"averagePlacement":4.1,"impactPlacement":-0.1}]}]}
    """;
    var q = TavernTracker.Core.Stats.PickStatsService.ParseQuests(questJson);
    Check(q.Quests["BG24_Quest_111"].Completion == 80 && q.Rewards["BG24_Reward_1"].Tribes.Count == 4, "quest and reward stats parsed");
    File.WriteAllText(AppPaths.File("stats_quests.json"), questJson);
    File.WriteAllText(AppPaths.File("stats_heroes_solo.json"), heroesJson);
    using var ps = new TavernTracker.Core.Stats.PickStatsService();
    ps.Ensure(ChoiceKind.Quest, false);
    ps.Ensure(ChoiceKind.Hero, false);
    for (int i = 0; i < 50 && !(ps.Ensure(ChoiceKind.Quest, false) && ps.Ensure(ChoiceKind.Hero, false)); i++) Thread.Sleep(50);
    var quest = new BgChoice { Id = 1, Kind = ChoiceKind.Quest, Options = new[] { new ChoiceOption { CardId = "BG24_Quest_111", RewardDbfId = 20 } } };
    var qs = ps.Build(quest, false, new[] { "MURLOC", "BEAST", "DRAGON", "UNDEAD", "NAGA" }, x => x, d => d == 20 ? "BG24_Reward_1" : null)!;
    var o = qs.Options[0]!;
    Check(o.AvgPlacement == 4.2 && o.Third == 80 && o.Note!.Contains("turn 6"), "quest overlay: reward avg place, completion %, turn");
    Check(o.Tribes.Count == 3 && o.Tribes[0].Label == "Murlocs" && o.Tribes[0].InLobby && !o.Tribes[1].InLobby, "quest overlay: best 3 tribes, pirates marked as not in the lobby");
    var hero = new BgChoice { Id = 2, Kind = ChoiceKind.Hero, Options = new[] { new ChoiceOption { CardId = "BG20_HERO_242_SKIN_A" }, new ChoiceOption { CardId = "UNKNOWN" } } };
    var hs = ps.Build(hero, false, Array.Empty<string>(), id => id.Replace("_SKIN_A", ""), _ => null)!;
    Check(hs.Options[0]!.Third == 30 && hs.Options[0]!.Tier != null && hs.Options[1] == null, "hero overlay: pick rate 30%, tier, unknown hero left blank");
}

// ---------------------------------------------------------------- settings
{
    var st = AppSettings.Load();
    st.Region = "US"; st.SnapshotMinutes = 1; st.Save();
    var back = AppSettings.Load();
    Check(back.Region == "US" && back.SnapshotMinutes == 5, "settings round-trip, interval clamped to 5 min");
}

try { Directory.Delete(dir, true); } catch { }
Console.WriteLine(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
return fails == 0 ? 0 : 1;
