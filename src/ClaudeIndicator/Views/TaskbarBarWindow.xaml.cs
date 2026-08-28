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

/// <summary>O que este painel mostra.</summary>
public enum PanelKind
{
    /// <summary>Limites da assinatura Claude.</summary>
    Ai,

    /// <summary>Sensores do computador: CPU, GPU e memória.</summary>
    Pc
}

/// <summary>
/// Indicadores desenhados dentro da barra de tarefas, no espaço livre dela.
///
/// O Windows 11 não aceita mais deskbands, então isto é uma janela sem borda posicionada sobre
/// a barra e mantida por cima. Um timer reposiciona quando a barra muda (resolução, DPI, mover
/// de lado, ocultar automaticamente) e esconde o painel quando um aplicativo em tela cheia está
/// na frente. A mesma janela serve aos dois painéis — o da IA e o do computador —, mudando o
/// conteúdo e o lado conforme o <see cref="PanelKind"/>.
/// </summary>
public partial class TaskbarBarWindow : Window
{
    private readonly DispatcherTimer _follow = new() { Interval = TimeSpan.FromMilliseconds(900) };

    /// <summary>Tipo de painel — decide o conteúdo e de que lado da barra ele fica.</summary>
    public PanelKind Kind { get; }

    private HardwareSnapshot _hardware = HardwareSnapshot.Empty;
    private AppSettings _settings = new();
    private UsageSnapshot? _snapshot;
    private bool _hidden;
    private DateTime _lastTopmost = DateTime.MinValue;
    private bool _pendingRender;
    private bool _timelinePending;

    public TaskbarBarWindow(PanelKind kind = PanelKind.Ai)
    {
        Kind = kind;
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
            if (_pendingRender) RenderCurrent();
            else if (_timelinePending) DrawCallTimeline();
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

        // marcada na raiz, a preferência desce sozinha para todo texto do painel
        OutlinedText.SetOutlineEnabled(Root, s.PanelOutline);

        RenderCurrent();
        Reposition();
    }

    /// <summary>
    /// Redesenha o conteúdo certo para este painel. Existe porque chamar o caminho da IA no
    /// painel do PC pintava "Claude · carregando…" — não há UsageSnapshot ali — até a próxima
    /// leitura de sensores, o que aparecia como uma recarga com mensagem piscando.
    /// </summary>
    private void RenderCurrent()
    {
        if (Kind == PanelKind.Pc) RenderHardware(_hardware, _settings);
        else Render(_snapshot, _settings);
    }

    // ------------------------------------------------------------------
    // Conteúdo
    // ------------------------------------------------------------------

    /// <summary>Conteúdo do painel do PC. O da IA usa Render(UsageSnapshot, ...).</summary>
    public void RenderHardware(HardwareSnapshot hw, AppSettings s)
    {
        _hardware = hw;
        _settings = s;

        if (IsMouseOver)
        {
            _pendingRender = true;
            return;
        }
        _pendingRender = false;

        CellsPanel.Children.Clear();
        var cells = 0;

        if (s.PcShowCpu)
        {
            if (cells++ > 0) CellsPanel.Children.Add(Divider());
            CellsPanel.Children.Add(BuildHardwareCell("CPU", hw.Cpu, s, hw));
        }
        if (s.PcShowGpu)
        {
            if (cells++ > 0) CellsPanel.Children.Add(Divider());
            CellsPanel.Children.Add(BuildHardwareCell("GPU", hw.Gpu, s, hw));
        }
        if (s.PcShowRam)
        {
            if (cells++ > 0) CellsPanel.Children.Add(Divider());
            CellsPanel.Children.Add(BuildHardwareCell("RAM", hw.Ram, s, hw));
        }

        if (cells == 0 || (!hw.Ok && !hw.Cpu.HasAnything && !hw.Gpu.HasAnything))
        {
            CellsPanel.Children.Clear();
            CellsPanel.Children.Add(new OutlinedText
            {
                Text = hw.Error != null ? "PC · sem leitura" : "PC · lendo…",
                FontSize = 11.5,
                Foreground = BarRenderer.Swatch("MutedBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = hw.Error
            });
        }

        Reposition();
    }

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
            CellsPanel.Children.Add(new OutlinedText
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

        DrawCallTimeline();
        Reposition();
    }

    /// <summary>Redesenha só a linha do tempo — chamada a cada batimento, sem refazer as células.</summary>
    public void RefreshTimeline() => DrawCallTimeline();

    /// <summary>
    /// Linha do tempo dos últimos ciclos de comunicação com a API, a mais recente à direita.
    /// Verde respondeu, âmbar não conseguiu falar por limite, vermelho falhou, e o ponto vazado é
    /// ciclo sem consulta porque o consumo não mudou.
    /// </summary>
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
        if (!_settings.ShowCallTimeline) return;

        var calls = AppHost.Current?.Calls.Recent() ?? new List<ApiCall>();
        for (var i = 0; i < ApiCallLog.Capacity - calls.Count; i++)
            CallsPanel.Children.Add(Dot(null));
        foreach (var call in calls)
            CallsPanel.Children.Add(Dot(call));
    }

    private UIElement Dot(ApiCall? call)
    {
        var cor = call?.Outcome switch
        {
            ApiOutcome.Ok => BarRenderer.Swatch("OkBrush"),
            ApiOutcome.RateLimited => BarRenderer.Swatch("WarnBrush"),
            ApiOutcome.Failed => BarRenderer.Swatch("DangerBrush"),
            _ => _settings.PanelOutline ? TrilhaBorda : BarRenderer.Swatch("TrackBrush")
        };

        var bolinha = new System.Windows.Shapes.Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = cor,
            Opacity = call == null || call.Outcome == ApiOutcome.Idle ? 0.5 : 1,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        return new Border
        {
            Child = bolinha,
            Background = System.Windows.Media.Brushes.Transparent,
            Width = 11,
            Height = 22,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = call?.Describe() ?? "ciclo ainda não registrado"
        };
    }

    /// <summary>
    /// Célula de um componente: rótulo, uso em destaque e as medidas de apoio (temperatura e
    /// watts) numa linha abaixo. A cor segue a métrica que mais preocupa, que é a temperatura
    /// quando existe — uso alto é trabalho, temperatura alta é problema.
    /// </summary>
    private UIElement BuildHardwareCell(string rotulo, ComponentReading c, AppSettings s, HardwareSnapshot hw)
    {
        var scale = Math.Clamp(s.TaskbarBarScale, 0.8, 1.6);
        var cell = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
        head.Children.Add(new OutlinedText
        {
            Text = rotulo,
            FontSize = 9.5 * scale,
            Foreground = BarRenderer.Swatch("MutedBrush")
        });

        var apoio = Support(c, rotulo);
        if (apoio.Length > 0)
        {
            head.Children.Add(new OutlinedText
            {
                Text = "  " + apoio,
                FontSize = 9.5 * scale,
                Foreground = new SolidColorBrush(HardwareColor(c)),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        cell.Children.Add(head);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new OutlinedText
        {
            Text = c.Load.Format("%"),
            FontSize = 12.5 * scale,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(LoadColor(c.Load)),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 34 * scale
        });

        var largura = 44 * scale;
        var track = new Border
        {
            Width = largura,
            Height = 5,
            CornerRadius = new CornerRadius(2.5),
            Background = FundoDaTrilha,
            BorderBrush = TrilhaBorda,
            BorderThickness = EsperaDaTrilha,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(5, 0, 0, 0),
            ClipToBounds = true
        };
        var grid = new Grid();
        var frac = Math.Clamp((c.Load.Value ?? 0) / 100.0, 0, 1);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(frac, 0.0001), GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - frac, 0.0001), GridUnitType.Star) });
        var fill = new Border
        {
            CornerRadius = new CornerRadius(2.5),
            Background = ScaleGradient(largura),
            MinWidth = frac > 0 ? 3 : 0
        };
        Grid.SetColumn(fill, 0);
        grid.Children.Add(fill);
        track.Child = grid;
        row.Children.Add(track);

        cell.Children.Add(row);

        return new Border
        {
            Child = cell,
            Background = System.Windows.Media.Brushes.Transparent,
            Padding = new Thickness(2, 0, 2, 0),
            ToolTip = DescribeHardware(rotulo, c, hw)
        };
    }

    /// <summary>Medidas de apoio da célula: temperatura, watts ou memória, conforme o componente.</summary>
    private static string Support(ComponentReading c, string rotulo)
    {
        var partes = new List<string>();
        if (c.Temperature.HasValue) partes.Add(c.Temperature.Format("°"));
        if (c.Power.HasValue) partes.Add(c.Power.Format(" W"));
        if (rotulo == "RAM" && c.MemoryUsed.HasValue) partes.Add(c.MemoryUsed.Format(" GB", 1));
        return string.Join(" · ", partes);
    }

    private static readonly Color Verde = Color.FromArgb(255, 76, 195, 138);
    private static readonly Color Amarelo = Color.FromArgb(255, 232, 176, 75);
    private static readonly Color Vermelho = Color.FromArgb(255, 240, 92, 92);
    private static readonly Color Cinza = Color.FromArgb(255, 156, 151, 145);

    /// <summary>
    /// Régua de cor da barra: verde no início, amarela no meio, vermelha no fim. O gradiente é
    /// medido em unidades absolutas sobre a largura da trilha, e não sobre a parte preenchida —
    /// sem isso ele se comprimiria dentro do preenchimento e a barra ficaria vermelha já nos
    /// primeiros por cento, que é o oposto da ideia.
    /// </summary>
    private static LinearGradientBrush ScaleGradient(double larguraDaTrilha)
    {
        var g = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, 0),
            EndPoint = new Point(larguraDaTrilha, 0)
        };
        g.GradientStops.Add(new GradientStop(Verde, 0.0));
        g.GradientStops.Add(new GradientStop(Amarelo, 0.5));
        g.GradientStops.Add(new GradientStop(Vermelho, 1.0));
        g.Freeze();
        return g;
    }

    /// <summary>Cor da mesma régua no ponto onde a barra parou — é o que o número mostra.</summary>
    private static Color LoadColor(Reading load)
    {
        if (!load.HasValue) return Cinza;

        var f = Math.Clamp(load.Value!.Value / 100.0, 0, 1);
        return f <= 0.5
            ? Mix(Verde, Amarelo, f / 0.5)
            : Mix(Amarelo, Vermelho, (f - 0.5) / 0.5);
    }

    private static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            255,
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    /// <summary>Temperatura manda na cor de apoio; sem ela, os watts não têm faixa universal.</summary>
    /// <summary>
    /// A trilha vazia da barra precisa aparecer tanto sobre a barra escura quanto sobre um papel de
    /// parede claro. Um preenchimento escuro resolve o segundo caso e a borda clara o primeiro —
    /// isolados, cada um sumiria justamente no outro. É o par que acompanha o texto com contorno.
    /// </summary>
    private static readonly Brush TrilhaEscura = Congelado(Color.FromArgb(0x73, 0, 0, 0));
    private static readonly Brush TrilhaBorda = Congelado(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF));

    /// <summary>Preenchimento da trilha no estilo escolhido.</summary>
    private Brush FundoDaTrilha => _settings.PanelOutline ? TrilhaEscura : BarRenderer.Swatch("TrackBrush");

    /// <summary>Borda da trilha; sem contorno não há borda, como era antes.</summary>
    private Thickness EsperaDaTrilha => new(_settings.PanelOutline ? 1 : 0);

    private static Brush Congelado(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Color HardwareColor(ComponentReading c)
    {
        if (!c.Temperature.HasValue) return Cinza;
        var t = c.Temperature.Value!.Value;
        if (t >= 90) return Vermelho;
        if (t >= 80) return Amarelo;
        return Cinza;
    }

    private static string DescribeHardware(string rotulo, ComponentReading c, HardwareSnapshot hw)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(rotulo);
        if (c.Name.Length > 0) sb.Append(" — ").Append(c.Name);

        if (c.Load.HasValue) sb.Append("\nUso: ").Append(c.Load.Format("%"));
        if (c.Temperature.HasValue) sb.Append("\nTemperatura: ").Append(c.Temperature.Format(" °C"));
        if (c.Power.HasValue) sb.Append("\nConsumo: ").Append(c.Power.Format(" W", 1));
        if (c.MemoryUsed.HasValue)
        {
            sb.Append("\nMemória: ").Append(c.MemoryUsed.Format(" GB", 1));
            if (c.MemoryTotal.HasValue) sb.Append(" de ").Append(c.MemoryTotal.Format(" GB", 1));
        }

        if (rotulo == "CPU" && c.Temperature.HasValue && hw.CpuTemperatureFromThermalZone)
        {
            sb.Append("\n\nA temperatura vem da zona térmica ACPI — o conjunto ao redor do ")
              .Append("processador, não o sensor interno dele. Acompanha o aquecimento de perto, ")
              .Append("mas pode diferir alguns graus do que o Afterburner mostra.");
        }

        if (rotulo == "CPU" && !c.Power.HasValue)
        {
            sb.Append("\n\nOs watts da CPU só existem nos registradores do processador, que ")
              .Append("precisam de um driver de kernel — é a única medida daqui que depende disso.");
        }

        return sb.ToString();
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
        header.Children.Add(new OutlinedText
        {
            Text = s.LabelFor(s.RateKind),
            FontSize = 9.5 * scale,
            Foreground = BarRenderer.Swatch("MutedBrush")
        });
        header.Children.Add(new OutlinedText
        {
            Text = " ↻",
            FontSize = 9 * scale,
            Foreground = BarRenderer.Swatch("MutedBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        text.Children.Add(header);

        text.Children.Add(new OutlinedText
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

        cell.Children.Add(new OutlinedText
        {
            Text = s.LabelFor(bar.Kind),
            FontSize = 9.5 * scale,
            Foreground = BarRenderer.Swatch("MutedBrush"),
            Margin = new Thickness(0, 0, 0, 2)
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new OutlinedText
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
            Background = FundoDaTrilha,
            BorderBrush = TrilhaBorda,
            BorderThickness = EsperaDaTrilha,
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

        // marca do tempo decorrido, no próprio trilho: preenchimento além dela é consumo adiantado
        var timeFrac = s.ShowTimeProgress ? bar.TimeFraction() : null;
        bars.Children.Add(BarRenderer.TrackWithMarker(track, timeFrac, 5));

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

    /// <summary>Lado da barra onde este painel se ancora.</summary>
    private TaskbarAnchor Anchor =>
        Kind == PanelKind.Pc ? _settings.PcPanelAnchor : _settings.TaskbarBarAnchor;

    private void Reposition()
    {
        if (_hidden) return;

        var span = TaskbarInfo.FreeSpan(Anchor);
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
        if (TaskbarInfo.FullscreenAppInFront())
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        Visibility = Visibility.Visible;

        var height = bar.Height * scaleY;
        var width = ActualWidth > 0 ? ActualWidth : 260;
        var availableDip = (span.Value.To - span.Value.From) * scaleX;
        if (width > availableDip) width = availableDip;

        var left = Anchor == TaskbarAnchor.Left
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
