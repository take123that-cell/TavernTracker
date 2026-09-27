using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TavernTracker.App.Controls;
using TavernTracker.Core;
using TavernTracker.Core.Hearthstone;
using TavernTracker.Core.Leaderboard;
using static TavernTracker.App.Theme;

namespace TavernTracker.App.Pages;

public sealed class SettingsPage : IPage
{
    private readonly TrackerEngine _engine;
    private readonly OverlayWindow _overlay;
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Segmented _region;
    private readonly Segmented _overlayToggle;
    private readonly Segmented _memoryToggle;
    private readonly Segmented _sessionToggle;
    private readonly Segmented _lobbyToggle;
    private readonly Segmented _browserToggle;
    private readonly Segmented _overlaySize;
    private readonly Segmented _overlayOpacity;
    private readonly Segmented _oddsToggle;
    private readonly Segmented _pickToggle;
    private readonly Segmented _tier7Toggle;
    private readonly TextBlock _cardStatus = T("", 12, Faint);
    private readonly TextBox _tag = Input();
    private readonly TextBox _hsDir = Input();
    private readonly TextBox _interval = Input();
    private readonly TextBox _gap = Input();
    private readonly TextBlock _message = T("", 13, Up);

    public string Title => "Settings";
    public UIElement View => _scroll;

    public SettingsPage(TrackerEngine engine, OverlayWindow overlay)
    {
        _engine = engine;
        _overlay = overlay;
        var s = engine.Settings;
        _region = new Segmented(LeaderboardClient.Regions.Select(r => (r, r)), s.Region);
        _overlayToggle = OnOff(s.OverlayEnabled);
        _memoryToggle = OnOff(s.MemoryReading);
        _sessionToggle = OnOff(s.ShowSessionPanel);
        _lobbyToggle = OnOff(s.ShowLobbyPanel);
        _browserToggle = OnOff(s.ShowMinionBrowser);
        _overlayOpacity = new Segmented(new[] { ("0.45", "Very clear"), ("0.6", "Clear"), ("0.72", "Balanced"), ("0.9", "Solid") }, OpacityKey(s.OverlayOpacity));
        _oddsToggle = OnOff(s.ShowCombatOdds);
        _pickToggle = OnOff(s.ShowPickStats);
        _tier7Toggle = OnOff(s.AlwaysShowTier7);
        _cardStatus.TextWrapping = TextWrapping.Wrap;
        _cardStatus.TextTrimming = TextTrimming.None;
        _overlaySize = new Segmented(new[] { ("0.65", "Small"), ("0.8", "Medium"), ("0.95", "Large"), ("1.1", "Extra large") }, SizeKey(s.OverlayScale));
        _interval.Width = 90;
        _gap.Width = 90;

        var hsRow = Columns(Star(), Px(8), Auto, Px(8), Auto);
        hsRow.Children.Add(_hsDir.At(0));
        hsRow.Children.Add(Btn("Browse…", Browse).At(2));
        hsRow.Children.Add(Btn("Find", () => { _hsDir.Text = HearthstoneSetup.FindInstall(null) ?? ""; if (_hsDir.Text.Length == 0) Say("Couldn't find Hearthstone automatically.", false); }).At(4));

        var form = Stack(Orientation.Vertical, 18,
            Field("Leaderboard region", "The region you play on. Your rating and the leaderboard page use it.", _region),
            Field("Your BattleTag", "Filled in from your first game. Only the part before # is shown on the leaderboard.", _tag),
            Field("Hearthstone folder", "The folder with Hearthstone.exe. Its Logs folder is where games are read from.", hsRow),
            Field("Save the leaderboard every (minutes)", "Each copy adds to everyone's rating history. 20 is a good balance.", _interval),
            Field("New session after a break of (hours)", "Games closer together than this count as one session.", _gap),
            Field("In-game overlay", "Panels drawn over Hearthstone during Battlegrounds (and your MMR on the Battlegrounds menu). Works in windowed or borderless full screen.", _overlayToggle),
            Field("  Panel size", "How big the in-game panels are. Medium is about HDT's size.", _overlaySize),
            Field("  Transparency", "How see-through the panel backgrounds are. Text always stays readable.", _overlayOpacity),
            Field("  Combat odds", "Win / tie / loss / lethal chances at the top of the screen during each fight, from Firestone's open-source simulator.", _oddsToggle),
            Field("  Session panel", "Available tribes, MMR start/current and latest games, on the left.", _sessionToggle),
            Field("  Lobby MMR panel", "Every player in the lobby with their public rating and how often you've met.", _lobbyToggle),
            Field("  Pick stats", "Avg placement, tier and pick rate above the heroes, trinkets and quests you're offered (quests also show the best tribes for the reward). Free public data from Firestone: players in the top 50% of MMR, this patch.", _pickToggle),
            Field("  Minion browser", "Tier buttons at the top right: the lobby's minions by tier, with tribe and keyword filters. Right-click a card to hide it.", _browserToggle),
            Field("  Always show tier 7", "Normally the tier 7 button only appears once you can reach tier 7.", _tier7Toggle),
            Field("  Card pool", "Where the minion list comes from, and how many live corrections were applied.", _cardStatus),
            Field("Read ratings, names and tribes from the game", "Like HDT and Firestone, this reads (never changes) Hearthstone's memory for your exact MMR, the lobby's tribes and players' names. Needs updating when Blizzard changes the game. Restart Tavern Tracker after changing this.", _memoryToggle));

        var buttons = Stack(Orientation.Horizontal, 10, Btn("Save", Save, primary: true), _message);

        var tools = Stack(Orientation.Horizontal, 10,
            Btn("Import a Power.log…", Import),
            Btn("Refresh leaderboard now", () =>
            {
                _ = _engine.Snapshots.RefreshAsync(_engine.Settings.Region, false);
                _ = _engine.Snapshots.RefreshAsync(_engine.Settings.Region, true);
                Say("Downloading…", true);
            }),
            Btn("Open data folder", () => Open(AppPaths.Root)));

        var about = new StackPanel();
        if (Asset("logo-wide.png") is { } wide)
        {
            var img = new Image { Source = wide, MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Left };
            System.Windows.Media.RenderOptions.SetBitmapScalingMode(img, System.Windows.Media.BitmapScalingMode.HighQuality);
            about.Children.Add(img);
        }
        var version = typeof(SettingsPage).Assembly.GetName().Version;
        about.Children.Add(Note($"Version {version?.ToString(3)} · free and open source. Game data from HearthstoneJSON, Firestone and HSReplay; ratings from Blizzard's public leaderboard. Not affiliated with Blizzard Entertainment."));

        _scroll.Content = Stack(Orientation.Vertical, 18,
            Heading("Settings"),
            CardBox(Stack(Orientation.Vertical, 22, form, buttons), 22),
            CardBox(Stack(Orientation.Vertical, 12, Caption("Tools"), tools,
                Note("Import reads an older Power.log (for example from a previous Hearthstone session in the Logs folder) and adds any Battlegrounds games it finds. Games already recorded are skipped.")), 22),
            CardBox(Stack(Orientation.Vertical, 8, Caption("How it works"),
                Note("Games: Tavern Tracker turns on Hearthstone's own game log (the same one HDT and other trackers use) and reads your games from it."),
                Note("Memory (optional): your exact MMR, the lobby's tribes and players' names are read from the game's memory, the same way HDT and Firestone do it (built on the open-source UnitySpy)."),
                Note("Ratings: from Blizzard's public leaderboard. Blizzard only publishes the top players, so if you or someone else is below the cutoff, no exact rating is available."),
                Note("History: the app saves a copy of the leaderboard regularly while it's running. Rating graphs start from the first copy and fill in over time.")), 22),
            CardBox(about, 22));
    }

    public void Refresh()
    {
        var s = _engine.Settings;
        _region.Select(s.Region, raise: false);
        _tag.Text = s.BattleTag;
        _hsDir.Text = _engine.HearthstoneDirectory ?? s.HearthstoneDirectory;
        _interval.Text = s.SnapshotMinutes.ToString(CultureInfo.InvariantCulture);
        _gap.Text = s.SessionGapHours.ToString("0.#", CultureInfo.InvariantCulture);
        _overlayToggle.Select(s.OverlayEnabled ? "on" : "off", raise: false);
        _memoryToggle.Select(s.MemoryReading ? "on" : "off", raise: false);
        _sessionToggle.Select(s.ShowSessionPanel ? "on" : "off", raise: false);
        _lobbyToggle.Select(s.ShowLobbyPanel ? "on" : "off", raise: false);
        _browserToggle.Select(s.ShowMinionBrowser ? "on" : "off", raise: false);
        _overlaySize.Select(SizeKey(s.OverlayScale), raise: false);
        _overlayOpacity.Select(OpacityKey(s.OverlayOpacity), raise: false);
        _oddsToggle.Select(s.ShowCombatOdds ? "on" : "off", raise: false);
        _pickToggle.Select(s.ShowPickStats ? "on" : "off", raise: false);
        _tier7Toggle.Select(s.AlwaysShowTier7 ? "on" : "off", raise: false);
        _cardStatus.Text = _engine.Cards.Status;
    }

    private void Save()
    {
        var s = _engine.Settings;
        if (!int.TryParse(_interval.Text.Trim(), out var minutes) || minutes < 5 || minutes > 720)
        {
            Say("Leaderboard interval must be between 5 and 720 minutes.", false);
            return;
        }
        if (!double.TryParse(_gap.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var gap) || gap <= 0 || gap > 48)
        {
            Say("Session break must be between 0 and 48 hours.", false);
            return;
        }

        bool regionChanged = s.Region != _region.Selected;
        bool dirChanged = !string.Equals(s.HearthstoneDirectory, _hsDir.Text.Trim(), StringComparison.OrdinalIgnoreCase);

        s.Region = _region.Selected;
        s.BattleTag = _tag.Text.Trim();
        s.HearthstoneDirectory = _hsDir.Text.Trim();
        s.SnapshotMinutes = minutes;
        s.SessionGapHours = gap;
        s.OverlayEnabled = _overlayToggle.Selected == "on";
        bool memoryChanged = s.MemoryReading != (_memoryToggle.Selected == "on");
        s.MemoryReading = _memoryToggle.Selected == "on";
        s.ShowSessionPanel = _sessionToggle.Selected == "on";
        s.ShowLobbyPanel = _lobbyToggle.Selected == "on";
        s.ShowMinionBrowser = _browserToggle.Selected == "on";
        s.OverlayScale = double.Parse(_overlaySize.Selected, CultureInfo.InvariantCulture);
        s.OverlayOpacity = double.Parse(_overlayOpacity.Selected, CultureInfo.InvariantCulture);
        s.ShowCombatOdds = _oddsToggle.Selected == "on";
        s.ShowPickStats = _pickToggle.Selected == "on";
        s.AlwaysShowTier7 = _tier7Toggle.Selected == "on";
        s.Save();

        _overlay.ApplySettings();
        if (regionChanged)
        {
            _ = _engine.Snapshots.EnsureFreshAsync(s.Region, false, TimeSpan.FromMinutes(s.SnapshotMinutes));
            _ = _engine.Snapshots.EnsureFreshAsync(s.Region, true, TimeSpan.FromMinutes(s.SnapshotMinutes));
        }
        if (dirChanged && !_engine.StartLogReading())
        {
            Say("Saved, but Hearthstone.exe isn't in that folder.", false);
            return;
        }
        Say(memoryChanged ? "Saved. Restart Tavern Tracker to apply the memory-reading change." : "Saved.", true);
    }

    private static string OpacityKey(double o) =>
        new[] { "0.45", "0.6", "0.72", "0.9" }.OrderBy(k => Math.Abs(double.Parse(k, CultureInfo.InvariantCulture) - o)).First();

    private static string SizeKey(double scale) =>
        new[] { "0.65", "0.8", "0.95", "1.1" }.OrderBy(k => Math.Abs(double.Parse(k, CultureInfo.InvariantCulture) - scale)).First();

    private static Segmented OnOff(bool on) => new(new[] { ("on", "On"), ("off", "Off") }, on ? "on" : "off");

    private void Browse()
    {
        var dlg = new OpenFolderDialog { Title = "Choose the folder that contains Hearthstone.exe" };
        if (dlg.ShowDialog() == true) _hsDir.Text = dlg.FolderName;
    }

    private async void Import()
    {
        var start = _engine.HearthstoneDirectory != null ? HearthstoneSetup.LogsDirectory(_engine.HearthstoneDirectory) : null;
        var dlg = new OpenFileDialog
        {
            Title = "Choose a Power.log",
            Filter = "Hearthstone Power log|Power.log;*.log|All files|*.*",
            InitialDirectory = start != null && System.IO.Directory.Exists(start) ? start : "",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            Say("Reading…", true);
            var file = dlg.FileName;
            int added = await Task.Run(() => _engine.ImportLog(file));
            Say(added == 0 ? "No new Battlegrounds games in that log." : $"Added {added} game{(added == 1 ? "" : "s")}.", true);
        }
        catch (Exception ex)
        {
            Log.Error("Import failed", ex);
            Say("Couldn't read that file.", false);
        }
    }

    private void Say(string text, bool ok)
    {
        _message.Text = text;
        _message.Foreground = ok ? Up : Down;
    }

    private static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Could not open folder", ex); }
    }

    private static UIElement Field(string label, string help, UIElement input)
    {
        var helpText = T(help, 12, Faint);
        helpText.TextWrapping = TextWrapping.Wrap;
        helpText.TextTrimming = TextTrimming.None;
        if (input is FrameworkElement fe && fe is not Grid) fe.HorizontalAlignment = HorizontalAlignment.Left;
        if (input is TextBox tb && double.IsNaN(tb.Width)) tb.Width = 360;
        return Stack(Orientation.Vertical, 6, T(label, 14, Text, FontWeights.SemiBold), input, helpText);
    }

    private static TextBlock Note(string text)
    {
        var t = T(text, 13, Muted);
        t.TextWrapping = TextWrapping.Wrap;
        t.TextTrimming = TextTrimming.None;
        return t;
    }
}
