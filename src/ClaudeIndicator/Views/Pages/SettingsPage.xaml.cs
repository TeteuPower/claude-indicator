using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ClaudeIndicator.Core;
using ClaudeIndicator.Views;

namespace ClaudeIndicator.Views.Pages;

/// <summary>
/// Configurações agrupadas por assunto, uma categoria por vez, com a barra de salvar aparecendo
/// só quando existe mudança pendente — antes era um único rolo com oito cartões e um rodapé fixo.
/// </summary>
public partial class SettingsPage : UserControl
{
    private readonly AppHost _host;
    private bool _ready;
    private bool _resetPosition;
    private string _baseline = "";

    public SettingsPage(AppHost host)
    {
        _host = host;
        InitializeComponent();

        TabDisplay.IsChecked = true;
        LoadUi(host.Settings);
        _ready = true;

        _baseline = CollectDraft().Serialize();
        WireDirtyTracking();
        UpdateLabels();
        RenderPreview();

        Loaded += (_, _) =>
        {
            _host.Updated -= OnUsageUpdated;
            _host.Updated += OnUsageUpdated;
            RenderPreview();
        };
        Unloaded += (_, _) => _host.Updated -= OnUsageUpdated;
    }

    // ------------------------------------------------------------------
    // Carga e coleta
    // ------------------------------------------------------------------

    private void LoadUi(AppSettings s)
    {
        ChkTray.IsChecked = s.TrayEnabled;
        ChkTaskbar.IsChecked = s.ShowTaskbarBar;
        ChkGadget.IsChecked = s.GadgetEnabled;

        OrientVertical.IsChecked = s.TrayOrientation == BarOrientation.Vertical;
        OrientHorizontal.IsChecked = s.TrayOrientation == BarOrientation.Horizontal;

        TbLeft.IsChecked = s.TaskbarBarAnchor == TaskbarAnchor.Left;
        TbRight.IsChecked = s.TaskbarBarAnchor == TaskbarAnchor.Right;
        SldTbOffset.Value = s.TaskbarBarOffset;
        SldTbScale.Value = s.TaskbarBarScale;
        SldTbOpacity.Value = s.TaskbarBarOpacity;

        GadgetVertical.IsChecked = s.GadgetOrientation == BarOrientation.Vertical;
        GadgetHorizontal.IsChecked = s.GadgetOrientation == BarOrientation.Horizontal;
        SldOpacity.Value = s.GadgetOpacity;
        SldScale.Value = s.GadgetScale;
        ChkTopmost.IsChecked = s.GadgetTopmost;
        ChkLocked.IsChecked = s.GadgetLocked;
        ChkShowReset.IsChecked = s.GadgetShowReset;

        ChkSession.IsChecked = s.ShowSession;
        ChkWeekly.IsChecked = s.ShowWeekly;
        ChkFable.IsChecked = s.ShowFable;
        TxtSessionLabel.Text = s.SessionLabel;
        TxtWeeklyLabel.Text = s.WeeklyLabel;
        TxtFableLabel.Text = s.FableLabel;

        SldWarn.Value = s.WarnThreshold;
        SldAlert.Value = s.AlertThreshold;
        ChkNotify.IsChecked = s.NotifyOnThreshold;

        SrcClaudeCode.IsChecked = s.CredentialSource != "Manual";
        SrcManual.IsChecked = s.CredentialSource == "Manual";
        TxtToken.Text = s.ManualAccessToken;
        TxtToken.IsEnabled = SrcManual.IsChecked == true;

        ChkRateTaskbar.IsChecked = s.ShowRateTaskbar;
        ChkRateGadget.IsChecked = s.ShowRateGadget;
        RateWeekly.IsChecked = s.RateKind == BarKind.Weekly;
        RateSession.IsChecked = s.RateKind == BarKind.Session;
        RateFable.IsChecked = s.RateKind == BarKind.Fable;

        Win5.IsChecked = s.RateWindowMinutes == 5;
        Win20.IsChecked = s.RateWindowMinutes == 20;
        Win60.IsChecked = s.RateWindowMinutes == 60;
        Win1440.IsChecked = s.RateWindowMinutes == 1440;
        ChkTimeProgress.IsChecked = s.ShowTimeProgress;

        ChkStartup.IsChecked = s.StartWithWindows;
        ChkStartHidden.IsChecked = s.StartHidden;
        SldRefresh.Value = s.RefreshSeconds;
        ChkCheckUpdates.IsChecked = s.CheckUpdates;
        TxtUpdateRepo.Text = s.UpdateRepository;

        KeepForever.IsChecked = s.HistoryRetentionDays <= 0;
        KeepDays.IsChecked = s.HistoryRetentionDays > 0;
        SldRetention.Value = s.HistoryRetentionDays > 0 ? Math.Clamp(s.HistoryRetentionDays, 7, 730) : 31;

        TxtEndpoints.Text = string.Join(Environment.NewLine, s.UsageEndpoints);
        TxtKwSession.Text = s.SessionKeywords;
        TxtKwWeekly.Text = s.WeeklyKeywords;
        TxtKwFable.Text = s.FableKeywords;
        TxtWeightOutput.Text = s.WeightOutput.ToString(CultureInfo.InvariantCulture);
        TxtWeightCacheWrite.Text = s.WeightCacheWrite.ToString(CultureInfo.InvariantCulture);
        TxtWeightCacheRead.Text = s.WeightCacheRead.ToString(CultureInfo.InvariantCulture);
        TxtFableModels.Text = s.FableModelIds;

        AccountStatus.Text = DescribeAccount();
    }

    private AppSettings CollectDraft()
    {
        var s = _host.Settings.Clone();

        s.ShowTrayIcon = ChkTray.IsChecked == true;
        s.ShowTaskbarBar = ChkTaskbar.IsChecked == true;
        s.ShowGadget = ChkGadget.IsChecked == true;

        s.TrayOrientation = OrientHorizontal.IsChecked == true ? BarOrientation.Horizontal : BarOrientation.Vertical;

        s.TaskbarBarAnchor = TbRight.IsChecked == true ? TaskbarAnchor.Right : TaskbarAnchor.Left;
        s.TaskbarBarOffset = Math.Round(SldTbOffset.Value);
        s.TaskbarBarScale = Math.Round(SldTbScale.Value, 2);
        s.TaskbarBarOpacity = Math.Round(SldTbOpacity.Value, 2);

        s.GadgetOrientation = GadgetHorizontal.IsChecked == true ? BarOrientation.Horizontal : BarOrientation.Vertical;
        s.GadgetOpacity = SldOpacity.Value;
        s.GadgetScale = SldScale.Value;
        s.GadgetTopmost = ChkTopmost.IsChecked == true;
        s.GadgetLocked = ChkLocked.IsChecked == true;
        s.GadgetShowReset = ChkShowReset.IsChecked == true;
        if (_resetPosition)
        {
            s.GadgetLeft = -1;
            s.GadgetTop = -1;
        }

        s.ShowSession = ChkSession.IsChecked == true;
        s.ShowWeekly = ChkWeekly.IsChecked == true;
        s.ShowFable = ChkFable.IsChecked == true;
        s.SessionLabel = TxtSessionLabel.Text;
        s.WeeklyLabel = TxtWeeklyLabel.Text;
        s.FableLabel = TxtFableLabel.Text;

        s.WarnThreshold = (int)Math.Round(SldWarn.Value);
        s.AlertThreshold = (int)Math.Round(SldAlert.Value);
        s.NotifyOnThreshold = ChkNotify.IsChecked == true;

        s.CredentialSource = SrcManual.IsChecked == true ? "Manual" : "ClaudeCode";
        s.ManualAccessToken = TxtToken.Text.Trim();

        s.ShowRateTaskbar = ChkRateTaskbar.IsChecked == true;
        s.ShowRateGadget = ChkRateGadget.IsChecked == true;
        s.RateKind = RateSession.IsChecked == true ? BarKind.Session
            : RateFable.IsChecked == true ? BarKind.Fable : BarKind.Weekly;
        s.RateWindowMinutes = Win5.IsChecked == true ? 5
            : Win60.IsChecked == true ? 60
            : Win1440.IsChecked == true ? 1440 : 20;
        s.ShowTimeProgress = ChkTimeProgress.IsChecked == true;

        s.StartWithWindows = ChkStartup.IsChecked == true;
        s.StartHidden = ChkStartHidden.IsChecked == true;
        s.RefreshSeconds = (int)Math.Round(SldRefresh.Value);
        s.CheckUpdates = ChkCheckUpdates.IsChecked == true;
        if (TxtUpdateRepo.Text.Trim().Length > 0) s.UpdateRepository = TxtUpdateRepo.Text.Trim();

        s.HistoryRetentionDays = KeepDays.IsChecked == true ? (int)Math.Round(SldRetention.Value) : 0;

        var endpoints = new List<string>();
        foreach (var line in TxtEndpoints.Text.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length > 0) endpoints.Add(t);
        }
        if (endpoints.Count > 0) s.UsageEndpoints = endpoints;

        if (TxtKwSession.Text.Trim().Length > 0) s.SessionKeywords = TxtKwSession.Text.Trim();
        if (TxtKwWeekly.Text.Trim().Length > 0) s.WeeklyKeywords = TxtKwWeekly.Text.Trim();
        if (TxtKwFable.Text.Trim().Length > 0) s.FableKeywords = TxtKwFable.Text.Trim();
        if (TxtFableModels.Text.Trim().Length > 0) s.FableModelIds = TxtFableModels.Text.Trim();
        s.WeightOutput = ParseWeight(TxtWeightOutput.Text, s.WeightOutput);
        s.WeightCacheWrite = ParseWeight(TxtWeightCacheWrite.Text, s.WeightCacheWrite);
        s.WeightCacheRead = ParseWeight(TxtWeightCacheRead.Text, s.WeightCacheRead);

        s.Sanitize();
        return s;
    }

    // ------------------------------------------------------------------
    // Estado "há mudanças"
    // ------------------------------------------------------------------

    /// <summary>Liga todos os interruptores e opções ao aviso de alteração pendente.</summary>
    private void WireDirtyTracking()
    {
        void Hook(ToggleButton t)
        {
            t.Checked += (_, _) => { MarkDirty(); RenderPreview(); };
            t.Unchecked += (_, _) => { MarkDirty(); RenderPreview(); };
        }

        foreach (var c in new ToggleButton[]
                 {
                     ChkTray, ChkTaskbar, ChkGadget, ChkTopmost, ChkLocked, ChkShowReset,
                     ChkSession, ChkWeekly, ChkFable, ChkNotify, ChkStartup, ChkStartHidden,
                     OrientVertical, OrientHorizontal, TbLeft, TbRight,
                     GadgetVertical, GadgetHorizontal, ChkCheckUpdates,
                     ChkRateTaskbar, ChkRateGadget, RateWeekly, RateSession, RateFable,
                     Win5, Win20, Win60, Win1440, ChkTimeProgress
                 })
        {
            Hook(c);
        }
    }

    private void MarkDirty()
    {
        if (!_ready) return;
        var changed = CollectDraft().Serialize() != _baseline;
        SaveBar.Visibility = changed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAnyChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        UpdateLabels();
        MarkDirty();
    }

    private void OnAnyTextChanged(object sender, TextChangedEventArgs e)
    {
        MarkDirty();
        RenderPreview();
    }

    private void OnTabChanged(object sender, RoutedEventArgs e)
    {
        if (PanelDisplay == null) return;
        PanelDisplay.Visibility = Vis(TabDisplay);
        PanelBars.Visibility = Vis(TabBars);
        PanelRate.Visibility = Vis(TabRate);
        PanelAccount.Visibility = Vis(TabAccount);
        PanelSystem.Visibility = Vis(TabSystem);
        PanelData.Visibility = Vis(TabData);
        PanelAdvanced.Visibility = Vis(TabAdvanced);

        if (TabData.IsChecked == true) UpdateRetentionUi();
        if (TabBars.IsChecked == true) RenderPreview();
        if (TabRate.IsChecked == true) RenderRatePreview();
        if (TabAdvanced.IsChecked == true) UpdateUpdateUi();
    }

    private static Visibility Vis(RadioButton rb) => rb.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    // ------------------------------------------------------------------
    // Eventos
    // ------------------------------------------------------------------

    private void UpdateLabels()
    {
        LblOpacity.Text = Math.Round(SldOpacity.Value * 100) + "%";
        LblScale.Text = Math.Round(SldScale.Value * 100) + "%";
        LblRefresh.Text = FormatInterval((int)Math.Round(SldRefresh.Value));
        LblWarn.Text = Math.Round(SldWarn.Value) + "%";
        LblAlert.Text = Math.Round(SldAlert.Value) + "%";
        LblRetention.Text = Math.Round(SldRetention.Value) + " dias";
        LblTbOffset.Text = Math.Round(SldTbOffset.Value) + " px";
        LblTbScale.Text = Math.Round(SldTbScale.Value * 100) + "%";
        LblTbOpacity.Text = Math.Round(SldTbOpacity.Value * 100) + "%";
    }

    private static string FormatInterval(int seconds) =>
        seconds < 60 ? seconds + " s" : (seconds / 60) + " min";

    private static double ParseWeight(string text, double fallback)
    {
        var t = (text ?? "").Trim().Replace(',', '.');
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0 && !double.IsNaN(v)
            ? v
            : fallback;
    }

    private void OnRefreshIntervalChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        LblRefresh.Text = FormatInterval((int)Math.Round(e.NewValue));
        MarkDirty();
    }

    private void OnWarnChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        LblWarn.Text = Math.Round(e.NewValue) + "%";
        if (SldAlert.Value <= e.NewValue) SldAlert.Value = Math.Min(100, e.NewValue + 5);
        MarkDirty();
        RenderPreview();
    }

    private void OnAlertChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        LblAlert.Text = Math.Round(e.NewValue) + "%";
        MarkDirty();
        RenderPreview();
    }

    private void OnRetentionChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        UpdateRetentionUi();
        MarkDirty();
    }

    private void OnRetentionDaysChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        LblRetention.Text = Math.Round(e.NewValue) + " dias";
        MarkDirty();
    }

    private void OnSourceChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        TxtToken.IsEnabled = SrcManual.IsChecked == true;
        MarkDirty();
    }

    private void OnResetPositionClick(object sender, RoutedEventArgs e)
    {
        _resetPosition = true;
        MarkDirty();
        MessageBox.Show(Window.GetWindow(this), "O gadget voltará ao canto inferior direito ao salvar.",
            AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        BtnTest.IsEnabled = false;
        TestResult.Text = "Testando…";
        try
        {
            var draft = CollectDraft();
            var snap = await _host.TestAsync(draft);
            TxtRaw.Text = snap.RawJson ?? "(sem resposta)";
            if (snap.Ok && snap.Bars.Count > 0)
            {
                var names = new List<string>();
                foreach (var b in snap.Bars) names.Add($"{draft.LabelFor(b.Kind)} {Math.Round(b.Percent)}%");
                TestResult.Text = "OK — " + string.Join(" · ", names);
            }
            else
            {
                TestResult.Text = snap.Error ?? "Falhou sem mensagem.";
            }
        }
        catch (Exception ex)
        {
            TestResult.Text = ex.Message;
        }
        finally
        {
            BtnTest.IsEnabled = true;
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var draft = CollectDraft();
        _host.ApplySettings(draft);
        _resetPosition = false;
        _baseline = draft.Serialize();
        SaveBar.Visibility = Visibility.Collapsed;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _ready = false;
        _resetPosition = false;
        LoadUi(_host.Settings);
        _ready = true;
        UpdateLabels();
        RenderPreview();
        SaveBar.Visibility = Visibility.Collapsed;
    }

    private void OnExitAppClick(object sender, RoutedEventArgs e)
    {
        var r = MessageBox.Show(Window.GetWindow(this), "Fechar o Claude Indicator?", AppInfo.Name,
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r == MessageBoxResult.Yes) _host.Exit();
    }

    // ------------------------------------------------------------------
    // Prévia e diagnóstico
    // ------------------------------------------------------------------

    private void OnUsageUpdated(UsageSnapshot? snap)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RenderPreview));
            return;
        }
        RenderPreview();
    }

    private void RenderPreview()
    {
        if (!_ready) return;

        PreviewPanel.Children.Clear();
        var s = CollectDraft();
        var snap = _host.Last;

        if (snap == null)
        {
            PreviewPanel.Children.Add(Hint("Consultando a API…"));
            return;
        }

        var bars = snap.Visible(s);
        if (bars.Count == 0)
        {
            PreviewPanel.Children.Add(Hint(snap.Error ?? "Nenhuma barra disponível."));
        }
        else
        {
            foreach (var bar in bars)
                PreviewPanel.Children.Add(BarRenderer.BuildRow(bar, s, true, 12));
        }

        TxtRaw.Text = snap.RawJson ?? "(sem resposta)";
        var info = new StringBuilder();
        if (!string.IsNullOrEmpty(snap.EndpointUsed)) info.Append("Endpoint: ").Append(snap.EndpointUsed);
        foreach (var bar in snap.Bars)
        {
            info.Append('\n').Append(bar.Kind).Append(" ← ")
                .Append(string.IsNullOrEmpty(bar.SourcePath) ? "(raiz)" : bar.SourcePath)
                .Append(" = ").Append(Math.Round(bar.Percent, 1)).Append('%');
        }
        DiagInfo.Text = info.ToString();
        AccountStatus.Text = DescribeAccount();
    }

    private void UpdateRetentionUi()
    {
        var keepDays = KeepDays.IsChecked == true;
        SldRetention.IsEnabled = keepDays;
        LblRetentionCaption.Opacity = keepDays ? 1 : 0.45;
        LblRetention.Opacity = keepDays ? 1 : 0.45;

        var sb = new StringBuilder();
        var bytes = UsageHistory.FileSizeBytes();
        var oldest = UsageHistory.OldestPoint();

        if (bytes == 0)
        {
            sb.Append("Nenhum registro ainda. ");
        }
        else
        {
            sb.Append(bytes < 1024 * 1024
                ? $"Arquivo atual: {bytes / 1024.0:0.#} KB"
                : $"Arquivo atual: {bytes / (1024.0 * 1024.0):0.#} MB");
            if (oldest != null)
                sb.Append(", desde ").Append(oldest.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm"));
            sb.Append(". ");
        }

        var seconds = Math.Max(60, (int)Math.Round(SldRefresh.Value));
        var perMonth = (int)Math.Round(30.0 * 24 * 3600 / seconds * 62 / 1024.0);
        sb.Append("No intervalo atual dá cerca de ").Append(perMonth).Append(" KB por mês. ")
          .Append(keepDays ? "Registros mais antigos que o limite são apagados." : "Nada é apagado automaticamente.");

        HistoryInfo.Text = sb.ToString();
    }

    /// <summary>Prévia do velocímetro com o ritmo real do momento.</summary>
    private void RenderRatePreview()
    {
        var s = CollectDraft();
        var kind = s.RateKind;
        var span = TimeSpan.FromMinutes(Math.Max(120, s.RateWindowMinutes * 2));
        var rate = ConsumptionRate.Measure(UsageHistory.Load(span), _host.Last?.Get(kind), kind, s.RateWindowMinutes);

        RatePreviewHost.Content = GaugeRenderer.Build(rate, 64);
        RatePreviewValue.Text = ConsumptionRate.Format(rate);
        RatePreviewValue.Foreground = new System.Windows.Media.SolidColorBrush(GaugeRenderer.ColorFor(rate));

        if (!rate.HasData)
        {
            RatePreviewCaption.Text = "Ainda sem medição suficiente — o ritmo aparece depois de alguns minutos de histórico.";
            return;
        }

        var left = ConsumptionRate.FormatTimeLeft(rate);
        RatePreviewCaption.Text = rate.Sustainable > 0
            ? (left.Length > 0 ? left + " · " : "") +
              $"o limite aguenta até {rate.Sustainable:0.###}% p/min"
            : left;
    }

    // ------------------------------------------------------------------
    // Atualizações
    // ------------------------------------------------------------------

    private void UpdateUpdateUi()
    {
        var info = _host.Updates.Available;
        var last = _host.Updates.LastCheck;

        UpdateStatus.Text = $"Instalada: versão {AppInfo.Version}."
                            + (last == DateTimeOffset.MinValue
                                ? " Ainda não consultei o GitHub."
                                : $" Última consulta: {last.ToLocalTime():dd/MM HH:mm}.");

        if (info != null)
        {
            UpdateResult.Text = $"Versão {info.Version} disponível.";
            BtnInstallUpdate.Visibility = Visibility.Visible;
        }
        else
        {
            BtnInstallUpdate.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        BtnCheckUpdate.IsEnabled = false;
        UpdateResult.Text = "Consultando o GitHub…";
        try
        {
            var info = await _host.CheckUpdatesAsync(force: true);
            UpdateResult.Text = info == null
                ? $"Você está na versão mais recente ({AppInfo.Version})."
                : $"Versão {info.Version} disponível.";
            BtnInstallUpdate.Visibility = info == null ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception ex)
        {
            UpdateResult.Text = "Falha ao consultar: " + ex.Message;
        }
        finally
        {
            BtnCheckUpdate.IsEnabled = true;
            UpdateUpdateUi();
        }
    }

    private async void OnInstallUpdateClick(object sender, RoutedEventArgs e)
    {
        var info = _host.Updates.Available;
        if (info == null) return;

        if (string.IsNullOrWhiteSpace(info.DownloadUrl))
        {
            UpdateResult.Text = "A release não tem instalador anexado. Abrindo a página…";
            UpdateChecker.OpenPage(info.PageUrl);
            return;
        }

        BtnInstallUpdate.IsEnabled = false;
        var progress = new Progress<double>(p => UpdateResult.Text = $"Baixando… {p * 100:0}%");
        try
        {
            var file = await UpdateChecker.DownloadAsync(info, progress);
            if (file == null)
            {
                UpdateResult.Text = "Não foi possível baixar. Abrindo a página da release…";
                UpdateChecker.OpenPage(info.PageUrl);
                return;
            }

            UpdateResult.Text = "Instalando…";
            if (!UpdateChecker.RunInstaller(file))
                UpdateResult.Text = "Não foi possível iniciar o instalador.";
        }
        catch (Exception ex)
        {
            UpdateResult.Text = "Falha: " + ex.Message;
        }
        finally
        {
            BtnInstallUpdate.IsEnabled = true;
        }
    }

    private string DescribeAccount()
    {
        var sb = new StringBuilder();
        if (CredentialStore.ClaudeCodeDetected)
        {
            sb.Append("Login do Claude Code encontrado em ").Append(CredentialStore.ClaudeCodeCredentialsPath);
            var cred = CredentialStore.ReadClaudeCodeFile();
            if (cred?.SubscriptionType is { Length: > 0 })
                sb.Append(" · plano ").Append(cred.SubscriptionType);
        }
        else
        {
            sb.Append("Login do Claude Code não encontrado. Rode `claude` no terminal e faça login, ou use um token manual.");
        }
        return sb.ToString();
    }

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Foreground = BarRenderer.Swatch("MutedBrush")
    };
}
