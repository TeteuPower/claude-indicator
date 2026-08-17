using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClaudeIndicator.Core;

namespace ClaudeIndicator.Views;

/// <summary>Monta as barras de consumo usadas no gadget e na prévia das configurações.</summary>
public static class BarRenderer
{
    public static Brush Swatch(string key)
    {
        var res = System.Windows.Application.Current?.TryFindResource(key);
        return res as Brush ?? Brushes.Gray;
    }

    public static Brush BrushFor(double percent, AppSettings s)
    {
        if (percent >= s.AlertThreshold) return Swatch("DangerBrush");
        if (percent >= s.WarnThreshold) return Swatch("WarnBrush");
        return Swatch("OkBrush");
    }

    public static UIElement BuildRow(UsageBar bar, AppSettings s, bool showReset, double labelSize = 11)
    {
        var container = new StackPanel { Margin = new Thickness(0, 0, 0, 11) };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labelText = s.LabelFor(bar.Kind);
        if (showReset && bar.ResetsAt != null)
            labelText += "  ·  " + bar.ResetText();

        var label = new TextBlock
        {
            Text = labelText,
            FontSize = labelSize,
            Foreground = Swatch("MutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(label, 0);
        header.Children.Add(label);

        var value = new TextBlock
        {
            Text = Math.Round(bar.Percent).ToString("0") + "%",
            FontSize = labelSize + 1,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushFor(bar.Percent, s),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(value, 1);
        header.Children.Add(value);

        container.Children.Add(header);

        var track = new Border
        {
            Height = 7,
            CornerRadius = new CornerRadius(3.5),
            Background = Swatch("TrackBrush"),
            Margin = new Thickness(0, 5, 0, 0),
            ClipToBounds = true
        };

        var grid = new Grid();
        var frac = Math.Max(0.0, Math.Min(1.0, bar.Fraction));
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(frac, 0.0001), GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - frac, 0.0001), GridUnitType.Star) });

        var fill = new Border
        {
            CornerRadius = new CornerRadius(3.5),
            Background = BrushFor(bar.Percent, s),
            MinWidth = bar.Percent > 0 ? 4 : 0
        };
        Grid.SetColumn(fill, 0);
        grid.Children.Add(fill);
        track.Child = grid;

        container.Children.Add(track);
        return container;
    }
}
