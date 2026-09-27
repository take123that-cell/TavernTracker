namespace TavernTracker.Core;

public sealed class AppSettings
{
    /// <summary>Leaderboard region: US, EU or AP.</summary>
    public string Region { get; set; } = "EU";

    /// <summary>Your BattleTag. Filled in automatically from the game log; can be overridden.</summary>
    public string BattleTag { get; set; } = "";

    /// <summary>Folder containing Hearthstone.exe. Empty = find it automatically.</summary>
    public string HearthstoneDirectory { get; set; } = "";

    /// <summary>How often to save a copy of the leaderboard (builds everyone's rating history).</summary>
    public int SnapshotMinutes { get; set; } = 20;

    public double SessionGapHours { get; set; } = 3;

    public bool OverlayEnabled { get; set; } = true;
    public bool ShowSessionPanel { get; set; } = true;
    /// <summary>Size of the in-game panels relative to HDT-like 1080p size (0.6 - 1.3).</summary>
    public double OverlayScale { get; set; } = 0.8;
    public bool ShowLobbyPanel { get; set; } = true;
    public bool ShowMinionBrowser { get; set; } = true;
    /// <summary>Win / tie / loss odds at the top of the screen during combat.</summary>
    public bool ShowCombatOdds { get; set; } = true;
    /// <summary>Avg placement / tier / pick rate above hero, trinket and quest choices.</summary>
    public bool ShowPickStats { get; set; } = true;
    /// <summary>Show the tier 7 button even when you can't reach tier 7 this game.</summary>
    public bool AlwaysShowTier7 { get; set; }
    /// <summary>Cards you hid from the minion list (right-click), e.g. if one shows up that isn't in the game.</summary>
    public List<string> HiddenCards { get; set; } = new();
    /// <summary>How see-through the in-game panels are (0.3 = very transparent, 1 = solid).</summary>
    public double OverlayOpacity { get; set; } = 0.72;
    /// <summary>Read rating, lobby names and tribes from the game (like HDT and Firestone do).</summary>
    public bool MemoryReading { get; set; } = true;
    public double OverlayLeft { get; set; } = -1;
    public double OverlayTop { get; set; } = -1;

    public static string FilePath => AppPaths.File("settings.json");

    public static AppSettings Load()
    {
        var s = JsonFile.Load<AppSettings>(FilePath) ?? new AppSettings();
        if (s.SnapshotMinutes < 5) s.SnapshotMinutes = 5;
        if (s.SessionGapHours <= 0) s.SessionGapHours = 3;
        if (s.OverlayScale < 0.6 || s.OverlayScale > 1.3) s.OverlayScale = 0.8;
        if (s.OverlayOpacity < 0.3 || s.OverlayOpacity > 1) s.OverlayOpacity = 0.72;
        if (Leaderboard.LeaderboardClient.Regions.All(r => r != s.Region)) s.Region = "EU";
        return s;
    }

    public void Save() => JsonFile.Save(FilePath, this);
}
