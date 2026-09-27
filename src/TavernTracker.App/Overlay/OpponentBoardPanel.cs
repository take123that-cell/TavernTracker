using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using TavernTracker.Core;
using TavernTracker.Core.Hearthstone;
using static TavernTracker.App.Overlay.OverlayStyle;
using static TavernTracker.App.Theme;

namespace TavernTracker.App.Overlay;

/// <summary>
/// HDT's "last known board": hover an opponent's portrait on the leaderboard and their board from the
/// last time you fought them appears at the top of the screen, with their tier and how long ago it was.
/// </summary>
public sealed class OpponentBoardPanel : Border
{
    private static readonly Brush PanelTop = Frozen(Color.FromArgb(0xF2, 0x3A, 0x26, 0x14));
    private static readonly Brush PanelBottom = Frozen(Color.FromArgb(0xF2, 0x1E, 0x13, 0x0A));
    private static readonly Brush PanelEdge = Frozen(Color.FromRgb(0x8A, 0x62, 0x2A));
    private static readonly Brush AttackFill = Frozen(Color.FromRgb(0xE8, 0xA3, 0x17));
    private static readonly Brush HealthFill = Frozen(Color.FromRgb(0xC7, 0x2C, 0x25));
    private static readonly Brush Ring = Frozen(Color.FromRgb(0x5A, 0x4E, 0x3C));
    private static readonly Brush GoldRing = Frozen(Color.FromRgb(0xFF, 0xC9, 0x3C));
    private static readonly Brush ShieldGlow = Frozen(Color.FromArgb(0xB0, 0xFF, 0xE8, 0x7A));
    private static readonly Brush RebornRing = Frozen(Color.FromArgb(0xD0, 0x6F, 0xE8, 0xFF));
    private static readonly Brush TauntFill = Frozen(Color.FromRgb(0x7D, 0x74, 0x66));

    private readonly TrackerEngine _engine;
    private readonly TextBlock _title = Small("", Brushes.White, 15, bold: true);
    private readonly TextBlock _age = Small("", Frozen(Color.FromRgb(0xDC, 0xDD, 0xDE)), 13);
    private readonly StackPanel _board = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _empty = Small("", Frozen(Color.FromRgb(0xDC, 0xDD, 0xDE)), 16);
    private string _signature = "";

    public OpponentBoardPanel(TrackerEngine engine)
    {
        _engine = engine;
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;
        Background = new LinearGradientBrush(((SolidColorBrush)PanelTop).Color, ((SolidColorBrush)PanelBottom).Color, 90);
        BorderBrush = PanelEdge;
        BorderThickness = new Thickness(2, 0, 2, 2);
        CornerRadius = new CornerRadius(0, 0, 6, 6);
        MinWidth = 740;
        Padding = new Thickness(14, 6, 14, 8);

        _title.HorizontalAlignment = HorizontalAlignment.Center;
        _title.FontFamily = Display;
        _title.Foreground = HsGold;
        _title.Effect = TextEdge();
        _age.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.Margin = new Thickness(40, 36, 40, 36);

        var stack = new StackPanel();
        stack.Children.Add(_title);
        stack.Children.Add(_board);
        stack.Children.Add(_empty);
        stack.Children.Add(_age);
        Child = stack;
    }

    /// <summary>Shows the board for this hero (or hides the panel when hero is null).</summary>
    public void Update(BgGame? game, BgHero? hero)
    {
        if (game == null || hero == null || hero.IsMine)
        {
            if (Visibility == Visibility.Visible) Visibility = Visibility.Collapsed;
            _signature = "";
            return;
        }

        var seen = _engine.LastSeenBoard(game.Id, hero.CardId);
        var sig = $"{hero.CardId}|{seen?.Turn}|{game.Turn}|{hero.TavernTier}|{hero.IsDead}";
        if (sig != _signature)
        {
            bool fresh = !_signature.StartsWith(hero.CardId + "|", StringComparison.Ordinal);
            _signature = sig;
            Build(game, hero, seen);
            if (fresh) BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        }
        Visibility = Visibility.Visible;
    }

    private void Build(BgGame game, BgHero hero, SeenBoard? seen)
    {
        var name = hero.Name.Length > 0 ? hero.Name
            : seen?.HeroName is { Length: > 0 } n ? n
            : _engine.Cards.NameOf(_engine.Cards.NormalizeHero(hero.CardId)) ?? "Opponent";
        int tier = hero.TavernTier > 0 ? hero.TavernTier : seen?.TavernTier ?? 0;
        _title.Text = tier > 0 ? $"{name}  ·  Tavern Tier {tier}" : name;

        _board.Children.Clear();
        if (seen == null)
        {
            _empty.Text = "You haven't fought this player yet.";
            _empty.Visibility = Visibility.Visible;
            _age.Text = "";
            return;
        }
        if (seen.Minions.Count == 0)
        {
            _empty.Text = "No minions on their board last time.";
            _empty.Visibility = Visibility.Visible;
        }
        else
        {
            _empty.Visibility = Visibility.Collapsed;
            foreach (var m in seen.Minions) _board.Children.Add(Token(m));
        }
        int ago = Math.Max(0, game.Turn - seen.Turn);
        _age.Text = ago == 0 ? $"Board from this turn (turn {seen.Turn})"
            : $"Last seen on turn {seen.Turn} · {ago} turn{(ago == 1 ? "" : "s")} ago";
    }

    /// <summary>A minion like HDT's BattlegroundsMinion: oval portrait, attack and health gems, keyword marks.</summary>
    private FrameworkElement Token(SeenMinion m)
    {
        const double w = 92, h = 108;
        var g = new Grid { Width = w + 10, Height = h + 14, Margin = new Thickness(0, 10, -4, 4) };

        if (m.Taunt)
        {
            g.Children.Add(new Path
            {
                Data = Geometry.Parse("M 50,0 L 100,12 C 100,70 82,100 50,118 C 18,100 0,70 0,12 Z"),
                Fill = TauntFill,
                Stroke = Brushes.Black,
                StrokeThickness = 1.5,
                Stretch = Stretch.Fill,
                Margin = new Thickness(0, 0, 0, 2),
            });
        }

        var oval = new Grid { Width = w, Height = h, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var art = new Image { Stretch = Stretch.UniformToFill, Clip = new EllipseGeometry(new Point(w / 2, h / 2), w / 2, h / 2) };
        RenderOptions.SetBitmapScalingMode(art, BitmapScalingMode.HighQuality);
        oval.Children.Add(new Ellipse { Fill = Frozen(Color.FromRgb(0x2B, 0x2B, 0x2B)) });
        oval.Children.Add(art);
        SetImage(art, "art:" + m.CardId, async () => await _engine.Tiles.GetArtAsync(m.CardId) ?? await _engine.Tiles.GetAsync(m.CardId));
        oval.Children.Add(new Ellipse { Stroke = m.Golden ? GoldRing : Ring, StrokeThickness = m.Golden ? 5 : 3.5 });
        if (m.Reborn) oval.Children.Add(new Ellipse { Stroke = RebornRing, StrokeThickness = 3, Margin = new Thickness(-3) });
        if (m.DivineShield)
        {
            oval.Children.Add(new Ellipse
            {
                Stroke = ShieldGlow,
                StrokeThickness = 6,
                Margin = new Thickness(-5),
                Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 4 },
            });
        }
        if (m.Stealth) oval.Opacity = 0.75;
        g.Children.Add(oval);

        g.Children.Add(Gem(m.Attack, AttackFill, HorizontalAlignment.Left));
        g.Children.Add(Gem(m.Health, HealthFill, HorizontalAlignment.Right));

        var marks = new List<string>();
        if (m.Venomous) marks.Add("☠");
        if (m.Windfury) marks.Add("≋");
        if (marks.Count > 0)
        {
            var badge = new Border
            {
                Background = Frozen(Color.FromArgb(0xE0, 0x14, 0x16, 0x17)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(6, 0, 6, 1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Child = Small(string.Join(" ", marks), m.Venomous ? Frozen(Color.FromRgb(0x7C, 0xE0, 0x5A)) : Brushes.White, 13, bold: true),
            };
            g.Children.Add(badge);
        }
        return g;
    }

    private static FrameworkElement Gem(int value, Brush fill, HorizontalAlignment side)
    {
        var gem = new Grid { Width = 34, Height = 34, HorizontalAlignment = side, VerticalAlignment = VerticalAlignment.Bottom };
        gem.Children.Add(new Ellipse { Fill = fill, Stroke = Brushes.Black, StrokeThickness = 2 });
        gem.Children.Add(new TextBlock
        {
            Text = value.ToString(Num),
            Foreground = Brushes.White,
            FontWeight = FontWeights.Black,
            FontSize = value >= 100 ? 13 : 18,
            FontFamily = Font,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = TextEdge(),
        });
        return gem;
    }
}
