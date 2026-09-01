using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Runtime.InteropServices;
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

        // Toda ativação de janela refaz a ordem-Z. Escutar o aviso do sistema tira a espera pelo
        // próximo tique: o painel volta para cima em milissegundos, e não em quase um segundo.
        ForegroundWatcher.Changed += OnForegroundChanged;
        Closed += (_, _) =>
        {
            _follow.Stop();
            ForegroundWatcher.Changed -= OnForegroundChanged;
        };

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

        if (s.ShowThemeToggle && cells > 0)
        {
            CellsPanel.Children.Add(Divider());
            CellsPanel.Children.Add(BuildThemeCell(s));
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

        // o botão do tema mora no painel do computador; sem ele, vem para cá em vez de sumir
        if (s.ShowThemeToggle && !s.ShowPcPanel)
        {
            CellsPanel.Children.Add(Divider());
            CellsPanel.Children.Add(BuildThemeCell(s));
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

        var apoio = HardwareRenderer.Support(c, rotulo);
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
            ToolTip = HardwareRenderer.Describe(rotulo, c, hw)
        };
    }

    // ------------------------------------------------------------------
    // Tema do Windows
    // ------------------------------------------------------------------

    /// <summary>
    /// Botão que troca o tema claro/escuro do Windows. Mostra o tema de DESTINO, não o atual: um
    /// sol quando está escuro, uma lua quando está claro — é o que o clique vai fazer, e o balão
    /// diz isso com todas as letras para não sobrar dúvida.
    ///
    /// Os ícones são desenhados como forma, e não como caractere de fonte. Já houve glifo virando
    /// quadrado vazio neste app por causa do estilo global de fonte, e um botão que não se explica
    /// é pior que botão nenhum.
    /// </summary>
    private UIElement BuildThemeCell(AppSettings s)
    {
        var claro = WindowsTheme.IsLight();
        var scale = s.TaskbarBarScale;
        var lado = 17 * scale;

        var icone = new Grid { Width = lado, Height = lado };
        var cor = BarRenderer.Swatch("TextBrush");

        // cópia escura por baixo, mais grossa: é o mesmo contorno do texto, para o ícone não
        // sumir sobre papel de parede claro
        if (s.PanelOutline)
        {
            foreach (var parte in BarRenderer.ThemeIcon(claro, lado, Contorno, 1.5))
                icone.Children.Add(parte);
        }
        foreach (var parte in BarRenderer.ThemeIcon(claro, lado, cor, 0))
            icone.Children.Add(parte);

        var alvo = claro ? "escuro" : "claro";
        var botao = new Border
        {
            Child = icone,
            Background = System.Windows.Media.Brushes.Transparent,
            Padding = new Thickness(6, 0, 2, 0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = $"Tema do Windows: {(claro ? "claro" : "escuro")}.\nClique para mudar para o {alvo}."
        };
        botao.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;   // senão o clique subiria e abriria o painel
            WindowsTheme.Toggle();
            RenderCurrent();    // o ícone passa a mostrar o novo destino
        };
        return botao;
    }

    private static readonly Brush Contorno = Congelado(Color.FromArgb(0xE6, 0, 0, 0));

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
    private static Color HardwareColor(ComponentReading c)
    {
        if (!c.Temperature.HasValue) return Cinza;
        var t = c.Temperature.Value!.Value;
        if (t >= 90) return Vermelho;
        if (t >= 80) return Amarelo;
        return Cinza;
    }

    /// <summary>Borda da trilha; sem contorno não há borda, como era antes.</summary>
    private Thickness EsperaDaTrilha => new(_settings.PanelOutline ? 1 : 0);

    private static Brush Congelado(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>
    /// A trilha vazia da barra precisa aparecer tanto sobre a barra escura quanto sobre um papel de
    /// parede claro. Um preenchimento escuro resolve o segundo caso e a borda clara o primeiro —
    /// isolados, cada um sumiria justamente no outro. É o par que acompanha o texto com contorno.
    /// </summary>
    private static readonly Brush TrilhaEscura = Congelado(Color.FromArgb(0x73, 0, 0, 0));
    private static readonly Brush TrilhaBorda = Congelado(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF));

    /// <summary>Preenchimento da trilha no estilo escolhido.</summary>
    private Brush FundoDaTrilha => _settings.PanelOutline ? TrilhaEscura : BarRenderer.Swatch("TrackBrush");

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

    /// <summary>
    /// Monitor escolhido para este painel. Vazio é "onde estiver a barra principal" — que era o
    /// único comportamento possível antes.
    /// </summary>
    private string MonitorDevice =>
        Kind == PanelKind.Pc ? _settings.PcPanelMonitor : _settings.TaskbarBarMonitor;

    /// <summary>
    /// Onde o painel tem de estar, em pixels de tela. Uma das bordas é fixa — a esquerda ou a
    /// direita, conforme a âncora — e é ela que manda quando o conteúdo muda de largura.
    /// </summary>
    private sealed record Alvo(int? Esquerda, int? Direita, int Topo, int Altura);

    private Alvo? _alvo;

    /// <summary>
    /// Posiciona em pixels de tela, e não pelas propriedades Left/Top do WPF.
    ///
    /// O motivo é a mistura de escalas: com telas em 100% e 175% ao mesmo tempo, o WPF converte
    /// Left/Top usando um DPI que não é necessariamente o do monitor de destino, e o painel
    /// parava a centenas de pixels do lugar — às vezes no monitor errado, sem nunca convergir.
    /// Em pixels não há conversão nenhuma para dar errado: a barra é medida em pixels e a janela
    /// é colocada em pixels.
    /// </summary>
    private void Reposition()
    {
        if (_hidden) return;

        var taskbar = TaskbarInfo.Resolve(MonitorDevice);
        var span = TaskbarInfo.FreeSpan(taskbar, Anchor);
        if (taskbar == null || span == null || !taskbar.IsHorizontal)
        {
            // barra na vertical ou não encontrada: não há espaço previsível para ocupar
            _alvo = null;
            Visibility = Visibility.Collapsed;
            return;
        }

        if (TaskbarInfo.FullscreenAppInFront(taskbar))
        {
            _alvo = null;
            Visibility = Visibility.Collapsed;
            return;
        }

        Visibility = Visibility.Visible;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var atual)) return;

        var bar = taskbar.Bar;
        var larguraPx = Math.Max(atual.Right - atual.Left, 1);
        var disponivelPx = Math.Max(span.Value.To - span.Value.From, 1);
        var usadaPx = Math.Min(larguraPx, disponivelPx);

        // a distância da borda é escolhida em unidades de tela; convertida pela escala do monitor
        // de destino, ela vale o mesmo tanto no monitor de 100% quanto no de 175%
        var recuoPx = (int)Math.Round(_settings.TaskbarBarOffset * TaskbarInfo.ScaleOf(taskbar));

        _alvo = Anchor == TaskbarAnchor.Left
            ? new Alvo(span.Value.From + recuoPx, null, bar.Top, bar.Height)
            : new Alvo(null, span.Value.To - recuoPx, bar.Top, bar.Height);

        var esquerdaPx = _alvo.Esquerda ?? _alvo.Direita!.Value - usadaPx;

        // Só escrever quando muda de verdade: reposicionar a cada tique fazia o tooltip fechar e
        // reabrir sem parar, porque mexer em posição ou Topmost derruba o balão aberto.
        if (atual.Left != esquerdaPx || atual.Top != bar.Top || atual.Bottom - atual.Top != bar.Height)
        {
            SetWindowPos(hwnd, IntPtr.Zero, esquerdaPx, bar.Top, larguraPx, bar.Height,
                SWP_NOZORDER | SWP_NOACTIVATE);
        }

        ReassertTopmost();
    }

    /// <summary>
    /// Todo movimento passa por aqui e é corrigido para o alvo — inclusive os que o próprio WPF
    /// faz, e são muitos: o conteúdo muda de largura a cada leitura (SizeToContent), reafirmar o
    /// Topmost reposiciona, e a mudança de DPI ao entrar noutra tela também. Sem esta trava, cada
    /// um desses eventos devolvia a janela para a posição antiga em unidades do WPF, e o painel
    /// ficava indo e voltando.
    ///
    /// Ancorado à direita, a borda fixa é a direita: quando a largura muda, o x acompanha, e o
    /// painel não invade o relógio nem descola dele até o próximo tique.
    /// </summary>
    private IntPtr OnWindowPosChanging(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_WINDOWPOSCHANGING || _alvo == null || lParam == IntPtr.Zero) return IntPtr.Zero;

        var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
        var mantemTamanho = (pos.flags & SWP_NOSIZE) != 0;

        var largura = mantemTamanho
            ? (GetWindowRect(hwnd, out var r) ? r.Right - r.Left : pos.cx)
            : pos.cx;

        pos.x = _alvo.Esquerda ?? _alvo.Direita!.Value - Math.Max(largura, 1);
        pos.y = _alvo.Topo;
        pos.flags &= ~SWP_NOMOVE;

        if (!mantemTamanho) pos.cy = _alvo.Altura;

        Marshal.StructureToPtr(pos, lParam, false);
        return IntPtr.Zero;
    }

    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>
    /// A barra de tarefas também é topmost, e fechar ou ativar qualquer janela remexe a ordem-Z:
    /// o painel acaba atrás da barra e some.
    ///
    /// Reafirmar o topo a cada tique resolveria, mas mexer em Topmost derruba o tooltip aberto —
    /// era por isso que existia um intervalo de 10 segundos, e era por isso que fechar a janela do
    /// app fazia os indicadores sumirem por vários segundos até o próximo reforço.
    ///
    /// Agora a pergunta é outra: em vez de reafirmar de tempos em tempos, ele checa se está
    /// coberto e só age quando está. Coberto, volta na hora — e nem precisa poupar o tooltip,
    /// porque janela coberta não tem cursor em cima.
    /// </summary>
    private void ReassertTopmost()
    {
        // Com o cursor em cima não se mexe em nada: mexer em Topmost fecha o tooltip aberto, e o
        // balão do tooltip pode até cair sobre o painel e ser confundido com "estou coberto" — o
        // que viraria um ciclo de fechar e reabrir. E não se perde nada: janela coberta não tem
        // cursor em cima, por definição.
        if (IsMouseOver) return;

        // rede de segurança, para o caso de a checagem não enxergar alguma sobreposição
        if (!EstouCoberto() && DateTime.UtcNow - _lastTopmost < TimeSpan.FromSeconds(30)) return;

        _lastTopmost = DateTime.UtcNow;
        Topmost = false;
        Topmost = true;
    }

    /// <summary>
    /// Tem alguma janela na frente? Pergunta ao Windows quem atende no centro do painel: se a
    /// resposta não somos nós, alguém passou por cima. Duas chamadas, e sem depender de cursor.
    /// </summary>
    private bool EstouCoberto()
    {
        try
        {
            var meu = new WindowInteropHelper(this).Handle;
            if (meu == IntPtr.Zero) return false;
            if (!GetWindowRect(meu, out var r)) return false;
            if (r.Right - r.Left < 4 || r.Bottom - r.Top < 4) return false;

            var quem = WindowFromPoint(new NativePoint
            {
                X = (r.Left + r.Right) / 2,
                Y = (r.Top + r.Bottom) / 2
            });
            return quem != meu;
        }
        catch
        {
            return false; // sem resposta, deixa a rede de segurança cuidar
        }
    }

    /// <summary>
    /// Alguém foi para a frente: confere na hora se isso nos cobriu. O <see cref="ReassertTopmost"/>
    /// só age se cobriu de verdade, então este caminho não custa nada quando não houve problema.
    /// </summary>
    private void OnForegroundChanged()
    {
        if (_hidden || Visibility != Visibility.Visible) return;
        ReassertTopmost();
    }

    /// <summary>Volta para cima agora, sem esperar o próximo tique. Usado quando uma janela do app fecha.</summary>
    public void BringToFront() => OnForegroundChanged();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    /// <summary>Não rouba o foco ao aparecer: continua sendo um indicador, não uma janela.</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var helper = new WindowInteropHelper(this);
        NativeMethods.MakeNoActivate(helper.Handle);

        var source = HwndSource.FromHwnd(helper.Handle);
        source?.AddHook(OnWindowPosChanging);
        if (source != null) Closed += (_, _) => source.RemoveHook(OnWindowPosChanging);

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
