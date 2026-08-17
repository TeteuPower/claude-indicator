using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ClaudeIndicator.Core;

namespace ClaudeIndicator.Views.Pages;

/// <summary>
/// Painel inicial: quanto já foi consumido de cada limite, com que ritmo, e para onde foi.
/// A pergunta que o app existe para responder é "posso continuar trabalhando hoje?", então o
/// destaque é o que resta e a projeção até a renovação.
/// </summary>
public partial class OverviewPage : UserControl
{
    private readonly AppHost _host;
    private readonly TranscriptIndex _index = new();
    private List<HistoryPoint> _history = new();
    private bool _projectsLoaded;

    private static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(90);

    public OverviewPage(AppHost host)
    {
        _host = host;
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += (_, _) => _host.Updated -= OnUsageUpdated;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _host.Updated -= OnUsageUpdated;
        _host.Updated += OnUsageUpdated;
        Refresh();

        if (_projectsLoaded) return;
        _projectsLoaded = true;
        _ = LoadProjectsAsync();
    }

    private void OnUsageUpdated(UsageSnapshot? snap)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(Refresh));
            return;
        }
        Refresh();
    }

    // ------------------------------------------------------------------

    private void Refresh()
    {
        _history = UsageHistory.Load(TimeSpan.FromDays(2));
        var snap = _host.Last;

        DrawAlert(snap);
        DrawBars(snap);
        DrawStats(snap);
        DrawSpark();
    }

    private void DrawAlert(UsageSnapshot? snap)
    {
        if (snap == null || (snap.Ok && !snap.Stale))
        {
            AlertBox.Visibility = Visibility.Collapsed;
            return;
        }

        AlertBox.Visibility = Visibility.Visible;
        AlertText.Text = snap.Stale
            ? snap.Error + " Os valores abaixo são de " +
              (snap.DataAt ?? snap.FetchedAt).ToLocalTime().ToString("HH:mm") + "."
            : snap.Error ?? "Não foi possível consultar o consumo.";
    }

    private void DrawBars(UsageSnapshot? snap)
    {
        BarsHost.Items.Clear();
        var settings = _host.Settings;
        var bars = snap?.Visible(settings) ?? new List<UsageBar>();

        if (bars.Count == 0)
        {
            BarsHost.Items.Add(new TextBlock
            {
                Text = snap == null ? "Consultando a API…" : "Nenhuma barra disponível.",
                Foreground = BarRenderer.Swatch("MutedBrush"),
                FontSize = 12.5,
                Margin = new Thickness(2, 0, 0, 0)
            });
            return;
        }

        for (var i = 0; i < bars.Count; i++)
            BarsHost.Items.Add(BuildHeroCard(bars[i], settings, i == 0, i == bars.Count - 1));
    }

    /// <summary>Cartão grande de uma barra: o que resta em destaque, o consumido como apoio.</summary>
    private UIElement BuildHeroCard(UsageBar bar, AppSettings s, bool first, bool last)
    {
        var card = new Border
        {
            Background = BarRenderer.Swatch("PanelBrush"),
            BorderBrush = BarRenderer.Swatch("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18, 16, 18, 17),
            Margin = new Thickness(first ? 0 : 7, 0, last ? 0 : 7, 0)
        };

        var stack = new StackPanel();

        stack.Children.Add(new TextBlock
        {
            Text = s.LabelFor(bar.Kind),
            FontSize = 12.5,
            Foreground = BarRenderer.Swatch("MutedBrush")
        });

        var remaining = Math.Max(0, 100 - bar.Percent);
        var valueRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        valueRow.Children.Add(new TextBlock
        {
            Text = remaining.ToString("0.#"),
            FontSize = 34,
            FontWeight = FontWeights.SemiBold,
            Foreground = BarRenderer.BrushFor(bar.Percent, s)
        });
        valueRow.Children.Add(new TextBlock
        {
            Text = "% restantes",
            FontSize = 13,
            Foreground = BarRenderer.Swatch("MutedBrush"),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(5, 0, 0, 6)
        });
        stack.Children.Add(valueRow);

        // trilha do consumo
        var track = new Border
        {
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = BarRenderer.Swatch("TrackBrush"),
            Margin = new Thickness(0, 10, 0, 0),
            ClipToBounds = true
        };
        var grid = new Grid();
        var frac = Math.Clamp(bar.Fraction, 0, 1);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(frac, 0.0001), GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - frac, 0.0001), GridUnitType.Star) });
        var fill = new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = BarRenderer.BrushFor(bar.Percent, s),
            MinWidth = bar.Percent > 0 ? 4 : 0
        };
        Grid.SetColumn(fill, 0);
        grid.Children.Add(fill);
        track.Child = grid;
        stack.Children.Add(track);

        var footer = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var used = new TextBlock
        {
            Text = Math.Round(bar.Percent) + "% usado",
            FontSize = 11.5,
            Foreground = BarRenderer.Swatch("MutedBrush")
        };
        Grid.SetColumn(used, 0);
        footer.Children.Add(used);

        var reset = new TextBlock
        {
            Text = bar.ResetText(),
            FontSize = 11.5,
            Foreground = BarRenderer.Swatch("MutedBrush"),
            ToolTip = bar.ResetsAt == null ? null : "renova em " + bar.ResetClock()
        };
        Grid.SetColumn(reset, 1);
        footer.Children.Add(reset);

        stack.Children.Add(footer);
        card.Child = stack;
        return card;
    }

    // ------------------------------------------------------------------
    // Ritmo
    // ------------------------------------------------------------------

    private BarKind MainKind =>
        _host.Settings.ShowWeekly ? BarKind.Weekly :
        _host.Settings.ShowSession ? BarKind.Session : BarKind.Fable;

    private void DrawStats(UsageSnapshot? snap)
    {
        var hour = SumConsumption(TimeSpan.FromHours(1));
        var day = SumConsumption(TimeSpan.FromHours(24));

        StatHour.Text = Format(hour);
        StatDay.Text = Format(day);

        // projeção: mantido o ritmo das últimas 24 h, quanto teria sido gasto até a renovação
        var bar = snap?.Get(MainKind);
        if (day == null || day.Value <= 0 || bar?.ResetsAt == null)
        {
            StatProjection.Text = "—";
            StatProjectionCaption.Text = "projeção até a renovação";
            StatProjection.Foreground = BarRenderer.Swatch("TextBrush");
            return;
        }

        var hoursLeft = (bar.ResetsAt.Value - DateTimeOffset.Now).TotalHours;
        if (hoursLeft <= 0)
        {
            StatProjection.Text = "—";
            return;
        }

        var projected = bar.Percent + day.Value / 24.0 * hoursLeft;
        StatProjection.Text = projected.ToString("0") + "%";
        StatProjection.Foreground = BarRenderer.BrushFor(Math.Min(projected, 100), _host.Settings);
        StatProjectionCaption.Text = projected > 100
            ? "no ritmo atual, o limite acaba antes de renovar"
            : $"projeção de {_host.Settings.LabelFor(MainKind).ToLowerInvariant()} na renovação";
    }

    private static string Format(double? pts) =>
        pts == null ? "—" : "+" + pts.Value.ToString("0.#") + " pts";

    /// <summary>Soma das subidas da barra principal na janela pedida.</summary>
    private double? SumConsumption(TimeSpan window)
    {
        var cutoff = DateTimeOffset.Now - window;
        double sum = 0;
        var any = false;
        HistoryPoint? prev = null;

        foreach (var p in _history)
        {
            var v = p.Get(MainKind);
            if (v == null) continue;
            if (prev != null && p.At >= cutoff && p.At - prev.At <= MaxGap)
            {
                var pv = prev.Get(MainKind);
                if (pv != null)
                {
                    var d = v.Value - pv.Value;
                    if (d > 0) sum += d;
                    any = true;
                }
            }
            prev = p;
        }
        return any ? Math.Round(sum, 1) : null;
    }

    private void OnSparkSizeChanged(object sender, SizeChangedEventArgs e) => DrawSpark();

    /// <summary>Colunas de consumo por hora nas últimas 24 h.</summary>
    private void DrawSpark()
    {
        var c = SparkCanvas;
        c.Children.Clear();

        var w = c.ActualWidth;
        var h = c.ActualHeight;
        if (w < 40 || h < 20) return;

        var now = DateTimeOffset.Now;
        var end = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset).AddHours(1);
        var start = end.AddHours(-24);

        var sums = new double[24];
        var any = false;
        HistoryPoint? prev = null;
        foreach (var p in _history)
        {
            var v = p.Get(MainKind);
            if (v == null) continue;
            if (prev != null && p.At > start && p.At - prev.At <= MaxGap)
            {
                var idx = (int)((p.At - start).TotalHours);
                var pv = prev.Get(MainKind);
                if (idx >= 0 && idx < 24 && pv != null)
                {
                    var d = v.Value - pv.Value;
                    if (d > 0) { sums[idx] += d; any = true; }
                }
            }
            prev = p;
        }

        if (!any)
        {
            var msg = new TextBlock
            {
                Text = "O histórico começa quando o app roda. Volte aqui depois de algumas horas.",
                FontSize = 11.5,
                Foreground = BarRenderer.Swatch("MutedBrush")
            };
            Canvas.SetLeft(msg, 2);
            Canvas.SetTop(msg, h / 2 - 9);
            c.Children.Add(msg);
            SparkCaption.Text = "";
            return;
        }

        var max = 0.0;
        foreach (var v in sums) if (v > max) max = v;
        SparkCaption.Text = $"pico de {max:0.#} pts em uma hora";

        const double labelH = 15;
        var plotH = h - labelH;
        var slot = w / 24;
        var barW = Math.Max(3, slot - 3);

        for (var i = 0; i < 24; i++)
        {
            var bucket = start.AddHours(i);
            var x = slot * i + (slot - barW) / 2;

            if (sums[i] > 0)
            {
                var barH = Math.Max(2, plotH * (sums[i] / max));
                var rect = new Rectangle
                {
                    Width = barW,
                    Height = barH,
                    RadiusX = 2.5,
                    RadiusY = 2.5,
                    Fill = BarRenderer.Swatch("AccentBrush"),
                    ToolTip = $"{bucket.ToLocalTime():HH'h'} · +{sums[i]:0.#} pts"
                };
                Canvas.SetLeft(rect, x);
                Canvas.SetTop(rect, plotH - barH);
                c.Children.Add(rect);
            }
            else
            {
                // marca a hora sem consumo, para o eixo não ficar com buracos
                var dot = new Rectangle
                {
                    Width = barW, Height = 2, RadiusX = 1, RadiusY = 1,
                    Fill = BarRenderer.Swatch("TrackBrush")
                };
                Canvas.SetLeft(dot, x);
                Canvas.SetTop(dot, plotH - 2);
                c.Children.Add(dot);
            }

            if (i % 6 == 0)
            {
                var label = new TextBlock
                {
                    Text = bucket.ToLocalTime().ToString("HH'h'"),
                    FontSize = 10,
                    Foreground = BarRenderer.Swatch("MutedBrush")
                };
                Canvas.SetLeft(label, x);
                Canvas.SetTop(label, plotH + 2);
                c.Children.Add(label);
            }
        }
    }

    // ------------------------------------------------------------------
    // Projetos
    // ------------------------------------------------------------------

    private async System.Threading.Tasks.Task LoadProjectsAsync()
    {
        if (!TranscriptIndex.Available)
        {
            TopProjectsPanel.Children.Add(Hint("Transcrições do Claude Code não encontradas."));
            return;
        }

        TopProjectsPanel.Children.Add(Hint("Lendo transcrições…"));
        try
        {
            await _index.RefreshAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            TopProjectsPanel.Children.Clear();
            TopProjectsPanel.Children.Add(Hint("Falha ao ler as transcrições: " + ex.Message));
            return;
        }

        TopProjectsPanel.Children.Clear();

        var bar = _host.Last?.Get(BarKind.Weekly);
        var from = bar?.ResetsAt != null
            ? bar.ResetsAt.Value.ToLocalTime().AddDays(-7)
            : DateTimeOffset.Now.AddDays(-7);

        var projects = _index.Aggregate(from, DateTimeOffset.Now, _host.Settings);
        if (projects.Count == 0)
        {
            TopProjectsPanel.Children.Add(Hint("Nada registrado nesta semana."));
            return;
        }

        var measured = bar?.Percent;
        var shown = 0;
        foreach (var p in projects)
        {
            if (shown++ >= 4) break;
            TopProjectsPanel.Children.Add(BuildProjectRow(p, measured));
        }
    }

    private UIElement BuildProjectRow(ProjectUsage p, double? measuredPts)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 9) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });

        var name = new TextBlock
        {
            Text = p.Name,
            FontSize = 12.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = p.Path
        };
        Grid.SetColumn(name, 0);
        grid.Children.Add(name);

        var track = new Border
        {
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = BarRenderer.Swatch("TrackBrush"),
            Margin = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds = true
        };
        var inner = new Grid();
        var frac = Math.Clamp(p.Share, 0, 1);
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(frac, 0.0001), GridUnitType.Star) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - frac, 0.0001), GridUnitType.Star) });
        var fill = new Border
        {
            CornerRadius = new CornerRadius(3),
            Background = BarRenderer.Swatch("AccentBrush"),
            MinWidth = p.Share > 0 ? 3 : 0
        };
        Grid.SetColumn(fill, 0);
        inner.Children.Add(fill);
        track.Child = inner;
        Grid.SetColumn(track, 1);
        grid.Children.Add(track);

        var value = new TextBlock
        {
            Text = measuredPts != null
                ? (p.Share * measuredPts.Value).ToString("0.#") + " pts"
                : (p.Share * 100).ToString("0.#") + "%",
            FontSize = 12.5,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = BarRenderer.Swatch("AccentBrush"),
            ToolTip = $"{p.Share * 100:0.#}% do consumo da semana"
        };
        Grid.SetColumn(value, 2);
        grid.Children.Add(value);

        return grid;
    }

    private void OnSeeProjectsClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main) main.Navigate(MainSection.Projects);
    }

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Foreground = BarRenderer.Swatch("MutedBrush")
    };
}
