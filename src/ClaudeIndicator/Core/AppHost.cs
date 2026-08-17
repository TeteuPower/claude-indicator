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

    /// <summary>Disparado quando o GitHub tem uma versão mais nova que a instalada.</summary>
    public event Action<UpdateInfo>? UpdateFound;

    public UpdateChecker Updates => _updates;

    private readonly CredentialStore _store = new();
    private readonly UsageService _service;
    private readonly UpdateChecker _updates = new();
    private readonly DispatcherTimer _timer = new();
    private readonly Dictionary<BarKind, bool> _alerted = new();

    private WinForms.NotifyIcon? _tray;
    private Drawing.Icon? _trayIcon;
    private GadgetWindow? _gadget;
    private TaskbarBarWindow? _taskbarBar;
    private MainWindow? _main;
    private bool _busy;

    // Resiliência: última consulta que veio com barras, e pausa imposta por HTTP 429.
    private UsageSnapshot? _lastGood;
    private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;
    private int _rateLimitStreak;
    private int _okStreak;

    /// <summary>Consultas seguidas em que nada mudou: espaça as próximas.</summary>
    private int _idleStreak;

    /// <summary>Teto do espaçamento automático quando o consumo está parado.</summary>
    private const int MaxIdleSeconds = 600;

    /// <summary>Piso enquanto o limite de consultas estiver sendo respeitado.</summary>
    private const int CooldownSeconds = 300;

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

        BuildTray();
        ApplyDisplayMode();

        _timer.Interval = TimeSpan.FromSeconds(Settings.RefreshSeconds);
        _timer.Tick += (_, _) => _ = RefreshAsync();
        _timer.Start();
        ScheduleNext();

        _ = RefreshAsync();

        var minimized = Array.Exists(args, a =>
            string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "/minimized", StringComparison.OrdinalIgnoreCase));

        if (firstRun || (!minimized && !Settings.StartHidden))
            ShowDashboard();

        _ = CheckUpdatesAsync();
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
            _taskbarBar?.Hide();
        }
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
        _taskbarBar?.HideByUser();
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
        _main.Closed += (_, _) => _main = null;
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
        _pausedUntil = DateTimeOffset.MinValue;
        _rateLimitStreak = 0;
        _okStreak = 0;
        _idleStreak = 0;

        ScheduleNext();
        ApplyDisplayMode();
        _ = RefreshAsync(true);
    }

    // ------------------------------------------------------------------
    // Atualização
    // ------------------------------------------------------------------

    public async Task<UsageSnapshot?> RefreshAsync(bool force = false)
    {
        if (_busy && !force) return Last;
        if (!force && DateTimeOffset.Now < _pausedUntil) return Last; // aguardando o 429 passar
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
    /// Reprograma o timer. O consumo só muda quando você usa o Claude, e o limite de consultas
    /// é da conta inteira — cada sessão do Claude Code aberta consulta o mesmo endpoint. Então:
    /// quando nada muda, espaça; depois de um 429, segura por um bom tempo.
    /// </summary>
    private void ScheduleNext()
    {
        var seconds = (double)Settings.RefreshSeconds;

        if (_idleStreak > 0)
            seconds = Math.Min(seconds * (1 + _idleStreak), MaxIdleSeconds);

        if (_rateLimitStreak > 0)
            seconds = Math.Max(seconds, CooldownSeconds);

        var untilPause = (_pausedUntil - DateTimeOffset.Now).TotalSeconds;
        if (untilPause > seconds) seconds = untilPause;

        _timer.Stop();
        _timer.Interval = TimeSpan.FromSeconds(Math.Clamp(seconds, 15, 3600));
        _timer.Start();
    }

    private static bool SameReading(UsageSnapshot? a, UsageSnapshot? b)
    {
        if (a == null || b == null || a.Bars.Count != b.Bars.Count) return false;
        foreach (var bar in a.Bars)
        {
            var other = b.Get(bar.Kind);
            if (other == null || Math.Abs(other.Percent - bar.Percent) > 0.001) return false;
        }
        return true;
    }

    /// <summary>Ritmo de consumo atual, recalculado a cada publicação.</summary>
    public RateReading Rate { get; private set; } = RateReading.Empty;

    private void UpdateRate(UsageSnapshot snap)
    {
        var kind = Settings.RateKind;
        Rate = ConsumptionRate.Measure(UsageHistory.Load(TimeSpan.FromHours(2)), snap.Get(kind), kind);
    }

    private void Publish(UsageSnapshot snap)
    {
        if (snap.Ok && snap.Bars.Count > 0)
        {
            _idleStreak = SameReading(snap, _lastGood) ? _idleStreak + 1 : 0;

            // o alívio do 429 só vem depois de algumas consultas boas seguidas: voltar ao
            // intervalo curto na primeira que funciona é o caminho de bater no limite de novo
            if (_rateLimitStreak > 0 && ++_okStreak >= 3)
            {
                _rateLimitStreak = 0;
                _okStreak = 0;
            }

            _lastGood = snap;
            UsageHistory.Append(snap, Settings);
        }
        else if (snap.Bars.Count == 0 && _lastGood != null)
        {
            // A consulta falhou, mas o consumo de minutos atrás continua sendo a melhor
            // informação disponível: mantém as barras na tela em vez de trocá-las pelo erro.
            snap.Bars = _lastGood.Bars;
            snap.DataAt = _lastGood.DataAt ?? _lastGood.FetchedAt;
            snap.Stale = true;
        }

        if (snap.RateLimited)
        {
            // Respeita o Retry-After quando vier; sem ele (é o caso deste endpoint, que não
            // publica limites), backoff 5 min → 10 → 15, com teto de 15 minutos.
            _rateLimitStreak++;
            _okStreak = 0;
            _idleStreak = 0;
            var baseDelay = snap.RetryAfterSeconds ?? CooldownSeconds * Math.Min(3, _rateLimitStreak);
            var delay = Math.Clamp(baseDelay, 60, 900);
            _pausedUntil = DateTimeOffset.Now.AddSeconds(delay);
            snap.Error = $"Limite de consultas da API atingido. Nova tentativa às " +
                         $"{_pausedUntil.ToLocalTime():HH:mm} — as barras seguem com os últimos valores.";
        }

        Last = snap;
        UpdateRate(snap);
        ScheduleNext();
        UpdateTray();
        _gadget?.Render(snap, Settings);
        _taskbarBar?.Render(snap, Settings);
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
        _timer.Stop();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
        _trayIcon?.Dispose();
        _gadget?.Close();
        _taskbarBar?.Close();
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
    public async Task<UpdateInfo?> CheckUpdatesAsync(bool force = false)
    {
        try
        {
            var info = await _updates.CheckAsync(Settings, force).ConfigureAwait(true);
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
