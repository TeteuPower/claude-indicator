using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClaudeIndicator.Core;
using ClaudeIndicator.Views;

namespace ClaudeIndicator.Views;

public partial class GadgetWindow : Window
{
    private bool _locked;
    private bool _canHide = true;

    public GadgetWindow()
    {
        InitializeComponent();
        VersionItem.Header = AppInfo.NameWithVersion;
    }

    // ------------------------------------------------------------------

    public void ApplySettings(AppSettings s)
    {
        Topmost = s.GadgetTopmost;
        Opacity = s.GadgetOpacity;

        // vertical: largura fixa (as linhas se esticam); horizontal: a largura vem das células
        Root.Width = s.GadgetOrientation == BarOrientation.Horizontal ? double.NaN : 214;
        RootScale.ScaleX = s.GadgetScale;
        RootScale.ScaleY = s.GadgetScale;
        _locked = s.GadgetLocked;
        Root.Cursor = _locked ? Cursors.Arrow : Cursors.SizeAll;

        // sendo o único indicador visível, esconder deixaria o app sem porta de entrada
        _canHide = s.TrayEnabled || s.ShowTaskbarBar;
        CloseBtn.Visibility = _canHide ? Visibility.Visible : Visibility.Collapsed;

        if (s.GadgetLeft >= 0 && s.GadgetTop >= 0)
        {
            Left = s.GadgetLeft;
            Top = s.GadgetTop;
            ClampToScreen();
        }
        else
        {
            PlaceBottomRight();
        }
    }

    private void PlaceBottomRight()
    {
        var area = SystemParameters.WorkArea;
        var w = ActualWidth > 0 ? ActualWidth : 214;
        var h = ActualHeight > 0 ? ActualHeight : 130;
        Left = Math.Max(area.Left, area.Right - w - 24);
        Top = Math.Max(area.Top, area.Bottom - h - 24);
        AppHost.Current?.SaveGadgetPosition(Left, Top);
    }

    public void Render(UsageSnapshot? snap, AppSettings s)
    {
        BarsPanel.Children.Clear();

        if (snap == null)
        {
            BarsPanel.Children.Add(new TextBlock
            {
                Text = "Carregando…",
                Foreground = Swatch("MutedBrush"),
                FontSize = 12
            });
            FooterText.Text = "";
            return;
        }

        var bars = snap.Visible(s);

        if (bars.Count == 0)
        {
            var msg = new TextBlock
            {
                Text = snap.Error ?? "Sem dados de consumo.",
                Foreground = Swatch("MutedBrush"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 190,
                Margin = new Thickness(0, 0, 0, 8)
            };
            BarsPanel.Children.Add(msg);

            var btn = new Button
            {
                Content = "Abrir configurações",
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 5, 10, 5),
                FontSize = 11.5
            };
            btn.Click += OnSettingsClick;
            BarsPanel.Children.Add(btn);
        }
        else if (s.GadgetOrientation == BarOrientation.Horizontal)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            for (var i = 0; i < bars.Count; i++)
            {
                if (i > 0) row.Children.Add(BarRenderer.BuildCellSeparator());
                row.Children.Add(BarRenderer.BuildCell(bars[i], s, s.GadgetShowReset));
            }
            BarsPanel.Children.Add(row);
        }
        else
        {
            foreach (var bar in bars)
                BarsPanel.Children.Add(BuildRow(bar, s));
        }

        if (bars.Count > 0 && s.ShowRateGadget)
            BarsPanel.Children.Add(BuildRateRow(s));

        // o conteúdo muda de tamanho (linha do ritmo, orientação, rótulos): reencaixa na tela
        // depois do layout, senão a largura ainda é a antiga e o gadget fica cortado na borda
        Dispatcher.BeginInvoke(new Action(ClampToScreen), DispatcherPriority.Loaded);

        var when = (snap.DataAt ?? snap.FetchedAt).ToLocalTime();
        string footer;
        if (snap.Stale)
            footer = (snap.RateLimited ? "aguardando limite da API" : "falha ao atualizar") + $" · dados de {when:HH:mm}";
        else
            footer = $"atualizado {when:HH:mm}" + (!snap.Ok && bars.Count > 0 ? " · falha ao atualizar" : "");
        FooterText.Text = footer;
    }

    private UIElement BuildRow(UsageBar bar, AppSettings s)
        => BarRenderer.BuildRow(bar, s, s.GadgetShowReset);

    /// <summary>Linha do velocímetro: arco à esquerda, ritmo e tempo restante à direita.</summary>
    private UIElement BuildRateRow(AppSettings s)
    {
        var rate = AppHost.Current?.Rate ?? RateReading.Empty;

        var border = new Border
        {
            BorderBrush = Swatch("LineBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 9, 0, 0),
            Margin = new Thickness(0, 2, 0, 0),
            ToolTip = GaugeRenderer.Describe(rate, s, s.RateKind)
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Border
        {
            Child = GaugeRenderer.Build(rate, 44),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = ConsumptionRate.Format(rate),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(GaugeRenderer.ColorFor(rate))
        });

        var caption = ConsumptionRate.FormatTimeLeft(rate);
        text.Children.Add(new TextBlock
        {
            Text = caption.Length > 0 ? caption : s.LabelFor(s.RateKind).ToLowerInvariant(),
            FontSize = 10.5,
            Foreground = Swatch("MutedBrush"),
            Margin = new Thickness(0, 1, 0, 0)
        });
        row.Children.Add(text);

        border.Child = row;
        return border;
    }

    private static Brush Swatch(string key) => BarRenderer.Swatch(key);

    // ------------------------------------------------------------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var host = AppHost.Current;
        if (host == null) return;

        if (host.Settings.GadgetLeft < 0 || host.Settings.GadgetTop < 0)
            PlaceBottomRight();
        else
            ClampToScreen();
    }

    private void ClampToScreen()
    {
        var area = SystemParameters.WorkArea;
        var w = ActualWidth > 0 ? ActualWidth : 214;
        var h = ActualHeight > 0 ? ActualHeight : 120;

        if (double.IsNaN(Left)) Left = area.Left + 40;
        if (double.IsNaN(Top)) Top = area.Top + 40;

        var before = (Left, Top);

        if (Left + w > area.Right) Left = Math.Max(area.Left, area.Right - w);
        if (Top + h > area.Bottom) Top = Math.Max(area.Top, area.Bottom - h);
        if (Left < area.Left) Left = area.Left + 8;
        if (Top < area.Top) Top = area.Top + 8;

        if (before != (Left, Top)) AppHost.Current?.SaveGadgetPosition(Left, Top);
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_locked) return;
        try
        {
            DragMove();
        }
        catch
        {
            return;
        }
        AppHost.Current?.SaveGadgetPosition(Left, Top);
    }

    private void OnRootMouseEnter(object sender, MouseEventArgs e) => Tools.Opacity = 1;

    private void OnRootMouseLeave(object sender, MouseEventArgs e) => Tools.Opacity = 0;

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        var host = AppHost.Current;
        if (host != null) _ = host.RefreshAsync(true);
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => AppHost.Current?.ShowSettings();

    private void OnDashboardClick(object sender, RoutedEventArgs e) => AppHost.Current?.ShowDashboard();

    private void OnHistoryClick(object sender, RoutedEventArgs e) => AppHost.Current?.ShowHistory();

    private void OnProjectsClick(object sender, RoutedEventArgs e) => AppHost.Current?.ShowProjects();

    private void OnHideClick(object sender, RoutedEventArgs e)
    {
        if (!_canHide)
        {
            AppHost.Current?.ShowSettings();
            return;
        }
        Hide();
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => AppHost.Current?.Exit();
}
