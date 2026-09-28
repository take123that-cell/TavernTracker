using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TavernTracker.App.Overlay;
using TavernTracker.Core;
using TavernTracker.Core.Hearthstone;

namespace TavernTracker.App;

/// <summary>
/// A transparent layer sized exactly to Hearthstone's game area. Empty areas let clicks through to the
/// game; the panels on it (session, lobby, minion browser) are interactive but never take focus.
/// Layout is designed for 1080p and scaled to the game's height, like HDT's overlay.
/// </summary>
public sealed class OverlayWindow : Window
{
    private readonly TrackerEngine _engine;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Canvas _canvas = new();
    private readonly StackPanel _leftColumn = new();
    private readonly SessionPanel _session;
    private readonly LobbyPanel _lobby;
    private readonly MinionBrowser _browser;
    private readonly CombatOddsBar _odds = new();
    private readonly ChoiceStatsLayer _choices;
    private readonly PassThroughWindow _passThrough = new();
    private readonly OpponentBoardPanel _opponent;
    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private BgGame? _lastGame;
    private (int X, int Y, int Width, int Height)? _lastArea;
    private readonly ScaleTransform _scale = new(1, 1);
    private string? _gameId;
    private IntPtr _hwnd;

    public OverlayWindow(TrackerEngine engine)
    {
        _engine = engine;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Title = "Tavern Tracker overlay";
        Left = -10000; // off-screen until we know where Hearthstone is
        Width = 10;
        Height = 10;

        _session = new SessionPanel(engine);
        _lobby = new LobbyPanel(engine) { Margin = new Thickness(0, 10, 0, 0) };
        _browser = new MinionBrowser(engine);
        _choices = new ChoiceStatsLayer(engine);
        _opponent = new OpponentBoardPanel(engine) { LayoutTransform = _scale };

        _leftColumn.Children.Add(_session);
        _leftColumn.Children.Add(_lobby);
        _leftColumn.LayoutTransform = _scale;
        _browser.LayoutTransform = _scale;
        _odds.LayoutTransform = _scale;

        _canvas.Children.Add(_leftColumn);
        _canvas.Children.Add(_browser);
        // Read-only pieces go on a second, click-through layer so they never swallow clicks meant for the game.
        _passThrough.Layer.Children.Add(_odds);
        _passThrough.Layer.Children.Add(_choices);
        _passThrough.Layer.Children.Add(_opponent);
        _hoverTimer.Tick += (_, _) =>
        {
            try { UpdateOpponentBoard(); }
            catch (Exception ex) { Log.Error("Opponent board update failed", ex); }
        };
        Closed += (_, _) => _passThrough.Close();
        Content = _canvas;

        SourceInitialized += (_, _) => MakeNonActivating();
        _timer.Tick += (_, _) => Tick();
        ApplySettings();
    }

    public void ApplySettings()
    {
        if (_engine.Settings.OverlayEnabled) { _timer.Start(); _hoverTimer.Start(); }
        else
        {
            _timer.Stop();
            _hoverTimer.Stop();
            Hide();
            _passThrough.Hide();
        }
    }

    private void Tick()
    {
        try { TickCore(); }
        catch (Exception ex) { Log.Error("Overlay update failed", ex); }
    }

    private void TickCore()
    {
        var s = _engine.Settings;
        var area = s.OverlayEnabled ? HsWindow.ClientArea() : null;
        if (area == null || !HsWindow.IsActive(_hwnd))
        {
            HideAll();
            return;
        }

        var game = _engine.LiveGame();
        _lastGame = game;
        _lastArea = area;
        bool menu = game == null && _engine.InBattlegroundsMenu;
        if (game == null && !menu)
        {
            HideAll();
            return;
        }

        if (game != null && game.Id != _gameId)
        {
            _gameId = game.Id;
            _browser.Reset();
        }

        _session.Visibility = s.ShowSessionPanel ? Visibility.Visible : Visibility.Collapsed;
        _lobby.Visibility = game != null && s.ShowLobbyPanel ? Visibility.Visible : Visibility.Collapsed;
        _browser.Visibility = game != null && s.ShowMinionBrowser ? Visibility.Visible : Visibility.Collapsed;
        OverlayStyle.ApplyOpacity(s.OverlayOpacity);
        var combat = _engine.CurrentCombat;
        _odds.Update(combat, game != null && s.ShowCombatOdds && combat.GameId == game.Id);

        _session.Update(game);
        if (game != null)
        {
            var dead = game.Heroes.Where(h => h.IsDead).Select(h => h.CardId).ToHashSet();
            _lobby.Update(game.IsDuos, dead);
            _browser.Update(game);
        }

        if (!IsVisible) Show();
        if (!_passThrough.IsVisible) _passThrough.Show();
        var size = Place(area.Value);
        _choices.Update(game != null ? _engine.CurrentChoiceStats() : null, size.X, size.Y);
        UpdateOpponentBoard();
    }

    // ------------------------------------------------------------------ last known boards (HDT's leaderboard hover)

    /// <summary>
    /// Which leaderboard portrait is under the mouse: read from the game (like HDT and Firestone), or, without
    /// memory reading, worked out from the cursor position using HDT's leaderboard geometry.
    /// </summary>
    private void UpdateOpponentBoard()
    {
        var game = _lastGame;
        if (game == null || !IsVisible || _lastArea == null)
        {
            _opponent.Update(null, null);
            return;
        }
        BgHero? hero = null;
        var hovered = _engine.HoveredLeaderboardHero;
        if (!string.IsNullOrEmpty(hovered))
        {
            var key = _engine.Cards.NormalizeHero(hovered);
            hero = game.Heroes.FirstOrDefault(h => h.CardId == hovered)
                   ?? game.Heroes.FirstOrDefault(h => _engine.Cards.NormalizeHero(h.CardId) == key);
        }
        else if (hovered == null)
        {
            hero = HeroUnderCursor(game, _lastArea.Value);
        }
        _opponent.Update(game, hero);

        // The board takes the top-centre slot; the odds bar steps aside meanwhile (as in HDT).
        bool showing = _opponent.Visibility == Visibility.Visible;
        if (showing)
        {
            _odds.Visibility = Visibility.Collapsed;
            _opponent.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(_opponent, Math.Max(0, (_passThrough.Layer.Width - _opponent.DesiredSize.Width) / 2));
            Canvas.SetTop(_opponent, 0);
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CursorPoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint p);

    private static BgHero? HeroUnderCursor(BgGame game, (int X, int Y, int Width, int Height) area)
    {
        if (!GetCursorPos(out var c)) return null;
        double x = c.X - area.X, y = c.Y - area.Y, w = area.Width, h = area.Height;
        // HDT's leaderboard geometry: portraits start 15% down, at the left edge of the 4:3 play area.
        double ratio = Math.Min(1.0, (4.0 / 3.0) / (w / h));
        double top = h * 0.15, left = w * (1 - ratio) / 2;
        double tile = h * 0.69 / 8;
        if (x < left - tile * 0.1 || x > left + tile * 1.25 || y < top) return null;
        if (!game.IsDuos)
        {
            if (y >= top + tile * 8) return null;
            int place = (int)((y - top) / tile) + 1;
            return game.Heroes.FirstOrDefault(hh => hh.Place == place);
        }
        // Duos: four teams of two portraits with a gap between teams (HDT: 13.7% of the column is gaps).
        const double spacingRatio = 0.137;
        double duoTile = h * 0.69 * (1 - spacingRatio) / 8, gap = h * 0.69 * spacingRatio / 3;
        for (int i = 0; i < 8; i++)
        {
            double t = top + duoTile * i + gap * (i / 2);
            if (y < t || y >= t + duoTile) continue;
            int teamPlace = i / 2 + 1;
            var team = game.Heroes.Where(hh => hh.Place == teamPlace).OrderBy(hh => hh.EntityId).ToList();
            if (team.Count == 0) return null;
            return team[Math.Min(i % 2, team.Count - 1)];
        }
        return null;
    }

    private void HideAll()
    {
        if (IsVisible) Hide();
        if (_passThrough.IsVisible) _passThrough.Hide();
    }

    /// <summary>Cover Hearthstone's game area and scale the panels to its height.</summary>
    private Point Place((int X, int Y, int Width, int Height) px)
    {
        var source = PresentationSource.FromVisual(this);
        var m = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = m.Transform(new Point(px.X, px.Y));
        var size = m.Transform(new Point(px.Width, px.Height));

        if (Math.Abs(Left - topLeft.X) > 0.5) Left = topLeft.X;
        if (Math.Abs(Top - topLeft.Y) > 0.5) Top = topLeft.Y;
        if (Math.Abs(Width - size.X) > 0.5) Width = size.X;
        if (Math.Abs(Height - size.Y) > 0.5) Height = size.Y;
        _passThrough.Cover(topLeft, size);

        double scale = Math.Clamp(size.Y / 1080.0 * _engine.Settings.OverlayScale, 0.4, 2.5);
        _scale.ScaleX = _scale.ScaleY = scale;

        Canvas.SetLeft(_leftColumn, 0);
        Canvas.SetTop(_leftColumn, size.Y * 0.15);
        Canvas.SetRight(_browser, 0);
        Canvas.SetTop(_browser, 0);
        // Odds bar: centred at the top, like Bob's Buddy.
        _odds.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(_odds, Math.Max(0, (size.X - _odds.DesiredSize.Width) / 2));
        Canvas.SetTop(_odds, 0);
        _canvas.Width = size.X;
        _canvas.Height = size.Y;
        _passThrough.Layer.Width = size.X;
        _passThrough.Layer.Height = size.Y;
        return size;
    }

    // ---------------------------------------------------------------- Win32

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>Clicking a panel must never pull focus away from the game.</summary>
    private void MakeNonActivating()
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(_hwnd, GWL_EXSTYLE, GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }
}

/// <summary>
/// A transparent layer over the game that ignores the mouse entirely (WS_EX_TRANSPARENT), for read-only
/// pieces like the combat odds and pick stats: clicks go straight through them to Hearthstone.
/// </summary>
public sealed class PassThroughWindow : Window
{
    public Canvas Layer { get; } = new();

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public PassThroughWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false;
        Focusable = false;
        Title = "Tavern Tracker overlay (read-only)";
        Left = -10000;
        Width = 10;
        Height = 10;
        Content = Layer;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        };
    }

    public void Cover(Point topLeft, Point size)
    {
        if (Math.Abs(Left - topLeft.X) > 0.5) Left = topLeft.X;
        if (Math.Abs(Top - topLeft.Y) > 0.5) Top = topLeft.Y;
        if (Math.Abs(Width - size.X) > 0.5) Width = size.X;
        if (Math.Abs(Height - size.Y) > 0.5) Height = size.Y;
    }
}
