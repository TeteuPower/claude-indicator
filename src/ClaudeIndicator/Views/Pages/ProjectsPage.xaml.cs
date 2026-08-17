using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClaudeIndicator.Core;
using ClaudeIndicator.Views;

namespace ClaudeIndicator.Views.Pages;

/// <summary>
/// Reparte o consumo entre os projetos do Claude Code e mostra os prompts de cada um.
///
/// A repartição é proporcional: a API só informa a porcentagem do limite, e as transcrições
/// só informam tokens. O total do período vem da medição real (a barra da semana é justamente
/// o quanto já foi consumido na janela) e é distribuído entre os projetos na proporção do
/// custo estimado de cada um.
/// </summary>
public partial class ProjectsPage : UserControl
{
    private readonly AppHost _host;
    private readonly TranscriptIndex _index = new();
    private CancellationTokenSource? _cts;

    private List<ProjectUsage> _projects = new();
    private string? _selected;
    private bool _ready;
    private bool _scanning;

    public ProjectsPage(AppHost host)
    {
        _host = host;
        InitializeComponent();

        PerWeek.IsChecked = true;
        ScopeAll.IsChecked = true;
        SortRecent.IsChecked = true;
        _ready = true;

        _index.Progress += OnIndexProgress;
        Loaded += (_, _) => _ = ScanAsync();
        Unloaded += (_, _) =>
        {
            _index.Progress -= OnIndexProgress;
            _cts?.Cancel();
        };
    }

    // ------------------------------------------------------------------
    // Varredura
    // ------------------------------------------------------------------

    private void OnIndexProgress(string message)
    {
        // BeginInvoke (e não Invoke): a varredura segura o lock do índice e não pode
        // ficar esperando a UI, que pode estar prestes a consultá-lo.
        Dispatcher.BeginInvoke(new Action(() => StatusText.Text = message));
    }

    private async System.Threading.Tasks.Task ScanAsync()
    {
        if (_scanning) return;
        if (!TranscriptIndex.Available)
        {
            SummaryLine.Text = "Transcrições do Claude Code não encontradas.";
            SummaryHint.Text = $"Esperava a pasta {TranscriptIndex.ProjectsRoot}. Sem ela não há como repartir o consumo por projeto.";
            return;
        }

        _scanning = true;
        StatusText.Text = "Lendo transcrições…";
        try
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            await _index.RefreshAsync(_cts.Token);
            StatusText.Text = "Índice atualizado " + _index.ScannedAt.ToLocalTime().ToString("dd/MM HH:mm");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Leitura cancelada.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Falha ao ler as transcrições: " + ex.Message;
        }
        finally
        {
            _scanning = false;
        }

        Redraw();
    }

    private void OnRescanClick(object sender, RoutedEventArgs e) => _ = ScanAsync();

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (_ready && !_scanning) Redraw();
    }

    private void OnSortChanged(object sender, RoutedEventArgs e)
    {
        if (_ready && !_scanning) DrawPrompts();
    }

    // ------------------------------------------------------------------
    // Período e âncora de medição
    // ------------------------------------------------------------------

    private bool FableScope => ScopeFable.IsChecked == true;

    private BarKind AnchorKind => FableScope ? BarKind.Fable : BarKind.Weekly;

    private (DateTimeOffset From, DateTimeOffset To, string Label) Period()
    {
        var now = DateTimeOffset.Now;
        if (Per24h.IsChecked == true) return (now.AddHours(-24), now, "últimas 24 horas");
        if (Per30d.IsChecked == true) return (now.AddDays(-30), now, "últimos 30 dias");
        if (PerAll.IsChecked == true) return (DateTimeOffset.FromUnixTimeSeconds(0), now, "todo o histórico");

        // Semana atual: a janela do limite semanal começa 7 dias antes da renovação
        var bar = _host.Last?.Get(AnchorKind);
        if (bar?.ResetsAt != null)
        {
            var start = bar.ResetsAt.Value.ToLocalTime().AddDays(-7);
            return (start, now, "semana atual");
        }
        return (now.AddDays(-7), now, "últimos 7 dias");
    }

    /// <summary>
    /// Pontos do limite já consumidos no período. Na semana atual isto é exato: a barra é,
    /// por definição, o quanto foi gasto desde a renovação. Nos outros períodos usamos a soma
    /// das subidas registradas no histórico, que só cobre o tempo com o app aberto.
    /// </summary>
    private (double? Pts, bool Exact) MeasuredPts(DateTimeOffset from, DateTimeOffset to)
    {
        var bar = _host.Last?.Get(AnchorKind);
        if (PerWeek.IsChecked == true && bar != null) return (bar.Percent, true);

        var points = UsageHistory.Load(TimeSpan.MaxValue);
        double sum = 0;
        var any = false;
        HistoryPoint? prev = null;
        foreach (var p in points)
        {
            var v = p.Get(AnchorKind);
            if (v == null) continue;
            if (prev != null && p.At >= from && p.At <= to)
            {
                var prevV = prev.Get(AnchorKind);
                if (prevV != null && p.At - prev.At <= TimeSpan.FromMinutes(90))
                {
                    var d = v.Value - prevV.Value;
                    if (d > 0) sum += d;
                    any = true;
                }
            }
            prev = p;
        }
        return (any ? sum : null, false);
    }

    // ------------------------------------------------------------------
    // Desenho
    // ------------------------------------------------------------------

    private void Redraw()
    {
        if (!_ready) return;

        var (from, to, label) = Period();
        var settings = _host.Settings;
        _projects = _index.Aggregate(from, to, settings);

        var kindLabel = settings.LabelFor(AnchorKind);
        var bar = _host.Last?.Get(AnchorKind);
        var (measured, exact) = MeasuredPts(from, to);

        // Resumo
        if (bar != null)
        {
            var remaining = Math.Max(0, 100 - bar.Percent);
            SummaryLine.Text = $"{kindLabel}: {bar.Percent:0.#}% consumido · restam {remaining:0.#}%"
                               + (bar.ResetsAt != null ? $" · {bar.ResetText()}" : "");
        }
        else
        {
            SummaryLine.Text = $"{kindLabel}: consumo atual indisponível.";
        }

        var hint = new System.Text.StringBuilder();
        hint.Append("Período: ").Append(label).Append(". ");
        if (measured != null)
        {
            hint.Append(exact
                ? $"Os {measured:0.#} pts consumidos na janela são repartidos abaixo na proporção do custo de cada projeto."
                : $"Repartindo {measured:0.#} pts somados do histórico local (só o tempo com o app aberto).");
        }
        else
        {
            hint.Append("Sem medição de pontos para este período: as fatias são relativas.");
        }
        hint.Append(" Consumo fora do Claude Code (claude.ai, outra máquina) não aparece nas transcrições e acaba diluído entre os projetos.");
        SummaryHint.Text = hint.ToString();

        ProjectsTitle.Text = FableScope ? "Projetos — só Fable 5" : "Projetos — todos os modelos";
        DrawProjects(measured);

        if (_selected != null && !_projects.Exists(p => p.Path == _selected)) _selected = null;
        if (_selected == null && _projects.Count > 0) _selected = _projects[0].Path;
        DrawPrompts();
    }

    private void DrawProjects(double? measuredPts)
    {
        ProjectsPanel.Children.Clear();

        if (_projects.Count == 0)
        {
            ProjectsPanel.Children.Add(Hint(_scanning
                ? "Lendo…"
                : "Nenhum consumo registrado neste período."));
            return;
        }

        var settings = _host.Settings;
        var top = 0.0;
        foreach (var p in _projects)
        {
            var share = FableScope ? p.FableShare : p.Share;
            if (share > top) top = share;
        }

        foreach (var p in _projects)
        {
            var share = FableScope ? p.FableShare : p.Share;
            var totals = FableScope ? p.Fable : p.All;
            if (totals.Turns == 0) continue;

            ProjectsPanel.Children.Add(BuildProjectRow(p, share, totals, measuredPts, top));
        }
    }

    private UIElement BuildProjectRow(ProjectUsage p, double share, TokenTotals totals, double? measuredPts, double topShare)
    {
        var selected = p.Path == _selected;

        var border = new Border
        {
            Background = selected ? BarRenderer.Swatch("PanelBrush2") : Brushes.Transparent,
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 0, 0, 3),
            Cursor = Cursors.Hand,
            ToolTip = p.Path
        };
        border.MouseLeftButtonUp += (_, _) =>
        {
            _selected = p.Path;
            DrawProjects(MeasuredPts(Period().From, Period().To).Pts);
            DrawPrompts();
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var name = new TextBlock
        {
            Text = p.Name,
            FontSize = 13,
            FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(name, 0);
        grid.Children.Add(name);

        var pct = new TextBlock
        {
            Text = (share * 100).ToString("0.#") + "%",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = BarRenderer.Swatch("AccentBrush")
        };
        Grid.SetColumn(pct, 1);
        grid.Children.Add(pct);

        var ptsText = measuredPts != null
            ? (share * measuredPts.Value).ToString("0.#") + " pts"
            : "—";
        var pts = new TextBlock
        {
            Text = ptsText,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = BarRenderer.Swatch("TextBrush"),
            ToolTip = "Pontos percentuais do limite atribuídos a este projeto"
        };
        Grid.SetColumn(pts, 2);
        grid.Children.Add(pts);

        // barra de participação, normalizada pelo maior projeto
        var track = new Border
        {
            Height = 5,
            CornerRadius = new CornerRadius(2.5),
            Background = BarRenderer.Swatch("TrackBrush"),
            Margin = new Thickness(0, 6, 0, 0),
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var inner = new Grid();
        var frac = topShare > 0 ? Math.Clamp(share / topShare, 0, 1) : 0;
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(frac, 0.0001), GridUnitType.Star) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - frac, 0.0001), GridUnitType.Star) });
        var fill = new Border
        {
            CornerRadius = new CornerRadius(2.5),
            Background = BarRenderer.Swatch("AccentBrush"),
            MinWidth = share > 0 ? 3 : 0
        };
        Grid.SetColumn(fill, 0);
        inner.Children.Add(fill);
        track.Child = inner;
        Grid.SetRow(track, 1);
        Grid.SetColumnSpan(track, 3);
        grid.Children.Add(track);

        var detail = new TextBlock
        {
            Text = $"{totals.Turns:n0} turnos · {p.Prompts:n0} prompts · {totals.Output:n0} tokens de saída",
            FontSize = 10.5,
            Foreground = BarRenderer.Swatch("MutedBrush"),
            Margin = new Thickness(0, 5, 0, 0)
        };
        Grid.SetRow(detail, 1);
        Grid.SetColumnSpan(detail, 3);

        var stack = new StackPanel();
        stack.Children.Add(grid);
        stack.Children.Add(detail);
        border.Child = stack;
        return border;
    }

    private void DrawPrompts()
    {
        PromptsPanel.Children.Clear();

        if (_selected == null)
        {
            PromptsTitle.Text = "Prompts";
            PromptsPanel.Children.Add(Hint("Escolha um projeto acima."));
            return;
        }

        var (from, to, _) = Period();
        var settings = _host.Settings;
        var prompts = _index.PromptsFor(_selected, from, to, settings);

        PromptsTitle.Text = "Prompts — " + TranscriptIndex.FriendlyName(_selected);

        if (prompts.Count == 0)
        {
            PromptsPanel.Children.Add(Hint(
                "Nenhum prompt digitado neste projeto no período. O consumo veio de subagentes ou de "
                + "sessões cuja pasta de trabalho é esta, com o prompt registrado no projeto de origem."));
            return;
        }

        if (SortCost.IsChecked == true)
            prompts.Sort((a, b) => b.Cost.Weighted(settings).CompareTo(a.Cost.Weighted(settings)));

        var project = _projects.Find(p => p.Path == _selected);
        var projectShare = project != null ? (FableScope ? project.FableShare : project.Share) : 0;
        var (measured, _) = MeasuredPts(from, to);
        var projectPts = measured != null ? projectShare * measured.Value : (double?)null;

        const int max = 300;
        var shown = 0;
        foreach (var e in prompts)
        {
            if (shown++ >= max) break;
            PromptsPanel.Children.Add(BuildPromptRow(e, projectPts));
        }

        if (prompts.Count > max)
            PromptsPanel.Children.Add(Hint($"Mostrando {max} de {prompts.Count} prompts do período."));
    }

    private UIElement BuildPromptRow(PromptEntry e, double? projectPts)
    {
        var text = TranscriptIndex.ReadPromptText(e.File, e.Offset, 600);
        if (string.IsNullOrWhiteSpace(text)) text = "(sem texto)";
        var oneLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
        if (oneLine.Length > 150) oneLine = oneLine.Substring(0, 150) + "…";

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });

        var when = new TextBlock
        {
            Text = e.At.ToLocalTime().ToString("dd/MM HH:mm"),
            FontSize = 11,
            Foreground = BarRenderer.Swatch("MutedBrush"),
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetColumn(when, 0);
        grid.Children.Add(when);

        var body = new TextBlock
        {
            Text = oneLine,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            ToolTip = new ToolTip
            {
                Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 },
                MaxWidth = 560
            }
        };
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);

        var costText = projectPts != null
            ? (e.Share * projectPts.Value).ToString("0.##") + " pts"
            : (e.Share * 100).ToString("0.#") + "%";
        var cost = new TextBlock
        {
            Text = costText,
            FontSize = 11.5,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = BarRenderer.Swatch("AccentBrush"),
            ToolTip = $"{e.Cost.Turns} turnos · {e.Cost.Output:n0} tokens de saída · " +
                      $"{e.Cost.CacheRead:n0} de leitura de cache"
        };
        Grid.SetColumn(cost, 2);
        grid.Children.Add(cost);

        return grid;
    }

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Foreground = BarRenderer.Swatch("MutedBrush")
    };
}

