using System.Windows;
using System.Windows.Controls;
using TavernTracker.App.Controls;
using TavernTracker.Core;
using TavernTracker.Core.Games;
using TavernTracker.Core.Hearthstone;
using static TavernTracker.App.Theme;

namespace TavernTracker.App.Pages;

/// <summary>You at a glance: rating, session, the game in progress, recent games.</summary>
public sealed class HomePage : IPage
{
    private readonly TrackerEngine _engine;
    private readonly MainWindow _main;
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly StackPanel _root = new();
    private readonly Segmented _mode;
    private readonly TextBlock _heading = Heading("Home");
    private readonly ContentControl _rating = new();
    private readonly ContentControl _session = new();
    private readonly ContentControl _live = new();
    private readonly RatingChart _chart = new();
    private readonly StackPanel _recent = new();
    private string _liveSignature = "";

    public string Title => "Home";
    public UIElement View => _scroll;

    private bool Duos => _mode.Selected == "duos";

    public HomePage(TrackerEngine engine, MainWindow main)
    {
        _engine = engine;
        _main = main;
        _mode = new Segmented(new[] { ("solo", "Solo"), ("duos", "Duos") }, "solo");
        _mode.Changed += _ => Refresh();

        var header = Columns(Star(), Auto);
        header.Children.Add(_heading.At(0));
        header.Children.Add(_mode.At(1));
        _root.Children.Add(header);

        var cards = Columns(Star(), Px(16), Star(), Px(16), Star(1.2));
        cards.Margin = new Thickness(0, 20, 0, 0);
        cards.Children.Add(CardBox(_rating).At(0));
        cards.Children.Add(CardBox(_session).At(2));
        cards.Children.Add(CardBox(_live).At(4));
        _root.Children.Add(cards);

        var chartCard = CardBox(Stack(Orientation.Vertical, 12, Caption("Your rating over time"), _chart));
        chartCard.Margin = new Thickness(0, 16, 0, 0);
        _root.Children.Add(chartCard);

        var recentCard = CardBox(Stack(Orientation.Vertical, 10, Caption("Recent games"), _recent));
        recentCard.Margin = new Thickness(0, 16, 0, 0);
        _root.Children.Add(recentCard);

        _scroll.Content = _root;
    }

    public void Refresh()
    {
        var tag = _engine.Settings.BattleTag;
        _heading.Text = string.IsNullOrWhiteSpace(tag) ? "Home" : Names.Display(tag);

        var board = _engine.HomeBoard(Duos);
        _rating.Content = RatingCard(board, tag);
        _session.Content = SessionCard(_engine.Session(Duos));

        var profile = string.IsNullOrWhiteSpace(tag) ? null : board.Profile(tag, DateTime.UtcNow);
        _chart.EmptyText = string.IsNullOrWhiteSpace(tag)
            ? "Play a game (or set your BattleTag in Settings) and your rating history will show here."
            : "No rating history yet. It fills in while you're on the public leaderboard and the app is running.";
        _chart.SetPoints(profile?.History ?? Array.Empty<Core.Leaderboard.RatingPoint>());

        _recent.Children.Clear();
        var games = _engine.Games.All().Where(g => g.Duos == Duos).Reverse().Take(8).ToList();
        if (games.Count == 0)
            _recent.Children.Add(T("No games recorded yet. Games show up here when they finish.", 13, Muted));
        foreach (var g in games) _recent.Children.Add(GamesPage.GameRow(g));

        _liveSignature = "";
        RefreshLive();
    }

    /// <summary>Called every second: only redraws the live card when something changed.</summary>
    public void RefreshLive()
    {
        var game = _engine.LiveGame();
        var sig = game == null ? "none" :
            $"{game.Turn}|{string.Join(",", game.Heroes.Select(h => $"{h.CardId}:{h.Place}:{h.TavernTier}:{h.IsDead}"))}";
        if (sig == _liveSignature) return;
        _liveSignature = sig;
        _live.Content = LiveCard(game);
    }

    private UIElement RatingCard(Core.Leaderboard.BoardHistory board, string tag)
    {
        var p = Stack(Orientation.Vertical, 6);
        p.Children.Add(Caption($"Rating · {_engine.Settings.Region} {(Duos ? "duos" : "solo")}"));
        if (string.IsNullOrWhiteSpace(tag))
        {
            p.Children.Add(T("—", 34, Faint, FontWeights.Bold));
            p.Children.Add(Wrap(T("Your BattleTag is picked up from your first game, or set it in Settings.", 12.5, Muted)));
            return p;
        }
        if (!board.HasData)
        {
            p.Children.Add(T("…", 34, Faint, FontWeights.Bold));
            p.Children.Add(T("Downloading the leaderboard…", 12.5, Muted));
            return p;
        }
        var row = board.Find(tag, out var same);
        int live = _engine.CurrentRating(Duos);
        if (live > 0)
        {
            // Exact rating read from the game.
            p.Children.Add(T(N(live), 34, Gold, FontWeights.Bold));
            p.Children.Add(T(row != null ? $"#{N(row.Rank)} on the {_engine.Settings.Region} leaderboard" : $"Below the public leaderboard (top {N(board.Cutoff)}+)", 13, Muted));
            if (row != null)
            {
                var liveLink = Clickable(T("View profile ›", 12.5, Blue, FontWeights.SemiBold),
                    () => _main.ShowPlayer(tag, _engine.Settings.Region, Duos), padX: 0, padY: 2);
                liveLink.HorizontalAlignment = HorizontalAlignment.Left;
                p.Children.Add(liveLink);
            }
            return p;
        }
        if (row == null)
        {
            p.Children.Add(T("Unranked", 30, Muted, FontWeights.Bold));
            p.Children.Add(Wrap(T($"Not on the public leaderboard (it lists ratings above {N(board.Cutoff)}). Blizzard doesn't publish ratings below that.", 12.5, Muted)));
            return p;
        }
        var prof = board.Profile(tag, DateTime.UtcNow);
        p.Children.Add(T(N(row.Rating), 34, Gold, FontWeights.Bold));
        var line = Stack(Orientation.Horizontal, 10, T($"#{N(row.Rank)}", 14, Text, FontWeights.SemiBold), Change(prof?.Change24h, 14, " today"));
        p.Children.Add(line);
        if (same > 1) p.Children.Add(Wrap(T($"{same} players share this name on the board; this is the highest one.", 12, Amber)));
        var link = Clickable(T("View profile ›", 12.5, Blue, FontWeights.SemiBold),
            () => _main.ShowPlayer(tag, _engine.Settings.Region, Duos), padX: 0, padY: 2);
        link.HorizontalAlignment = HorizontalAlignment.Left;
        p.Children.Add(link);
        return p;
    }

    private static UIElement SessionCard(SessionSummary s)
    {
        var p = Stack(Orientation.Vertical, 6);
        p.Children.Add(Caption("This session"));
        if (s.Count == 0)
        {
            p.Children.Add(T("0 games", 30, Muted, FontWeights.Bold));
            p.Children.Add(T("A session is games with no break over 3 hours.", 12.5, Muted));
            return p;
        }
        var top = Stack(Orientation.Horizontal, 12,
            T(s.AvgPlace.HasValue ? s.AvgPlace.Value.ToString("0.00", Num) : "—", 30, Text, FontWeights.Bold),
            Stack(Orientation.Vertical, 0, T("avg place", 12, Muted), T($"{s.Count} games · top 4 in {s.Top4}", 12, Muted)));
        p.Children.Add(top);

        var rating = Stack(Orientation.Horizontal, 8);
        if (s.Delta.HasValue)
        {
            rating.Children.Add(T($"{N(s.RatingStart)} → {N(s.RatingNow)}", 13, Text));
            rating.Children.Add(Change(s.Delta, 13));
        }
        else
        {
            rating.Children.Add(T("Rating change shows while you're on the leaderboard.", 12, Faint));
        }
        p.Children.Add(rating);

        var badges = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var place in s.Placements.TakeLast(12))
        {
            var b = PlaceBadge(place, 24);
            ((FrameworkElement)b).Margin = new Thickness(0, 0, 5, 5);
            badges.Children.Add(b);
        }
        p.Children.Add(badges);
        return p;
    }

    private UIElement LiveCard(BgGame? g)
    {
        var p = Stack(Orientation.Vertical, 6);
        p.Children.Add(Caption("Live game"));
        if (g == null)
        {
            p.Children.Add(T("Not in a Battlegrounds game", 16, Muted, FontWeights.SemiBold));
            p.Children.Add(Wrap(T("When you start one, your hero, place and the lobby show here and in the overlay.", 12.5, Faint)));
            return p;
        }

        var me = g.MyHero;
        var head = Columns(Auto, Star());
        head.Children.Add(PlaceBadge(me?.Place ?? 0, 38).At(0));
        var who = Stack(Orientation.Vertical, 2,
            T(me?.Name is { Length: > 0 } n ? n : "Picking a hero…", 16, Text, FontWeights.SemiBold),
            T($"Tier {me?.TavernTier ?? 0} · Turn {g.Turn}{(g.IsDuos ? " · Duos" : "")}", 12.5, Muted));
        who.Margin = new Thickness(12, 0, 0, 0);
        head.Children.Add(who.At(1));
        p.Children.Add(head);

        var list = Stack(Orientation.Vertical, 2);
        list.Margin = new Thickness(0, 6, 0, 0);
        foreach (var h in g.Heroes)
        {
            var row = Columns(Px(22), Star(), Auto);
            row.Opacity = h.IsDead ? 0.4 : 1;
            row.Children.Add(T(h.Place.ToString(Num), 12, PlaceBrush(h.Place), FontWeights.Bold).At(0));
            row.Children.Add(T(h.Name.Length > 0 ? h.Name : h.CardId, 12.5, h.IsMine ? Gold : Text,
                h.IsMine ? FontWeights.SemiBold : FontWeights.Normal).At(1));
            row.Children.Add(T(h.IsDead ? "out" : $"T{h.TavernTier} · {h.HealthLeft}hp", 12, Muted).At(2));
            list.Children.Add(row);
        }
        p.Children.Add(list);
        return p;
    }

    private static TextBlock Wrap(TextBlock t)
    {
        t.TextWrapping = TextWrapping.Wrap;
        t.TextTrimming = TextTrimming.None;
        return t;
    }
}
