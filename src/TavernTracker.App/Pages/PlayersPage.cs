using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TavernTracker.App.Controls;
using TavernTracker.Core;
using TavernTracker.Core.Leaderboard;
using static TavernTracker.App.Theme;

namespace TavernTracker.App.Pages;

/// <summary>Search anyone on the leaderboard and see their profile: rank, rating, peak, history graph.</summary>
public sealed class PlayersPage : IPage
{
    private readonly TrackerEngine _engine;
    private readonly Grid _root = Columns(Px(300), Px(20), Star());
    private readonly TextBox _query = Input(hint: "Player name (BattleTag without the #1234)");
    private readonly Segmented _region;
    private readonly Segmented _mode;
    private readonly StackPanel _results = Stack(Orientation.Vertical, 2);
    private readonly ScrollViewer _profileScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private string? _openName;

    public string Title => "Players";
    public UIElement View => _root;

    private bool Duos => _mode.Selected == "duos";
    private string Region => _region.Selected;

    public PlayersPage(TrackerEngine engine)
    {
        _engine = engine;
        _region = new Segmented(LeaderboardClient.Regions.Select(r => (r, r)), engine.Settings.Region);
        _mode = new Segmented(new[] { ("solo", "Solo"), ("duos", "Duos") }, "solo");
        _region.Changed += _ => { EnsureBoard(); Search(); RenderProfile(); };
        _mode.Changed += _ => { EnsureBoard(); Search(); RenderProfile(); };
        _query.TextChanged += (_, _) => Search();
        _query.KeyDown += (_, e) => { if (e.Key == Key.Enter) OpenFirstResult(); };

        var left = new DockPanel();
        var top = Stack(Orientation.Vertical, 10, Heading("Players"), _query,
            Stack(Orientation.Horizontal, 8, _region, _mode));
        top.Margin = new Thickness(0, 0, 0, 14);
        DockPanel.SetDock(top, Dock.Top);
        left.Children.Add(top);
        left.Children.Add(new ScrollViewer { Content = _results, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        _root.Children.Add(left.At(0));
        _root.Children.Add(_profileScroll.At(2));

        RenderProfile();
    }

    public void Refresh()
    {
        Search();
        RenderProfile();
    }

    /// <summary>Open a profile directly (from the leaderboard, home, etc.).</summary>
    public void Open(string name, string region, bool duos)
    {
        _region.Select(region, raise: false);
        _mode.Select(duos ? "duos" : "solo", raise: false);
        _query.Text = Names.Display(name);
        _openName = name;
        EnsureBoard();
        Search();
        RenderProfile();
    }

    private BoardHistory Board => _engine.Snapshots.Board(Region, Duos);

    private void EnsureBoard()
    {
        // Other regions aren't refreshed on a timer; fetch them when viewed (at most every 30 min).
        _ = _engine.Snapshots.EnsureFreshAsync(Region, Duos, TimeSpan.FromMinutes(30));
    }

    private void Search()
    {
        _results.Children.Clear();
        var board = Board;
        if (!board.HasData)
        {
            _results.Children.Add(T("Downloading this leaderboard…", 13, Muted));
            EnsureBoard();
            return;
        }
        var q = _query.Text.Trim();
        if (q.Length == 0)
        {
            _results.Children.Add(Hint($"Type a name to search the {Region} {(Duos ? "duos" : "solo")} leaderboard ({N(board.Count)} players)."));
            return;
        }
        var hits = board.Search(q, 30);
        if (hits.Count == 0)
        {
            _results.Children.Add(Hint($"Nobody called “{q}” on this leaderboard. Only players above {N(board.Cutoff)} are listed."));
            return;
        }
        foreach (var r in hits)
        {
            var row = Columns(Px(58), Star(), Auto);
            row.Children.Add(T($"#{N(r.Rank)}", 12.5, Muted).At(0));
            row.Children.Add(T(r.Name, 13.5, Text, FontWeights.SemiBold).At(1));
            row.Children.Add(T(N(r.Rating), 13, Gold, FontWeights.SemiBold).At(2));
            var name = r.Name;
            var item = Clickable(row, () => { _openName = name; RenderProfile(); }, padX: 10, padY: 7);
            _results.Children.Add(item);
        }
    }

    private void OpenFirstResult()
    {
        var q = _query.Text.Trim();
        var hit = q.Length > 0 ? Board.Search(q, 1).FirstOrDefault() : null;
        if (hit == null) return;
        _openName = hit.Name;
        RenderProfile();
    }

    private void RenderProfile()
    {
        var p = Stack(Orientation.Vertical, 16);
        _profileScroll.Content = p;

        if (_openName == null)
        {
            p.Children.Add(CardBox(Stack(Orientation.Vertical, 8,
                T("Look anyone up", 18, Text, FontWeights.SemiBold),
                Hint("Search a name on the left to see their rank, rating and how their rating has moved. " +
                     "History builds up from the moment Tavern Tracker starts saving the leaderboard, so it grows the longer the app runs."))));
            return;
        }

        var prof = Board.Profile(_openName, DateTime.UtcNow);
        if (prof == null)
        {
            p.Children.Add(CardBox(Stack(Orientation.Vertical, 8,
                T(Names.Display(_openName), 18, Text, FontWeights.SemiBold),
                Hint($"Not on the {Region} {(Duos ? "duos" : "solo")} leaderboard, and not seen on it since the app started saving it."))));
            return;
        }

        var head = Stack(Orientation.Horizontal, 10, Heading(prof.Name),
            Pill($"{prof.Region} · {(prof.Duos ? "Duos" : "Solo")}", Muted, Line));
        if (prof.Rank == 0) ((Panel)head).Children.Add(Pill("Dropped off the board", Amber, GoldSoft));
        p.Children.Add(head);

        if (prof.SameNameCount > 1)
            p.Children.Add(Hint($"{prof.SameNameCount} players on this leaderboard use this name. The leaderboard doesn't show BattleTag numbers, so these stats follow the highest-ranked one."));

        var stats = Columns(Star(), Star(), Star(), Star(), Star(), Star());
        stats.Children.Add(Stat("Rating", T(prof.Rating > 0 ? N(prof.Rating) : "—", 24, Gold, FontWeights.Bold)).At(0));
        stats.Children.Add(Stat("Rank", T(prof.Rank > 0 ? $"#{N(prof.Rank)}" : "—", 24, Text, FontWeights.Bold)).At(1));
        stats.Children.Add(Stat("Peak seen", T(N(prof.Peak), 20, Text, FontWeights.SemiBold)).At(2));
        stats.Children.Add(Stat("Last 24 h", Change(prof.Change24h, 20)).At(3));
        stats.Children.Add(Stat("Last 7 days", Change(prof.Change7d, 20)).At(4));
        stats.Children.Add(Stat("Tracked since", T(prof.FirstSeenUtc.HasValue ? prof.FirstSeenUtc.Value.ToLocalTime().ToString("d MMM", Num) : "—", 20, Text, FontWeights.SemiBold)).At(5));
        p.Children.Add(CardBox(stats));

        var chart = new RatingChart { Height = 260 };
        chart.SetPoints(prof.History);
        p.Children.Add(CardBox(Stack(Orientation.Vertical, 12, Caption("Rating history (this season)"), chart)));

        if (prof.History.Count > 1)
        {
            var changes = Stack(Orientation.Vertical, 4);
            var list = prof.History.ToList();
            for (int i = list.Count - 1; i >= Math.Max(1, list.Count - 12); i--)
            {
                var row = Columns(Px(140), Px(80), Px(70), Star());
                row.Children.Add(T(list[i].Utc.ToLocalTime().ToString("ddd d MMM HH:mm", Num), 12.5, Muted).At(0));
                row.Children.Add(T(N(list[i].Rating), 13, Text, FontWeights.SemiBold).At(1));
                row.Children.Add(Change(list[i].Rating - list[i - 1].Rating, 13).At(2));
                row.Children.Add(T($"#{N(list[i].Rank)}", 12.5, Faint).At(3));
                changes.Children.Add(row);
            }
            p.Children.Add(CardBox(Stack(Orientation.Vertical, 10, Caption("Recent rating changes"), changes)));
        }
    }

    private static TextBlock Hint(string text)
    {
        var t = T(text, 13, Muted);
        t.TextWrapping = TextWrapping.Wrap;
        t.TextTrimming = TextTrimming.None;
        return t;
    }
}
