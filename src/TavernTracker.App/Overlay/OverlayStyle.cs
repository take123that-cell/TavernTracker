using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TavernTracker.Core.Cards;
using static TavernTracker.App.Theme;

namespace TavernTracker.App.Overlay;

/// <summary>The look of the in-game panels (close to HDT's so they sit naturally on the board).</summary>
public static class OverlayStyle
{
    // Background brushes are shared and not frozen, so changing the transparency setting
    // updates every panel at once. Text stays fully opaque; only backgrounds fade.
    public static readonly SolidColorBrush PanelBg = new(Color.FromArgb(0xB8, 0x1E, 0x14, 0x0C));
    public static readonly SolidColorBrush HeaderBg = new(Color.FromArgb(0xC8, 0x3A, 0x26, 0x14));
    public static readonly SolidColorBrush RowBg = new(Color.FromArgb(0xB8, 0x1E, 0x14, 0x0C));
    public static readonly SolidColorBrush TribeHeader = new(Color.FromArgb(0xD0, 0x4A, 0x2C, 0x14));
    public static readonly Brush RowAlt = Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
    public static readonly Brush Edge = Frozen(Color.FromArgb(0x90, 0x8A, 0x62, 0x2A));

    /// <summary>0.3 = very see-through, 1 = solid.</summary>
    public static void ApplyOpacity(double opacity)
    {
        opacity = Math.Clamp(opacity, 0.3, 1.0);
        byte A(double factor) => (byte)Math.Round(255 * Math.Min(1.0, opacity * factor));
        PanelBg.Color = Color.FromArgb(A(1.0), 0x1E, 0x14, 0x0C);
        RowBg.Color = Color.FromArgb(A(1.0), 0x1E, 0x14, 0x0C);
        HeaderBg.Color = Color.FromArgb(A(1.1), 0x3A, 0x26, 0x14);
        TribeHeader.Color = Color.FromArgb(A(1.15), 0x4A, 0x2C, 0x14);
    }

    public static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>A section with a centred title bar, like HDT's "MMR" / "Latest Games" blocks.</summary>
    public static Border Section(string title, UIElement body)
    {
        var header = new Border
        {
            Background = HeaderBg,
            Padding = new Thickness(6, 5, 6, 5),
            Child = new TextBlock
            {
                Text = title,
                Foreground = HsGold,
                FontWeight = FontWeights.Bold,
                FontSize = 12.5,
                FontFamily = Display,
                HorizontalAlignment = HorizontalAlignment.Center,
            },
        };
        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(body);
        return new Border { Child = stack, Background = PanelBg, BorderBrush = Edge, BorderThickness = new Thickness(0, 0, 0, 1) };
    }

    public static TextBlock Small(string text, Brush? color = null, double size = 11.5, bool bold = false, TextAlignment align = TextAlignment.Left) => new()
    {
        Text = text,
        Foreground = color ?? Brushes.White,
        FontSize = size,
        FontFamily = Font,
        FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
        TextAlignment = align,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static string Ordinal(int n) => n switch
    {
        1 => "1st", 2 => "2nd", 3 => "3rd", >= 4 and <= 8 => $"{n}th", _ => "—",
    };

    /// <summary>Colour per tribe for the tribe chips.</summary>
    public static Brush TribeBrush(string key) => key switch
    {
        "ABERRATION" => Frozen(Color.FromRgb(0x9B, 0x5D, 0xE5)),
        "BEAST" => Frozen(Color.FromRgb(0x5F, 0xA8, 0x4A)),
        "DEMON" => Frozen(Color.FromRgb(0x7C, 0xC4, 0x3F)),
        "DRAGON" => Frozen(Color.FromRgb(0xC2, 0x4A, 0x3D)),
        "ELEMENTAL" => Frozen(Color.FromRgb(0xE0, 0x8A, 0x2E)),
        "MECHANICAL" => Frozen(Color.FromRgb(0x4A, 0x90, 0xD9)),
        "MURLOC" => Frozen(Color.FromRgb(0x3F, 0xB8, 0xB0)),
        "NAGA" => Frozen(Color.FromRgb(0x5B, 0x7F, 0xE0)),
        "PIRATE" => Frozen(Color.FromRgb(0xC9, 0xA2, 0x3A)),
        "QUILBOAR" => Frozen(Color.FromRgb(0xD9, 0x6A, 0x9A)),
        "UNDEAD" => Frozen(Color.FromRgb(0x8F, 0xA3, 0xAD)),
        _ => Frozen(Color.FromRgb(0x70, 0x76, 0x80)),
    };

    /// <summary>Loads a cached PNG without locking the file.</summary>
    public static BitmapImage? LoadImage(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ images

    private static readonly Dictionary<string, ImageSource> Images = new(StringComparer.Ordinal);

    /// <summary>
    /// Shows a cached picture right away (no flicker when lists are rebuilt); otherwise downloads it and
    /// fades it in. Must be called on the UI thread.
    /// </summary>
    public static void SetImage(Image target, string key, Func<Task<string?>> fetch)
    {
        // Images get reused (the hover preview): remember which picture this one should end up showing,
        // and drop any running fade so Opacity can be set directly.
        target.Tag = key;
        target.BeginAnimation(UIElement.OpacityProperty, null);
        if (Images.TryGetValue(key, out var cached))
        {
            target.Source = cached;
            target.Opacity = 1;
            return;
        }
        target.Source = null;
        target.Opacity = 0;
        _ = LoadAsync(target, key, fetch);
    }

    private static async Task LoadAsync(Image target, string key, Func<Task<string?>> fetch)
    {
        var path = await fetch();
        if (path == null) return;
        var img = await Task.Run(() => LoadImage(path));
        if (img == null) return;
        Images[key] = img; // continuation runs on the UI thread
        if (!Equals(target.Tag, key)) return; // the image has moved on to another card meanwhile
        target.Source = img;
        target.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
    }

    public static readonly Brush TileBg = Frozen(Color.FromRgb(0x2B, 0x21, 0x19));
    public static readonly Brush GroupBg = new SolidColorBrush(Color.FromArgb(0xE6, 0x23, 0x27, 0x2A));
    public static readonly Brush GroupEdge = Frozen(Color.FromRgb(0x6E, 0x4C, 0x22));
    public static readonly Brush GroupHeaderBg = new LinearGradientBrush(Color.FromRgb(0x5E, 0x38, 0x18), Color.FromRgb(0x3E, 0x24, 0x0E), 90);
    public static readonly Brush HsGold = Frozen(Color.FromRgb(0xF2, 0xC1, 0x4E));

    /// <summary>Soft black edge around light text so it reads on card art (HDT draws an outline).</summary>
    public static System.Windows.Media.Effects.Effect TextEdge() => new System.Windows.Media.Effects.DropShadowEffect
    {
        BlurRadius = 3, ShadowDepth = 0, Opacity = 1, Color = Colors.Black,
    };

    /// <summary>
    /// A card tile like HDT's Battlegrounds list: dark strip, card art on the right fading to the left,
    /// name on the left, gold coin with the cost for tavern spells.
    /// </summary>
    public static Border CardRow(BgCard card, TileCache tiles, Action<BgCard?, FrameworkElement?> onHover,
        Action<BgCard>? onRightClick = null, bool darkened = false, double width = 216)
    {
        var art = new Image
        {
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(-30, 0, -10, 0),
        };
        RenderOptions.SetBitmapScalingMode(art, BitmapScalingMode.HighQuality);
        var artHost = new Grid { ClipToBounds = true, Width = width * 0.8, HorizontalAlignment = HorizontalAlignment.Right };
        artHost.Children.Add(art);
        artHost.OpacityMask = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(0.55, 0.5),
            GradientStops = { new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.0), new GradientStop(Colors.Black, 1.0) },
        };

        var name = new TextBlock
        {
            Text = card.Name,
            Foreground = Brushes.White,
            FontFamily = Font,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(7, 0, card.IsSpell ? 34 : 8, 0),
        };

        var grid = new Grid { Height = 32, Width = width - 2, ClipToBounds = true, Background = TileBg };
        grid.Children.Add(artHost);
        grid.Children.Add(name);
        if (card.IsSpell)
        {
            var coin = new Grid { Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 5, 0) };
            coin.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Fill = new RadialGradientBrush(Color.FromRgb(0xFF, 0xE0, 0x7A), Color.FromRgb(0xC8, 0x8A, 0x1A)),
                Stroke = Frozen(Color.FromRgb(0x5A, 0x3A, 0x08)),
                StrokeThickness = 1.5,
            });
            coin.Children.Add(new TextBlock
            {
                Text = card.Cost.ToString(Num),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                FontFamily = Font,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
            grid.Children.Add(coin);
        }
        if (darkened) grid.Children.Add(new Border { Background = Frozen(Color.FromArgb(0x99, 0, 0, 0)) });

        var row = new Border
        {
            Child = grid,
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        var hoverEdge = Frozen(Color.FromRgb(0xF2, 0xC1, 0x4E));
        row.MouseEnter += (_, _) => { row.BorderBrush = hoverEdge; onHover(card, row); };
        row.MouseLeave += (_, _) => { row.BorderBrush = Brushes.Black; onHover(null, null); };
        if (onRightClick != null)
            row.MouseRightButtonUp += (_, e) => { onRightClick(card); e.Handled = true; };

        SetImage(art, "tile:" + card.Id, () => tiles.GetAsync(card.Id));
        return row;
    }

    /// <summary>
    /// The tavern tier emblem: a gold-rimmed shield with the tier number and a row of stars, drawn in code
    /// (the game's own icons can't be shipped).
    /// </summary>
    public static FrameworkElement TierIcon(int tier, double size = 36)
    {
        var host = new Grid { Width = size, Height = size * 1.1 };
        var shield = Geometry.Parse("M 50,2 L 92,14 C 92,60 80,86 50,106 C 20,86 8,60 8,14 Z");
        var rim = new System.Windows.Shapes.Path
        {
            Data = shield,
            Stretch = Stretch.Fill,
            Fill = new LinearGradientBrush(Color.FromRgb(0xFF, 0xDB, 0x7A), Color.FromRgb(0x9A, 0x6A, 0x16), 90),
        };
        var inner = new System.Windows.Shapes.Path
        {
            Data = shield,
            Stretch = Stretch.Fill,
            Margin = new Thickness(size * 0.09),
            Fill = new LinearGradientBrush(Color.FromRgb(0x3E, 0x5A, 0x7A), Color.FromRgb(0x1A, 0x24, 0x36), 90),
        };
        host.Children.Add(rim);
        host.Children.Add(inner);
        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, size * 0.1) };
        content.Children.Add(new TextBlock
        {
            Text = tier.ToString(Num),
            Foreground = HsGold,
            FontWeight = FontWeights.Black,
            FontSize = size * 0.42,
            FontFamily = Font,
            HorizontalAlignment = HorizontalAlignment.Center,
            Effect = TextEdge(),
        });
        var stars = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -size * 0.04, 0, 0) };
        double starSize = Math.Min(size * 0.14, size * 0.62 / Math.Max(1, tier));
        for (int i = 0; i < tier; i++)
        {
            stars.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 5,0 L 6.2,3.6 L 10,3.6 L 6.9,5.8 L 8.1,9.5 L 5,7.2 L 1.9,9.5 L 3.1,5.8 L 0,3.6 L 3.8,3.6 Z"),
                Fill = HsGold,
                Stretch = Stretch.Uniform,
                Width = starSize,
                Height = starSize,
            });
        }
        content.Children.Add(stars);
        host.Children.Add(content);
        return host;
    }
}
