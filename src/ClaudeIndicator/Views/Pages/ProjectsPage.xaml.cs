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
    private double _periodTotal;
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
        _periodTotal = _index.TotalWeight(from, to, settings, FableScope);

        var kindLabel = settings.LabelFor(AnchorKind);
        var bar = _host.Last?.Get(AnchorKind);
        var (measured, exact) = MeasuredPts(from, to);

        if (bar != null)
        {
            var remaining = Math.Max(0, 100 - bar.Percent);
            SummaryLine.Text = $"{kindLabel}: {bar.Percent:0.#}% do limite consumido · restam {remaining:0.#}%"
                               + (bar.ResetsAt != null ? $" · {bar.ResetText()}" : "");
        }
        else
        {
            SummaryLine.Text = $"{kindLabel}: consumo atual indisponível.";
        }

        var hint = new System.Text.StringBuilder();
        hint.Append("Período: ").Append(label)
            .Append(". As fatias abaixo são do consumo do período, não do limite: somadas dão 100%. ");
        if (measured != null && exact)
            hint.Append($"Como {measured:0.#}% do limite foi gasto na janela, cada 10% aqui equivale a {measured.Value / 10:0.#} pontos do limite. ");
        hint.Append("Consumo fora do Claude Code (claude.ai, outra máquina) não aparece nas transcrições e acaba diluído entre os projetos.");
        SummaryHint.Text = hint.ToString();

        ProjectsTitle.Text = FableScope ? "Projetos — só Fable 5" : "Projetos — todos os modelos";
        DrawProjects();

        if (_selected != null && !_projects.Exists(p => p.Path == _selected)) _selected = null;
        if (_selected == null && _projects.Count > 0) _selected = _projects[0].Path;
        DrawPrompts();
    }

    private void DrawProjects()
    {
        ProjectsPanel.Children.Clear();

        if (_projects.Count == 0)
        {
            ProjectsPanel.Children.Add(Hint(_scanning ? "Lendo…" : "Nenhum consumo registrado neste período."));
            return;
        }

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
            ProjectsPanel.Children.Add(BuildProjectCard(p, share, totals, top));
        }
    }

    /// <summary>Cartão de um projeto: fatia do consumo em destaque, com o volume como apoio.</summary>
    private UIElement BuildProjectCard(ProjectUsage p, double share, TokenTotals totals, double topShare)
    {
        var selected = p.Path == _selected;

        var card = new Border
        {
            Width = 254,
            Background = BarRenderer.Swatch(selected ? "PanelBrush2" : "PanelBrush"),
            BorderBrush = selected ? BarRenderer.Swatch("AccentBrush") : BarRenderer.Swatch("LineBrush"),
            BorderThickness = new Thickness(selected ? 1.5 : 1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12, 14, 13),
            Margin = new Thickness(0, 0, 12, 12),
            Cursor = Cursors.Hand,
            ToolTip = p.FolderExists
                ? p.Path
                : p.Path + "\n\nEsta pasta não existe mais. O caminho é o que estava gravado na "
                         + "transcrição quando o consumo aconteceu — o projeto foi movido, renomeado ou apagado desde então."
        };
        card.MouseLeftButtonUp += (_, _) =>
        {
            _selected = p.Path;
            DrawProjects();
            DrawPrompts();
        };

        var stack = new StackPanel();

        stack.Children.Add(new TextBlock
        {
            Text = p.Name,
            FontSize = 12.5,
            FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = BarRenderer.Swatch("TextBrush")
        });

        var valueRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
        valueRow.Children.Add(new TextBlock
        {
            Text = BarRenderer.FormatShare(share),
            FontSize = 25,
            FontWeight = FontWeights.SemiBold,
            Foreground = BarRenderer.Swatch("AccentBrush")
        });
        valueRow.Children.Add(new TextBlock
        {
            Text = "do consumo",
            FontSize = 11,
            Foreground = BarRenderer.Swatch("MutedBrush"),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(6, 0, 0, 4)
        });
        stack.Children.Add(valueRow);

        // barra proporcional ao maior projeto, para comparar de relance
        var track = new Border
        {
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = BarRenderer.Swatch("TrackBrush"),
            Margin = new Thickness(0, 9, 0, 0),
            ClipToBounds = true
        };
        var grid = new Grid();
        var frac = topShare > 0 ? Math.Clamp(share / topShare, 0, 1) : 0;
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(frac, 0.0001), GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - frac, 0.0001), GridUnitType.Star) });
        var fill = new Border
        {
            CornerRadius = new CornerRadius(3),
            Background = BarRenderer.Swatch("AccentBrush"),
            MinWidth = share > 0 ? 3 : 0
        };
        Grid.SetColumn(fill, 0);
        grid.Children.Add(fill);
        track.Child = grid;
        stack.Children.Add(track);

        var meta = $"{totals.Turns:n0} turnos · {p.Prompts:n0} prompts";
        if (!p.FolderExists) meta += "  ·  pasta não existe mais";
        stack.Children.Add(new TextBlock
        {
            Text = meta,
            FontSize = 10.5,
            Foreground = BarRenderer.Swatch(p.FolderExists ? "MutedBrush" : "WarnBrush"),
            Margin = new Thickness(0, 9, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        if (!FableScope && p.Fable.Turns > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = _host.Settings.FableLabel + ": " + BarRenderer.FormatShare(p.FableShare) + " do consumo do modelo",
                FontSize = 10.5,
                Foreground = BarRenderer.Swatch("WarnBrush"),
                Margin = new Thickness(0, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        card.Child = stack;
        return card;
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
        var prompts = _index.PromptsFor(_selected, from, to, settings, _periodTotal);

        PromptsTitle.Text = "Prompts — " + TranscriptIndex.FriendlyName(_selected);

        if (prompts.Count == 0)
        {
            PromptsPanel.Children.Add(Hint(
                "Nenhum prompt digitado neste projeto no período. O consumo veio de subagentes ou de "
                + "sessões cuja pasta de trabalho é esta, com o prompt registrado no projeto de origem."));
            return;
        }

        if (SortCost.IsChecked == true)
            prompts.Sort((a, b) => b.Share.CompareTo(a.Share));

        var maxShare = 0.0;
        foreach (var e in prompts)
        {
            if (e.Share > maxShare) maxShare = e.Share;
        }

        const int max = 300;
        var shown = 0;
        foreach (var e in prompts)
        {
            if (shown++ >= max) break;
            PromptsPanel.Children.Add(BuildPromptRow(e, maxShare));
        }

        if (prompts.Count > max)
            PromptsPanel.Children.Add(Hint($"Mostrando {max} de {prompts.Count} prompts do período."));
    }

    private UIElement BuildPromptRow(PromptEntry e, double maxShare)
    {
        var text = TranscriptIndex.ReadPromptText(e.File, e.Offset, 600);
        if (string.IsNullOrWhiteSpace(text)) text = "(sem texto)";
        var oneLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
        if (oneLine.Length > 150) oneLine = oneLine.Substring(0, 150) + "…";

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });

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
            Margin = new Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);

        // barrinha relativa ao prompt mais caro do projeto: dá escala a valores minúsculos
        var track = new Border
        {
            Height = 5,
            Width = 46,
            CornerRadius = new CornerRadius(2.5),
            Background = BarRenderer.Swatch("TrackBrush"),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 5, 0, 0),
            ClipToBounds = true
        };
        var inner = new Grid();
        var frac = maxShare > 0 ? Math.Clamp(e.Share / maxShare, 0, 1) : 0;
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(frac, 0.0001), GridUnitType.Star) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - frac, 0.0001), GridUnitType.Star) });
        var fill = new Border
        {
            CornerRadius = new CornerRadius(2.5),
            Background = BarRenderer.Swatch("AccentBrush"),
            MinWidth = e.Share > 0 ? 2 : 0
        };
        Grid.SetColumn(fill, 0);
        inner.Children.Add(fill);
        track.Child = inner;
        Grid.SetColumn(track, 2);
        grid.Children.Add(track);

        var cost = new TextBlock
        {
            Text = BarRenderer.FormatShare(e.Share),
            FontSize = 11.5,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = BarRenderer.Swatch("AccentBrush"),
            ToolTip = $"{BarRenderer.FormatShare(e.Share)} do consumo do período\n" +
                      $"{e.Cost.Turns} turnos · {e.Cost.Output:n0} tokens de saída · " +
                      $"{e.Cost.CacheRead:n0} de leitura de cache"
        };
        Grid.SetColumn(cost, 3);
        grid.Children.Add(cost);

        // linha inteira clicável: abre o prompt completo, com o custo detalhado
        var row = new Border
        {
            Child = grid,
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 5, 6, 5),
            Margin = new Thickness(-6, 0, -6, 4),
            Cursor = Cursors.Hand,
            ToolTip = "Clique para ver o prompt completo"
        };
        row.MouseEnter += (_, _) => row.Background = BarRenderer.Swatch("PanelBrush2");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => ShowPromptDetail(e);

        return row;
    }

    private void ShowPromptDetail(PromptEntry entry)
    {
        var project = _projects.Find(p => p.Path == entry.Project);
        var window = new PromptDetailWindow(entry, project?.Name ?? TranscriptIndex.FriendlyName(entry.Project),
            project?.FolderExists ?? true)
        {
            Owner = Window.GetWindow(this)
        };
        window.ShowDialog();
    }

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Foreground = BarRenderer.Swatch("MutedBrush")
    };
}

   