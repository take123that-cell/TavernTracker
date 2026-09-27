using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TavernTracker.Core;
using TavernTracker.Core.Hearthstone;
using TavernTracker.Core.Stats;
using static TavernTracker.App.Overlay.OverlayStyle;
using static TavernTracker.App.Theme;

namespace TavernTracker.App.Overlay;

/// <summary>
/// Stats above each card of a hero, trinket or quest choice, laid out like HDT's pick overlays:
/// Avg Placement · Tier · Pick Rate (or Completion for quests) above every option, and for quests the
/// three best tribes for the reward underneath, with an X on tribes this lobby doesn't have.
/// Positions follow HDT's layouts (designed at 1080p and centred on the game), scaled to the game window.
/// Everything here lets clicks through to the game.
/// </summary>
public sealed class ChoiceStatsLayer : Canvas
{
    private static readonly Brush Purple = Frozen(Color.FromRgb(0x6A, 0x3A, 0x14));
    private static readonly Brush Black = Frozen(Color.FromArgb(0xF0, 0x1E, 0x13, 0x0A));
    private static readonly Brush BarOn = Frozen(Color.FromRgb(0x6A, 0x9D, 0x36));
    private static readonly Brush BarOff = Frozen(Color.FromRgb(0x55, 0x55, 0x55));
    private static readonly Brush BarTrack = Frozen(Color.FromRgb(0x2A, 0x2D, 0x30));

    private readonly TrackerEngine _engine;
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _slide = new();
    private readonly Canvas _content = new();
    private string _signature = "";
    private ChoiceStats? _last;
    private readonly List<(FrameworkElement Element, double X, double Y, bool CenterX)> _placed = new();

    public ChoiceStatsLayer(TrackerEngine engine)
    {
        _engine = engine;
        IsHitTestVisible = false;
        _content.RenderTransform = _slide;
        Children.Add(_content);
    }

    /// <summary>Shows (or hides) the stats. Width/height are the game area in overlay units.</summary>
    public void Update(ChoiceStats? stats, double width, double height)
    {
        if (stats == null)
        {
            if (_last != null)
            {
                _last = null;
                _signature = "";
                _content.Children.Clear();
                _placed.Clear();
            }
            Visibility = Visibility.Collapsed;
            return;
        }
        Visibility = Visibility.Visible;
        // The engine hands out the same object until something changes (the offer, the lobby's tribes,
        // stats finishing their download), so a new object means redraw.
        if (!ReferenceEquals(stats, _last))
        {
            bool fresh = stats.Signature != _signature;
            _last = stats;
            _signature = stats.Signature;
            Build(stats);
            if (fresh) Animate();
        }
        Place(width, height);
    }

    private void Animate()
    {
        _content.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        _slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-20, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = new QuadraticEase() });
    }

    // HDT's layouts, in 1080p units relative to the centre of the game area.
    private static (double Pitch, double OffsetX, double HeaderTop, double TribesTop) Layout(ChoiceKind kind) => kind switch
    {
        ChoiceKind.Hero => (340, 7, -255.5, double.NaN),
        ChoiceKind.Trinket => (277, 10, -297.5, double.NaN),
        _ => (385, -2.5, -363, 364), // quests
    };

    private void Build(ChoiceStats stats)
    {
        _content.Children.Clear();
        _placed.Clear();
        var (pitch, offsetX, headerTop, tribesTop) = Layout(stats.Kind);
        int n = stats.Options.Count;

        if (stats.Message != null)
        {
            AddCaption(stats.Message, 0, headerTop + 18);
            return;
        }

        for (int i = 0; i < n; i++)
        {
            double x = offsetX + (i - (n - 1) / 2.0) * pitch;
            var o = stats.Options[i];
            Add(Header(o, stats.Duos), x, headerTop);
            if (o != null && o.Note != null) Add(NoteLine(o.Note), x, headerTop + 64);
            if (!double.IsNaN(tribesTop) && o != null && o.Tribes.Count > 0) Add(TribeBox(o.Tribes), x, tribesTop);
        }
        AddCaption(stats.Caption, 0, headerTop - 24);
    }

    private void Add(FrameworkElement e, double x, double y, bool centerX = true)
    {
        _content.Children.Add(e);
        _placed.Add((e, x, y, centerX));
    }

    private void AddCaption(string text, double x, double y)
    {
        var t = Small(text, Frozen(Color.FromRgb(0xDC, 0xDD, 0xDE)), 11.5);
        var b = new Border { Background = Black, BorderBrush = Purple, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 3, 8, 3), Child = t };
        Add(b, x, y);
    }

    private void Place(double width, double height)
    {
        double s = height / 1080.0;
        _scale.ScaleX = _scale.ScaleY = s;
        double cx = width / 2, cy = height / 2;
        foreach (var (e, x, y, centerX) in _placed)
        {
            e.LayoutTransform = _scale;
            e.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double w = e.DesiredSize.Width;
            SetLeft(e, cx + x * s - (centerX ? w / 2 : 0));
            SetTop(e, cy + y * s);
        }
    }

    // ------------------------------------------------------------------ pieces

    /// <summary>243 × 60 header: Avg Placement | Tier | Pick Rate, HDT's colours and sizes.</summary>
    private static FrameworkElement Header(OptionStats? o, bool duos)
    {
        var grid = new Grid { Width = 243, Height = 60 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        string avg = o?.AvgPlacement is double a ? a.ToString("0.00", Num) : "–";
        grid.Children.Add(StatBox("Avg Placement", avg, o?.AvgPlacement is double v ? PlacementBrush(v, duos) : Brushes.White).At(0));
        var tier = TierBox(o?.Tier);
        Grid.SetColumn(tier, 1);
        grid.Children.Add(tier);
        string third = o?.Third is double t ? t.ToString("0.0", Num) + "%" : "–";
        grid.Children.Add(StatBox(o?.ThirdLabel ?? "Pick Rate", third, Brushes.White).At(2));
        if (o == null) grid.Opacity = 0.75;
        return grid;
    }

    private static Border StatBox(string label, string value, Brush valueBrush)
    {
        var top = new Border
        {
            Background = Purple,
            CornerRadius = new CornerRadius(4, 4, 0, 0),
            Height = 20,
            Child = new TextBlock { Text = label, Foreground = Brushes.White, FontSize = 12, FontFamily = Font, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
        };
        var val = new TextBlock
        {
            Text = value,
            Foreground = valueBrush,
            FontSize = 21,
            FontWeight = FontWeights.Black,
            FontFamily = new FontFamily("Segoe UI Black, Segoe UI"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = TextEdge(),
        };
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(val);
        return new Border { Background = Black, BorderBrush = Purple, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Child = dock };
    }

    private static Border TierBox(string? tier)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock { Text = "TIER", Foreground = Brushes.White, FontSize = 10, FontFamily = Font, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock
        {
            Text = tier ?? "–",
            Foreground = Brushes.White,
            FontSize = 28,
            FontWeight = FontWeights.Bold,
            FontFamily = Font,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, -6, 0, 0),
            Effect = TextEdge(),
        });
        var g = TierGradient(tier);
        return new Border
        {
            Background = g,
            BorderBrush = Frozen(Color.FromArgb(0x2E, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(4, 0, 4, 7),
            Child = stack,
        };
    }

    /// <summary>HDT's tier colours (StatsHeaderViewModel.TierGradient).</summary>
    private static Brush TierGradient(string? tier)
    {
        (byte, byte, byte, byte, byte, byte)? c = tier?.ToLowerInvariant() switch
        {
            "s" => (0x40, 0x8A, 0xBF, 0x38, 0x5F, 0x7A),
            "a" => (0x6A, 0x9D, 0x36, 0x58, 0x79, 0x37),
            "b" => (0x92, 0xA0, 0x36, 0x68, 0x79, 0x37),
            "c" => (0xA0, 0x7C, 0x36, 0x79, 0x5F, 0x37),
            "d" => (0xA0, 0x48, 0x36, 0x79, 0x42, 0x37),
            "f" => (0xA0, 0x36, 0x36, 0x79, 0x37, 0x37),
            _ => null,
        };
        if (c is not { } v) return Frozen(Color.FromRgb(0x14, 0x16, 0x17));
        var b = new LinearGradientBrush(Color.FromRgb(v.Item1, v.Item2, v.Item3), Color.FromRgb(v.Item4, v.Item5, v.Item6), 90);
        b.Freeze();
        return b;
    }

    /// <summary>Green for good placements, red for bad, white around the middle (HDT's colouring).</summary>
    private static Brush PlacementBrush(double avg, bool duos)
    {
        double pivot = duos ? 2.5 : 4.5, factor = duos ? 0.5 : 1.0;
        double t = Math.Clamp((pivot - avg) / 3.5 * factor * 1.8, -1, 1);
        Color white = Color.FromRgb(0xFF, 0xFF, 0xFF), good = Color.FromRgb(0x6A, 0xDF, 0x5A), bad = Color.FromRgb(0xFF, 0x5A, 0x4F);
        var target = t >= 0 ? good : bad;
        double k = Math.Abs(t);
        var col = Color.FromRgb((byte)(white.R + (target.R - white.R) * k), (byte)(white.G + (target.G - white.G) * k), (byte)(white.B + (target.B - white.B) * k));
        return Frozen(col);
    }

    private static FrameworkElement NoteLine(string text) => new Border
    {
        Background = Black,
        BorderBrush = Purple,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(3),
        Padding = new Thickness(6, 1, 6, 1),
        Child = Small(text, Frozen(Color.FromRgb(0xDC, 0xDD, 0xDE)), 11),
    };

    /// <summary>Like HDT's composition box: tribe name, a bar and the value, faded with an X when the tribe isn't in the lobby.</summary>
    private FrameworkElement TribeBox(IReadOnlyList<TribeRow> rows)
    {
        var stack = new StackPanel();
        var head = Small("Best tribes · avg place", Frozen(Color.FromRgb(0xDC, 0xDD, 0xDE)), 10.5);
        head.HorizontalAlignment = HorizontalAlignment.Center;
        head.Margin = new Thickness(0, 2, 0, 2);
        stack.Children.Add(head);
        foreach (var r in rows) stack.Children.Add(TribeLine(r));
        return new Border
        {
            Width = 273,
            Background = Black,
            BorderBrush = Purple,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Child = stack,
        };
    }

    private FrameworkElement TribeLine(TribeRow r)
    {
        var grid = new Grid { Height = 26 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });

        // Art of one of the tribe's minions behind the name, fading out (HDT shows a key minion here).
        var art = new Image { Stretch = Stretch.UniformToFill, Height = 24, Width = 110, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-40, 0, 0, 0) };
        art.OpacityMask = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(0.85, 0.5),
            GradientStops = { new GradientStop(Colors.Black, 0.5), new GradientStop(Color.FromArgb(0, 0, 0, 0), 1) },
        };
        var artHost = new Canvas { ClipToBounds = true, Width = 100, Height = 26 };
        artHost.Children.Add(art);
        var sample = SampleMinion(r.Tribe);
        if (sample != null) SetImage(art, "tile:" + sample, () => _engine.Tiles.GetAsync(sample));
        grid.Children.Add(artHost);

        var name = Small(r.Label, Brushes.White, 12, bold: true);
        name.Margin = new Thickness(6, 0, 0, 0);
        name.Effect = TextEdge();
        grid.Children.Add(name);

        var track = new Border { Background = BarTrack, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(6, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        var fill = new Border { Background = r.InLobby ? BarOn : BarOff, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left };
        var barGrid = new Grid();
        barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.02, r.Bar), GridUnitType.Star) });
        barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0, 1 - r.Bar), GridUnitType.Star) });
        fill.HorizontalAlignment = HorizontalAlignment.Stretch;
        barGrid.Children.Add(fill);
        track.Child = barGrid;
        grid.Children.Add(track.At(1));

        var val = Small(r.Value, Brushes.White, 12, bold: true, align: TextAlignment.Right);
        val.Margin = new Thickness(0, 0, 8, 0);
        grid.Children.Add(val.At(2));

        var line = new Grid();
        line.Children.Add(grid);
        if (!r.InLobby)
        {
            grid.Opacity = 0.5;
            line.Children.Add(new TextBlock
            {
                Text = "✕",
                Foreground = Frozen(Color.FromRgb(0xFF, 0x4D, 0x4D)),
                FontWeight = FontWeights.Black,
                FontSize = 15,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Effect = TextEdge(),
            });
            name.Margin = new Thickness(22, 0, 0, 0);
        }
        return line;
    }

    /// <summary>A high-tier minion of the tribe, for the row's picture.</summary>
    private string? SampleMinion(string tribe) => _engine.Cards.All()
        .Where(c => c.InPool && !c.IsSpell && c.Races.Count == 1 && c.Races[0] == tribe)
        .OrderByDescending(c => c.Tier).ThenBy(c => c.Name)
        .Select(c => c.Id)
        .FirstOrDefault();
}
