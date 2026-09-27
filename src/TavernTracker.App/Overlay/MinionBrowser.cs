using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TavernTracker.Core;
using TavernTracker.Core.Cards;
using TavernTracker.Core.Hearthstone;
using static TavernTracker.App.Overlay.OverlayStyle;
using static TavernTracker.App.Theme;

namespace TavernTracker.App.Overlay;

/// <summary>
/// HDT's Battlegrounds minion browser: tier emblems along the top right (tier 7 only when you can reach it),
/// plus a filter button. Clicking a tier lists its cards grouped by tribe (a two-tribe minion is listed
/// under both, neutrals and spells last); the filter lists one tribe or one keyword across all tiers.
/// Only cards this lobby can offer are shown. Hover a card for its picture; right-click hides it.
/// </summary>
public sealed class MinionBrowser : StackPanel
{
    private const double ListWidth = 216;
    private const string NeutralKey = "NEUTRAL";
    private const string SpellKey = "SPELL";

    private readonly TrackerEngine _engine;
    private readonly StackPanel _strip = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly StackPanel _tierStrip = new() { Orientation = Orientation.Horizontal };
    private readonly Dictionary<int, Border> _tierButtons = new();
    private readonly Border _filterButton;
    private readonly Border _filterBox;
    private readonly StackPanel _list = new();
    private readonly ScrollViewer _scroll;
    private readonly Border _listBox;
    private readonly Grid _body = new() { HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Canvas _previewLayer = new() { Width = 0, ClipToBounds = false };
    private readonly Border _preview;
    private readonly Image _previewImage = new() { Width = 190, Stretch = Stretch.Uniform };
    private readonly TextBlock _previewText = Small("", Brushes.White, 12);
    private readonly TextBlock _turn = Small("", Brushes.White, 15, bold: true);

    private int _tier;                 // 0 = no tier open
    private string? _tribe;            // one tribe (or Neutral / Spells) across all tiers
    private string? _keyword;          // one keyword across all tiers
    private bool _filterOpen;
    private string _signature = "";
    private bool _duos;
    private int _myTier;
    private HashSet<int> _availableTiers = new() { 1, 2, 3, 4, 5, 6 };
    private bool _showTier7;

    public MinionBrowser(TrackerEngine engine)
    {
        _engine = engine;
        HorizontalAlignment = HorizontalAlignment.Right;

        var turnBox = new Border
        {
            Background = PanelBg,
            BorderBrush = GroupEdge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(0, 4, 6, 4),
            Child = _turn,
        };
        _strip.Children.Add(turnBox);
        _strip.Children.Add(_tierStrip);
        for (int t = 1; t <= 7; t++)
        {
            int tier = t;
            var b = new Border
            {
                Child = TierIcon(tier, 34),
                Padding = new Thickness(2, 2, 2, 2),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                ToolTip = $"Tavern Tier {tier}",
            };
            b.MouseLeftButtonUp += (_, e) => { SelectTier(tier); e.Handled = true; };
            b.MouseEnter += (_, _) => { if (_tier != tier) b.Child.Opacity = 1; };
            b.MouseLeave += (_, _) => StyleTierButtons();
            _tierButtons[tier] = b;
            _tierStrip.Children.Add(b);
        }
        _filterButton = new Border
        {
            Width = 34,
            Height = 34,
            Margin = new Thickness(4, 4, 0, 4),
            CornerRadius = new CornerRadius(4),
            Background = PanelBg,
            BorderBrush = GroupEdge,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = "Filter by tribe or keyword",
            Child = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 0,0 L 16,0 L 10,7 L 10,14 L 6,12 L 6,7 Z"),
                Fill = Brushes.White,
                Stretch = Stretch.Uniform,
                Width = 16,
                Height = 16,
            },
        };
        _filterButton.MouseLeftButtonUp += (_, e) => { _filterOpen = !_filterOpen; Rebuild(); e.Handled = true; };
        _strip.Children.Add(_filterButton);
        Children.Add(_strip);

        _filterBox = new Border
        {
            Background = PanelBg,
            BorderBrush = GroupEdge,
            BorderThickness = new Thickness(1),
            Width = 176,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            Padding = new Thickness(6),
            Margin = new Thickness(0, 5, 6, 0),
            ToolTip = "Right-click a card in the list to hide it",
        };

        _scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, MaxHeight = 700, CanContentScroll = false };
        _listBox = new Border { Width = ListWidth + 4, Child = _scroll, Visibility = Visibility.Collapsed };

        // Card picture on hover, to the left of the list (like HDT's card tooltip).
        _previewText.TextWrapping = TextWrapping.Wrap;
        _previewText.TextTrimming = TextTrimming.None;
        _preview = new Border
        {
            Width = 196,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Child = Stack(Orientation.Vertical, 0, _previewImage, new Border
            {
                Background = HeaderBg,
                Padding = new Thickness(8, 6, 8, 6),
                Child = _previewText,
            }),
        };
        _previewLayer.Children.Add(_preview);

        // Filters open to the LEFT of the list, so the cards stay right under the tier shields.
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _body.Children.Add(_previewLayer);
        Grid.SetColumn(_filterBox, 1);
        _body.Children.Add(_filterBox);
        Grid.SetColumn(_listBox, 2);
        _body.Children.Add(_listBox);
        Children.Add(_body);

        _engine.Cards.Loaded += () => Dispatcher.BeginInvoke(() => { _signature = ""; });
        StyleTierButtons();
    }

    public void Update(BgGame? game)
    {
        _turn.Text = game != null ? $"Turn {Math.Max(1, game.Turn)}" : "";
        _duos = game?.IsDuos ?? false;
        _myTier = game?.MyHero?.TavernTier ?? 0;
        var anomaly = game != null && game.AnomalyDbfId > 0 ? _engine.Cards.IdOf(game.AnomalyDbfId) : null;
        _availableTiers = AvailableTiers(anomaly);
        _showTier7 = _engine.Settings.AlwaysShowTier7 || _myTier >= 7 || _availableTiers.Contains(7);

        var races = _engine.LobbyRaces();
        var sig = $"{string.Join(",", races)}|{_duos}|{_engine.Cards.Ready}|{_engine.Cards.UpdatedUtc.Ticks}|{_engine.Cards.CorrectionCount}|{anomaly}|{_showTier7}|{_engine.Settings.HiddenCards.Count}";
        if (sig != _signature)
        {
            _signature = sig;
            if (!_showTier7 && _tier == 7) _tier = 0;
            _tierButtons[7].Visibility = _showTier7 ? Visibility.Visible : Visibility.Collapsed;
            // Fetch every card's art now so opening a tier is instant.
            if (_engine.Cards.Ready) _engine.Tiles.Prefetch(Pool().Select(c => c.Id));
            Rebuild(keepScroll: true);
        }
    }

    /// <summary>Closes everything when a new game starts.</summary>
    public void Reset()
    {
        _tier = 0;
        _tribe = null;
        _keyword = null;
        _filterOpen = false;
        _signature = "";
        Rebuild();
    }

    /// <summary>Anomalies that change which tiers exist (same list as HDT's BattlegroundsUtils).</summary>
    private static HashSet<int> AvailableTiers(string? anomaly) => anomaly switch
    {
        "BG27_Anomaly_100" => new() { 3, 4, 5, 6 },          // Big League
        "BG27_Anomaly_102" => new() { 2, 4, 6 },             // How to EVEN??
        "BG27_Anomaly_800" => new() { 1, 2, 3, 4 },          // Little League
        "BG27_Anomaly_504" => new() { 1, 2, 3, 4, 5, 6, 7 }, // Secrets of Norgannon
        "BG27_Anomaly_556" => new() { 2, 3, 4, 5, 6 },       // Valuation Inflation
        "BG27_Anomaly_101" => new() { 1, 3, 5 },             // What Are The Odds??
        _ => new() { 1, 2, 3, 4, 5, 6 },
    };

    // ------------------------------------------------------------------ interaction

    private void SelectTier(int tier)
    {
        _tier = _tier == tier ? 0 : tier;
        _tribe = null;
        _keyword = null;
        _filterOpen = false;
        Rebuild();
    }

    private void SelectTribe(string key)
    {
        _tribe = _tribe == key ? null : key;
        _keyword = null;
        _tier = 0;
        Rebuild();
    }

    private void SelectKeyword(string label)
    {
        _keyword = _keyword == label ? null : label;
        _tribe = null;
        _tier = 0;
        Rebuild();
    }

    private void ToggleHidden(BgCard card)
    {
        var hidden = _engine.Settings.HiddenCards;
        if (!hidden.Remove(card.Id)) hidden.Add(card.Id);
        _engine.Settings.Save();
        Rebuild(keepScroll: true);
    }

    private IReadOnlyList<BgCard> Pool()
    {
        var races = _engine.LobbyRaces().Select(Races.Key).ToHashSet();
        var hidden = _engine.Settings.HiddenCards.ToHashSet();
        return _engine.Cards.Pool(races.Count > 0 ? races : null, _duos)
            .Where(c => !hidden.Contains(c.Id))
            .Where(c => c.Tier <= 6 || _showTier7)
            .ToList();
    }

    private void StyleTierButtons()
    {
        bool filtering = _tribe != null || _keyword != null;
        foreach (var (tier, b) in _tierButtons)
        {
            bool active = tier == _tier;
            b.Child.Opacity = active ? 1.0 : (_tier != 0 || filtering) ? 0.45 : _availableTiers.Contains(tier) ? 0.92 : 0.35;
            b.Effect = active ? new System.Windows.Media.Effects.DropShadowEffect { Color = Color.FromRgb(0xFF, 0xD7, 0x5E), BlurRadius = 14, ShadowDepth = 0, Opacity = 0.95 } : null;
        }
        _filterButton.Background = _filterOpen || filtering ? GroupHeaderBg : PanelBg;
    }

    private void Rebuild(bool keepScroll = false)
    {
        double offset = _scroll.VerticalOffset;
        StyleTierButtons();
        BuildFilter();
        _filterBox.Visibility = _filterOpen ? Visibility.Visible : Visibility.Collapsed;

        _list.Children.Clear();
        ShowPreview(null, null);
        bool filtering = _tribe != null || _keyword != null;
        if (_tier == 0 && !filtering)
        {
            _listBox.Visibility = Visibility.Collapsed;
            return;
        }
        if (_listBox.Visibility != Visibility.Visible)
        {
            _listBox.Visibility = Visibility.Visible;
            _listBox.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        }

        if (!_engine.Cards.Ready)
        {
            _list.Children.Add(Group("Loading card data…", null, Array.Empty<BgCard>(), false));
            return;
        }

        var pool = Pool();
        if (_tier > 0)
        {
            bool tierAvailable = _availableTiers.Contains(_tier) || _tier == 7;
            var cards = pool.Where(c => c.Tier == _tier).ToList();
            var groups = new List<(int Order, string Title, List<BgCard> Cards)>();
            // A two-tribe minion is listed under each of its tribes that this lobby has.
            var lobby = _engine.LobbyRaces().Select(Races.Key).ToHashSet();
            foreach (var key in cards.Where(c => !c.IsSpell).SelectMany(c => c.Races).Where(r => r != "ALL" && (lobby.Count == 0 || lobby.Contains(r))).Distinct())
                groups.Add((1, TribePlural(key), cards.Where(c => !c.IsSpell && c.Races.Contains(key)).ToList()));
            var all = cards.Where(c => !c.IsSpell && c.Races.Contains("ALL")).ToList();
            if (all.Count > 0) groups.Add((0, "All Tribes", all));
            var neutral = cards.Where(c => !c.IsSpell && c.Races.Count == 0).ToList();
            if (neutral.Count > 0) groups.Add((2, "Neutral", neutral));
            var spells = cards.Where(c => c.IsSpell).OrderBy(c => c.Cost).ThenBy(c => c.Name).ToList();
            if (spells.Count > 0) groups.Add((3, "Spells", spells));

            foreach (var g in groups.OrderBy(g => g.Order).ThenBy(g => g.Title))
                _list.Children.Add(Group(g.Title, $"Tier {_tier}", g.Order == 3 ? g.Cards : g.Cards.OrderBy(c => c.Name).ToList(), darkened: !tierAvailable));
            if (groups.Count == 0) _list.Children.Add(Group($"Tavern Tier {_tier}", null, Array.Empty<BgCard>(), false, "Nothing in this tier for this lobby."));
        }
        else
        {
            var matches = pool.Where(c => _keyword != null ? Keywords.Has(c, _keyword) : MatchesTribe(c, _tribe!)).ToList();
            var subtitle = _keyword ?? TribePlural(_tribe!);
            foreach (var g in matches.GroupBy(c => c.Tier).OrderBy(g => g.Key))
                _list.Children.Add(Group($"Tavern Tier {g.Key}", subtitle,
                    g.OrderBy(c => c.IsSpell ? 1 : 0).ThenBy(c => c.Name).ToList(), darkened: !_availableTiers.Contains(g.Key) && g.Key != 7));
            if (matches.Count == 0) _list.Children.Add(Group(subtitle, null, Array.Empty<BgCard>(), false, "Nothing matches in this lobby."));
        }
        if (keepScroll) _scroll.ScrollToVerticalOffset(offset);
        else _scroll.ScrollToTop();
    }

    private static bool MatchesTribe(BgCard c, string key) => key switch
    {
        NeutralKey => !c.IsSpell && c.Races.Count == 0,
        SpellKey => c.IsSpell,
        // Like HDT: a tribe's list includes the minions that count as every tribe.
        _ => !c.IsSpell && (c.Races.Contains(key) || c.Races.Contains("ALL")),
    };

    private static string TribePlural(string key) => key switch
    {
        NeutralKey => "Neutral",
        SpellKey => "Spells",
        _ => Races.PluralForKey(key),
    };

    // ------------------------------------------------------------------ filter panel

    private void BuildFilter()
    {
        var box = new StackPanel();
        box.Children.Add(FilterTitle("Minion Types"));
        // This lobby's tribes only (once known), then neutrals and spells.
        var lobby = _engine.LobbyRaces().Select(Races.Key).ToList();
        var keys = (lobby.Count > 0 ? lobby : Races.BattlegroundsKeys.ToList()).Where(k => k != "ALL").OrderBy(TribePlural).ToList();
        keys.Add(NeutralKey);
        keys.Add(SpellKey);
        var tribes = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        foreach (var key in keys)
        {
            var k = key;
            tribes.Children.Add(FilterItem(TribePlural(k), _tribe == k, TribeBrush(k), () => SelectTribe(k)));
        }
        box.Children.Add(tribes);
        if (lobby.Count == 0)
        {
            var note = Small("Lobby tribes not known yet", Muted, 10);
            note.Margin = new Thickness(2, 2, 0, 0);
            box.Children.Add(note);
        }

        // Only keywords some card in this lobby has (like HDT).
        var pool = _engine.Cards.Ready ? Pool() : Array.Empty<BgCard>();
        box.Children.Add(FilterTitle("Mechanics"));
        var mech = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        foreach (var (label, _, _) in Keywords.All)
        {
            if (pool.Count > 0 && !pool.Any(c => Keywords.Has(c, label))) continue;
            var l = label;
            mech.Children.Add(FilterItem(l, _keyword == l, GroupHeaderBg, () => SelectKeyword(l)));
        }
        box.Children.Add(mech);

        int hidden = _engine.Settings.HiddenCards.Count;
        if (hidden > 0)
        {
            var reset = Clickable(Small($"Show {hidden} hidden card{(hidden == 1 ? "" : "s")}", Blue, 10.5, bold: true), () =>
            {
                _engine.Settings.HiddenCards.Clear();
                _engine.Settings.Save();
                Rebuild(keepScroll: true);
            }, padX: 2, padY: 1);
            reset.HorizontalAlignment = HorizontalAlignment.Left;
            reset.Margin = new Thickness(0, 4, 0, 0);
            box.Children.Add(reset);
        }
        _filterBox.Child = box;
    }

    private static TextBlock FilterTitle(string text)
    {
        var t = Small(text, HsGold, 11, bold: true);
        t.FontFamily = Display;
        t.Margin = new Thickness(2, 2, 0, 3);
        return t;
    }

    /// <summary>A compact filter button; the selected one is lit in its colour.</summary>
    private static Border FilterItem(string text, bool on, Brush onBrush, Action click)
    {
        var label = Small(text, on ? Brushes.White : Frozen(Color.FromRgb(0xE6, 0xD6, 0xB8)), 10.5, bold: on);
        var item = new Border
        {
            Background = on ? onBrush : RowAlt,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 2, 5, 2),
            Margin = new Thickness(1.5),
            Cursor = Cursors.Hand,
            BorderBrush = on ? HsGold : Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Child = label,
        };
        item.MouseLeftButtonUp += (_, e) => { click(); e.Handled = true; };
        return item;
    }

    // ------------------------------------------------------------------ pieces

    /// <summary>A group box like HDT's: blue header with the title (and a small tab on the right), then card tiles.</summary>
    private UIElement Group(string title, string? tab, IReadOnlyList<BgCard> cards, bool darkened, string? empty = null)
    {
        var header = new DockPanel { Background = GroupHeaderBg };
        if (tab != null)
        {
            var t = new Border
            {
                Background = Frozen(Color.FromRgb(0x2B, 0x1C, 0x10)),
                BorderBrush = GroupEdge,
                BorderThickness = new Thickness(1, 1, 0, 0),
                Padding = new Thickness(8, 1, 8, 1),
                VerticalAlignment = VerticalAlignment.Bottom,
                Child = Small(tab, Frozen(Color.FromRgb(0xDC, 0xDD, 0xDE)), 10, bold: true),
            };
            DockPanel.SetDock(t, Dock.Right);
            header.Children.Add(t);
        }
        var name = Small(title, HsGold, 14, bold: true);
        name.FontFamily = Display;
        name.Margin = new Thickness(7, 3, 7, 3);
        name.Effect = TextEdge();
        header.Children.Add(name);

        var stack = new StackPanel();
        stack.Children.Add(new Border { Child = header, BorderBrush = GroupEdge, BorderThickness = new Thickness(0, 0, 0, 1) });
        foreach (var c in cards) stack.Children.Add(CardRow(c, _engine.Tiles, ShowPreview, ToggleHidden, darkened, ListWidth));
        if (empty != null)
        {
            var e = Small(empty, Muted, 12);
            e.Margin = new Thickness(8, 6, 8, 6);
            e.TextWrapping = TextWrapping.Wrap;
            stack.Children.Add(e);
        }
        return new Border
        {
            Child = stack,
            Background = PanelBg,
            BorderBrush = GroupEdge,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 5, 0, 0),
            Width = ListWidth + 2,
        };
    }

    private void ShowPreview(BgCard? card, FrameworkElement? row)
    {
        if (card == null || row == null)
        {
            _preview.Visibility = Visibility.Collapsed;
            return;
        }
        var tribes = card.IsSpell ? "Tavern spell" : card.Races.Count == 0 ? "Neutral" : string.Join(" / ", card.Races.Select(r => r == "ALL" ? "All tribes" : Races.LabelForKey(r)));
        _previewText.Text = card.IsSpell
            ? $"{card.Name} · Tier {card.Tier} · {card.Cost} gold\n{card.PlainText}"
            : $"{card.Name} · {card.Attack}/{card.Health} · Tier {card.Tier}\n{tribes}\n{card.PlainText}";
        _previewImage.Source = null;
        SetImage(_previewImage, "render:" + card.Id, () => _engine.Tiles.GetRenderAsync(card.Id));

        // Line the picture up with the hovered row, to the left of the list.
        double y = 0;
        try { y = row.TranslatePoint(new Point(0, 0), _body).Y; } catch { /* not in the tree yet */ }
        Canvas.SetLeft(_preview, -_preview.Width - 6);
        Canvas.SetTop(_preview, Math.Max(0, y - 90));
        _preview.Visibility = Visibility.Visible;
    }
}
