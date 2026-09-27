using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TavernTracker.App;

/// <summary>Colors and small UI building blocks. The whole UI is built in code (no XAML).</summary>
public static class Theme
{
    public static readonly CultureInfo Num = CultureInfo.InvariantCulture;

    // Tavern palette: dark oak, warm lamplight, parchment text and brass trim.
    public static readonly Brush Bg = B(0x1A, 0x11, 0x0B);
    public static readonly Brush Sidebar = B(0x14, 0x0D, 0x08);
    public static readonly Brush Card = B(0x2A, 0x1C, 0x12);
    public static readonly Brush CardHover = B(0x3A, 0x28, 0x19);
    public static readonly Brush Line = B(0x5A, 0x3E, 0x24);
    public static readonly Brush Text = B(0xF4, 0xE6, 0xC8);
    public static readonly Brush Muted = B(0xC4, 0xA6, 0x7A);
    public static readonly Brush Faint = B(0x8C, 0x72, 0x52);
    public static readonly Brush Gold = B(0xF2, 0xC1, 0x4E);
    public static readonly Brush GoldSoft = B(0x4A, 0x35, 0x14);
    public static readonly Brush Up = B(0x9C, 0xD6, 0x7E);
    public static readonly Brush Down = B(0xEF, 0x7A, 0x5E);
    public static readonly Brush Blue = B(0xEE, 0xB8, 0x6A);
    public static readonly Brush Amber = B(0xF5, 0xB0, 0x41);

    public static readonly FontFamily Font = new("Segoe UI");
    /// <summary>Cinzel (SIL Open Font License), shipped inside the exe: headings, titles and the logo.</summary>
    public static readonly FontFamily Display = LoadDisplayFont();

    private static FontFamily LoadDisplayFont()
    {
        try { return new FontFamily(new Uri("pack://application:,,,/"), "./Fonts/#Cinzel, Georgia"); }
        catch { return new FontFamily("Georgia"); }
    }

    /// <summary>Brass-to-gold gradient used for primary buttons and trim.</summary>
    public static readonly Brush Brass = Grad(0xFF, 0xDD, 0x86, 0xC9, 0x8E, 0x2A);
    public static readonly Brush BrassHover = Grad(0xFF, 0xE9, 0xA8, 0xDB, 0xA2, 0x3C);
    /// <summary>Card background: a touch lighter at the top, like lamplit wood.</summary>
    public static readonly Brush CardFill = Grad(0x33, 0x23, 0x17, 0x25, 0x18, 0x0F);

    public static Brush Grad(byte r1, byte g1, byte b1, byte r2, byte g2, byte b2)
    {
        var g = new LinearGradientBrush(Color.FromRgb(r1, g1, b1), Color.FromRgb(r2, g2, b2), 90);
        g.Freeze();
        return g;
    }

    /// <summary>A tiled picture from the exe's resources (the wood texture), or null.</summary>
    public static ImageBrush? Texture(string name, double tile = 512, double opacity = 1)
    {
        try
        {
            var img = new System.Windows.Media.Imaging.BitmapImage(new Uri($"pack://application:,,,/Assets/{name}"));
            var b = new ImageBrush(img)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, tile, tile),
                ViewportUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.Fill,
                Opacity = opacity,
            };
            b.Freeze();
            return b;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>An image from the exe's resources, or null.</summary>
    public static ImageSource? Asset(string name)
    {
        try { return new System.Windows.Media.Imaging.BitmapImage(new Uri($"pack://application:,,,/Assets/{name}")); }
        catch { return null; }
    }

    public static Brush B(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public static Brush PlaceBrush(int place) => place switch
    {
        1 => Gold,
        2 or 3 or 4 => Up,
        >= 5 => Down,
        _ => Muted,
    };

    public static string Signed(int v) => v.ToString("+#,0;-#,0;±0", Num);
    public static string N(int v) => v.ToString("N0", Num);

    // ---------------------------------------------------------------- factories

    public static TextBlock T(string text, double size = 13, Brush? color = null, FontWeight? weight = null)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = color ?? Text,
            FontWeight = weight ?? FontWeights.Normal,
            FontFamily = Font,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    public static TextBlock Heading(string text)
    {
        var t = T(text, 26, Gold, FontWeights.Bold);
        t.FontFamily = Display;
        t.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 2, Opacity = 0.7, Color = Colors.Black };
        return t;
    }

    public static TextBlock Caption(string text)
    {
        var t = T(text.ToUpperInvariant(), 11.5, Muted, FontWeights.Bold);
        t.FontFamily = Display;
        return t;
    }

    /// <summary>A panel like a framed board on the tavern wall: warm wood, brass edge, soft shadow.</summary>
    public static Border CardBox(UIElement child, double padding = 16) => new()
    {
        Background = CardFill,
        BorderBrush = Line,
        BorderThickness = new Thickness(1.5),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(padding),
        Child = child,
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.45, Color = Colors.Black },
    };

    public static StackPanel Stack(Orientation o = Orientation.Vertical, double gap = 0, params UIElement[] children)
    {
        var s = new SpacedStack(gap) { Orientation = o };
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    /// <summary>A flat clickable element (WPF's default Button looks out of place on a dark theme).</summary>
    public static Border Clickable(UIElement content, Action onClick, Brush? background = null, Brush? hover = null,
        double padX = 12, double padY = 7)
    {
        var normal = background ?? Brushes.Transparent;
        var over = hover ?? CardHover;
        var b = new Border
        {
            Child = content,
            Background = normal,
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(padX, padY, padX, padY),
            Cursor = Cursors.Hand,
        };
        b.MouseEnter += (_, _) => b.Background = over;
        b.MouseLeave += (_, _) => b.Background = normal;
        b.MouseLeftButtonUp += (_, e) => { onClick(); e.Handled = true; };
        return b;
    }

    public static Border Btn(string text, Action onClick, bool primary = false)
    {
        var b = Clickable(T(text, 13, primary ? B(0x2A, 0x16, 0x08) : Text, FontWeights.Bold), onClick,
            primary ? Brass : B(0x4A, 0x31, 0x1E), primary ? BrassHover : B(0x5E, 0x40, 0x27));
        b.BorderBrush = primary ? B(0x7A, 0x52, 0x14) : B(0x6E, 0x4C, 0x2C);
        b.BorderThickness = new Thickness(1);
        b.CornerRadius = new CornerRadius(5);
        return b;
    }

    public static TextBox Input(string text = "", string? hint = null)
    {
        var box = new TextBox
        {
            Text = text,
            FontSize = 14,
            FontFamily = Font,
            Background = B(0x14, 0x0D, 0x08),
            Foreground = Text,
            CaretBrush = Gold,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 7, 10, 7),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        if (hint != null) box.ToolTip = hint;
        return box;
    }

    public static UIElement Pill(string text, Brush fg, Brush bg) => new Border
    {
        Background = bg,
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(8, 2, 8, 2),
        Child = T(text, 12, fg, FontWeights.SemiBold),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A place badge: 1st gold, top 4 green, bottom 4 red.</summary>
    public static UIElement PlaceBadge(int place, double size = 26)
    {
        var brush = PlaceBrush(place);
        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            BorderBrush = brush,
            BorderThickness = new Thickness(1.5),
            Child = new TextBlock
            {
                Text = place > 0 ? place.ToString(Num) : "?",
                Foreground = brush,
                FontWeight = FontWeights.Bold,
                FontSize = size * 0.5,
                FontFamily = Font,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    /// <summary>Signed change, colored.</summary>
    public static TextBlock Change(int? delta, double size = 13, string suffix = "")
    {
        if (delta == null) return T("—" + suffix, size, Faint);
        var brush = delta > 0 ? Up : delta < 0 ? Down : Muted;
        return T(Signed(delta.Value) + suffix, size, brush, FontWeights.SemiBold);
    }

    /// <summary>Label-over-value stat block.</summary>
    public static StackPanel Stat(string label, UIElement value) => Stack(Orientation.Vertical, 4, Caption(label), value);

    public static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span.TotalMinutes < 2) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} h ago";
        if (span.TotalDays < 45) return $"{(int)span.TotalDays} days ago";
        return utc.ToLocalTime().ToString("d MMM yyyy", Num);
    }

    public static Grid Columns(params GridLength[] widths)
    {
        var g = new Grid();
        foreach (var w in widths) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        return g;
    }

    public static T At<T>(this T element, int column, int row = 0) where T : UIElement
    {
        Grid.SetColumn(element, column);
        Grid.SetRow(element, row);
        return element;
    }

    public static GridLength Star(double v = 1) => new(v, GridUnitType.Star);
    public static GridLength Px(double v) => new(v);
    public static readonly GridLength Auto = GridLength.Auto;
}

/// <summary>StackPanel that puts a gap between children, including ones added later.</summary>
public sealed class SpacedStack : StackPanel
{
    private readonly double _gap;

    public SpacedStack(double gap) => _gap = gap;

    protected override void OnVisualChildrenChanged(DependencyObject visualAdded, DependencyObject visualRemoved)
    {
        base.OnVisualChildrenChanged(visualAdded, visualRemoved);
        if (_gap <= 0) return;
        for (int i = 0; i < Children.Count; i++)
        {
            if (Children[i] is not FrameworkElement fe) continue;
            var m = fe.Margin;
            // Only touch margins we set ourselves (or never set), not ones a caller chose.
            bool ours = m == default || fe.Tag as string == "gap";
            if (!ours) continue;
            fe.Tag = "gap";
            fe.Margin = i == 0 ? default :
                Orientation == Orientation.Vertical ? new Thickness(0, _gap, 0, 0) : new Thickness(_gap, 0, 0, 0);
        }
    }
}
