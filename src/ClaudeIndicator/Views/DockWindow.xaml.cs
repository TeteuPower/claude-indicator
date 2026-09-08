using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ClaudeIndicator.Core;

namespace ClaudeIndicator.Views;

/// <summary>
/// Uma barra de tarefas nossa, encostada numa borda que o Windows deixou livre — topo, esquerda
/// ou direita —, com os painéis do app dentro.
///
/// A diferença entre isto e as outras janelas do app é o registro como <b>appbar</b>
/// (<see cref="DesktopAppBar"/>): quando ligado, o shell tira aquela faixa da área útil e as
/// janelas maximizadas param nela, como param na barra do Windows. Sem o registro a barra apenas
/// flutua por cima, o que também é oferecido — são as duas metades do "empurra as janelas OU fica
/// por cima" e podem valer ao mesmo tempo, que é como a barra do Windows se comporta.
///
/// O rodapé não é oferecido de propósito: lá já existe a barra do Windows, e duas barras
/// disputando a mesma borda só reserva o dobro de espaço.
/// </summary>
public partial class DockWindow : Window
{
    private readonly DesktopAppBar _appBar = new();

    // Rede de segurança do posicionamento: monitor que entra ou sai, barra do Windows que muda de
    // lado, tela que troca de resolução. O aviso do shell resolve a maioria dos casos; o tique
    // cobre o resto sem custo perceptível.
    private readonly DispatcherTimer _follow = new() { Interval = TimeSpan.FromMilliseconds(1500) };

    private AppSettings _settings = new();
    private UsageSnapshot? _snapshot;
    private HardwareSnapshot _hardware = HardwareSnapshot.Empty;

    private TaskbarInfo.RECT? _target;
    private bool _hiddenByUser;
    private bool _fullscreenApp;
    private bool _pendingRender;
    private DateTime _lastTopmost = DateTime.MinValue;

    public DockWindow()
    {
        InitializeComponent();
        VersionItem.Header = AppInfo.NameWithVersion;

        _follow.Tick += (_, _) => Reposition();

        ToolTipService.SetShowDuration(this, 120000);
        ToolTipService.SetInitialShowDelay(this, 350);
        ToolTipService.SetBetweenShowDelay(this, 0);

        // Com o cursor em cima, redesenhar fecharia o tooltip que está sendo lido: o conteúdo novo
        // espera a saída do mouse, igual aos outros painéis.
        MouseLeave += (_, _) =>
        {
            if (_pendingRender) Rebuild();
        };

        Loaded += (_, _) =>
        {
            Reposition();
            _follow.Start();
        };

        // Uma faixa reservada por uma janela que morreu fica presa até o Explorer reiniciar. Por
        // isso a remoção acontece em três pontos: fechar a janela, sair do app e o fim do processo.
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        Closed += (_, _) =>
        {
            _follow.Stop();
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
            _appBar.Remove();
        };
    }

    private void OnProcessExit(object? sender, EventArgs e) => _appBar.Remove();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.MakeNoActivate(hwnd);

        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(OnWindowMessage);

        // posiciona antes do primeiro quadro: a barra nasce no lugar, sem piscar no meio da tela
        Reposition();
    }

    // ------------------------------------------------------------------
    // Configuração e conteúdo
    // ------------------------------------------------------------------

    public void ApplySettings(AppSettings s)
    {
        var mudouRegistro = s.DockReserveSpace != _settings.DockReserveSpace
                            || s.DockEdge != _settings.DockEdge
                            || !string.Equals(s.DockMonitor, _settings.DockMonitor, StringComparison.OrdinalIgnoreCase);

        _settings = s;

        // Alfa 0 deixaria a barra clicável-através: numa janela transparente o Windows decide o
        // hit-test pelo alfa do pixel. 1/255 é invisível a olho nu e mantém o clique.
        var alpha = (byte)Math.Clamp(Math.Round(s.DockOpacity * 255), 1, 255);
        Root.Background = new SolidColorBrush(Color.FromArgb(alpha, 0x1B, 0x1A, 0x19));

        // a linha fica na borda de dentro — a que encara o desktop
        Edge.HorizontalAlignment = s.DockEdge switch
        {
            DockEdge.Left => HorizontalAlignment.Right,
            DockEdge.Right => HorizontalAlignment.Left,
            _ => HorizontalAlignment.Stretch
        };
        Edge.VerticalAlignment = s.DockEdge == DockEdge.Top ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
        Edge.Width = s.DockEdge == DockEdge.Top ? double.NaN : 1;
        Edge.Height = s.DockEdge == DockEdge.Top ? 1 : double.NaN;

        RootScale.ScaleX = s.DockScale;
        RootScale.ScaleY = s.DockScale;

        Topmost = s.DockTopmost && !_fullscreenApp;
        OutlinedText.SetOutlineEnabled(Root, s.PanelOutline);

        // Trocar de borda ou de modo é registrar de novo: o shell guarda a borda no registro, e
        // pedir SETPOS numa borda diferente da registrada não reserva nada.
        if (mudouRegistro) _appBar.Remove();

        Rebuild();
        Reposition();
    }

    public void Render(UsageSnapshot? snap, AppSettings s)
    {
        _snapshot = snap;
        _settings = s;
        Rebuild();
    }

    public void RenderHardware(HardwareSnapshot hw, AppSettings s)
    {
        _hardware = hw;
        _settings = s;
        Rebuild();
    }

    /// <summary>
    /// Monta os painéis. Em pé, cada painel é uma linha empilhada — o mesmo desenho do gadget.
    /// Deitada, cada painel é uma célula lado a lado — o mesmo desenho do painel da barra de
    /// tarefas. Nos dois casos o que muda é só a forma; os medidores são os mesmos do app.
    /// </summary>
    private void Rebuild()
    {
        if (IsMouseOver)
        {
            _pendingRender = true;
            return;
        }
        _pendingRender = false;

        var vertical = _settings.DockEdge != DockEdge.Top;

        Layout.Margin = vertical ? new Thickness(11, 10, 11, 10) : new Thickness(12, 5, 12, 5);
        MainPanel.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        MainPanel.VerticalAlignment = vertical ? VerticalAlignment.Top : VerticalAlignment.Center;
        MainPanel.Children.Clear();

        DockPanel.SetDock(SidePanel, vertical ? Dock.Bottom : Dock.Right);
        SidePanel.HorizontalAlignment = vertical ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        SidePanel.VerticalAlignment = vertical ? VerticalAlignment.Bottom : VerticalAlignment.Center;
        SidePanel.Margin = vertical ? new Thickness(0, 8, 0, 0) : new Thickness(12, 0, 0, 0);

        if (vertical) BuildVertical();
        else BuildHorizontal();

        BuildTimeline();
    }

    private void BuildVertical()
    {
        var blocos = 0;

        if (_settings.DockShowBars)
        {
            var bars = _snapshot?.Visible(_settings) ?? new List<UsageBar>();
            if (bars.Count > 0)
            {
                foreach (var bar in bars)
                    MainPanel.Children.Add(BarRenderer.BuildRow(bar, _settings, _settings.GadgetShowReset));
                blocos++;
            }
            else
            {
                MainPanel.Children.Add(Aviso(_snapshot == null ? "Consultando…" : _snapshot.Error ?? "Sem dados de consumo.", 168));
                blocos++;
            }
        }

        if (_settings.DockShowHardware)
        {
            var sensores = Sensores();
            if (sensores.Count > 0)
            {
                if (blocos > 0) MainPanel.Children.Add(Separador(true));
                foreach (var (rotulo, leitura) in sensores)
                    MainPanel.Children.Add(HardwareRenderer.Row(rotulo, leitura, _hardware));
                blocos++;
            }
        }

        if (_settings.DockShowRate && AppHost.Current is { } host)
        {
            if (blocos > 0) MainPanel.Children.Add(Separador(true));

            var leitura = host.Rate;
            var medidor = GaugeRenderer.Build(leitura, 78);
            if (medidor is FrameworkElement fe)
            {
                fe.HorizontalAlignment = HorizontalAlignment.Center;
                fe.ToolTip = GaugeRenderer.Describe(leitura, _settings, _settings.RateKind);
            }
            MainPanel.Children.Add(medidor);
        }
    }

    private void BuildHorizontal()
    {
        var celulas = 0;

        if (_settings.DockShowBars)
        {
            var bars = _snapshot?.Visible(_settings) ?? new List<UsageBar>();
            if (bars.Count > 0)
            {
                foreach (var bar in bars)
                {
                    if (celulas++ > 0) MainPanel.Children.Add(BarRenderer.BuildCellSeparator());
                    MainPanel.Children.Add(BarRenderer.BuildCell(bar, _settings, _settings.GadgetShowReset));
                }
            }
            else
            {
                MainPanel.Children.Add(Aviso(_snapshot == null ? "Consultando…" : _snapshot.Error ?? "Sem dados de consumo.", 320));
                celulas++;
            }
        }

        if (_settings.DockShowHardware)
        {
            foreach (var (rotulo, leitura) in Sensores())
            {
                if (celulas++ > 0) MainPanel.Children.Add(BarRenderer.BuildCellSeparator());
                MainPanel.Children.Add(HardwareRenderer.Cell(rotulo, leitura, _hardware));
            }
        }

        if (_settings.DockShowRate && AppHost.Current is { } host)
        {
            if (celulas++ > 0) MainPanel.Children.Add(BarRenderer.BuildCellSeparator());

            var leitura = host.Rate;
            var medidor = GaugeRenderer.Build(leitura, 34);
            if (medidor is FrameworkElement fe)
            {
                fe.VerticalAlignment = VerticalAlignment.Center;
                fe.ToolTip = GaugeRenderer.Describe(leitura, _settings, _settings.RateKind);
            }
            MainPanel.Children.Add(medidor);
        }
    }

    private List<(string Rotulo, ComponentReading Leitura)> Sensores()
    {
        var quais = new List<(string, ComponentReading)>();
        if (_settings.PcShowCpu) quais.Add(("CPU", _hardware.Cpu));
        if (_settings.PcShowGpu) quais.Add(("GPU", _hardware.Gpu));
        if (_settings.PcShowRam) quais.Add(("RAM", _hardware.Ram));
        return quais;
    }

    private void BuildTimeline()
    {
        SidePanel.Children.Clear();
        if (!_settings.ShowCallTimeline) return;

        var calls = AppHost.Current?.Calls.Recent() ?? new List<ApiCall>();
        for (var i = 0; i < ApiCallLog.Capacity - calls.Count; i++) SidePanel.Children.Add(Dot(null));
        foreach (var call in calls) SidePanel.Children.Add(Dot(call));
    }

    /// <summary>
    /// Bolinha de uma consulta. Cada painel do app tem a sua — as alturas e o texto de apoio
    /// diferem conforme o espaço —, e aqui ela é baixa o bastante para caber numa barra deitada
    /// de 46 unidades.
    /// </summary>
    private UIElement Dot(ApiCall? call)
    {
        var cor = call?.Outcome switch
        {
            ApiOutcome.Ok => BarRenderer.Swatch("OkBrush"),
            ApiOutcome.RateLimited => BarRenderer.Swatch("WarnBrush"),
            ApiOutcome.Failed => BarRenderer.Swatch("DangerBrush"),
            _ => BarRenderer.Swatch("TrackBrush")
        };

        return new Border
        {
            Child = new System.Windows.Shapes.Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = cor,
                Opacity = call == null || call.Outcome == ApiOutcome.Idle ? 0.5 : 1,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            },
            Background = Brushes.Transparent,
            Width = 11,
            Height = 14,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = call?.Describe() ?? "ciclo ainda não registrado"
        };
    }

    private UIElement Separador(bool vertical) => new Border
    {
        Height = vertical ? 1 : double.NaN,
        Width = vertical ? double.NaN : 1,
        Background = BarRenderer.Swatch("LineBrush"),
        Margin = vertical ? new Thickness(0, 2, 0, 10) : new Thickness(10, 2, 10, 2)
    };

    private UIElement Aviso(string texto, double largura) => new TextBlock
    {
        Text = texto,
        FontSize = 11,
        Foreground = BarRenderer.Swatch("MutedBrush"),
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = largura,
        VerticalAlignment = VerticalAlignment.Center
    };

    // ------------------------------------------------------------------
    // Posição
    // ------------------------------------------------------------------

    /// <summary>
    /// Coloca a barra na borda escolhida, em pixels de tela — o shell fala em pixels, e é por isso
    /// que nada aqui passa por Left/Top do WPF.
    ///
    /// Reservando espaço, quem decide o retângulo final é o Windows: pedimos a borda inteira e ele
    /// devolve o que sobrou depois das barras que já estavam lá. É o que faz a nossa barra no topo
    /// conviver com a do Windows no rodapé sem as duas se sobreporem.
    /// </summary>
    private void Reposition()
    {
        if (_hiddenByUser || !_settings.ShowDock) return;

        var monitor = TaskbarInfo.ResolveMonitor(_settings.DockMonitor);
        if (monitor == null) return;

        // No modo por cima, jogo em tela cheia manda a barra sair da frente. Reservando espaço não
        // há o que esconder: a faixa está fora da área útil e o jogo cobre a tela toda de qualquer
        // jeito — e desregistrar a cada alt-tab piscaria a área de trabalho inteira.
        var esconder = !_settings.DockReserveSpace
                       && _settings.DockHideOnFullscreen
                       && TaskbarInfo.FullscreenAppOnMonitor(monitor);

        if (esconder)
        {
            Visibility = Visibility.Collapsed;
            return;
        }
        Visibility = Visibility.Visible;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var espessura = _settings.DockEdge == DockEdge.Top ? _settings.DockHeight : _settings.DockWidth;
        var alvo = DesktopAppBar.EdgeRect(_settings.DockEdge, monitor, espessura);

        if (_settings.DockReserveSpace)
        {
            if (!_appBar.Registered) _appBar.Register(hwnd);
            alvo = _appBar.Reserve(_settings.DockEdge, alvo);
        }
        else if (_appBar.Registered)
        {
            _appBar.Remove();
        }

        _target = alvo;

        // Só escreve quando muda de verdade: reposicionar a cada tique fecharia o tooltip aberto.
        if (!GetWindowRect(hwnd, out var atual)
            || atual.Left != alvo.Left || atual.Top != alvo.Top
            || atual.Right != alvo.Right || atual.Bottom != alvo.Bottom)
        {
            SetWindowPos(hwnd, IntPtr.Zero, alvo.Left, alvo.Top,
                Math.Max(alvo.Width, 1), Math.Max(alvo.Height, 1), SWP_NOZORDER | SWP_NOACTIVATE);

            if (_settings.DockReserveSpace) _appBar.NotifyMoved();
        }

        ReassertTopmost();
    }

    /// <summary>
    /// Reafirma o topo da pilha de vez em quando. Ativar qualquer janela refaz a ordem-Z, e uma
    /// barra que fica atrás não é uma barra. Com o cursor em cima não se mexe: mudar Topmost fecha
    /// o tooltip aberto — e janela coberta não tem cursor em cima, por definição.
    /// </summary>
    private void ReassertTopmost()
    {
        if (!_settings.DockTopmost || _fullscreenApp || IsMouseOver) return;
        if (DateTime.UtcNow - _lastTopmost < TimeSpan.FromSeconds(10)) return;

        _lastTopmost = DateTime.UtcNow;
        Topmost = false;
        Topmost = true;
    }

    /// <summary>
    /// Avisos que mexem na posição: os do shell sobre a faixa reservada e tela cheia, mais os do
    /// Windows sobre resolução, DPI e área de trabalho. O WM_WINDOWPOSCHANGING é a trava: sem ela,
    /// cada mudança de DPI devolvia a janela para o tamanho antigo em unidades do WPF.
    /// </summary>
    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == DesktopAppBar.CallbackMessage)
        {
            switch (wParam.ToInt32())
            {
                case DesktopAppBar.ABN_POSCHANGED:
                    Reposition();
                    break;

                case DesktopAppBar.ABN_FULLSCREENAPP:
                    _fullscreenApp = lParam != IntPtr.Zero;
                    Topmost = _settings.DockTopmost && !_fullscreenApp;
                    break;
            }
            return IntPtr.Zero;
        }

        if (msg is WM_DISPLAYCHANGE or WM_DPICHANGED or WM_SETTINGCHANGE)
        {
            Dispatcher.BeginInvoke(new Action(Reposition), DispatcherPriority.Background);
            return IntPtr.Zero;
        }

        if (msg == WM_WINDOWPOSCHANGING && _target != null && lParam != IntPtr.Zero)
        {
            var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            pos.x = _target.Value.Left;
            pos.y = _target.Value.Top;
            pos.cx = Math.Max(_target.Value.Width, 1);
            pos.cy = Math.Max(_target.Value.Height, 1);
            pos.flags &= ~(SWP_NOMOVE | SWP_NOSIZE);
            Marshal.StructureToPtr(pos, lParam, false);
        }

        return IntPtr.Zero;
    }

    // ------------------------------------------------------------------
    // Mostrar e esconder
    // ------------------------------------------------------------------

    public void ShowDock()
    {
        _hiddenByUser = false;
        Show();
        Reposition();
    }

    /// <summary>Esconde e devolve a faixa: barra oculta que continua reservando espaço é um bug.</summary>
    public void HideDock()
    {
        _hiddenByUser = true;
        _appBar.Remove();
        _target = null;
        Hide();
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

    private void OnHideClick(object sender, RoutedEventArgs e) => AppHost.Current?.HideDockBar();

    private void OnExitClick(object sender, RoutedEventArgs e) => AppHost.Current?.Exit();

    // ------------------------------------------------------------------

    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int WM_DPICHANGED = 0x02E0;
    private const int WM_SETTINGCHANGE = 0x001A;

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

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out TaskbarInfo.RECT rect);
}
