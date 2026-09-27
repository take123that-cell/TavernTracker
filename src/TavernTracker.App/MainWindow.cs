using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TavernTracker.App.Pages;
using TavernTracker.Core;
using static TavernTracker.App.Theme;

namespace TavernTracker.App;

public interface IPage
{
    string Title { get; }
    UIElement View { get; }
    /// <summary>Redraw from the engine's current state. Called on the UI thread.</summary>
    void Refresh();
}

/// <summary>The main window: sidebar navigation on the left, the current page on the right.</summary>
public sealed class MainWindow : Window
{
    private readonly TrackerEngine _engine;
    private readonly ContentControl _content = new();
    private readonly StackPanel _nav = new();
    private readonly TextBlock _status = T("", 11.5, Muted);
    private readonly Dictionary<string, (IPage Page, Border Button, TextBlock Label)> _pages = new();
    private IPage? _current;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public HomePage Home { get; }
    public PlayersPage Players { get; }
    public LeaderboardPage Leaderboard { get; }
    public GamesPage GamesList { get; }
    public SettingsPage SettingsView { get; }

    public MainWindow(TrackerEngine engine, OverlayWindow overlay)
    {
        _engine = engine;
        Title = "Tavern Tracker";
        Width = 1180;
        Height = 780;
        MinWidth = 900;
        MinHeight = 600;
        Background = (Brush?)Texture("wood.png") ?? Bg;
        FontFamily = Font;
        try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/TavernTracker.ico")); }
        catch { /* the exe's own icon is used */ }
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Home = new HomePage(engine, this);
        Players = new PlayersPage(engine);
        Leaderboard = new LeaderboardPage(engine, this);
        GamesList = new GamesPage(engine);
        SettingsView = new SettingsPage(engine, overlay);

        var root = Columns(Px(240), Star());

        // ---- sidebar
        var side = new DockPanel { Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x12, 0x0B, 0x06)), LastChildFill = true };
        // The wordmark (badge + name) heads the sidebar, like a sign over the tavern door.
        var brand = new StackPanel { Margin = new Thickness(12, 20, 12, 18) };
        if (Asset("logo-header.png") is { } header)
        {
            var logo = new Image { Source = header, Width = 216, HorizontalAlignment = HorizontalAlignment.Center, ToolTip = "Tavern Tracker" };
            RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
            brand.Children.Add(logo);
        }
        else
        {
            var name = T("TAVERN TRACKER", 20, Gold, FontWeights.Bold);
            name.FontFamily = Display;
            name.HorizontalAlignment = HorizontalAlignment.Center;
            brand.Children.Add(name);
        }
        brand.Children.Add(new Border { Height = 2, Background = Brass, Margin = new Thickness(10, 14, 10, 0), Opacity = 0.8 });
        DockPanel.SetDock(brand, Dock.Top);
        side.Children.Add(brand);

        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(22, 10, 18, 18);
        DockPanel.SetDock(_status, Dock.Bottom);
        side.Children.Add(_status);

        _nav.Margin = new Thickness(10, 0, 10, 0);
        side.Children.Add(_nav);
        var sideFrame = new Border { Child = side, BorderBrush = Line, BorderThickness = new Thickness(0, 0, 2, 0) };
        root.Children.Add(sideFrame.At(0));

        AddPage("home", "Home", Home);
        AddPage("players", "Players", Players);
        AddPage("leaderboard", "Leaderboard", Leaderboard);
        AddPage("games", "My games", GamesList);
        AddPage("settings", "Settings", SettingsView);

        _content.Margin = new Thickness(32, 26, 32, 20);
        root.Children.Add(_content.At(1));
        Content = root;

        Navigate("home");

        // Engine events arrive on background threads; bounce them to the UI thread.
        engine.GamesChanged += () => Dispatcher.BeginInvoke(RefreshCurrent);
        engine.BoardUpdated += _ => Dispatcher.BeginInvoke(RefreshCurrent);
        engine.Notice += msg => Dispatcher.BeginInvoke(() => _status.Text = msg);

        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        SourceInitialized += (_, _) => DarkTitleBar(this);
        Closed += (_, _) => _timer.Stop();
    }

    public void Navigate(string key)
    {
        if (!_pages.TryGetValue(key, out var entry)) return;
        foreach (var (_, (_, button, label)) in _pages)
        {
            button.Background = System.Windows.Media.Brushes.Transparent;
            label.Foreground = Muted;
        }
        entry.Button.Background = CardHover;
        entry.Label.Foreground = Text;
        _current = entry.Page;
        _content.Content = entry.Page.View;
        SafeRefresh(entry.Page);
    }

    public void ShowPlayer(string name, string region, bool duos)
    {
        Navigate("players");
        Players.Open(name, region, duos);
    }

    private void AddPage(string key, string label, IPage page)
    {
        var text = T(label, 14.5, Muted, FontWeights.Bold);
        text.FontFamily = Display;
        var button = Clickable(text, () => Navigate(key), padX: 14, padY: 10);
        button.Margin = new Thickness(0, 0, 0, 2);
        // Clickable resets the background on mouse-leave; keep the selected item highlighted.
        button.MouseLeave += (_, _) => { if (_current == page) button.Background = CardHover; };
        _nav.Children.Add(button);
        _pages[key] = (page, button, text);
    }

    private void RefreshCurrent()
    {
        if (_current != null) SafeRefresh(_current);
    }

    private static void SafeRefresh(IPage page)
    {
        try { page.Refresh(); }
        catch (Exception ex) { Log.Error($"Page {page.Title} failed to refresh", ex); }
    }

    private int _ticks;

    private void Tick()
    {
        _ticks++;
        // Live game changes every second; everything else every 30 s is plenty.
        if (_current == Home) Home.RefreshLive();
        if (_ticks % 30 == 0) RefreshCurrent();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var parts = new List<string>();
        if (_engine.HearthstoneDirectory == null) parts.Add("Hearthstone not found: set the folder in Settings.");
        else if (_engine.NeedsHearthstoneRestart) parts.Add("Game logging was just turned on. If Hearthstone is open, restart it once so games are recorded.");
        else if (_engine.CurrentLogFile == null) parts.Add("No game log yet: start Hearthstone.");
        else parts.Add(_engine.HearthstoneRunning() ? "Hearthstone is running. Games are being recorded." : "Hearthstone isn't running.");

        if (_engine.Settings.MemoryReading)
            parts.Add(_engine.MemoryAvailable ? "Reading rating and lobby from the game." : $"Game memory: {_engine.MemoryStatus}.");

        var board = _engine.HomeBoard(false);
        parts.Add(board.HasData
            ? $"{_engine.Settings.Region} leaderboard: updated {Ago(board.FetchedUtc)}."
            : $"{_engine.Settings.Region} leaderboard: downloading…");
        var text = string.Join("\n", parts);
        if (_status.Text != text && !_status.Text.StartsWith("Couldn't", StringComparison.Ordinal)) _status.Text = text;
        else if (_status.Text.StartsWith("Couldn't", StringComparison.Ordinal) && _ticks % 20 == 0) _status.Text = text;
    }

    // ---- dark title bar on Windows 10/11
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void DarkTitleBar(Window w)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            int on = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int)); // older Windows 10 builds
        }
        catch
        {
            // purely cosmetic
        }
    }
}
