using System;
using System.Windows;
using System.Windows.Controls;
using ClaudeIndicator.Core;
using ClaudeIndicator.Views.Pages;

namespace ClaudeIndicator.Views;

/// <summary>Seções do painel, usadas para abrir a janela já na parte certa.</summary>
public enum MainSection
{
    Overview,
    History,
    Projects,
    Performance,
    Settings
}

/// <summary>
/// Janela única do aplicativo: navegação à esquerda e a página escolhida à direita.
/// Antes eram três janelas soltas (configurações, histórico e projetos), o que obrigava
/// a fechar uma para achar a outra.
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppHost _host;

    private OverviewPage? _overview;
    private HistoryPage? _history;
    private ProjectsPage? _projects;
    private PerformancePage? _performance;
    private SettingsPage? _settings;

    private bool _ready;

    public MainWindow(AppHost host, MainSection section = MainSection.Overview)
    {
        _host = host;
        InitializeComponent();

        VersionLabel.Text = "versão " + AppInfo.Version;
        Title = AppInfo.NameWithVersion;

        _ready = true;
        Navigate(section);

        _host.Updated += OnUsageUpdated;
        UpdateStatus(_host.Last);
        UpdateGadgetButton();
    }

    // ------------------------------------------------------------------
    // Navegação
    // ------------------------------------------------------------------

    public void Navigate(MainSection section)
    {
        // marca o item sem disparar a navegação de novo
        _ready = false;
        NavOverview.IsChecked = section == MainSection.Overview;
        NavHistory.IsChecked = section == MainSection.History;
        NavProjects.IsChecked = section == MainSection.Projects;
        NavPerformance.IsChecked = section == MainSection.Performance;
        NavSettings.IsChecked = section == MainSection.Settings;
        _ready = true;

        Show(section);
    }

    /// <summary>Abre Configuracoes direto na aba Conta.</summary>
    public void NavigateToAccount()
    {
        Navigate(MainSection.Settings);
        _settings?.OpenAccountTab();
    }

    private void OnNavChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        Show(Current);
    }

    private MainSection Current =>
        NavHistory.IsChecked == true ? MainSection.History :
        NavProjects.IsChecked == true ? MainSection.Projects :
        NavPerformance.IsChecked == true ? MainSection.Performance :
        NavSettings.IsChecked == true ? MainSection.Settings : MainSection.Overview;

    private void Show(MainSection section)
    {
        // as páginas são criadas uma vez e reaproveitadas: trocar de aba não recarrega nada
        switch (section)
        {
            case MainSection.History:
                _history ??= new HistoryPage(_host);
                PageHost.Content = _history;
                SetHeader("Histórico", "Consumo ao longo do tempo, por hora ou por dia.");
                break;

            case MainSection.Projects:
                _projects ??= new ProjectsPage(_host);
                PageHost.Content = _projects;
                SetHeader("Projetos", "Onde o limite foi gasto, segundo as transcrições do Claude Code.");
                break;

            case MainSection.Performance:
                _performance ??= new PerformancePage();
                PageHost.Content = _performance;
                SetHeader("Desempenho do PC", "Uso, temperatura e energia ao longo do tempo, dos sensores do computador.");
                break;

            case MainSection.Settings:
                _settings ??= new SettingsPage(_host);
                PageHost.Content = _settings;
                SetHeader("Configurações", "As mudanças só valem depois de salvar.");
                break;

            default:
                _overview ??= new OverviewPage(_host);
                PageHost.Content = _overview;
                SetHeader("Visão geral", "Quanto do seu plano já foi usado agora.");
                break;
        }
    }

    private void SetHeader(string title, string subtitle)
    {
        PageTitleText.Text = title;
        PageSubtitle.Text = subtitle;
    }

    // ------------------------------------------------------------------
    // Estado
    // ------------------------------------------------------------------

    private void OnUsageUpdated(UsageSnapshot? snap)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => UpdateStatus(snap)));
            return;
        }
        UpdateStatus(snap);
    }

    private void UpdateStatus(UsageSnapshot? snap)
    {
        if (snap == null)
        {
            StatusLabel.Text = "consultando…";
            StatusDot.Fill = BarRenderer.Swatch("MutedBrush");
            return;
        }

        var when = (snap.DataAt ?? snap.FetchedAt).ToLocalTime();
        if (snap.Stale)
        {
            StatusLabel.Text = (snap.RateLimited ? "limite da API · " : "sem conexão · ") + when.ToString("HH:mm");
            StatusDot.Fill = BarRenderer.Swatch("WarnBrush");
        }
        else if (snap.Ok && snap.Bars.Count > 0)
        {
            StatusLabel.Text = "atualizado " + when.ToString("HH:mm");
            StatusDot.Fill = BarRenderer.Swatch("OkBrush");
        }
        else
        {
            StatusLabel.Text = "sem dados";
            StatusDot.Fill = BarRenderer.Swatch("DangerBrush");
        }

        AccountLabel.Text = snap.Account ?? "";
        AccountLabel.ToolTip = snap.Error;
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        BtnRefresh.IsEnabled = false;
        BtnRefresh.Content = "Atualizando…";
        StatusLabel.Text = "consultando…";
        try
        {
            await _host.RefreshAsync(true);
        }
        finally
        {
            BtnRefresh.Content = "Atualizar";
            BtnRefresh.IsEnabled = true;
        }
    }

    private void OnToggleGadgetClick(object sender, RoutedEventArgs e)
    {
        _host.ToggleGadget();
        UpdateGadgetButton();
    }

    /// <summary>O rótulo diz o que o clique vai fazer, não o estado atual.</summary>
    private void UpdateGadgetButton()
    {
        BtnGadget.Content = _host.GadgetVisible ? "Ocultar gadget" : "Mostrar gadget";
    }

    private void OnClosed(object sender, EventArgs e) => _host.Updated -= OnUsageUpdated;
}
