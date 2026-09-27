using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TavernTracker.Core;
using TavernTracker.Core.Games;
using static TavernTracker.App.Overlay.OverlayStyle;
using static TavernTracker.App.Theme;

namespace TavernTracker.App.Overlay;

/// <summary>Everyone in the lobby with their public rating, rank and how often you've met them.</summary>
public sealed class LobbyPanel : StackPanel
{
    private readonly TrackerEngine _engine;
    private readonly TextBlock _average = Small("", Muted, 11);
    private readonly StackPanel _rows = new();
    private readonly TextBlock _note = Small("", Muted, 10.5);
    private string _signature = "";

    public LobbyPanel(TrackerEngine engine)
    {
        _engine = engine;
        Width = 240;
        _note.TextWrapping = TextWrapping.Wrap;
        _note.Margin = new Thickness(8, 2, 8, 6);
        var body = Stack(Orientation.Vertical, 0, _average, _rows, _note);
        _average.Margin = new Thickness(8, 4, 8, 2);
        _average.TextAlignment = TextAlignment.Center;
        Children.Add(Section("Lobby MMR", body));
    }

    public void Update(bool duos, IReadOnlyCollection<string> deadHeroCardIds)
    {
        var lobby = _engine.Lobby();
        var sig = string.Join("|", lobby.Select(e => $"{e.Name}:{e.Rating}:{e.Place}:{deadHeroCardIds.Contains(e.HeroCardId)}")) + _engine.MemoryAvailable;
        if (sig == _signature) return;
        _signature = sig;

        _rows.Children.Clear();
        if (lobby.Count == 0)
        {
            _average.Text = "";
            _note.Text = _engine.MemoryAvailable
                ? "Reading player names…"
                : "Player names come from the game's memory. Turn on memory reading in Settings.";
            return;
        }

        var ranked = lobby.Where(e => !e.IsMe && e.Rating > 0).ToList();
        _average.Text = ranked.Count > 0 ? $"Average {N((int)ranked.Average(e => e.Rating))} · {ranked.Count} on the leaderboard" : "Nobody else is on the public leaderboard";
        int cutoff = lobby.FirstOrDefault(e => e.Cutoff > 0)?.Cutoff ?? 0;
        _note.Text = cutoff > 0 ? $"Blizzard only lists players above {N(cutoff)}." : "";

        foreach (var e in lobby.OrderBy(e => deadHeroCardIds.Contains(e.HeroCardId)).ThenByDescending(e => e.Rating))
        {
            bool dead = deadHeroCardIds.Contains(e.HeroCardId);
            var g = new Grid { Margin = new Thickness(8, 2, 8, 2), Opacity = dead ? 0.4 : 1 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });

            g.Children.Add(Small(e.Name + (e.SharedName ? " ?" : ""), e.IsMe ? Gold : Brushes.White, 12.5, bold: e.IsMe).At(0));
            if (e.TimesMet > 0)
            {
                var met = Small($"×{e.TimesMet}", Blue, 11, bold: true);
                met.Margin = new Thickness(4, 0, 4, 0);
                met.ToolTip = $"You've been in {e.TimesMet} lobby{(e.TimesMet == 1 ? "" : "s")} with {e.Name} before";
                g.Children.Add(met.At(1));
            }
            string rating = e.Rating > 0 ? N(e.Rating) : e.Cutoff > 0 ? $"<{N(e.Cutoff)}" : "—";
            g.Children.Add(Small(rating, e.Rating > 0 ? Brushes.White : Muted, 12.5, bold: e.Rating > 0, TextAlignment.Right).At(2));
            g.Children.Add(Small(e.Rank > 0 && !e.IsMe ? $"#{N(e.Rank)}" : "", Muted, 10.5, align: TextAlignment.Right).At(3));
            _rows.Children.Add(g);
        }
    }
}
