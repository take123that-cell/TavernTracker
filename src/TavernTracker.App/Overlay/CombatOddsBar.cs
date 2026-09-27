using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TavernTracker.Core.Combat;
using static TavernTracker.App.Overlay.OverlayStyle;
using static TavernTracker.App.Theme;

namespace TavernTracker.App.Overlay;

/// <summary>Top-centre bar: LETHAL | WIN | TIE | LOSS | LETHAL for the current combat (Firestone's simulator).</summary>
public sealed class CombatOddsBar : Border
{
    private readonly TextBlock[] _values = new TextBlock[5];
    private readonly TextBlock[] _labels = new TextBlock[5];
    private readonly TextBlock _caption = Small("", Muted, 11, align: TextAlignment.Center);
    private readonly Grid _grid = new();
    private string _signature = "";

    private static readonly string[] Titles = { "LETHAL", "WIN", "TIE", "LOSS", "LETHAL" };

    public CombatOddsBar()
    {
        Background = PanelBg;
        BorderBrush = Edge;
        BorderThickness = new Thickness(1, 0, 1, 1);
        CornerRadius = new CornerRadius(0, 0, 8, 8);
        Padding = new Thickness(10, 4, 10, 4);
        HorizontalAlignment = HorizontalAlignment.Center;
        MinWidth = 330;

        for (int i = 0; i < 5; i++)
        {
            _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(i == 0 || i == 4 ? 58 : 70) });
            _labels[i] = Small(Titles[i], i is 1 or 3 ? Brushes.White : Muted, i is 0 or 4 ? 10 : 11, bold: true, TextAlignment.Center);
            _values[i] = Small("—", Brushes.White, i is 0 or 4 ? 13 : 17, bold: i is 1 or 2 or 3, TextAlignment.Center);
            var cell = new StackPanel();
            cell.Children.Add(_labels[i]);
            cell.Children.Add(_values[i]);
            Grid.SetColumn(cell, i);
            _grid.Children.Add(cell);
        }

        var stack = new StackPanel();
        stack.Children.Add(_grid);
        stack.Children.Add(_caption);
        Child = stack;
        Visibility = Visibility.Collapsed;
    }

    public void Update(CombatState state, bool gameLive)
    {
        var sig = $"{gameLive}|{state.Phase}|{state.InCombat}|{state.Turn}|{state.Odds?.Won}|{state.Odds?.Lost}|{state.Message}";
        if (sig == _signature) return;
        _signature = sig;

        if (!gameLive || state.Phase == CombatPhase.None)
        {
            Visibility = Visibility.Collapsed;
            return;
        }
        Visibility = Visibility.Visible;

        switch (state.Phase)
        {
            case CombatPhase.Running:
                SetValues("…", "…", "…", "…", "…", dim: true);
                _caption.Text = "Simulating combat…";
                break;
            case CombatPhase.Unavailable:
                SetValues("—", "—", "—", "—", "—", dim: true);
                _caption.Text = state.Message ?? "No odds for this combat";
                break;
            case CombatPhase.Done when state.Odds != null:
                var o = state.Odds;
                SetValues(P(o.WonLethal), P(o.Won), P(o.Tied), P(o.Lost), P(o.LostLethal), dim: false);
                _values[0].Foreground = o.WonLethal > 0 ? Up : Muted;
                _values[1].Foreground = Up;
                _values[2].Foreground = Brushes.White;
                _values[3].Foreground = Down;
                _values[4].Foreground = o.LostLethal > 0 ? Down : Muted;
                string dmg = o.Won >= o.Lost
                    ? (o.AvgDamageWon > 0 ? $" · avg {o.AvgDamageWon:0.#} dmg dealt" : "")
                    : (o.AvgDamageLost > 0 ? $" · avg {o.AvgDamageLost:0.#} dmg taken" : "");
                _caption.Text = (state.InCombat ? "Current combat" : $"Last combat (turn {state.Turn})") + dmg;
                break;
        }
    }

    private void SetValues(string a, string b, string c, string d, string e, bool dim)
    {
        var values = new[] { a, b, c, d, e };
        for (int i = 0; i < 5; i++)
        {
            _values[i].Text = values[i];
            if (dim) _values[i].Foreground = Muted;
        }
    }

    private static string P(double v) => v <= 0 ? "0%" : v >= 99.95 ? "100%" : v < 0.1 ? "<0.1%" : $"{v:0.#}%";
}
