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
    private bool _timelinePending;

    /// <summary>Últimas preferências aplicadas, para redesenhar sem depender de quem chamou.</summary>
    private AppSettings _settings = new();

    public GadgetWindow()
    {
        InitializeComponent();
        VersionItem.Header = AppInfo.NameWithVersion;

        // tooltip que dura enquanto o mouse estiver ali, em vez dos 5 s padrão do WPF
        ToolTipService.SetShowDuration(this, 120000);
        ToolTipService.SetInitialShowDelay(this, 300);

        // redesenhar a faixa fecharia o tooltip aberto: espera o mouse sair dela
        CallsPanel.MouseLeave += (_, _) =>
        {
            if (_timelinePending) DrawCallTimeline();
        };

        HardwarePanel.MouseLeave += (_, _) =>
        {
            if (_hardwarePendente) RenderHardware(_hardware, _settings);
        };
    }

    // ------------------------------------------------------------------

    public void ApplySettings(AppSettings s)
    {
        _settings = s;
        Topmost = s.GadgetTopmost;
        Opacity = s.GadgetOpacity;

        // vertical: largura fixa (as linhas se esticam); horizontal: a largura vem das células
        Root.Width = s.GadgetOrientation == BarOrientation.Horizontal ? double.NaN : 214;
        RootScale.ScaleX = s.GadgetScale;
        RootScale.ScaleY = s.GadgetScale;
        _locked = s.GadgetLocked;
        DragHandle.Cursor = _locked ? Cursors.Arrow : Cursors.SizeAll;

        // sendo o único indicador visível, esconder deixaria o app sem porta de entrada
        _canHide = s.TrayEnabled || s.ShowTaskbarBar;
        CloseBtn.Visibility = _canHide ? Visibility.Visible : Visibility.Collapsed;

        if (s.GadgetLeft.HasValue && s.GadgetTop.HasValue)
        {
            Left = s.GadgetLeft.Value;
            Top = s.GadgetTop.Value;
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
        _settings = s;
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
        {
            BarsPanel.Children.Add(s.GadgetOrientation == BarOrientation.Horizontal
                ? BuildRateStrip(bars, s)
                : BuildRateRow(s));
        }

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
        DrawCallTimeline();
    }

    private UIElement BuildRow(UsageBar bar, AppSettings s)
        => BarRenderer.BuildRow(bar, s, s.GadgetShowReset);

    /// <summary>
    /// Um velocímetro por limite, alinhado com as células acima. No gadget horizontal sobra
    /// espaço à direita, e mostrar só um deixava a informação dos outros escondida atrás de um
    /// clique. Clicar em um deles passa a ser o limite acompanhado nos outros indicadores.
    /// </summary>
    private UIElement BuildRateStrip(System.Collections.Generic.List<UsageBar> bars, AppSettings s)
    {
        var host = AppHost.Current;

        var strip = new Border
        {
            BorderBrush = Swatch("LineBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 9, 0, 0),
            Margin = new Thickness(0, 2, 0, 0)
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            var rate = host?.RateFor(bar.Kind) ?? RateReading.Empty;
            var selected = bar.Kind == s.RateKind;

            if (i > 0) row.Children.Add(BarRenderer.BuildCellSeparator());

            var cell = new StackPanel();

            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new Border
            {
                Child = GaugeRenderer.Build(rate, 34),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0)
            });
            head.Children.Add(new TextBlock
            {
                Text = ConsumptionRate.Format(rate),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(GaugeRenderer.ColorFor(rate)),
                VerticalAlignment = VerticalAlignment.Center
            });
            cell.Children.Add(head);

            var caption = ConsumptionRate.FormatTimeLeft(rate);
            cell.Children.Add(new TextBlock
            {
                Text = caption.Length > 0 ? caption : s.LabelFor(bar.Kind),
                FontSize = 10,
                Foreground = Swatch("MutedBrush"),
                Margin = new Thickness(0, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            var hit = new Border
            {
                Child = cell,
                Background = selected ? Swatch("PanelBrush2") : Brushes.Transparent,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 4, 8, 4),
                Cursor = Cursors.Hand,
                ToolTip = GaugeRenderer.Describe(rate, s, bar.Kind)
                          + "\n\nClique para acompanhar este limite nos outros indicadores."
            };
            var kind = bar.Kind;
            hit.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                AppHost.Current?.SetRateKind(kind);
            };
            row.Children.Add(hit);
        }

        strip.Child = row;
        return strip;
    }

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
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = GaugeRenderer.Describe(rate, s, s.RateKind) + "\n\nClique para ver o ritmo de outro limite."
        };
        border.MouseLeftButtonUp += (_, e) =>
        {
            // o gadget inteiro arrasta com o botão esquerdo: aqui o clique é só do velocímetro
            e.Handled = true;
            AppHost.Current?.CycleRateKind();
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Border
        {
            Child = GaugeRenderer.Build(rate, 44),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock
        {
            Text = ConsumptionRate.Format(rate),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(GaugeRenderer.ColorFor(rate))
        });
        head.Children.Add(new TextBlock
        {
            Text = "  " + s.LabelFor(s.RateKind) + " ↻",
            FontSize = 10,
            Foreground = Swatch("MutedBrush"),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 2)
        });
        text.Children.Add(head);

        var caption = ConsumptionRate.FormatTimeLeft(rate);
        if (caption.Length > 0)
        {
            text.Children.Add(new TextBlock
            {
                Text = caption,
                FontSize = 10.5,
                Foreground = Swatch("MutedBrush"),
                Margin = new Thickness(0, 1, 0, 0)
            });
        }
        row.Children.Add(text);

        border.Child = row;
        return border;
    }

    /// <summary>
    /// Linha do tempo das ultimas consultas a API: a mais antiga a esquerda, a mais nova a direita.
    /// Cada consulta nova empurra a mais antiga para fora. Verde respondeu, ambar recusou por
    /// limite de consultas, vermelho falhou. Os vazios sao consultas que ainda nao aconteceram.
    /// </summary>
    /// <summary>Redesenha só a linha do tempo — chamado a cada batimento, sem tocar nas barras.</summary>
    public void RefreshTimeline() => DrawCallTimeline();

    // ------------------------------------------------------------------
    // Sensores do computador
    // ------------------------------------------------------------------

    private HardwareSnapshot _hardware = HardwareSnapshot.Empty;
    private bool _hardwarePendente;

    /// <summary>
    /// Bloco de CPU, GPU e memória embaixo dos limites, quando ligado nas configurações. Quais
    /// componentes aparecem são os mesmos escolhidos para o painel do computador — assim não
    /// existem duas listas dizendo coisas diferentes sobre os mesmos três sensores.
    /// </summary>
    public void RenderHardware(HardwareSnapshot hw, AppSettings s)
    {
        _hardware = hw;
        _settings = s;

        // a leitura chega a cada dois segundos e trocar os elementos fecha o tooltip aberto:
        // com o mouse em cima, espera ele sair
        if (HardwarePanel.IsMouseOver)
        {
            _hardwarePendente = true;
            return;
        }
        _hardwarePendente = false;

        HardwarePanel.Children.Clear();
        if (!s.GadgetShowHardware) return;

        var quais = new List<(string Rotulo, ComponentReading Leitura)>();
        if (s.PcShowCpu) quais.Add(("CPU", hw.Cpu));
        if (s.PcShowGpu) quais.Add(("GPU", hw.Gpu));
        if (s.PcShowRam) quais.Add(("RAM", hw.Ram));
        if (quais.Count == 0) return;

        var bloco = new Border
        {
            BorderBrush = Swatch("LineBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 9, 0, 0),
            Margin = new Thickness(0, 2, 0, 0)
        };

        var lendo = !hw.Ok && !hw.Cpu.HasAnything && !hw.Gpu.HasAnything;
        if (lendo)
        {
            bloco.Child = new TextBlock
            {
                Text = hw.Error != null ? "sensores sem leitura" : "lendo sensores…",
                FontSize = 11,
                Foreground = Swatch("MutedBrush"),
                ToolTip = hw.Error
            };
        }
        else if (s.GadgetOrientation == BarOrientation.Horizontal)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            for (var i = 0; i < quais.Count; i++)
            {
                if (i > 0) row.Children.Add(BarRenderer.BuildCellSeparator());
                row.Children.Add(HardwareRenderer.Cell(quais[i].Rotulo, quais[i].Leitura, hw));
            }
            bloco.Child = row;
        }
        else
        {
            var col = new StackPanel();
            foreach (var (rotulo, leitura) in quais)
                col.Children.Add(HardwareRenderer.Row(rotulo, leitura, hw));

            // a última linha não precisa da folga de baixo: ela empurraria o rodapé sem motivo
            if (col.Children.Count > 0 && col.Children[^1] is Border ultima && ultima.Child is StackPanel sp)
                sp.Margin = new Thickness(0);

            bloco.Child = col;
        }

        HardwarePanel.Children.Add(bloco);

        // o bloco muda a altura do gadget: reencaixa na tela depois do layout
        Dispatcher.BeginInvoke(new Action(ClampToScreen), DispatcherPriority.Loaded);
    }

    private void DrawCallTimeline()
    {
        // com o mouse sobre a faixa, trocar as bolinhas fecharia o tooltip que está sendo lido
        if (CallsPanel.IsMouseOver)
        {
            _timelinePending = true;
            return;
        }
        _timelinePending = false;

        CallsPanel.Children.Clear();
        if (AppHost.Current is { } h && !h.Settings.ShowCallTimeline) return;

        var calls = AppHost.Current?.Calls.Recent() ?? new System.Collections.Generic.List<ApiCall>();
        var vazios = ApiCallLog.Capacity - calls.Count;

        for (var i = 0; i < vazios; i++)
            CallsPanel.Children.Add(Dot(null));

        foreach (var call in calls)
            CallsPanel.Children.Add(Dot(call));

    }

    /// <summary>
    /// Uma bolinha da linha do tempo, com o horário e o resultado daquela consulta no tooltip.
    /// A bolinha tem 6 px, pequena demais para acertar com o mouse, então quem recebe o ponteiro
    /// é uma área transparente maior em volta dela.
    /// </summary>
    private UIElement Dot(ApiCall? call)
    {
        var cor = call?.Outcome switch
        {
            ApiOutcome.Ok => Swatch("OkBrush"),
            ApiOutcome.RateLimited => Swatch("WarnBrush"),
            ApiOutcome.Failed => Swatch("DangerBrush"),
            _ => Swatch("TrackBrush")
        };

        var bolinha = new System.Windows.Shapes.Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = cor,
            Opacity = call == null ? 0.45 : 1,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        return new Border
        {
            Child = bolinha,
            Background = Brushes.Transparent,
            Width = 11,
            Height = 14,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = call?.Describe() ?? "consulta ainda não realizada"
        };
    }


    private static Brush Swatch(string key) => BarRenderer.Swatch(key);

    // ------------------------------------------------------------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var host = AppHost.Current;
        if (host == null) return;

        if (!host.Settings.GadgetLeft.HasValue || !host.Settings.GadgetTop.HasValue)
            PlaceBottomRight();
        else
            ClampToScreen();
    }

    /// <summary>
    /// Área útil do monitor onde o gadget está — e não do primário.
    /// SystemParameters.WorkArea devolve sempre o monitor principal, o que arrastava o gadget de
    /// volta para ele a cada atualização em quem usa mais de uma tela.
    /// </summary>
    private Rect CurrentScreenWorkArea()
    {
        try
        {
            var source = PresentationSource.FromVisual(this);
            var toDevice = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
            var fromDevice = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;

            var w = ActualWidth > 0 ? ActualWidth : 214;
            var h = ActualHeight > 0 ? ActualHeight : 120;
            var cx = (double.IsNaN(Left) ? 0 : Left) + w / 2;
            var cy = (double.IsNaN(Top) ? 0 : Top) + h / 2;

            var center = toDevice.Transform(new Point(cx, cy));
            var screen = System.Windows.Forms.Screen.FromPoint(
                new System.Drawing.Point((int)center.X, (int)center.Y));
            var wa = screen.WorkingArea;

            var topLeft = fromDevice.Transform(new Point(wa.Left, wa.Top));
            var bottomRight = fromDevice.Transform(new Point(wa.Right, wa.Bottom));
            return new Rect(topLeft, bottomRight);
        }
        catch
        {
            return SystemParameters.WorkArea; // sem informação de monitor: melhor o primário que nada
        }
    }

    private void ClampToScreen()
    {
        var area = CurrentScreenWorkArea();
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
