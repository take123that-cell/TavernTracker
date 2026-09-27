using System.Windows;
using System.Windows.Controls;
using TavernTracker.App.Controls;
using TavernTracker.Core;
using TavernTracker.Core.Leaderboard;
using static TavernTracker.App.Theme;

namespace TavernTracker.App.Pages;

/// <summary>Browse the official leaderboard with each player's change over the last 24 hours.</summary>
public sealed class LeaderboardPage : IPage
{
    private const int PageSize = 100;

    private readonly TrackerEngine _engine;
    private readonly MainWindow _main;
    private readonly DockPanel _root = new();
    private readonly Segmented _region;
    private readonly Segmented _mode;
    private readonly TextBox _filter = Input(hint: "Filter by name");
    private readonly StackPanel _rows = Stack(Orientation.Vertical, 1);
    private readonly TextBlock _info = T("", 12.5, Muted);
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private int _shown = PageSize;

    public string Title => "Leaderboard";
    public UIElement View => _root;

    private bool Duos => _mode.Selected == "duos";

    public LeaderboardPage(TrackerEngine engine, MainWindow main)
    {
        _engine = engine;
        _main = main;
        _region = new Segmented(LeaderboardClient.Regions.Select(r => (r, r)), engine.Settings.Region);
        _mode = new Segmented(new[] { ("solo", "Solo"), ("duos", "Duos") }, "solo");
        _region.Changed += _ => { _shown = PageSize; Refresh(); };
        _mode.Changed += _ => { _shown = PageSize; Refresh(); };
        _filter.TextChanged += (_, _) => { _shown = PageSize; Refresh(); };
        _filter.Width = 240;

        var controls = Columns(Auto, Px(10), Auto, Px(10), Auto, Star(), Auto);
        controls.Children.Add(_region.At(0));
        controls.Children.Add(_mode.At(2));
        controls.Children.Add(_filter.At(4));
        controls.Children.Add(Btn("Refresh now", () =>
        {
            _info.Text = "Downloading…";
            _ = _engine.Snapshots.RefreshAsync(_region.Selected, Duos);
        }).At(6));

        var top = Stack(Orientation.Vertical, 14, Heading("Leaderboard"), controls, _info);
        top.Margin = new Thickness(0, 0, 0, 12);
        DockPanel.SetDock(top, Dock.Top);
        _root.Children.Add(top);

        var header = RowGrid();
        header.Margin = new Thickness(12, 0, 12, 6);
        header.Children.Add(Caption("Rank").At(0));
        header.Children.Add(Caption("Player").At(1));
        header.Children.Add(Caption("Rating").At(2));
        header.Children.Add(Caption("24 h").At(3));
        DockPanel.SetDock(header, Dock.Top);
        _root.Children.Add(header);

        _scroll.Content = _rows;
        _root.Children.Add(_scroll);
    }

    public void Refresh()
    {
        var board = _engine.Snapshots.Board(_region.Selected, Duos);
        _rows.Children.Clear();
        if (!board.HasData)
        {
            _info.Text = "Downloading this leaderboard…";
            _ = _engine.Snapshots.EnsureFreshAsync(_region.Selected, Duos, TimeSpan.FromMinutes(30));
            return;
        }

        var rows = board.Rows(DateTime.UtcNow, _filter.Text.Trim(), _shown + 1);
        _info.Text = $"{N(board.Count)} players · season {board.SeasonId} · updated {Ago(board.FetchedUtc)} · " +
                     $"players below {N(board.Cutoff)} aren't listed by Blizzard. 24 h change appears once the app has a day of history.";

        var myKey = Names.Key(_engine.Settings.BattleTag);
        foreach (var r in rows.Take(_shown))
        {
            bool me = myKey.Length > 0 && Names.Key(r.Name) == myKey;
            var g = RowGrid();
            g.Children.Add(T($"#{N(r.Rank)}", 13, r.Rank <= 3 ? Gold : Muted, FontWeights.SemiBold).At(0));
            g.Children.Add(T(r.Name, 14, me ? Gold : Text, FontWeights.SemiBold).At(1));
            g.Children.Add(T(N(r.Rating), 14, Text, FontWeights.SemiBold).At(2));
            g.Children.Add(Change(r.Change24h, 13).At(3));
            var name = r.Name;
            var item = Clickable(g, () => _main.ShowPlayer(name, _region.Selected, Duos),
                me ? GoldSoft : Card, CardHover, padY: 8);
            _rows.Children.Add(item);
        }

        if (rows.Count > _shown)
        {
            var more = Btn($"Show {PageSize} more", () => { _shown += PageSize; Refresh(); });
            more.Margin = new Thickness(0, 10, 0, 10);
            more.HorizontalAlignment = HorizontalAlignment.Center;
            _rows.Children.Add(more);
        }
        else if (rows.Count == 0)
        {
            _rows.Children.Add(T("No players match.", 13, Muted));
        }
    }

    private static Grid RowGrid() => Columns(Px(80), Star(), Px(110), Px(90));
}
