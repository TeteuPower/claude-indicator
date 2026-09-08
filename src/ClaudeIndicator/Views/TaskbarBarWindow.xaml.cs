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
            if (cells++ > 0) CellsPanel.Children.Add(PanelStyle.Divider());
            CellsPanel.Children.Add(PanelStyle.HardwareCell("CPU", hw.Cpu, s, hw, PanelStyle.ScaleOf(s)));
        }
        if (s.PcShowGpu)
        {
            if (cells++ > 0) CellsPanel.Children.Add(PanelStyle.Divider());
            CellsPanel.Children.Add(PanelStyle.HardwareCell("GPU", hw.Gpu, s, hw, PanelStyle.ScaleOf(s)));
        }
        if (s.PcShowRam)
        {
            if (cells++ > 0) CellsPanel.Children.Add(PanelStyle.Divider());
            CellsPanel.Children.Add(PanelStyle.HardwareCell("RAM", hw.Ram, s, hw, PanelStyle.ScaleOf(s)));
        }

        if (s.ShowThemeToggle && cells > 0)
        {
            CellsPanel.Children.Add(PanelStyle.Divider());
            CellsPanel.Children.Add(PanelStyle.ThemeCell(s, PanelStyle.ScaleOf(s), RenderCurrent));
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
            if (i > 0) CellsPanel.Children.Add(PanelStyle.Divider());
            CellsPanel.Children.Add(PanelStyle.Cell(bars[i], s, PanelStyle.ScaleOf(s)));
        }

        if (s.ShowRateTaskbar)
        {
            var rate = AppHost.Current?.Rate ?? RateReading.Empty;
            CellsPanel.Children.Add(PanelStyle.Divider());
            CellsPanel.Children.Add(PanelStyle.GaugeCell(rate, s, PanelStyle.ScaleOf(s)));
        }

        // o botão do tema mora no painel do computador; sem ele, vem para cá em vez de sumir
        if (s.ShowThemeToggle && !s.ShowPcPanel)
        {
            CellsPanel.Children.Add(PanelStyle.Divider());
            CellsPanel.Children.Add(PanelStyle.ThemeCell(s, PanelStyle.ScaleOf(s), RenderCurrent));
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
            CallsPanel.Children.Add(PanelStyle.Dot(null, _settings, 22));
        foreach (var call in calls)
            CallsPanel.Children.Add(PanelStyle.Dot(call, _settings, 22));
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
