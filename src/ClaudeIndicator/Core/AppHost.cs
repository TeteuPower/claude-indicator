using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using ClaudeIndicator.Views;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace ClaudeIndicator.Core;

/// <summary>Coordena configurações, atualização periódica, ícone da bandeja e gadget.</summary>
public sealed class AppHost
{
    public static AppHost? Current { get; private set; }

    public AppSettings Settings { get; private set; } = new();
    public UsageSnapshot? Last { get; private set; }

    public event Action<UsageSnapshot?>? Updated;

    /// <summary>As últimas consultas à API, para a linha do tempo do gadget.</summary>
    public ApiCallLog Calls => _calls;

    /// <summary>Disparado quando o GitHub tem uma versão mais nova que a instalada.</summary>
    public event Action<UpdateInfo>? UpdateFound;

    public UpdateChecker Updates => _updates;

    private readonly CredentialStore _store = new();
    private readonly UsageService _service;
    private readonly UpdateChecker _updates = new();
    private readonly ApiCallLog _calls = new();
    /// <summary>
    /// O relógio do app, na cadência configurada. Cada tique fecha o ciclo anterior na linha do
    /// tempo e dispara a consulta seguinte — um relógio só para as duas coisas, para que o ponto
    /// e a consulta nunca discordem. O intervalo nunca é reprogramado por consulta: se está em
    /// 1 min, é 1 min sempre, mudando apenas quando a configuração muda.
    /// </summary>
    private readonly DispatcherTimer _timer = new();

    /// <summary>A consulta deste ciclo já virou ponto na linha do tempo?</summary>
    private bool _registradoNoCiclo;

    /// <summary>O primeiro ciclo foi encurtado para emendar no que ficou da execução anterior.</summary>
    private bool _cicloAdiantado;
    private readonly Dictionary<BarKind, bool> _alerted = new();

    private WinForms.NotifyIcon? _tray;
    private Drawing.Icon? _trayIcon;
    private GadgetWindow? _gadget;
    private TaskbarBarWindow? _taskbarBar;
    private TaskbarBarWindow? _pcPanel;

    /// <summary>Barra de tarefas própria, numa borda que o Windows deixou livre.</summary>
    private DockWindow? _dock;

    /// <summary>
    /// Avisos do shell: barra recriada, tela que entra ou sai, compositor reiniciado. Reage na hora,
    /// em vez de esperar o próximo tique do relógio abaixo.
    /// </summary>
    private ShellWatcher? _shell;

    /// <summary>
    /// Reaplica a aparência da barra do Windows enquanto ela estiver escolhida.
    ///
    /// Não é defesa contra imprevisto: é necessidade medida. Aplicando uma vez e deixando quieto, a
    /// cor média da faixa volta ao original entre 10 e 20 segundos — o shell desfaz o efeito por
    /// conta própria, e nenhum aviso é emitido quando isso acontece. Por isso o relógio existe, e é
    /// por isso que programas como o TranslucentTB também reaplicam sem parar. O custo por tique é
    /// achar três janelas e mandar um atributo em cada.
    /// </summary>
    private readonly DispatcherTimer _taskbarClock = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly HardwareMonitor _hardware = new();

    // Indicador por cima do jogo: medição de quadros, detecção e a janela em si.
    private readonly FrameRateMonitor _frames = new();
    private GameDetector? _detector;
    private GameOverlayWindow? _overlay;
    private readonly DispatcherTimer _overlayClock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly HotkeyManager _atalhos = new();
    private MainWindow? _main;
    private bool _busy;

    /// <summary>
    /// Última consulta que veio com barras. Erro na consulta não apaga números da tela: o consumo
    /// de minutos atrás continua sendo a melhor informação disponível.
    /// </summary>
    private UsageSnapshot? _lastGood;

    public AppHost()
    {
        _service = new UsageService(_store);
        Current = this;
    }

    public void Start(string[] args)
    {
        var firstRun = !AppSettings.Exists;
        Settings = AppSettings.Load();

        // Instalação nova: o instalador pode ter marcado "iniciar junto com o Windows".
        // Sem isto o app apagaria a chave logo no primeiro start, por causa do padrão false.
        if (firstRun && StartupManager.IsEnabled())
        {
            Settings.StartWithWindows = true;
            Settings.Save();
        }

        StartupManager.Apply(Settings.StartWithWindows);

        var idadeDoRetrato = RestoreSession();

        ForegroundWatcher.Start();

        BuildTray();
        ApplyDisplayMode();

        _timer.Tick += (_, _) => Pulse();
        RestartClock();

        _shell = new ShellWatcher();
        _shell.Changed += ReaplicarBarraDoWindows;
        _taskbarClock.Tick += (_, _) => ReaplicarBarraDoWindows();

        _overlayClock.Tick += (_, _) => OverlayTick();

        // Com uma leitura recente restaurada, perguntar de novo agora não traria nada: o consumo
        // de segundos atrás continua sendo o consumo, e o limite de consultas é da conta inteira.
        // O relógio é adiantado para completar o ciclo que já estava em curso, e não recomeçado.
        var intervalo = TimeSpan.FromSeconds(Settings.RefreshSeconds);
        if (idadeDoRetrato is { } idade && idade < intervalo - TimeSpan.FromSeconds(2))
        {
            _timer.Interval = intervalo - idade;
            _cicloAdiantado = true;

            // o ciclo restaurado já tem o seu ponto na faixa; sem isto nasceria um cinza falso
            _registradoNoCiclo = true;
        }
        else
        {
            _ = RefreshAsync();
        }

        var minimized = Array.Exists(args, a =>
            string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "/minimized", StringComparison.OrdinalIgnoreCase));

        if (firstRun || (!minimized && !Settings.StartHidden))
            ShowDashboard();

        // sempre ao abrir: é quando o usuário está de fato disponível para atualizar
        _ = CheckUpdatesAsync(atStartup: true);
    }

    // ------------------------------------------------------------------
    // Bandeja
    // ------------------------------------------------------------------

    private void BuildTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Text = "Claude Indicator",
            Visible = false
        };

        var menu = new WinForms.ContextMenuStrip();
        var header = new WinForms.ToolStripMenuItem(AppInfo.NameWithVersion) { Enabled = false };
        menu.Items.Add(header);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Atualizar agora", null, (_, _) => _ = RefreshAsync(true));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Painel…", null, (_, _) => ShowDashboard());
        menu.Items.Add("Histórico de consumo…", null, (_, _) => ShowMain(MainSection.History));
        menu.Items.Add("Consumo por projeto…", null, (_, _) => ShowMain(MainSection.Projects));
        menu.Items.Add("Configurações…", null, (_, _) => ShowSettings());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Mostrar/ocultar gadget", null, (_, _) => ToggleGadget());
        menu.Items.Add("Mostrar/ocultar barra própria", null, (_, _) => ToggleDock());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => Exit());

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowDashboard();

        UpdateTray();
    }

    private void UpdateTray()
    {
        if (_tray == null) return;

        var wanted = Settings.TrayEnabled;
        if (!wanted)
        {
            _tray.Visible = false;
            return;
        }

        var icon = TrayIconRenderer.Render(Last, Settings);
        _tray.Icon = icon;
        _trayIcon?.Dispose();
        _trayIcon = icon;
        _tray.Text = TrayIconRenderer.Tooltip(Last, Settings, Rate);
        _tray.Visible = true;
    }

    // ------------------------------------------------------------------
    // Gadget
    // ------------------------------------------------------------------

    private void ApplyDisplayMode()
    {
        UpdateTray();

        if (Settings.GadgetEnabled)
        {
            EnsureGadget();
            _gadget!.ApplySettings(Settings);
            _gadget.Render(Last, Settings);
            _gadget.RenderHardware(_hardware.Current, Settings);
            _gadget.Show();
        }
        else
        {
            _gadget?.Hide();
        }

        if (Settings.ShowTaskbarBar)
        {
            EnsureTaskbarBar();
            _taskbarBar!.ApplySettings(Settings);
            _taskbarBar.Render(Last, Settings);
            _taskbarBar.ShowInTaskbarArea();
        }
        else
        {
            _taskbarBar?.HidePanel();
        }

        ApplyTaskbarLook();
        ApplyDock();
        ApplyPcPanel();
        ApplyOverlay();
        ApplyHotkeys();
    }

    /// <summary>
    /// Liga ou desliga a barra própria. Desligar fecha a janela em vez de só escondê-la: a faixa
    /// reservada some junto com ela, e uma barra invisível que continua tirando espaço da tela
    /// seria impossível de entender.
    /// </summary>
    private void ApplyDock()
    {
        if (Settings.ShowDock)
        {
            // Janela fechada não se reaproveita: o WPF recusa Show e até o pedido do handle depois
            // do fechamento, e era isso que estourava dois "erro inesperado" em sequência —
            // um no ApplySettings, que reposiciona, e outro no ShowDock, que mostra.
            //
            // Trocar o fundo fosco também pede janela nova: ele e a transparência por pixel do WPF
            // são exclusivos e se decidem antes de a janela existir.
            if (_dock is { Fechada: false } atual && atual.PrecisaRecriar(Settings))
            {
                _dock = null;
                atual.Close();
            }

            if (_dock is null or { Fechada: true })
            {
                _dock = new DockWindow(Settings.DockFrostedEffective);
                _dock.Closed += (_, _) => _dock = null;
            }

            _dock.ApplySettings(Settings);
            _dock.Render(Last, Settings);
            _dock.RenderHardware(_hardware.Current, Settings);
            _dock.ShowDock();
        }
        else
        {
            // solta a referência ANTES de fechar: se algo reentrante quiser desenhar durante o
            // fechamento, não encontra mais a janela pela metade
            var indoEmbora = _dock;
            _dock = null;
            indoEmbora?.Close();
        }
    }

    /// <summary>
    /// Aplica (ou devolve) a aparência da barra de tarefas do Windows, e mantém um relógio curto
    /// enquanto ela estiver estilizada: o Explorer recria as janelas da barra ao reiniciar, e o
    /// efeito não sobrevive à janela antiga — sem reaplicar, a barra volta ao normal sozinha e
    /// parece que o app desistiu.
    /// </summary>
    private void ApplyTaskbarLook()
    {
        if (Settings.TaskbarLook == TaskbarLook.Sistema)
        {
            _taskbarClock.Stop();
            if (TaskbarStyler.Ativo) TaskbarStyler.Restore();
            return;
        }

        TaskbarStyler.Apply(Settings.TaskbarLook, Settings.TaskbarTint);
        _taskbarClock.Start();
    }

    private void ReaplicarBarraDoWindows()
    {
        if (Settings.TaskbarLook != TaskbarLook.Sistema)
            TaskbarStyler.Apply(Settings.TaskbarLook, Settings.TaskbarTint);
    }

    /// <summary>Oculta a barra própria pelo menu dela. Fica guardado: esconder é uma decisão.</summary>
    public void HideDockBar()
    {
        Settings.ShowDock = false;
        Settings.Save();
        ApplyDock();
    }

    /// <summary>Liga e desliga a barra própria pela bandeja.</summary>
    public void ToggleDock()
    {
        Settings.ShowDock = !Settings.ShowDock;
        Settings.Save();
        ApplyDock();
    }

    /// <summary>A barra própria está na tela agora? Usado pela tela de configurações.</summary>
    public bool DockVisible => _dock is { IsVisible: true };

    /// <summary>
    /// Estado do fundo fosco da barra própria: null sem fosco pedido, true no ar, false recusado
    /// pelo Windows. A tela de configuração mostra — e é assim que "não funcionou" deixa de ser
    /// um mistério e passa a ser uma frase.
    /// </summary>
    public bool? DockFoscoAtivo => _dock?.FoscoAtivo;

    /// <summary>
    /// (Re)registra os atalhos globais. Solta tudo antes: mudar a combinação sem soltar a anterior
    /// deixaria a antiga presa até o app fechar, e o Windows recusaria a nova se fosse a mesma.
    /// </summary>
    private void ApplyHotkeys()
    {
        _atalhos.UnregisterAll();

        _atalhos.Register(Hotkey.Parse(Settings.OverlayToggleHotkey), ToggleGameOverlay);
        _atalhos.Register(Hotkey.Parse(Settings.OverlayCycleHotkey), CycleOverlayAnchor);
        _atalhos.Register(Hotkey.Parse(Settings.OverlayLayoutHotkey), CycleOverlayLayout);
    }

    /// <summary>Combinações que o Windows recusou, para a tela de configurações avisar.</summary>
    public IReadOnlyList<string> HotkeyFailures => _atalhos.Failures;

    /// <summary>
    /// Os atalhos que estão de fato valendo agora, cada um com o que faz. Combinação recusada pelo
    /// Windows fica de fora: anunciar um atalho que não funciona é pior que não anunciar nada.
    /// </summary>
    public List<(string Combo, string Acao)> ActiveHotkeys()
    {
        var lista = new List<(string, string)>();
        var recusados = _atalhos.Failures;

        void Somar(string? combo, string acao)
        {
            if (string.IsNullOrWhiteSpace(combo)) return;

            foreach (var recusado in recusados)
            {
                if (string.Equals(recusado, combo, StringComparison.OrdinalIgnoreCase)) return;
            }

            lista.Add((combo!, acao));
        }

        Somar(Settings.OverlayToggleHotkey, "ocultar");
        Somar(Settings.OverlayCycleHotkey, "mover");
        Somar(Settings.OverlayLayoutHotkey, "layout");
        return lista;
    }

    /// <summary>Liga ou desliga o indicador no jogo. Fica guardado: desligar é uma decisão.</summary>
    public void ToggleGameOverlay()
    {
        Settings.ShowGameOverlay = !Settings.ShowGameOverlay;
        Settings.Save();
        ApplyOverlay();
    }

    /// <summary>Alterna entre os layouts do indicador — compacto e medidores.</summary>
    public void CycleOverlayLayout()
    {
        Settings.OverlayLayout = Settings.OverlayLayout == OverlayLayout.Compact
            ? OverlayLayout.Gauges
            : OverlayLayout.Compact;
        Settings.Save();

        _overlay?.ApplySettings(Settings);
        OverlayTick();
    }

    /// <summary>Passa o indicador para o próximo dos nove cantos, em volta.</summary>
    public void CycleOverlayAnchor()
    {
        var cantos = Enum.GetValues<OverlayAnchor>();
        var atual = Array.IndexOf(cantos, Settings.OverlayAnchor);
        Settings.OverlayAnchor = cantos[(atual + 1) % cantos.Length];
        Settings.Save();

        _overlay?.ApplySettings(Settings);
        OverlayTick();   // move na hora, sem esperar o próximo quarto de segundo
    }

    /// <summary>
    /// Liga ou desliga o painel do PC junto com a leitura dos sensores. Ler hardware custa CPU e
    /// mantém um driver aberto, então nada disso roda sem alguém pedindo — painel do computador,
    /// indicador no jogo ou o bloco de sensores do gadget.
    /// </summary>
    private void ApplyPcPanel()
    {
        if (Settings.ShowPcPanel)
        {
            if (_pcPanel == null)
            {
                _pcPanel = new TaskbarBarWindow(PanelKind.Pc);
                _pcPanel.ApplySettings(Settings);
                _pcPanel.Closed += (_, _) => _pcPanel = null;
            }

            _pcPanel.ApplySettings(Settings);
            _pcPanel.RenderHardware(_hardware.Current, Settings);
            _pcPanel.ShowInTaskbarArea();
        }
        else
        {
            _pcPanel?.HidePanel();
        }

        ApplyHardware();
    }

    /// <summary>
    /// Liga a leitura de sensores quando alguém precisa dela — o painel do PC ou o indicador no
    /// jogo. Ler hardware custa CPU, então com os dois desligados nada roda.
    /// </summary>
    private void ApplyHardware()
    {
        var overlayQuerSensores = Settings.ShowGameOverlay
            && (Settings.OverlayShowCpu || Settings.OverlayShowGpu || Settings.OverlayShowRam);
        var gadgetQuerSensores = Settings.GadgetEnabled && Settings.GadgetShowHardware;
        var barraQuerSensores = Settings.ShowDock && Settings.DockShowHardware;

        if (Settings.ShowPcPanel || overlayQuerSensores || gadgetQuerSensores || barraQuerSensores)
        {
            if (!_assinouSensores)
            {
                _hardware.Updated += OnHardwareUpdated;
                _assinouSensores = true;
            }

            _hardware.SetInterval(Settings.PcIntervalSeconds);
            _hardware.Start(Settings.PcIntervalSeconds, Settings.PcCpuSensors && SystemGuard.CanReadCpuSensors);
        }
        else
        {
            if (_assinouSensores)
            {
                _hardware.Updated -= OnHardwareUpdated;
                _assinouSensores = false;
            }
            _hardware.Stop();
        }
    }

    private bool _assinouSensores;

    /// <summary>
    /// Liga ou desliga o indicador por cima do jogo. A medição de quadros só sobe junto: ela cria
    /// uma sessão de rastreamento do Windows, que não faz sentido manter aberta sem ninguém olhando.
    /// </summary>
    private void ApplyOverlay()
    {
        if (Settings.ShowGameOverlay)
        {
            if (!_frames.Running) _frames.Start();
            _detector ??= new GameDetector(_frames);
            _detector.TargetProcess = string.IsNullOrWhiteSpace(Settings.OverlayGameProcess)
                ? null
                : Settings.OverlayGameProcess.Trim();
            _detector.ShowWithoutFocus = Settings.OverlayWithoutFocus;
            _detector.Excluded.Clear();
            foreach (var n in Settings.OverlayExcluded) _detector.Excluded.Add(n);

            if (_overlay == null)
            {
                _overlay = new GameOverlayWindow();
                _overlay.Closed += (_, _) => _overlay = null;
            }

            _overlay.ApplySettings(Settings);
            _overlayClock.Start();
        }
        else
        {
            _overlayClock.Stop();
            _overlay?.Hide();
            _frames.Stop();
        }
    }

    /// <summary>Um passo do indicador no jogo: quem está na frente, e o que mostrar sobre ele.</summary>
    private void OverlayTick()
    {
        if (!Settings.ShowGameOverlay || _overlay == null || _detector == null) return;

        // Um erro aqui roda 4 vezes por segundo: sem o catch, uma janela que fecha no meio do
        // passo derrubaria o app inteiro.
        try
        {
            var jogo = _detector.Detect();
            CurrentGame = jogo;
            _overlay.Render(jogo, _hardware.Current, Last, Settings);
        }
        catch
        {
            // janela do jogo sumiu entre a leitura e o desenho: o próximo tique acerta
        }
    }

    /// <summary>O jogo detectado no último passo, para a tela de configurações mostrar.</summary>
    public GameInfo? CurrentGame { get; private set; }

    /// <summary>Por que o jogo escolhido não está aparecendo agora, quando não está.</summary>
    public string? GameTargetStatus => _detector?.TargetStatus;

    /// <summary>Por que a medição de quadros não está de pé, quando não está.</summary>
    public string? FrameMonitorError => _frames.Running ? null : _frames.Error;

    /// <summary>A medição de quadros está funcionando?</summary>
    public bool FrameMonitorRunning => _frames.Running;

    /// <summary>Quadros por segundo deste processo agora, ou zero sem leitura.</summary>
    public double FpsOf(int processId) => _frames.StatsFor(processId).Fps;

    /// <summary>Liga a medição sob demanda, para a tela de configurações poder testar na hora.</summary>
    public bool StartFrameMonitor() => _frames.Start();

    /// <summary>Chega da thread de leitura: volta para a interface antes de desenhar.</summary>
    private void OnHardwareUpdated(HardwareSnapshot snap)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) return;

        dispatcher.BeginInvoke(new Action(() =>
        {
            if (Settings.ShowPcPanel) _pcPanel?.RenderHardware(snap, Settings);
            if (Settings.GadgetEnabled && Settings.GadgetShowHardware) _gadget?.RenderHardware(snap, Settings);
            if (Settings.ShowDock && Settings.DockShowHardware) _dock?.RenderHardware(snap, Settings);
        }));
    }

    /// <summary>Retrato mais recente dos sensores, para a tela de configurações.</summary>
    public HardwareSnapshot Hardware => _hardware.Current;

    /// <summary>Últimas leituras de uso, para o traçado do indicador no jogo.</summary>
    public HardwareTrail HardwareTrail => _hardware.Trail();

    /// <summary>
    /// Reabre o app pedindo elevação. É o caminho para temperatura e watts da CPU: eles vêm de
    /// registradores do processador, que só um driver de kernel alcança.
    /// </summary>
    public bool RestartElevated()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas"
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch
        {
            return false; // inclui o usuário recusar o pedido de elevação
        }

        // a instância nova assume; esta sai para não haver duas
        Exit();
        return true;
    }

    private void EnsureTaskbarBar()
    {
        if (_taskbarBar != null) return;
        _taskbarBar = new TaskbarBarWindow();
        _taskbarBar.ApplySettings(Settings);
        _taskbarBar.Closed += (_, _) => _taskbarBar = null;
    }

    /// <summary>Ocultar pelo menu do próprio painel: desliga a opção para não voltar sozinho.</summary>
    public void HideTaskbarBar()
    {
        _taskbarBar?.HidePanel();
        Settings.ShowTaskbarBar = false;
        Settings.Sanitize();
        Settings.Save();
        ApplyDisplayMode();
    }

    private void EnsureGadget()
    {
        if (_gadget != null) return;
        _gadget = new GadgetWindow();
        _gadget.ApplySettings(Settings);
        _gadget.Closed += (_, _) => _gadget = null;
    }

    /// <summary>O gadget está aparecendo agora? Usado pelo rótulo do botão que o liga e desliga.</summary>
    public bool GadgetVisible => _gadget is { IsVisible: true };

    public void ToggleGadget()
    {
        if (_gadget != null && _gadget.IsVisible)
        {
            _gadget.Hide();
            return;
        }
        EnsureGadget();
        _gadget!.ApplySettings(Settings);
        _gadget.Render(Last, Settings);
        _gadget.RenderHardware(_hardware.Current, Settings);
        _gadget.Show();
        _gadget.Activate();
    }

    public void SaveGadgetPosition(double left, double top)
    {
        Settings.GadgetLeft = left;
        Settings.GadgetTop = top;
        Settings.Save();
    }

    // ------------------------------------------------------------------
    // Configurações
    // ------------------------------------------------------------------

    /// <summary>Abre o painel na seção pedida; se já estiver aberto, só navega até ela.</summary>
    public void ShowMain(MainSection section)
    {
        if (_main != null)
        {
            if (_main.WindowState == System.Windows.WindowState.Minimized)
                _main.WindowState = System.Windows.WindowState.Normal;
            _main.Navigate(section);
            _main.Activate();
            return;
        }

        _main = new MainWindow(this, section);
        _main.Closed += (_, _) =>
        {
            _main = null;
            // fechar uma janela remexe a ordem-Z: sem isto os painéis ficam atrás da barra de
            // tarefas até a próxima checagem, e o usuário vê os indicadores sumirem
            _taskbarBar?.BringToFront();
            _pcPanel?.BringToFront();
        };
        _main.Show();
        _main.Activate();
    }

    public void ShowDashboard() => ShowMain(MainSection.Overview);

    public void ShowSettings() => ShowMain(MainSection.Settings);

    public void ShowHistory() => ShowMain(MainSection.History);

    public void ShowProjects() => ShowMain(MainSection.Projects);

    public void ApplySettings(AppSettings updated)
    {
        updated.Sanitize();

        // preserva a posição atual do gadget se o usuário não a alterou pela tela
        Settings = updated;
        Settings.Save();

        StartupManager.Apply(Settings.StartWithWindows);
        UsageHistory.Prune(Settings.HistoryRetentionDays, force: true); // aplica na hora se acabou de ligar a retenção
        _store.Invalidate();
        _service.ForgetEndpointFailures();
        RestartClock();
        ApplyDisplayMode();
        _ = RefreshAsync(true);
    }

    // ------------------------------------------------------------------
    // Atualização
    // ------------------------------------------------------------------

    public async Task<UsageSnapshot?> RefreshAsync(bool force = false)
    {
        // Só não atropela uma consulta que ainda está em voo. Erro não gera espera: a cadência é
        // a que está configurada, e o ciclo seguinte tenta de novo — era a pausa que fazia o app
        // parecer ter desistido, obrigando a clicar em "Atualizar" para ele voltar a falar.
        if (_busy && !force) return Last;
        _busy = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var snap = await _service.FetchAsync(Settings, cts.Token).ConfigureAwait(true);
            Publish(snap);
            return snap;
        }
        catch (Exception ex)
        {
            Publish(new UsageSnapshot { FetchedAt = DateTimeOffset.Now, Error = ex.Message });
            return Last;
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Recoloca na tela o que o app sabia quando foi fechado. Devolve a idade da leitura
    /// restaurada, ou null se não havia nenhuma aproveitável.
    /// </summary>
    private TimeSpan? RestoreSession()
    {
        var estado = SessionState.Load();
        if (estado == null) return null;

        var ciclos = new List<ApiCall>();
        foreach (var c in estado.Calls)
            ciclos.Add(new ApiCall { At = c.At, Outcome = c.Outcome, Detail = c.Detail });
        if (ciclos.Count > 0) _calls.Restore(ciclos);

        // Uma leitura de horas atrás não ajuda ninguém: mostrar 40% de sessão quando a sessão
        // pode ter renovado três vezes seria pior que mostrar "carregando".
        var snap = estado.ToSnapshot(TimeSpan.FromHours(2));
        if (snap == null) return null;

        Last = snap;
        _lastGood = snap;
        UpdateRate(snap);
        return estado.SnapshotAge;
    }

    /// <summary>
    /// (Re)programa o relógio com o intervalo configurado. Só é chamado ao abrir o app e quando a
    /// configuração muda: a cadência é fixa de propósito, para que a linha do tempo signifique
    /// sempre a mesma coisa — um ponto por intervalo, sem o app decidir pular consultas.
    /// </summary>
    private void RestartClock()
    {
        _timer.Stop();
        _timer.Interval = TimeSpan.FromSeconds(Math.Clamp(Settings.RefreshSeconds, 15, 3600));
        _timer.Start();
    }

    /// <summary>
    /// Anota como terminou esta consulta, na hora em que ela terminou — é esse horário que o
    /// balão de cada ponto mostra. Tem que ser antes de o Publish reaproveitar as barras antigas
    /// e reescrever a mensagem: depois disso a falha ficaria com cara de sucesso.
    /// </summary>
    private void RecordCall(UsageSnapshot snap)
    {
        if (snap.RateLimited)
            _calls.Record(ApiOutcome.RateLimited, "HTTP 429");
        else if (snap.Ok && snap.Bars.Count > 0)
            _calls.Record(ApiOutcome.Ok, "");
        else
            _calls.Record(ApiOutcome.Failed, Shorten(snap.Error));

        _registradoNoCiclo = true;
        RefreshTimelines();
    }

    private void RefreshTimelines()
    {
        _gadget?.RefreshTimeline();
        _taskbarBar?.RefreshTimeline();
    }

    /// <summary>
    /// Um ciclo do relógio: garante o ponto deste ciclo e dispara a consulta do próximo.
    ///
    /// Quase sempre a consulta do ciclo já respondeu e já se registrou sozinha — este tique só
    /// começa a seguinte. O ponto nasce aqui quando não houve resposta nenhuma: âmbar se o app
    /// está cumprindo a pausa de um HTTP 429, cinza se a consulta simplesmente demorou mais que o
    /// intervalo. Assim a faixa nunca congela, mesmo quando não há o que consultar.
    /// </summary>
    private void Pulse()
    {

        // o ciclo encurtado da abertura acabou: daqui em diante, a cadência cheia
        if (_cicloAdiantado)
        {
            _cicloAdiantado = false;
            RestartClock();
        }

        // Ponto do ciclo que passou sem resposta nenhuma — consulta que demorou mais que o
        // intervalo. Assim a faixa nunca congela: um ponto por ciclo, sempre.
        if (!_registradoNoCiclo)
        {
            _calls.Record(ApiOutcome.Idle, "consulta sem resposta dentro do ciclo");
            RefreshTimelines();
        }

        _registradoNoCiclo = false;
        _ = RefreshAsync();
    }

    private static string Shorten(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = text.Trim();
        return t.Length <= 90 ? t : t.Substring(0, 89) + "…";
    }


    private Dictionary<BarKind, RateReading> _rates = new();

    /// <summary>Ritmo do limite escolhido para o velocímetro.</summary>
    public RateReading Rate => RateFor(Settings.RateKind);

    /// <summary>Ritmo de qualquer limite — o gadget mostra um velocímetro por barra.</summary>
    public RateReading RateFor(BarKind kind) =>
        _rates.TryGetValue(kind, out var r) ? r : RateReading.Empty;

    private void UpdateRate(UsageSnapshot snap)
    {
        // uma leitura do histórico serve para os três: ler o arquivo três vezes seria desperdício
        var span = TimeSpan.FromMinutes(Math.Max(120, Settings.RateWindowMinutes * 2));
        var history = UsageHistory.Load(span);

        var map = new Dictionary<BarKind, RateReading>();
        foreach (var kind in new[] { BarKind.Session, BarKind.Weekly, BarKind.Fable })
            map[kind] = ConsumptionRate.Measure(history, snap.Get(kind), kind, Settings.RateWindowMinutes);
        _rates = map;
    }

    /// <summary>
    /// Passa o ritmo para o próximo limite — é o clique no velocímetro. Só entram os limites
    /// ligados e que a API devolveu, senão o clique levaria a um velocímetro vazio.
    /// </summary>
    /// <summary>Escolhe diretamente o limite acompanhado — é o clique num velocímetro do gadget.</summary>
    public void SetRateKind(BarKind kind)
    {
        if (Settings.RateKind == kind) return;

        Settings.RateKind = kind;
        Settings.Save();

        if (Last != null) UpdateRate(Last);
        UpdateTray();
        _gadget?.Render(Last, Settings);
        _taskbarBar?.Render(Last, Settings);
        _dock?.Render(Last, Settings);
        Updated?.Invoke(Last);
    }

    public void CycleRateKind()
    {
        var available = new List<BarKind>();
        foreach (var kind in new[] { BarKind.Session, BarKind.Weekly, BarKind.Fable })
        {
            if (Settings.IsEnabled(kind) && Last?.Get(kind) != null) available.Add(kind);
        }
        if (available.Count == 0)
        {
            foreach (var kind in new[] { BarKind.Session, BarKind.Weekly, BarKind.Fable })
            {
                if (Settings.IsEnabled(kind)) available.Add(kind);
            }
        }
        if (available.Count == 0) return;

        var index = available.IndexOf(Settings.RateKind);
        Settings.RateKind = available[(index + 1) % available.Count];
        Settings.Save();

        if (Last != null) UpdateRate(Last);
        UpdateTray();
        _gadget?.Render(Last, Settings);
        _taskbarBar?.Render(Last, Settings);
        _dock?.Render(Last, Settings);
        Updated?.Invoke(Last);
    }

    private void Publish(UsageSnapshot snap)
    {
        RecordCall(snap);

        if (snap.Ok && snap.Bars.Count > 0)
        {
            _lastGood = snap;
            UsageHistory.Append(snap, Settings);
            SessionState.Save(snap, _calls.Recent());
        }
        else
        {
            if (snap.Bars.Count == 0 && _lastGood != null)
            {
                // A consulta falhou, mas o consumo de minutos atrás continua sendo a melhor
                // informação disponível: mantém as barras na tela em vez de trocá-las pelo erro.
                snap.Bars = _lastGood.Bars;
                snap.DataAt = _lastGood.DataAt ?? _lastGood.FetchedAt;
                snap.Stale = true;
            }
        }

        if (snap.RateLimited)
        {
            // Nada de espera imposta: o próximo ciclo tenta igual aos outros. Se o 429 insistir,
            // o remédio é aumentar o intervalo em Configurações › Sistema — decisão de quem usa,
            // e não uma pausa que o app aplica sozinho e que fazia o consumo congelar na tela.
            snap.Error = "Limite de consultas da API atingido (HTTP 429). O app tenta de novo no "
                         + $"próximo ciclo, em até {Settings.RefreshSeconds}s — as barras seguem "
                         + "com os últimos valores.";
        }

        Last = snap;
        UpdateRate(snap);

        // enquanto o app fica aberto por dias, o próprio ciclo de atualização carrega a
        // verificação de versão nova — o UpdateChecker limita a uma consulta a cada 6 h
        _ = CheckUpdatesAsync();
        UpdateTray();
        _gadget?.Render(snap, Settings);
        _taskbarBar?.Render(snap, Settings);
        _dock?.Render(snap, Settings);
        Updated?.Invoke(snap);
        CheckThresholds(snap);
    }

    private void CheckThresholds(UsageSnapshot snap)
    {
        if (!Settings.NotifyOnThreshold || _tray == null || !_tray.Visible) return;

        foreach (var kind in Settings.EnabledKinds())
        {
            var bar = snap.Get(kind);
            if (bar == null) continue;

            var over = bar.Percent >= Settings.AlertThreshold;
            _alerted.TryGetValue(kind, out var was);
            if (over && !was)
            {
                _tray.ShowBalloonTip(6000, "Claude Indicator",
                    $"{Settings.LabelFor(kind)} em {Math.Round(bar.Percent)}%. {bar.ResetText()}",
                    WinForms.ToolTipIcon.Warning);
            }
            _alerted[kind] = over;
        }
    }

    // ------------------------------------------------------------------

    public void Exit()
    {
        SessionState.Save(_lastGood, _calls.Recent());
        ForegroundWatcher.Stop();
        _atalhos.Dispose();
        _timer.Stop();
        _overlayClock.Stop();
        _overlay?.Close();
        _frames.Dispose();
        _hardware.Stop();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
        _trayIcon?.Dispose();
        _gadget?.Close();
        _taskbarBar?.Close();
        _pcPanel?.Close();

        // fechar a barra propria devolve ao Windows a faixa que ela reservou
        _dock?.Close();

        // e a barra do Windows volta ao desenho do sistema: efeito deixado para tras por um
        // programa que morreu so sai reiniciando o Explorer
        _shell?.Dispose();
        _taskbarClock.Stop();
        if (TaskbarStyler.Ativo) TaskbarStyler.Restore();
        _main?.Close();
        System.Windows.Application.Current?.Shutdown();
    }

    // ------------------------------------------------------------------
    // Atualização
    // ------------------------------------------------------------------

    /// <summary>
    /// Procura versão nova. Avisa uma vez por versão: quem mandou ignorar não é incomodado de
    /// novo até sair uma posterior.
    /// </summary>
    /// <param name="force">Pedido explícito do usuário: ignora o intervalo e a versão dispensada.</param>
    /// <param name="atStartup">Abertura do app: consulta mesmo dentro do intervalo, mas respeita o "ignorar".</param>
    public async Task<UpdateInfo?> CheckUpdatesAsync(bool force = false, bool atStartup = false)
    {
        try
        {
            if (atStartup && !Settings.CheckUpdates) return null;

            var info = await _updates.CheckAsync(Settings, force || atStartup).ConfigureAwait(true);
            if (info == null) return null;

            if (!force && string.Equals(info.Version, Settings.SkippedVersion, StringComparison.OrdinalIgnoreCase))
                return info;

            UpdateFound?.Invoke(info);

            if (_tray is { Visible: true } && !force)
            {
                _tray.ShowBalloonTip(7000, AppInfo.Name,
                    $"Versão {info.Version} disponível. Abra o painel para atualizar.",
                    WinForms.ToolTipIcon.Info);
            }
            return info;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Marca a versão como ignorada até sair uma mais nova.</summary>
    public void SkipUpdate(UpdateInfo info)
    {
        Settings.SkippedVersion = info.Version;
        Settings.Save();
    }

    public CredentialStore Credentials => _store;

    /// <summary>Testa uma configuração sem aplicá-la (botão "Testar conexão").</summary>
    public Task<UsageSnapshot> TestAsync(AppSettings candidate)
    {
        _store.Invalidate();
        return _service.FetchAsync(candidate);
    }
}
