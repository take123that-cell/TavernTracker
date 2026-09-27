using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TavernTracker.Core.Leaderboard;
using static TavernTracker.App.Theme;

namespace TavernTracker.App.Controls;

/// <summary>A row of pill buttons where one is selected (used for region and solo/duos).</summary>
public sealed class Segmented : Border
{
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal };
    private readonly List<(string Value, Border Item, TextBlock Label)> _items = new();

    public string Selected { get; private set; }
    public event Action<string>? Changed;

    public Segmented(IEnumerable<(string Value, string Label)> options, string selected)
    {
        Selected = selected;
        Background = B(0x14, 0x0D, 0x08);
        BorderBrush = Line;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(3);
        Child = _row;
        HorizontalAlignment = HorizontalAlignment.Left;

        foreach (var (value, label) in options)
        {
            var text = T(label, 12.5, Muted, FontWeights.SemiBold);
            var item = new Border
            {
                Child = text,
                Padding = new Thickness(12, 5, 12, 5),
                CornerRadius = new CornerRadius(6),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            var v = value;
            item.MouseLeftButtonUp += (_, e) => { Select(v, raise: true); e.Handled = true; };
            _items.Add((value, item, text));
            _row.Children.Add(item);
        }
        Select(selected, raise: false);
    }

    public void Select(string value, bool raise)
    {
        Selected = value;
        foreach (var (v, item, label) in _items)
        {
            bool on = v == value;
            item.Background = on ? Brass : Brushes.Transparent;
            label.Foreground = on ? Bg : Muted;
        }
        if (raise) Changed?.Invoke(value);
    }
}

/// <summary>Rating-over-time line chart drawn directly (no chart library needed).</summary>
public sealed class RatingChart : FrameworkElement
{
    private IReadOnlyList<RatingPoint> _points = Array.Empty<RatingPoint>();
    private static readonly Pen LinePen = MakePen(Gold, 2);
    private static readonly Pen GridPen = MakePen(B(0x3A, 0x2A, 0x1C), 1);
    private static readonly Brush Fill = MakeFill();

    public string EmptyText { get; set; } = "No rating history yet. It builds up as the app saves the leaderboard.";

    public RatingChart()
    {
        Height = 220;
        SnapsToDevicePixels = true;
    }

    public void SetPoints(IReadOnlyList<RatingPoint> points)
    {
        _points = points;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        if (_points.Count == 0)
        {
            var ft = Text(EmptyText, 13, Muted, dpi);
            ft.MaxTextWidth = Math.Max(10, w - 20);
            dc.DrawText(ft, new Point(Math.Max(0, (w - ft.Width) / 2), (h - ft.Height) / 2));
            return;
        }

        const double left = 52, right = 12, top = 12, bottom = 26;
        double pw = Math.Max(10, w - left - right), ph = Math.Max(10, h - top - bottom);

        // With a single point, draw a flat line up to now so it's still readable.
        var pts = _points.ToList();
        if (pts.Count == 1) pts.Add(pts[0] with { Utc = DateTime.UtcNow });

        var t0 = pts.First().Utc;
        var t1 = pts.Last().Utc;
        if (t1 <= t0) t1 = t0.AddHours(1);
        int min = pts.Min(p => p.Rating), max = pts.Max(p => p.Rating);
        int pad = Math.Max(25, (max - min) / 8);
        min -= pad; max += pad;
        double span = Math.Max(1, max - min);

        double X(DateTime t) => left + (t - t0).TotalSeconds / (t1 - t0).TotalSeconds * pw;
        double Y(int r) => top + (1 - (r - min) / span) * ph;

        // Horizontal grid + rating labels.
        for (int i = 0; i <= 3; i++)
        {
            int r = (int)Math.Round(min + span * i / 3);
            double y = Y(r);
            dc.DrawLine(GridPen, new Point(left, y), new Point(left + pw, y));
            var ft = Text(N(r), 11, Faint, dpi);
            dc.DrawText(ft, new Point(left - 8 - ft.Width, y - ft.Height / 2));
        }

        // Date labels at both ends.
        var d0 = Text(t0.ToLocalTime().ToString("d MMM HH:mm", Num), 11, Faint, dpi);
        var d1 = Text(t1.ToLocalTime().ToString("d MMM HH:mm", Num), 11, Faint, dpi);
        dc.DrawText(d0, new Point(left, top + ph + 6));
        dc.DrawText(d1, new Point(left + pw - d1.Width, top + ph + 6));

        // Step line: a rating holds until the next recorded change.
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var g = line.Open())
        using (var a = area.Open())
        {
            var start = new Point(X(pts[0].Utc), Y(pts[0].Rating));
            g.BeginFigure(start, false, false);
            a.BeginFigure(new Point(start.X, top + ph), true, true);
            a.LineTo(start, false, false);
            for (int i = 1; i < pts.Count; i++)
            {
                var flat = new Point(X(pts[i].Utc), Y(pts[i - 1].Rating));
                var next = new Point(X(pts[i].Utc), Y(pts[i].Rating));
                g.LineTo(flat, true, true);
                g.LineTo(next, true, true);
                a.LineTo(flat, false, false);
                a.LineTo(next, false, false);
            }
            a.LineTo(new Point(X(pts[^1].Utc), top + ph), false, false);
        }
        line.Freeze();
        area.Freeze();
        dc.DrawGeometry(Fill, null, area);
        dc.DrawGeometry(null, LinePen, line);

        var last = new Point(X(pts[^1].Utc), Y(pts[^1].Rating));
        dc.DrawEllipse(Gold, null, last, 4, 4);
    }

    private static FormattedText Text(string s, double size, Brush brush, double dpi) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(Font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), size, brush, dpi);

    private static Pen MakePen(Brush b, double width)
    {
        var p = new Pen(b, width) { LineJoin = PenLineJoin.Round };
        p.Freeze();
        return p;
    }

    private static Brush MakeFill()
    {
        var g = new LinearGradientBrush(Color.FromArgb(0x55, 0xF2, 0xC1, 0x4E), Color.FromArgb(0x00, 0xF2, 0xC1, 0x4E), 90);
        g.Freeze();
        return g;
    }
}
