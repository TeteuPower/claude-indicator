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

    /// <summary>
    /// Fatia (0..1) como porcentagem legível em qualquer ordem de grandeza: 20,3% e 0,004% têm
    /// que caber na mesma coluna sem virar "0%" nem "20,300%".
    /// </summary>
    public static string FormatShare(double share)
    {
        var pct = share * 100.0;
        if (pct <= 0) return "0%";
        if (pct < 0.001) return "<0,001%";
        if (pct < 0.1) return pct.ToString("0.###") + "%";
        if (pct < 1) return pct.ToString("0.##") + "%";
        if (pct < 10) return pct.ToString("0.#") + "%";
        return pct.ToString("0") + "%";
    }

    /// <summary>Consumo medido em pontos do limite (a barra subiu tanto), com sinal.</summary>
    public static string FormatLimitDelta(double? points)
    {
        if (points == null) return "—";
        var v = points.Value;
        if (v <= 0) return "0%";
        return "+" + (v < 0.1 ? v.ToString("0.##") : v < 10 ? v.ToString("0.#") : v.ToString("0")) + "%";
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

    /// <summary>
    /// Célula do gadget horizontal: rótulo em cima, porcentagem + barra na linha de baixo,
    /// horário de renovação embaixo. As células ficam lado a lado, com um separador entre elas.
    /// </summary>
    public static UIElement BuildCell(UsageBar bar, AppSettings s, bool showReset)
    {
        var cell = new StackPanel { Width = 118 };

        cell.Children.Add(new TextBlock
        {
            Text = s.LabelFor(bar.Kind),
            FontSize = 10.5,
            Foreground = Swatch("MutedBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var value = new TextBlock
        {
            Text = Math.Round(bar.Percent).ToString("0") + "%",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushFor(bar.Percent, s),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(value, 0);
        row.Children.Add(value);

        var track = new Border
        {
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = Swatch("TrackBrush"),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds = true
        };
        var grid = new Grid();
        var frac = Math.Max(0.0, Math.Min(1.0, bar.Fraction));
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(frac, 0.0001), GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - frac, 0.0001), GridUnitType.Star) });
        var fill = new Border
        {
            CornerRadius = new CornerRadius(3),
            Background = BrushFor(bar.Percent, s),
            MinWidth = bar.Percent > 0 ? 3 : 0
        };
        Grid.SetColumn(fill, 0);
        grid.Children.Add(fill);
        track.Child = grid;
        Grid.SetColumn(track, 1);
        row.Children.Add(track);

        cell.Children.Add(row);

        if (showReset && bar.ResetsAt != null)
        {
            cell.Children.Add(new TextBlock
            {
                Text = bar.ResetText(),
                FontSize = 9.5,
                Foreground = Swatch("MutedBrush"),
                Margin = new Thickness(0, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        return cell;
    }

    /// <summary>Separador vertical entre as células do gadget horizontal.</summary>
    public static UIElement BuildCellSeparator()
    {
        return new Border
        {
            Width = 1,
            Background = Swatch("LineBrush"),
            Margin = new Thickness(12, 2, 12, 2),
            VerticalAlignment = VerticalAlignment.Stretch
        };
    }
}
