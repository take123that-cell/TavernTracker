using System.Windows;
using System.Windows.Controls;
using TavernTracker.App.Controls;
using TavernTracker.Core;
using TavernTracker.Core.Games;
using static TavernTracker.App.Theme;

namespace TavernTracker.App.Pages;

/// <summary>Your game history with totals and per-hero stats.</summary>
public sealed class GamesPage : IPage
{
    private readonly TrackerEngine _engine;
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Segmented _mode;
    private readonly Segmented _range;
    private readonly ContentControl _body = new();

    public string Title => "My games";
    public UIElement View => _scroll;

    public GamesPage(TrackerEngine engine)
    {
        _engine = engine;
        _mode = new Segmented(new[] { ("solo", "Solo"), ("duos", "Duos") }, "solo");
        _range = new Segmented(new[] { ("50", "Last 50"), ("200", "Last 200"), ("all", "All") }, "50");
        _mode.Changed += _ => Refresh();
        _range.Changed += _ => Refresh();

        var header = Columns(Star(), Auto, Px(10), Auto);
        header.Children.Add(Heading("My games").At(0));
        header.Children.Add(_range.At(1));
        header.Children.Add(_mode.At(3));

        _scroll.Content = Stack(Orientation.Vertical, 18, header, _body);
    }

    public void Refresh()
    {
        bool duos = _mode.Selected == "duos";
        var all = _engine.Games.All().Where(g => g.Duos == duos).ToList();
        var games = _range.Selected switch
        {
            "50" => all.TakeLast(50).ToList(),
            "200" => all.TakeLast(200).ToList(),
            _ => all,
        };

        var p = Stack(Orientation.Vertical, 16);
        _body.Content = p;
        if (games.Count == 0)
        {
            p.Children.Add(CardBox(T("No games yet. Finished Battlegrounds games are saved here automatically.", 13.5, Muted)));
            return;
        }

        var placed = games.Where(g => g.Place > 0).ToList();
        double avg = placed.Count > 0 ? placed.Average(g => g.Place) : 0;
        int top4 = placed.Count(g => g.Top4);
        int wins = placed.Count(g => g.Place == 1);

        var stats = Columns(Star(), Star(), Star(), Star(), Star());
        stats.Children.Add(Stat("Games", T(N(games.Count), 26, Text, FontWeights.Bold)).At(0));
        stats.Children.Add(Stat("Average place", T(placed.Count > 0 ? avg.ToString("0.00", Num) : "—", 26, Gold, FontWeights.Bold)).At(1));
        stats.Children.Add(Stat("Top 4", T(placed.Count > 0 ? $"{100.0 * top4 / placed.Count:0}%" : "—", 26, Up, FontWeights.Bold)).At(2));
        stats.Children.Add(Stat("Wins", T(N(wins), 26, Text, FontWeights.Bold)).At(3));
        stats.Children.Add(Stat("Placements", Distribution(placed)).At(4));
        p.Children.Add(CardBox(stats));

        // Heroes you've played most, with average place.
        var heroes = placed.Where(g => g.HeroName.Length > 0)
            .GroupBy(g => g.HeroName)
            .Select(x => (Hero: x.Key, Games: x.Count(), Avg: x.Average(g => g.Place)))
            .OrderByDescending(x => x.Games).ThenBy(x => x.Avg)
            .Take(8)
            .ToList();
        if (heroes.Count > 0)
        {
            var list = Stack(Orientation.Vertical, 4);
            foreach (var h in heroes)
            {
                var row = Columns(Star(), Px(90), Px(80));
                row.Children.Add(T(h.Hero, 13.5).At(0));
                row.Children.Add(T($"{h.Games} {(h.Games == 1 ? "game" : "games")}", 12.5, Muted).At(1));
                row.Children.Add(T($"avg {h.Avg:0.0}", 13, PlaceBrush((int)Math.Round(h.Avg)), FontWeights.SemiBold).At(2));
                list.Children.Add(row);
            }
            p.Children.Add(CardBox(Stack(Orientation.Vertical, 10, Caption("Most played heroes"), list)));
        }

        var rows = Stack(Orientation.Vertical, 2);
        foreach (var g in Enumerable.Reverse(games).Take(150)) rows.Children.Add(GameRow(g));
        p.Children.Add(CardBox(Stack(Orientation.Vertical, 10, Caption("History"), rows)));
    }

    /// <summary>One game as a row (also used on the home page).</summary>
    public static UIElement GameRow(GameRecord g)
    {
        var row = Columns(Px(40), Star(), Px(90), Px(80), Px(120), Px(90));
        row.Margin = new Thickness(0, 3, 0, 3);
        row.Children.Add(PlaceBadge(g.Place, 28).At(0));
        var hero = Stack(Orientation.Vertical, 1,
            T(g.HeroName.Length > 0 ? g.HeroName : "Unknown hero", 14, Text, FontWeights.SemiBold),
            T(g.EndedLocal.ToString("ddd d MMM · HH:mm", Num), 11.5, Muted));
        hero.Margin = new Thickness(10, 0, 0, 0);
        row.Children.Add(hero.At(1));
        row.Children.Add(T(g.TavernTier > 0 ? $"Tier {g.TavernTier}" : "", 12.5, Muted).At(2));
        row.Children.Add(T(g.Turns > 0 ? $"{g.Turns} turns" : "", 12.5, Muted).At(3));
        row.Children.Add(T(g.Duration > TimeSpan.Zero ? $"{(int)g.Duration.TotalMinutes} min" : "", 12.5, Muted).At(4));
        UIElement ratingCell = !g.Ranked ? Pill("Casual", Muted, Line)
            : g.RatingDelta.HasValue ? Stack(Orientation.Vertical, 0, Change(g.RatingDelta, 13), T(N(g.RatingAfter), 11, Faint))
            : g.BoardRating > 0 ? T(N(g.BoardRating), 12.5, Faint) : T("", 12);
        row.Children.Add(ratingCell.At(5));
        return row;
    }

    /// <summary>Tiny bar per placement 1-8.</summary>
    private static UIElement Distribution(IReadOnlyList<GameRecord> placed)
    {
        var bars = new Grid { Height = 34 };
        int max = Math.Max(1, Enumerable.Range(1, 8).Max(p => placed.Count(g => g.Place == p)));
        for (int place = 1; place <= 8; place++)
        {
            bars.ColumnDefinitions.Add(new ColumnDefinition { Width = Px(14) });
            int count = placed.Count(g => g.Place == place);
            var bar = new Border
            {
                Background = PlaceBrush(place),
                Height = Math.Max(2, 30.0 * count / max),
                Width = 10,
                CornerRadius = new CornerRadius(2),
                VerticalAlignment = VerticalAlignment.Bottom,
                ToolTip = $"{Ordinal(place)}: {count}",
            };
            bars.Children.Add(bar.At(place - 1));
        }
        return bars;
    }

    private static string Ordinal(int n) => n switch { 1 => "1st", 2 => "2nd", 3 => "3rd", _ => $"{n}th" };
}
