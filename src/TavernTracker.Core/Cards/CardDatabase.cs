using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TavernTracker.Core.Cards;

/// <summary>A Battlegrounds tavern minion or spell.</summary>
public sealed class BgCard
{
    public string Id { get; set; } = "";
    public int DbfId { get; set; }
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
    public int Tier { get; set; }
    public int Attack { get; set; }
    public int Health { get; set; }
    public List<string> Races { get; set; } = new();
    public List<string> Mechanics { get; set; } = new();
    public bool IsSpell { get; set; }
    public bool DuosOnly { get; set; }
    /// <summary>Solo-only cards (not offered in Duos).</summary>
    public bool SoloOnly { get; set; }
    /// <summary>In the tavern pool according to the game data (before live corrections).</summary>
    public bool InPool { get; set; }
    /// <summary>Gold cost (tavern spells).</summary>
    public int Cost { get; set; }
    /// <summary>Only offered when one of these tribes is in the lobby (Firestone's card rules).</summary>
    public List<string> NeedTribes { get; set; } = new();
    /// <summary>Never offered when one of these tribes is in the lobby.</summary>
    public List<string> BannedWithTribes { get; set; } = new();

    /// <summary>Card art strip (the same tiles deck trackers use).</summary>
    public string TileUrl => $"https://art.hearthstonejson.com/v1/tiles/{Id}.png";

    /// <summary>Plain text without the game's markup.</summary>
    public string PlainText => Regex.Replace(Text.Replace("\n", " ").Replace("[x]", ""), "<.*?>|\\$|#|\\{\\d\\}", "").Trim();
}

/// <summary>Keyword filters shown in the minion browser.</summary>
public static class Keywords
{
    public static readonly (string Label, string[] Mechanics, string[] TextHints)[] All =
    {
        ("Battlecry", new[] { "BATTLECRY" }, new[] { "<b>battlecry" }),
        ("Deathrattle", new[] { "DEATHRATTLE" }, new[] { "<b>deathrattle" }),
        ("Avenge", new[] { "AVENGE" }, new[] { "<b>avenge" }),
        ("Rally", new[] { "RALLY" }, new[] { "<b>rally" }),
        ("Divine Shield", new[] { "DIVINE_SHIELD" }, new[] { "<b>divine shield" }),
        ("Taunt", new[] { "TAUNT" }, new[] { "<b>taunt" }),
        ("End of Turn", new[] { "END_OF_TURN_TRIGGER" }, new[] { "end of your turn" }),
        ("Start of Combat", new[] { "START_OF_COMBAT" }, new[] { "<b>start of combat" }),
        ("Reborn", new[] { "REBORN" }, new[] { "<b>reborn" }),
        ("Choose One", new[] { "CHOOSE_ONE" }, new[] { "<b>choose one" }),
        ("Magnetic", new[] { "MODULAR", "MAGNETIC" }, new[] { "<b>magnetic" }),
        ("Venomous", new[] { "VENOMOUS" }, new[] { "<b>venomous" }),
        ("Windfury", new[] { "WINDFURY" }, new[] { "<b>windfury" }),
        ("Spellcraft", new[] { "SPELLCRAFT" }, new[] { "<b>spellcraft" }),
    };

    public static bool Has(BgCard c, string label)
    {
        foreach (var (l, mech, hints) in All)
        {
            if (l != label) continue;
            if (c.Mechanics.Any(m => mech.Contains(m))) return true;
            var text = c.Text.ToLowerInvariant();
            return hints.Any(h => text.Contains(h, StringComparison.Ordinal));
        }
        return false;
    }
}

/// <summary>
/// Battlegrounds card data and the tavern pool for this patch.
///
/// Sources, best first:
///  1. Firestone's card file (static.zerotoheroes.com), rebuilt by Firestone on every patch. Its
///     isBaconPool flag plus the Timewarped / Darkmoon / buddy / solo / duo markers decide the pool,
///     the same way Firestone's own minion list does. Firestone's card rules add "only with tribe X in
///     the lobby" and "never with tribe Y" restrictions.
///  2. HearthstoneJSON (the game's card data) when Firestone's file can't be downloaded.
/// On top of either, HSReplay's live Battlegrounds meta period (the source HDT uses) corrects which
/// cards are really in the pool and which tribes are in rotation.
/// </summary>
public sealed class CardDatabase : IDisposable
{
    private const string HsJsonUrl = "https://api.hearthstonejson.com/v1/latest/enUS/cards.json";
    private const string RulesUrl = "https://static.firestoneapp.com/data/cards/card-rules.gz.json";
    private const string MetaUrl = "https://hsreplay.net/api/v1/battlegrounds/meta_periods/live/";
    private const int PoolMinionTag = 1456; // IS_BACON_POOL_MINION
    private const int PoolSpellTag = 3081;  // IS_BACON_POOL_SPELL

    private readonly HttpClient _http;
    private readonly object _gate = new();
    private List<BgCard> _cards = new();
    private Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private Dictionary<int, string> _dbfToId = new();
    private Dictionary<string, string> _heroParents = new(StringComparer.Ordinal);
    private Dictionary<int, bool> _poolOverrides = new();
    private HashSet<string> _rotationRaces = new();
    private Dictionary<string, CardRule> _rules = new(StringComparer.Ordinal);
    private Task? _loading;

    public DateTime UpdatedUtc { get; private set; }
    /// <summary>Where the card data came from ("Firestone" or "HearthstoneJSON").</summary>
    public string Source { get; private set; } = "";
    public bool Ready { get { lock (_gate) return _cards.Count > 0; } }
    public int CorrectionCount { get { lock (_gate) return _poolOverrides.Count; } }
    public int RuleCount { get { lock (_gate) return _rules.Count; } }
    public event Action? Loaded;

    private static string CachePath => AppPaths.File("bgcards3.json");

    public CardDatabase()
    {
        _http = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromMinutes(3),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) TavernTracker/1.1");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public void Dispose() => _http.Dispose();

    /// <summary>One line for the settings page.</summary>
    public string Status
    {
        get
        {
            lock (_gate)
            {
                if (_cards.Count == 0) return "Loading…";
                int pool = _cards.Count(c => c.InPool && !c.IsSpell);
                return $"{Source}: {pool} minions in the pool · HSReplay corrections: {(_poolOverrides.Count > 0 ? _poolOverrides.Count.ToString() : "not loaded")} · tribe rules: {_rules.Count}";
            }
        }
    }

    /// <summary>Loads the cache, then refreshes in the background when it's a day old.</summary>
    public Task LoadAsync() => _loading ??= Task.Run(async () =>
    {
        await LoadMetaAsync().ConfigureAwait(false);
        await LoadRulesAsync().ConfigureAwait(false);
        var cached = JsonFile.Load<CacheFile>(CachePath);
        if (cached?.Cards is { Count: > 0 })
        {
            Set(cached.Cards, cached.Names ?? new(), cached.DbfToId ?? new(), cached.HeroParents ?? new(),
                new DateTime(cached.UpdatedUtcTicks, DateTimeKind.Utc), cached.Source ?? "");
            if (DateTime.UtcNow - UpdatedUtc < TimeSpan.FromDays(1) && cached.Source == "Firestone") return;
        }
        if (await TryFirestoneAsync().ConfigureAwait(false)) return;
        try
        {
            await DownloadHsJsonAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("Card data download failed", ex);
        }
    });

    public IReadOnlyList<BgCard> All()
    {
        lock (_gate) return _cards;
    }

    /// <summary>Card name for any Battlegrounds card id (heroes too), or null.</summary>
    public string? NameOf(string cardId)
    {
        lock (_gate) return _names.TryGetValue(cardId, out var n) ? n : null;
    }

    /// <summary>Card id for a Battlegrounds card's database id, or null.</summary>
    public string? IdOf(int dbfId)
    {
        lock (_gate) return _dbfToId.TryGetValue(dbfId, out var id) ? id : null;
    }

    /// <summary>A hero skin's base hero ("BG20_HERO_242_SKIN_A" → "BG20_HERO_242"). Stats are kept per base hero.</summary>
    public string NormalizeHero(string cardId)
    {
        if (string.IsNullOrEmpty(cardId)) return cardId;
        lock (_gate)
        {
            if (_heroParents.TryGetValue(cardId, out var parent)) return parent;
        }
        var i = cardId.IndexOf("_SKIN_", StringComparison.Ordinal);
        return i > 0 ? cardId[..i] : cardId;
    }

    /// <summary>
    /// Cards you can find in this lobby: its tribes (plus neutrals), solo or duos. Before the lobby's tribes
    /// are known, the tribes in rotation this season.
    /// </summary>
    public IReadOnlyList<BgCard> Pool(IReadOnlyCollection<string>? races, bool duos, bool includeSpells = true)
    {
        var all = All();
        Dictionary<int, bool> overrides;
        bool lobbyKnown = races is { Count: > 0 };
        lock (_gate)
        {
            overrides = _poolOverrides;
            if (!lobbyKnown) races = _rotationRaces.Count > 0 ? _rotationRaces.ToHashSet() : null;
        }
        return all
            .Where(c => overrides.TryGetValue(c.DbfId, out var inPool) ? inPool : c.InPool)
            .Where(c => duos ? !c.SoloOnly : !c.DuosOnly)
            .Where(c => includeSpells || !c.IsSpell)
            .Where(c => races == null || races.Count == 0 || c.IsSpell || c.Races.Count == 0
                        || c.Races.Contains("ALL") || c.Races.Any(races.Contains))
            .Where(c => !lobbyKnown || RulesAllow(c, races!))
            .OrderBy(c => c.Tier).ThenBy(c => c.Name)
            .ToList();
    }

    private static bool RulesAllow(BgCard c, IReadOnlyCollection<string> lobby)
    {
        if (c.BannedWithTribes.Any(lobby.Contains)) return false;
        return c.NeedTribes.Count == 0 || c.NeedTribes.Any(lobby.Contains);
    }

    // ------------------------------------------------------------------ HSReplay live corrections

    private async Task LoadMetaAsync()
    {
        var path = AppPaths.File("bgmeta.json");
        var cached = JsonFile.Load<MetaFile>(path);
        if (cached?.Overrides != null) SetOverrides(cached.Overrides);
        if (cached?.Rotation != null) SetRotation(cached.Rotation);
        if (cached is { Overrides.Count: > 0 } && DateTime.UtcNow - new DateTime(cached.UpdatedUtcTicks, DateTimeKind.Utc) < TimeSpan.FromHours(6)) return;
        try
        {
            using var response = await _http.GetAsync(MetaUrl).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warn($"HSReplay's live Battlegrounds pool isn't available right now (HTTP {(int)response.StatusCode}); using the card data as-is.");
                return;
            }
            var meta = await response.Content.ReadFromJsonAsync<LiveMeta>().ConfigureAwait(false);
            var overrides = (meta?.TagOverrides ?? new())
                .Where(o => o.Tag == PoolMinionTag || o.Tag == PoolSpellTag)
                .GroupBy(o => o.DbfId)
                .ToDictionary(g => g.Key, g => g.Last().Value == 1);
            SetOverrides(overrides);
            var rotation = (meta?.MinionTypes ?? new()).Select(Hearthstone.Races.Key).ToList();
            SetRotation(rotation);
            JsonFile.Save(path, new MetaFile { UpdatedUtcTicks = DateTime.UtcNow.Ticks, Overrides = overrides, Rotation = rotation });
            Log.Info($"HSReplay pool corrections: {overrides.Count}; tribes in rotation: {string.Join(", ", rotation)}");
            Loaded?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't load the live Battlegrounds pool from HSReplay ({ex.Message}); using the card data as-is.");
        }
    }

    private void SetRotation(List<string> races)
    {
        lock (_gate) _rotationRaces = races.ToHashSet();
    }

    private void SetOverrides(Dictionary<int, bool> overrides)
    {
        lock (_gate) _poolOverrides = new Dictionary<int, bool>(overrides);
    }

    // ------------------------------------------------------------------ Firestone card rules

    private async Task LoadRulesAsync()
    {
        var path = AppPaths.File("bgrules.json");
        var cached = JsonFile.Load<RulesFile>(path);
        if (cached?.Rules != null) SetRules(cached.Rules);
        if (cached is { Rules.Count: > 0 } && DateTime.UtcNow - new DateTime(cached.UpdatedUtcTicks, DateTimeKind.Utc) < TimeSpan.FromDays(1)) return;
        try
        {
            var bytes = await _http.GetByteArrayAsync(RulesUrl).ConfigureAwait(false);
            var rules = ParseRules(Combat.SimCardData.Decode(bytes));
            SetRules(rules);
            JsonFile.Save(path, new RulesFile { UpdatedUtcTicks = DateTime.UtcNow.Ticks, Rules = rules });
            Log.Info($"Firestone card rules: {rules.Count} tribe restrictions");
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't load Firestone's card rules ({ex.Message})");
        }
    }

    public sealed class CardRule
    {
        public List<string> Need { get; set; } = new();
        public List<string> Banned { get; set; } = new();
    }

    /// <summary>Reads Firestone's card-rules file: { cardId: { bgsMinionTypesRules: { needTypesInLobby, bannedWithTypesInLobby } } }.</summary>
    public static Dictionary<string, CardRule> ParseRules(string json)
    {
        var result = new Dictionary<string, CardRule>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
        foreach (var card in doc.RootElement.EnumerateObject())
        {
            if (card.Value.ValueKind != JsonValueKind.Object || !card.Value.TryGetProperty("bgsMinionTypesRules", out var r) || r.ValueKind != JsonValueKind.Object) continue;
            var rule = new CardRule
            {
                Need = Strings(r, "needTypesInLobby").Select(NormalizeRace).ToList(),
                Banned = Strings(r, "bannedWithTypesInLobby").Select(NormalizeRace).ToList(),
            };
            if (rule.Need.Count > 0 || rule.Banned.Count > 0) result[card.Name] = rule;
        }
        return result;
    }

    private void SetRules(Dictionary<string, CardRule> rules)
    {
        lock (_gate)
        {
            _rules = new Dictionary<string, CardRule>(rules, StringComparer.Ordinal);
            ApplyRules(_cards, _rules);
        }
    }

    private static void ApplyRules(List<BgCard> cards, Dictionary<string, CardRule> rules)
    {
        foreach (var c in cards)
        {
            var need = new List<string>(SpecialTribes.TryGetValue(c.Id, out var t) ? new[] { t } : Array.Empty<string>());
            var banned = new List<string>();
            if (rules.TryGetValue(c.Id, out var rule))
            {
                need.AddRange(rule.Need);
                banned.AddRange(rule.Banned);
            }
            c.NeedTribes = need.Distinct().ToList();
            c.BannedWithTribes = banned.Distinct().ToList();
        }
    }

    /// <summary>Old cards that only appear with a tribe they reference (Firestone's getTribesForInclusion).</summary>
    private static readonly Dictionary<string, string> SpecialTribes = new(StringComparer.Ordinal)
    {
        ["BG21_002"] = "BEAST", ["BGS_017"] = "BEAST", ["CFM_816"] = "BEAST", ["BG_DS1_070"] = "BEAST",
        ["BG21_007"] = "DEMON", ["BGS_002"] = "DEMON", ["BGS_004"] = "DEMON", ["BG27_081"] = "DEMON",
        ["BG21_011"] = "MURLOC",
        ["BGS_040"] = "DRAGON", ["BG21_013"] = "DRAGON",
        ["BGS_105"] = "ELEMENTAL", ["BG21_036"] = "ELEMENTAL", ["BGS_104"] = "ELEMENTAL",
        ["BGS_012"] = "MECHANICAL",
        ["BG21_018"] = "PIRATE", ["BG31_827"] = "PIRATE",
        ["BG20_205"] = "QUILBOAR", ["BG20_203"] = "QUILBOAR",
        ["BG28_303"] = "UNDEAD",
    };

    /// <summary>Firestone writes "MECH" where the game data says "MECHANICAL".</summary>
    public static string NormalizeRace(string race)
    {
        var r = race.Trim().ToUpperInvariant();
        return r switch { "MECH" => "MECHANICAL", "QUILLBOAR" => "QUILBOAR", _ => r };
    }

    private static IEnumerable<string> Strings(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var a) || a.ValueKind != JsonValueKind.Array) yield break;
        foreach (var x in a.EnumerateArray())
        {
            if (x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 } s) yield return s;
            else if (x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out var n)) yield return Hearthstone.Races.Key(n);
        }
    }

    // ------------------------------------------------------------------ Firestone card file

    private async Task<bool> TryFirestoneAsync()
    {
        try
        {
            var path = await Combat.SimCardData.EnsureFileAsync(_http).ConfigureAwait(false);
            if (path == null) return false;
            Parsed parsed;
            using (var stream = File.OpenRead(path))
            using (var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false))
                parsed = ParseFirestone(doc.RootElement);
            if (parsed.Cards.Count(c => c.InPool) < 50)
            {
                Log.Warn($"Firestone card data looked incomplete ({parsed.Cards.Count} cards); trying HearthstoneJSON");
                return false;
            }
            Save(parsed, "Firestone");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't read Firestone's card data ({ex.Message}); trying HearthstoneJSON");
            return false;
        }
    }

    public sealed class Parsed
    {
        public List<BgCard> Cards { get; } = new();
        public Dictionary<string, string> Names { get; } = new(StringComparer.Ordinal);
        public Dictionary<int, string> DbfToId { get; } = new();
        public Dictionary<string, string> HeroParents { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>Reads Firestone's reference card list (an array of cards). Public for tests.</summary>
    public static Parsed ParseFirestone(JsonElement root)
    {
        var p = new Parsed();
        var parentDbf = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in root.EnumerateArray())
        {
            var id = Str(e, "id");
            if (id.Length == 0) continue;
            int dbf = Int(e, "dbfId");
            bool hero = Bool(e, "battlegroundsHero");
            int tech = Int(e, "techLevel");
            var type = Str(e, "type").ToUpperInvariant();
            bool bgish = hero || tech > 0 || id.StartsWith("BG", StringComparison.Ordinal) || id.Contains("Bacon", StringComparison.Ordinal);
            if (!bgish) continue;
            var name = Str(e, "name");
            if (dbf > 0) p.DbfToId[dbf] = id;
            if (name.Length > 0) p.Names[id] = name;
            if (hero && Int(e, "battlegroundsHeroParentDbfId") is > 0 and var parent) parentDbf[id] = parent;

            if (tech <= 0 || Bool(e, "premium") || Int(e, "battlegroundsNormalDbfId") > 0) continue;
            bool minion = type == "MINION";
            bool spell = type == "BATTLEGROUND_SPELL";
            if (!minion && !spell) continue;

            var mechanics = StrArray(e, "mechanics");
            bool inPool = Bool(e, "isBaconPool")
                          && !string.Equals(Str(e, "set"), "Vanilla", StringComparison.OrdinalIgnoreCase)
                          && !Str(e, "spellSchool").Contains("UPGRADE", StringComparison.OrdinalIgnoreCase)
                          && !mechanics.Contains("BACON_BUDDY")
                          && !mechanics.Contains("BACON_TIMEWARPED")
                          && !mechanics.Contains("IS_DARKMOON_PRIZE");
            var races = StrArray(e, "races").Select(NormalizeRace).Where(r => r is not ("INVALID" or "BLANK")).Distinct().ToList();
            if (races.Count == 0 && Str(e, "race") is { Length: > 0 } single && NormalizeRace(single) is not ("INVALID" or "BLANK")) races.Add(NormalizeRace(single));

            p.Cards.Add(new BgCard
            {
                Id = id,
                DbfId = dbf,
                Name = name.Length > 0 ? name : id,
                Text = Str(e, "text"),
                Tier = tech,
                Attack = Int(e, "attack"),
                Health = Int(e, "health"),
                Cost = Int(e, "cost"),
                Races = races,
                Mechanics = mechanics,
                IsSpell = spell,
                DuosOnly = mechanics.Contains("BG_DUO_EXCLUSIVE") || mechanics.Contains("IS_BACON_DUOS_EXCLUSIVE"),
                SoloOnly = mechanics.Contains("BG_SOLO_EXCLUSIVE"),
                InPool = inPool,
            });
        }
        foreach (var (child, dbf) in parentDbf)
            if (p.DbfToId.TryGetValue(dbf, out var parentId)) p.HeroParents[child] = parentId;
        return p;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static List<string> StrArray(JsonElement e, string name)
    {
        var list = new List<string>();
        if (!e.TryGetProperty(name, out var a) || a.ValueKind != JsonValueKind.Array) return list;
        foreach (var x in a.EnumerateArray())
            if (x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 } s) list.Add(s);
        return list;
    }

    // ------------------------------------------------------------------ HearthstoneJSON fallback

    private async Task DownloadHsJsonAsync()
    {
        using var stream = await _http.GetStreamAsync(HsJsonUrl).ConfigureAwait(false);
        var raw = await JsonSerializer.DeserializeAsync<List<RawCard>>(stream).ConfigureAwait(false) ?? new();
        var parsed = FilterParsed(raw);
        if (parsed.Cards.Count == 0) throw new InvalidOperationException("No Battlegrounds cards in the download");
        Save(parsed, "HearthstoneJSON");
    }

    /// <summary>Keeps only tavern minions/spells (and names of heroes). Public for tests.</summary>
    public static (List<BgCard> Cards, Dictionary<string, string> Names) Filter(IEnumerable<RawCard> raw)
    {
        var p = FilterParsed(raw);
        return (p.Cards, p.Names);
    }

    private static Parsed FilterParsed(IEnumerable<RawCard> raw)
    {
        var p = new Parsed();
        foreach (var r in raw)
        {
            if (string.IsNullOrEmpty(r.Id)) continue;
            if (r.BattlegroundsHero == true && !string.IsNullOrEmpty(r.Name)) p.Names[r.Id] = r.Name!;
            if (r.DbfId > 0 && (r.BattlegroundsHero == true || (r.TechLevel ?? 0) > 0 || r.Id!.StartsWith("BG", StringComparison.Ordinal) || r.Id.Contains("Bacon", StringComparison.Ordinal)))
            {
                p.DbfToId[r.DbfId] = r.Id!;
                if (!string.IsNullOrEmpty(r.Name)) p.Names[r.Id!] = r.Name!;
            }
            if ((r.TechLevel ?? 0) <= 0) continue;
            if (r.BattlegroundsNormalDbfId is > 0) continue; // golden copy
            bool minion = r.Type == "MINION";
            bool spell = r.IsBattlegroundsPoolSpell == true || r.Type == "BATTLEGROUND_SPELL";
            if (!minion && !spell) continue;
            var mechanics = r.Mechanics?.ToList() ?? new List<string>();
            bool inPool = (minion ? r.IsBattlegroundsPoolMinion == true : r.IsBattlegroundsPoolSpell == true)
                          && !mechanics.Contains("BACON_TIMEWARPED") && !mechanics.Contains("BACON_BUDDY") && !mechanics.Contains("IS_DARKMOON_PRIZE");
            var races = (r.Races?.ToList() ?? (r.Race != null && r.Race != "INVALID" ? new List<string> { r.Race } : new List<string>()))
                .Select(NormalizeRace).Distinct().ToList();
            p.Cards.Add(new BgCard
            {
                Id = r.Id!,
                DbfId = r.DbfId,
                Name = r.Name ?? r.Id!,
                Text = r.Text ?? "",
                Tier = r.TechLevel ?? 0,
                Attack = r.Attack ?? 0,
                Health = r.Health ?? 0,
                Cost = r.Cost ?? 0,
                Races = races,
                Mechanics = mechanics,
                IsSpell = spell && !minion,
                DuosOnly = r.IsBattlegroundsDuosExclusive == true || mechanics.Contains("BG_DUO_EXCLUSIVE"),
                SoloOnly = mechanics.Contains("BG_SOLO_EXCLUSIVE"),
                InPool = inPool,
            });
            p.Names[r.Id!] = r.Name ?? r.Id!;
        }
        return p;
    }

    // ------------------------------------------------------------------ state

    private void Save(Parsed p, string source)
    {
        var now = DateTime.UtcNow;
        Set(p.Cards, p.Names, p.DbfToId, p.HeroParents, now, source);
        JsonFile.Save(CachePath, new CacheFile
        {
            UpdatedUtcTicks = now.Ticks,
            Source = source,
            Cards = p.Cards,
            Names = p.Names,
            DbfToId = p.DbfToId,
            HeroParents = p.HeroParents,
        });
        Log.Info($"Card data updated from {source}: {p.Cards.Count} tavern cards, {p.Cards.Count(c => c.InPool)} in the pool");
    }

    private void Set(List<BgCard> cards, Dictionary<string, string> names, Dictionary<int, string> dbf,
        Dictionary<string, string> parents, DateTime updated, string source)
    {
        lock (_gate)
        {
            ApplyRules(cards, _rules);
            _cards = cards;
            _names = new Dictionary<string, string>(names, StringComparer.Ordinal);
            _dbfToId = new Dictionary<int, string>(dbf);
            _heroParents = new Dictionary<string, string>(parents, StringComparer.Ordinal);
            UpdatedUtc = updated;
            Source = source;
        }
        Loaded?.Invoke();
    }

    /// <summary>For tests.</summary>
    public void SetForTests(List<BgCard> cards, Dictionary<int, bool> overrides, List<string>? rotation = null,
        Dictionary<string, CardRule>? rules = null, Dictionary<int, string>? dbfToId = null)
    {
        SetOverrides(overrides);
        SetRotation(rotation ?? new List<string>());
        lock (_gate) _rules = rules ?? new(StringComparer.Ordinal);
        Set(cards, cards.ToDictionary(c => c.Id, c => c.Name), dbfToId ?? new(), new(), DateTime.UtcNow, "Test");
    }

    private sealed class CacheFile
    {
        public long UpdatedUtcTicks { get; set; }
        public string? Source { get; set; }
        public List<BgCard>? Cards { get; set; }
        public Dictionary<string, string>? Names { get; set; }
        public Dictionary<int, string>? DbfToId { get; set; }
        public Dictionary<string, string>? HeroParents { get; set; }
    }

    private sealed class MetaFile
    {
        public long UpdatedUtcTicks { get; set; }
        public Dictionary<int, bool>? Overrides { get; set; }
        public List<string>? Rotation { get; set; }
    }

    private sealed class RulesFile
    {
        public long UpdatedUtcTicks { get; set; }
        public Dictionary<string, CardRule>? Rules { get; set; }
    }

    private sealed class LiveMeta
    {
        [JsonPropertyName("tag_overrides")] public List<TagOverride>? TagOverrides { get; set; }
        [JsonPropertyName("minion_types")] public List<int>? MinionTypes { get; set; }
    }

    private sealed class TagOverride
    {
        [JsonPropertyName("dbf_id")] public int DbfId { get; set; }
        [JsonPropertyName("tag")] public int Tag { get; set; }
        [JsonPropertyName("value")] public int Value { get; set; }
    }

    /// <summary>Field names from HearthSim's hearthstonejson-client type definitions.</summary>
    public sealed class RawCard
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("dbfId")] public int DbfId { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("text")] public string? Text { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("cost")] public int? Cost { get; set; }
        [JsonPropertyName("attack")] public int? Attack { get; set; }
        [JsonPropertyName("health")] public int? Health { get; set; }
        [JsonPropertyName("race")] public string? Race { get; set; }
        [JsonPropertyName("races")] public string[]? Races { get; set; }
        [JsonPropertyName("mechanics")] public string[]? Mechanics { get; set; }
        [JsonPropertyName("techLevel")] public int? TechLevel { get; set; }
        [JsonPropertyName("isBattlegroundsPoolMinion")] public bool? IsBattlegroundsPoolMinion { get; set; }
        [JsonPropertyName("isBattlegroundsPoolSpell")] public bool? IsBattlegroundsPoolSpell { get; set; }
        [JsonPropertyName("isBattlegroundsDuosExclusive")] public bool? IsBattlegroundsDuosExclusive { get; set; }
        [JsonPropertyName("battlegroundsHero")] public bool? BattlegroundsHero { get; set; }
        [JsonPropertyName("battlegroundsNormalDbfId")] public int? BattlegroundsNormalDbfId { get; set; }
    }
}
