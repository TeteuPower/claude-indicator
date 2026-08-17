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
    private bool _userHidden;

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
    }

    public void ApplySettings(AppSettings s)
    {
        _settings = s;
        Root.Background = new SolidColorBrush(Color.FromArgb(
            (byte)Math.Clamp(s.TaskbarBarOpacity * 255, 0, 255), 0x1F, 0x1E, 0x1D));
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

    /// <summary>Velocímetro do ritmo: arco pequeno + o número, que é o que se lê de relance.</summary>
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
        text.Children.Add(new TextBlock
        {
            Text = "Ritmo",
            FontSize = 9.5 * scale,
            Foreground = BarRenderer.Swatch("MutedBrush"),
            Margin = new Thickness(0, 0, 0, 2)
        });
        text.Children.Add(new TextBlock
        {
            Text = ConsumptionRate.Format(rate),
            FontSize = 12 * scale,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(GaugeRenderer.ColorFor(rate))
        });
        row.Children.Add(text);

        row.ToolTip = GaugeRenderer.Describe(rate, s, s.RateKind);
        return row;
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

        var track = new Border
        {
            Width = 52 * scale,
            Height = 5,
            CornerRadius = new CornerRadius(2.5),
            Background = BarRenderer.Swatch("TrackBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(5, 0, 0, 0),
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
        row.Children.Add(track);

        cell.Children.Add(row);

        var tip = $"{s.LabelFor(bar.Kind)}: {bar.Percent:0.#}% usado, restam {Math.Max(0, 100 - bar.Percent):0.#}%";
        if (bar.ResetsAt != null) tip += $"\n{bar.ResetText()} (às {bar.ResetClock()})";
        cell.ToolTip = tip;

        return cell;
    }

    // ------------------------------------------------------------------
    // Posicionamento
    // ------------------------------------------------------------------

    private void Reposition()
    {
        if (_userHidden) return;

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
        Height = bar.Height * scaleY;

        var width = ActualWidth > 0 ? ActualWidth : 260;
        var availableDip = (span.Value.To - span.Value.From) * scaleX;
        if (width > availableDip) width = availableDip;

        var left = _settings.TaskbarBarAnchor == TaskbarAnchor.Left
            ? span.Value.From * scaleX + _settings.TaskbarBarOffset
            : span.Value.To * scaleX - width - _settings.TaskbarBarOffset;

        Left = left;
        Top = bar.Top * scaleY;

        // a barra de tarefas também é topmost: reafirmar mantém o painel visível sobre ela
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

    public void HideByUser()
    {
        _userHidden = true;
        Hide();
    }

    public void ShowInTaskbarArea()
    {
        _userHidden = false;
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
