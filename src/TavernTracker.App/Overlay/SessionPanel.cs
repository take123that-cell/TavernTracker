using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TavernTracker.Core;
using TavernTracker.Core.Hearthstone;
using static TavernTracker.App.Overlay.OverlayStyle;
using static TavernTracker.App.Theme;

namespace TavernTracker.App.Overlay;

/// <summary>Left column: available tribes, MMR start/current and latest games (like HDT's session panel).</summary>
public sealed class SessionPanel : StackPanel
{
    private readonly TrackerEngine _engine;
    private readonly WrapPanel _tribes = new() { Margin = new Thickness(6, 6, 6, 6), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _start = Small("—", Brushes.White, 17, bold: true, TextAlignment.Center);
    private readonly TextBlock _current = Small("—", Brushes.White, 17, bold: true, TextAlignment.Center);
    private readonly StackPanel _games = new();
    private readonly TextBlock _status = Small("", Gold, 12, bold: true);
    private readonly TextBlock _detail = Small("", Muted, 11);
    private readonly Border _statusBox;
    private string _signature = "";

    public SessionPanel(TrackerEngine engine)
    {
        _engine = engine;
        Width = 240;

        _statusBox = new Border
        {
            Background = PanelBg,
            Padding = new Thickness(8, 6, 8, 6),
            Child = Stack(Orientation.Vertical, 1, _status, _detail),
            BorderBrush = Edge,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        Children.Add(_statusBox);

        Children.Add(Section("Available Minions", _tribes));

        var mmr = new Grid { Margin = new Thickness(0, 4, 0, 6) };
        mmr.ColumnDefinitions.Add(new ColumnDefinition());
        mmr.ColumnDefinitions.Add(new ColumnDefinition());
        mmr.Children.Add(Stack(Orientation.Vertical, 0, Small("Start", Muted, 11, align: TextAlignment.Center), _start).At(0));
        mmr.Children.Add(Stack(Orientation.Vertical, 0, Small("Current", Muted, 11, align: TextAlignment.Center), _current).At(1));
        Children.Add(Section("MMR", mmr));

        Children.Add(Section("Latest Games", _games));
    }

    /// <summary>Redraws when something changed. <paramref name="game"/> is null on the menu.</summary>
    public void Update(BgGame? game)
    {
        bool duos = game?.IsDuos ?? false;
        var session = _engine.Session(duos);
        var races = _engine.LobbyRaces();
        var sig = $"{game?.Turn}|{game?.MyPlace}|{game?.MyHero?.TavernTier}|{session.RatingStart}|{session.RatingNow}|{session.Count}|{string.Join(",", races)}|{_engine.Games.All().Count}|{_engine.MemoryAvailable}";
        if (sig == _signature) return;
        _signature = sig;

        // Your status (in a game only).
        if (game != null)
        {
            _statusBox.Visibility = Visibility.Visible;
            var me = game.MyHero;
            var heroName = me?.Name is { Length: > 0 } n ? n : (me != null ? _engine.Cards.NameOf(me.CardId) : null) ?? "Choosing a hero…";
            _status.Text = me != null && me.Place > 0 ? $"{heroName} · #{me.Place}" : heroName;
            int alive = game.Heroes.Count(h => !h.IsDead);
            _detail.Text = $"Tier {me?.TavernTier ?? 1} · Turn {Math.Max(1, game.Turn)} · {(alive > 0 ? alive : 8)} left";
        }
        else
        {
            _statusBox.Visibility = Visibility.Collapsed;
        }

        // Tribes
        _tribes.Children.Clear();
        if (races.Count == 0)
        {
            _tribes.Children.Add(Small(game == null ? "Waiting for next game" : _engine.MemoryAvailable ? "Reading tribes…" : "Needs memory reading (Settings)", Muted, 11.5));
        }
        else
        {
            foreach (var r in races.OrderBy(Races.Label))
            {
                var key = Races.Key(r);
                var chip = new Border
                {
                    Background = TribeBrush(key),
                    CornerRadius = new CornerRadius(9),
                    Padding = new Thickness(7, 2, 7, 2),
                    Margin = new Thickness(2),
                    Child = Small(Races.Label(r), Brushes.White, 11, bold: true),
                };
                _tribes.Children.Add(chip);
            }
        }

        // MMR
        _start.Text = session.RatingStart > 0 ? N(session.RatingStart) : "—";
        _current.Text = session.RatingNow > 0 ? N(session.RatingNow) : "—";
        _current.Foreground = session.Delta is > 0 ? Up : session.Delta is < 0 ? Down : Brushes.White;

        // Latest games (this session, newest first)
        _games.Children.Clear();
        var header = Row("Hero", "Place", "MMR", Muted, Muted, Muted, 10.5);
        _games.Children.Add(header);
        var games = session.Games.Reverse().Take(4).ToList();
        if (games.Count == 0)
        {
            var empty = Small("No games played this session.\nLatest games will appear here.", Muted, 11, align: TextAlignment.Center);
            empty.TextWrapping = TextWrapping.Wrap;
            empty.Margin = new Thickness(6, 4, 6, 8);
            _games.Children.Add(empty);
        }
        foreach (var g in games)
        {
            var hero = g.HeroName.Length > 0 ? g.HeroName : _engine.Cards.NameOf(g.HeroCardId) ?? "?";
            var delta = g.RatingDelta;
            _games.Children.Add(Row(hero, Ordinal(g.Place),
                delta.HasValue ? Signed(delta.Value) : "—",
                Brushes.White, PlaceBrush(g.Place), delta is > 0 ? Up : delta is < 0 ? Down : Muted, 12));
        }
    }

    private static Grid Row(string a, string b, string c, Brush ca, Brush cb, Brush cc, double size)
    {
        var g = new Grid { Margin = new Thickness(8, 3, 8, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        g.Children.Add(Small(a, ca, size, bold: true).At(0));
        g.Children.Add(Small(b, cb, size, bold: true, TextAlignment.Center).At(1));
        g.Children.Add(Small(c, cc, size, bold: true, TextAlignment.Right).At(2));
        return g;
    }
}
