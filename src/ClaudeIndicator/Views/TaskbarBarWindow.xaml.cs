using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ClaudeIndicator.Core;

namespace ClaudeIndicator.Views;

/// <summary>
/// Indicadores desenhados dentro da barra de tarefas, no espaço livre dela.
///
/// O Windows 11 não aceita mais deskbands, então isto é uma janela sem borda posicionada sobre
/// a barra e mantida por cima. Um timer reposiciona quando a barra muda (resolução, DPI, mover
/// de lado, ocultar automaticamente) e esconde o painel quando um aplicativo em tela cheia está
/// na frente.
/// </summary>
public partial class TaskbarBarWindow : Window
{
    private readonly DispatcherTimer _follow = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private AppSettings _settings = new();
    private UsageSnapshot? _snapshot;
    private bool _hidden;
    private DateTime _lastTopmost = DateTime.MinValue;
    private bool _pendingRender;

    public TaskbarBarWindow()
    {
        InitializeComponent();
        VersionItem.Header = AppInfo.NameWithVersion;

        _follow.Tick += (_, _) => Reposition();
        Loaded += (_, _) =>
        {
            Reposition();
            _follow.Start();
        };
        Closed += (_, _) => _follow.Stop();

        // tooltip que fica aberto enquanto o mouse estiver ali, em vez dos 5 s padrão do WPF
        ToolTipService.SetShowDuration(this, 120000);
        ToolTipService.SetInitialShowDelay(this, 350);
        ToolTipService.SetBetweenShowDelay(this, 0);

        MouseLeave += (_, _) =>
        {
            if (_pendingRender) Render(_snapshot, _settings);
        };
    }

    public void ApplySettings(AppSettings s)
    {
        _settings = s;

        // Alfa 0 deixaria o painel clicável-através: numa janela transparente o Windows decide o
        // hit-test pelo alfa do pixel, e o clique iria para a barra de tarefas embaixo. Um alfa
        // de 1/255 é invisível a olho nu e mantém a janela clicável.
        var alpha = (byte)Math.Clamp(Math.Round(s.TaskbarBarOpacity * 255), 1, 255);
        Root.Background = new SolidColorBrush(Color.FromArgb(alpha, 0x1F, 0x1E, 0x1D));

        Render(_snapshot, s);
        Reposition();
    }

    // ------------------------------------------------------------------
    // Conteúdo
    // ------------------------------------------------------------------

    public void Render(UsageSnapshot? snap, AppSettings s)
    {
        _snapshot = snap;
        _settings = s;

        // Redesenhar troca os elementos e, com isso, fecha o tooltip que estiver aberto. Com o
        // mouse em cima, espera ele sair: o dado tem minutos de idade, a leitura é de segundos.
        if (IsMouseOver)
        {
            _pendingRender = true;
            return;
        }
        _pendingRender = false;

        CellsPanel.Children.Clear();

        var bars = snap?.Visible(s) ?? new List<UsageBar>();
        if (bars.Count == 0)
        {
            CellsPanel.Children.Add(new TextBlock
            {
                Text = snap == null ? "Claude · carregando…" : "Claude · sem dados",
                FontSize = 11.5,
                Foreground = BarRenderer.Swatch("MutedBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });
            Reposition();
            return;
        }

        for (var i = 0; i < bars.Count; i++)
        {
            if (i > 0) CellsPanel.Children.Add(Divider());
            CellsPanel.Children.Add(BuildCell(bars[i], s));
        }

        if (s.ShowRateTaskbar)
        {
            var rate = AppHost.Current?.Rate ?? RateReading.Empty;
            CellsPanel.Children.Add(Divider());
            CellsPanel.Children.Add(BuildGaugeCell(rate, s));
        }

        Reposition();
    }

    private static UIElement Divider() => new Border
    {
        Width = 1,
        Background = BarRenderer.Swatch("LineBrush"),
        Margin = new Thickness(10, 9, 10, 9)
    };

    /// <summary>
    /// Velocímetro do ritmo: arco pequeno + o número, que é o que se lê de relance. O rótulo diz
    /// de qual limite é o ritmo, e clicar passa para o próximo — por isso ele não diz só "Ritmo".
    /// </summary>
    private UIElement BuildGaugeCell(RateReading rate, AppSettings s)
    {
        var scale = Math.Clamp(s.TaskbarBarScale, 0.8, 1.6);

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new Border
        {
            Child = GaugeRenderer.Build(rate, 34 * scale),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0)
        });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        // linha do filtro: nome do limite + seta, indicando que dá para trocar
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
        header.Children.Add(new TextBlock
        {
            Text = s.LabelFor(s.RateKind),
            FontSize = 9.5 * scale,
            Foreground = BarRenderer.Swatch("MutedBrush")
        });
        header.Children.Add(new TextBlock
        {
            Text = " ↻",
            FontSize = 9 * scale,
            Foreground = BarRenderer.Swatch("MutedBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        text.Children.Add(header);

        text.Children.Add(new TextBlock
        {
            Text = ConsumptionRate.Format(rate),
            FontSize = 12 * scale,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(GaugeRenderer.ColorFor(rate))
        });
        row.Children.Add(text);

        var cell = new Border
        {
            Child = row,
            Background = System.Windows.Media.Brushes.Transparent,
            Cursor = Cursors.Hand,
            Padding = new Thickness(2, 0, 2, 0),
            ToolTip = GaugeRenderer.Describe(rate, s, s.RateKind) + "\n\nClique para ver o ritmo de outro limite."
        };
        cell.MouseLeftButtonUp += (_, e) =>
        {
            // sem isto o clique subiria para o painel e abriria a janela
            e.Handled = true;
            AppHost.Current?.CycleRateKind();
        };

        return cell;
    }

    /// <summary>Célula compacta: cabe na altura da barra sem apertar o texto.</summary>
    private UIElement BuildCell(UsageBar bar, AppSettings s)
    {
        var scale = Math.Clamp(s.TaskbarBarScale, 0.8, 1.6);
        var cell = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        cell.Children.Add(new TextBlock
        {
            Text = s.LabelFor(bar.Kind),
            FontSize = 9.5 * scale,
            Foreground = BarRenderer.Swatch("MutedBrush"),
            Margin = new Thickness(0, 0, 0, 2)
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = Math.Round(bar.Percent) + "%",
            FontSize = 12.5 * scale,
            FontWeight = FontWeights.SemiBold,
            Foreground = BarRenderer.BrushFor(bar.Percent, s),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 32 * scale
        });

        var bars = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) };

        var track = new Border
        {
            Width = 52 * scale,
            Height = 5,
            CornerRadius = new CornerRadius(2.5),
            Background = BarRenderer.Swatch("TrackBrush"),
            ClipToBounds = true
        };
        var grid = new Grid();
        var frac = Math.Clamp(bar.Fraction, 0, 1);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(frac, 0.0001), GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - frac, 0.0001), GridUnitType.Star) });
        var fill = new Border
        {
            CornerRadius = new CornerRadius(2.5),
            Background = BarRenderer.BrushFor(bar.Percent, s),
            MinWidth = bar.Percent > 0 ? 3 : 0
        };
        Grid.SetColumn(fill, 0);
        grid.Children.Add(fill);
        track.Child = grid;
        bars.Children.Add(track);

        // fio do tempo decorrido, logo abaixo: comparar com o consumo mostra se está adiantado
        var timeFrac = s.ShowTimeProgress ? bar.TimeFraction() : null;
        if (timeFrac != null)
        {
            bars.Children.Add(BarRenderer.BuildTimeLine(timeFrac.Value, 52 * scale, 2, new Thickness(0, 2, 0, 0)));
        }

        row.Children.Add(bars);
        cell.Children.Add(row);

        var tip = $"{s.LabelFor(bar.Kind)}: {bar.Percent:0.#}% usado, restam {Math.Max(0, 100 - bar.Percent):0.#}%";
        if (bar.ResetsAt != null) tip += $"\n{bar.ResetText()} (às {bar.ResetClock()})";
        tip += "\n\nClique para abrir o painel.";

        // área de clique da célula inteira, e não só onde há pixel pintado
        var hit = new Border
        {
            Child = cell,
            Background = System.Windows.Media.Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = tip
        };
        hit.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            AppHost.Current?.ShowDashboard();
        };

        return hit;
    }

    // ------------------------------------------------------------------
    // Posicionamento
    // ------------------------------------------------------------------

    private void Reposition()
    {
        if (_hidden) return;

        var span = TaskbarInfo.FreeSpan(_settings.TaskbarBarAnchor);
        var bounds = TaskbarInfo.Bounds();
        if (span == null || bounds == null || !TaskbarInfo.IsHorizontal())
        {
            // barra na vertical ou não encontrada: não há espaço previsível para ocupar
            Visibility = Visibility.Collapsed;
            return;
        }

        var source = PresentationSource.FromVisual(this);
        var m = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var scaleX = m.M11 == 0 ? 1 : m.M11;
        var scaleY = m.M22 == 0 ? 1 : m.M22;

        var bar = bounds.Value;
        if (TaskbarInfo.FullscreenAppInFront(SystemParameters.PrimaryScreenWidth / scaleX,
                                             SystemParameters.PrimaryScreenHeight / scaleY))
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        Visibility = Visibility.Visible;

        var height = bar.Height * scaleY;
        var width = ActualWidth > 0 ? ActualWidth : 260;
        var availableDip = (span.Value.To - span.Value.From) * scaleX;
        if (width > availableDip) width = availableDip;

        var left = _settings.TaskbarBarAnchor == TaskbarAnchor.Left
            ? span.Value.From * scaleX + _settings.TaskbarBarOffset
            : span.Value.To * scaleX - width - _settings.TaskbarBarOffset;
        var top = bar.Top * scaleY;

        // Só escrever quando muda de verdade: reposicionar a cada tique fazia o tooltip fechar e
        // reabrir sem parar, porque mexer em Left/Top/Topmost derruba o balão aberto.
        if (Math.Abs(Height - height) > 0.5) Height = height;
        if (Math.Abs(Left - left) > 0.5) Left = left;
        if (Math.Abs(Top - top) > 0.5) Top = top;

        ReassertTopmost();
    }

    /// <summary>
    /// A barra de tarefas também é topmost, então de tempos em tempos é preciso reafirmar a nossa
    /// posição na ordem-Z. Fazer isso a cada tique piscava o tooltip, e fazer com o mouse em cima
    /// o fecharia: só acontece de 10 em 10 segundos e nunca sob o cursor.
    /// </summary>
    private void ReassertTopmost()
    {
        if (IsMouseOver) return;
        if (DateTime.UtcNow - _lastTopmost < TimeSpan.FromSeconds(10)) return;

        _lastTopmost = DateTime.UtcNow;
        Topmost = false;
        Topmost = true;
    }

    /// <summary>Não rouba o foco ao aparecer: continua sendo um indicador, não uma janela.</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var helper = new WindowInteropHelper(this);
        NativeMethods.MakeNoActivate(helper.Handle);
        Reposition();
    }

    /// <summary>
    /// Esconde e marca como escondido. O flag e essencial: o timer que acompanha a barra de
    /// tarefas chama Reposition() a cada tique e voltaria a marcar a janela como visivel, ou seja,
    /// um Hide() puro seria desfeito em menos de um segundo.
    /// </summary>
    public void HidePanel()
    {
        _hidden = true;
        Hide();
    }

    public void ShowInTaskbarArea()
    {
        _hidden = false;
        Show();
        Reposition();
    }

    // ------------------------------------------------------------------
    // Interação
    // ------------------------------------------------------------------

    private void OnClick(object sender, MouseButtonEventArgs e) => AppHost.Current?.ShowDashboard();

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        var host = AppHost.Current;
        if (host != null) _ = host.RefreshAsync(true);
    }

    private void OnDashboardClick(object sender, RoutedEventArgs e) => AppHost.Current?.ShowDashboard();

    private void OnSettingsClick(object sender, RoutedEventArgs e) => AppHost.Current?.ShowSettings();

    private void OnHideClick(object sender, RoutedEventArgs e) => AppHost.Current?.HideTaskbarBar();

    private void OnExitClick(object sender, RoutedEventArgs e) => AppHost.Current?.Exit();
}
