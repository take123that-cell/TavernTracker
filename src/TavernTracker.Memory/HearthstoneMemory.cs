// Battlegrounds memory reads. The memory paths are adapted from Firestone's open-source
// BattlegroundsInfoReader (MIT): https://github.com/Zero-to-Heroes/unity-spy-.net4.5
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HackF5.UnitySpy;
using TavernTracker.Core;
using TavernTracker.Core.Hearthstone;

namespace TavernTracker.Memory
{
    /// <summary>
    /// Reads your Battlegrounds rating, the lobby's tribes and the players' names from Hearthstone.
    /// Read-only: it never writes to the game. Every failure is caught and reported as "not available".
    /// Call from a single background thread.
    /// </summary>
    public sealed class HearthstoneMemory : IGameMemory
    {
        private const int PlayerIdTag = 30; // GameTag.PLAYER_ID

        private HearthstoneImage _image;
        private int _processId;
        private DateTime _retryAfter = DateTime.MinValue;
        private int _failures;

        public string Status { get; private set; } = "Not connected";

        public MemorySnapshot Read(bool includeLobby)
        {
            if (DateTime.UtcNow < _retryAfter) return null;
            if (!OperatingSystem.IsWindows())
            {
                Status = "Memory reading only works on Windows";
                return null;
            }

            try
            {
                if (!Attach()) return null;

                var rating = ReadRatings();
                var snapshot = new MemorySnapshot
                {
                    Rating = rating.Solo,
                    DuosRating = rating.Duos,
                    NewRating = ReadNewRating(),
                    Scene = ReadScene(),
                    // Each part on its own: one failing read (after a game patch, say) mustn't hide the others.
                    AvailableRaces = includeLobby ? Safe("tribes", ReadRaces, Array.Empty<int>()) : Array.Empty<int>(),
                    Players = includeLobby ? Safe("players", ReadPlayers, Array.Empty<MemoryPlayer>()) : Array.Empty<MemoryPlayer>(),
                };
                _failures = 0;
                Status = "Connected";
                return snapshot;
            }
            catch (Exception ex)
            {
                // The game may be loading, restarting, or updated in a way the reader doesn't know yet.
                _failures++;
                Status = "Couldn't read the game: " + ex.Message;
                if (_failures == 1 || _failures % 20 == 0) Log.Warn($"Memory read failed ({_failures}x): {ex.GetType().Name}: {ex.Message}");
                if (_failures >= 3)
                {
                    Detach();
                    _retryAfter = DateTime.UtcNow.AddSeconds(Math.Min(60, 5 * _failures));
                }
                return null;
            }
        }

        public void Dispose() => Detach();

        private readonly HashSet<string> _reported = new HashSet<string>();

        private T Safe<T>(string what, Func<T> read, T fallback)
        {
            try
            {
                return read();
            }
            catch (Exception ex)
            {
                if (_reported.Add(what)) Log.Warn($"Memory: couldn't read the lobby's {what} ({ex.GetType().Name}: {ex.Message})");
                return fallback;
            }
        }

        /// <summary>
        /// PlayerLeaderboardManager.m_currentlyMousedOverTile, the same read Firestone uses (and HDT does via
        /// HearthMirror) to show an opponent's last board when you hover their portrait.
        /// </summary>
        public string HoveredLeaderboardHero()
        {
            if (_image == null) return null;
            try
            {
                var tile = _image["PlayerLeaderboardManager"]?["s_instance"]?["m_currentlyMousedOverTile"];
                if (tile == null) return "";
                dynamic entity = null;
                try { entity = tile["m_playerHeroEntity"]; } catch { /* older layout */ }
                if (entity == null) entity = tile["m_entity"];
                if (entity == null) return "";
                return (string)entity["m_cardIdInternal"] ?? "";
            }
            catch
            {
                return null;
            }
        }

        // ------------------------------------------------------------------ connection

        private bool Attach()
        {
            Process process = null;
            try
            {
                process = Process.GetProcessesByName("Hearthstone").FirstOrDefault();
                if (process == null)
                {
                    Detach();
                    Status = "Hearthstone isn't running";
                    return false;
                }
                if (_image != null && process.Id == _processId) return true;

                Detach();
                var image = AssemblyImageFactory.Create(process.Id, msg => { });
                _image = new HearthstoneImage(image);
                _processId = process.Id;
                Log.Info($"Memory reader attached to Hearthstone (pid {process.Id})");
                return true;
            }
            finally
            {
                process?.Dispose();
            }
        }

        private void Detach()
        {
            try { _image?.Dispose(); } catch { /* already gone */ }
            _image = null;
            _processId = 0;
        }

        // ------------------------------------------------------------------ reads

        private (int Solo, int Duos) ReadRatings()
        {
            var info = _image.GetNetCacheService("NetCacheBaconRatingInfo");
            if (info == null) return (-1, -1);
            int solo = info["<Rating>k__BackingField"] ?? -1;
            int duos = info["<DuosRating>k__BackingField"] ?? -1;
            return (solo, duos);
        }

        private int ReadNewRating()
        {
            try
            {
                return _image["GameState"]?["s_instance"]?["m_gameEntity"]?["<RatingChangeData>k__BackingField"]?["_NewRating"] ?? -1;
            }
            catch
            {
                return -1; // not in a Battlegrounds game
            }
        }

        private int ReadScene()
        {
            try
            {
                // Can briefly fail right after a screen change; that's fine.
                var mode = _image["SceneMgr"]?["s_instance"]?["m_mode"];
                return mode == null ? -1 : (int)mode;
            }
            catch
            {
                return -1;
            }
        }

        private IReadOnlyList<int> ReadRaces()
        {
            var result = new List<int>();
            var races = _image["GameState"]?["s_instance"]?["m_availableRacesInBattlegroundsExcludingAmalgam"];
            if (races == null) return result;
            int count = races["_size"] ?? 0;
            var items = races["_items"];
            for (int i = 0; i < count; i++)
            {
                object v = items[i];
                if (v == null) continue;
                result.Add(Convert.ToInt32(v));
            }
            return result;
        }

        private IReadOnlyList<MemoryPlayer> ReadPlayers()
        {
            var result = new List<MemoryPlayer>();
            var manager = _image["PlayerLeaderboardManager"]?["s_instance"];
            if (manager == null) return result;

            var tiles = PlayerTiles(manager);
            if (tiles.Count == 0) return result;

            var names = PlayerInfoNames();
            var lastNames = LastDisplayedNames();

            foreach (var tile in tiles)
            {
                if (tile == null) continue;
                var entity = tile["m_entity"];
                if (entity == null) continue;

                int playerId = -1;
                var tagValues = entity["m_tags"]?["m_values"];
                int tagCount = tagValues?["_count"] ?? 0;
                if (tagCount > 0)
                {
                    var entries = (object[])tagValues["_entries"];
                    for (int j = 0; j < tagCount && j < entries.Length; j++)
                    {
                        if (entries[j] is IManagedObjectInstance tag && tag.GetValue<int>("key") == PlayerIdTag)
                        {
                            playerId = tag.GetValue<int>("value");
                            break;
                        }
                    }
                }

                string name = null;
                if (!names.TryGetValue(playerId, out name) || string.IsNullOrEmpty(name))
                    lastNames.TryGetValue(playerId, out name);
                if (string.IsNullOrEmpty(name))
                {
                    try { name = tile["m_overlay"]?["m_heroActor"]?["m_playerNameText"]?["m_Text"]; }
                    catch { name = null; }
                }

                int triples = -1;
                try
                {
                    var panel = RecentCombatsPanel(tile);
                    triples = panel?["m_triplesCount"] ?? -1;
                }
                catch { /* optional */ }

                result.Add(new MemoryPlayer
                {
                    PlayerId = playerId,
                    Name = name ?? "",
                    HeroCardId = (string)entity["m_cardIdInternal"] ?? "",
                    Place = entity["m_realTimePlayerLeaderboardPlace"] ?? 0,
                    TavernTier = entity["m_realTimePlayerTechLevel"] ?? 0,
                    TriplesCount = triples,
                });
            }
            return result;
        }

        private static List<dynamic> PlayerTiles(dynamic manager)
        {
            var result = new List<dynamic>();
            try
            {
                var items = manager["m_playerTiles"]?["_items"];
                if (items != null) foreach (var t in items) if (t != null) result.Add(t);
                if (result.Count > 0) return result;
            }
            catch { /* duos layout below */ }

            // Duos: tiles are grouped per team.
            var teams = manager["m_teams"]?["_items"];
            if (teams == null) return result;
            foreach (var team in teams)
            {
                if (team == null) continue;
                var cards = team["m_playerLeaderboardCards"]?["_items"];
                if (cards == null) continue;
                foreach (var tile in cards) if (tile != null) result.Add(tile);
            }
            return result;
        }

        private static dynamic RecentCombatsPanel(dynamic tile)
        {
            try { return tile["m_overlay"]["m_recentCombatsPanel"]; }
            catch { return tile["m_recentCombatsPanel"]; }
        }

        /// <summary>GameState.m_playerInfoMap: player id -> name, for everyone in the lobby.</summary>
        private Dictionary<int, string> PlayerInfoNames()
        {
            var result = new Dictionary<int, string>();
            var map = _image["GameState"]?["s_instance"]?["m_playerInfoMap"];
            if (map == null) return result;
            int count = map["count"] ?? 0;
            if (count <= 0) return result;
            var keys = map["keySlots"];
            var values = (object[])map["valueSlots"];
            for (int i = 0; i < count && i < values.Length; i++)
            {
                var id = keys[i];
                if (id == null) continue;
                if (values[i] is IManagedObjectInstance info)
                {
                    var name = info.GetValue<string>("m_name");
                    if (!string.IsNullOrEmpty(name)) result[(int)id] = name;
                }
            }
            return result;
        }

        /// <summary>GameMgr.m_lastDisplayedPlayerNames: filled in as names resolve.</summary>
        private Dictionary<int, string> LastDisplayedNames()
        {
            var result = new Dictionary<int, string>();
            var names = _image.GetService("GameMgr")?["m_lastDisplayedPlayerNames"];
            if (names == null) return result;
            int count = names["count"] ?? 0;
            if (count <= 0) return result;
            var keys = names["keySlots"];
            var values = names["valueSlots"];
            for (int i = 0; i < count; i++)
            {
                var id = keys[i];
                string name = values[i];
                if (id != null && !string.IsNullOrEmpty(name)) result[(int)id] = name;
            }
            return result;
        }
    }
}
