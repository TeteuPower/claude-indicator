using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using ClaudeIndicator.Core;

namespace ClaudeIndicator.Views;

public partial class SettingsWindow : Window
{
    private readonly AppHost _host;
    private bool _ready;
    private bool _resetPosition;

    public SettingsWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();

        LoadUi(host.Settings);
        _ready = true;

        UpdateLabels();
        RenderPreview();
        WireLivePreview();

        _host.Updated += OnUsageUpdated;
    }

    // ------------------------------------------------------------------

    private void LoadUi(AppSettings s)
    {
        ModeTray.IsChecked = s.DisplayMode == DisplayMode.Tray;
        ModeGadget.IsChecked = s.DisplayMode == DisplayMode.Gadget;
        ModeBoth.IsChecked = s.DisplayMode == DisplayMode.Both;

        OrientVertical.IsChecked = s.TrayOrientation == BarOrientation.Vertical;
        OrientHorizontal.IsChecked = s.TrayOrientation == BarOrientation.Horizontal;
        GadgetVertical.IsChecked = s.GadgetOrientation == BarOrientation.Vertical;
        GadgetHorizontal.IsChecked = s.GadgetOrientation == BarOrientation.Horizontal;

        ChkSession.IsChecked = s.ShowSession;
        ChkWeekly.IsChecked = s.ShowWeekly;
        ChkFable.IsChecked = s.ShowFable;
        TxtSessionLabel.Text = s.SessionLabel;
        TxtWeeklyLabel.Text = s.WeeklyLabel;
        TxtFableLabel.Text = s.FableLabel;

        SldOpacity.Value = s.GadgetOpacity;
        SldScale.Value = s.GadgetScale;
        ChkTopmost.IsChecked = s.GadgetTopmost;
        ChkLocked.IsChecked = s.GadgetLocked;
        ChkShowReset.IsChecked = s.GadgetShowReset;

        SrcClaudeCode.IsChecked = s.CredentialSource != "Manual";
        SrcManual.IsChecked = s.CredentialSource == "Manual";
        TxtToken.Text = s.ManualAccessToken;
        TxtToken.IsEnabled = SrcManual.IsChecked == true;

        KeepForever.IsChecked = s.HistoryRetentionDays <= 0;
        KeepDays.IsChecked = s.HistoryRetentionDays > 0;
        SldRetention.Value = s.HistoryRetentionDays > 0 ? Math.Clamp(s.HistoryRetentionDays, 7, 730) : 31;

        ChkStartup.IsChecked = s.StartWithWindows;
        ChkStartHidden.IsChecked = s.StartHidden;
        ChkNotify.IsChecked = s.NotifyOnThreshold;
        SldRefresh.Value = s.RefreshSeconds;
        SldWarn.Value = s.WarnThreshold;
        SldAlert.Value = s.AlertThreshold;

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

    private void WireLivePreview()
    {
        void Hook(CheckBox cb)
        {
            cb.Checked += (_, _) => RenderPreview();
            cb.Unchecked += (_, _) => RenderPreview();
        }
        Hook(ChkSession);
        Hook(ChkWeekly);
        Hook(ChkFable);
        Hook(ChkShowReset);

        TxtSessionLabel.TextChanged += (_, _) => RenderPreview();
        TxtWeeklyLabel.TextChanged += (_, _) => RenderPreview();
        TxtFableLabel.TextChanged += (_, _) => RenderPreview();
    }

    private AppSettings CollectDraft()
    {
        var s = _host.Settings.Clone();

        s.DisplayMode = ModeGadget.IsChecked == true
            ? DisplayMode.Gadget
            : ModeBoth.IsChecked == true
                ? DisplayMode.Both
                : DisplayMode.Tray;

        s.TrayOrientation = OrientHorizontal.IsChecked == true
            ? BarOrientation.Horizontal
            : BarOrientation.Vertical;
        s.GadgetOrientation = GadgetHorizontal.IsChecked == true
            ? BarOrientation.Horizontal
            : BarOrientation.Vertical;

        s.ShowSession = ChkSession.IsChecked == true;
        s.ShowWeekly = ChkWeekly.IsChecked == true;
        s.ShowFable = ChkFable.IsChecked == true;
        s.SessionLabel = TxtSessionLabel.Text;
        s.WeeklyLabel = TxtWeeklyLabel.Text;
        s.FableLabel = TxtFableLabel.Text;

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

        s.CredentialSource = SrcManual.IsChecked == true ? "Manual" : "ClaudeCode";
        s.ManualAccessToken = TxtToken.Text.Trim();

        s.HistoryRetentionDays = KeepDays.IsChecked == true ? (int)Math.Round(SldRetention.Value) : 0;

        s.StartWithWindows = ChkStartup.IsChecked == true;
        s.StartHidden = ChkStartHidden.IsChecked == true;
        s.NotifyOnThreshold = ChkNotify.IsChecked == true;
        s.RefreshSeconds = (int)Math.Round(SldRefresh.Value);
        s.WarnThreshold = (int)Math.Round(SldWarn.Value);
        s.AlertThreshold = (int)Math.Round(SldAlert.Value);

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

    private void RenderPreview()
    {
        if (!_ready) return;

        PreviewPanel.Children.Clear();
        var s = CollectDraft();
        var snap = _host.Last;

        if (snap == null)
        {
            PreviewPanel.Children.Add(Hint("Consultando a API…"));
            HeaderStatus.Text = "Consultando a API…";
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

        var sb = new StringBuilder();
        sb.Append(string.IsNullOrEmpty(snap.Account) ? "Conta não identificada" : snap.Account);
        sb.Append(" · atualizado ").Append(snap.FetchedAt.ToLocalTime().ToString("HH:mm:ss"));
        if (!snap.Ok) sb.Append(" · ").Append(snap.Error);
        HeaderStatus.Text = sb.ToString();

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

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Foreground = BarRenderer.Swatch("MutedBrush")
    };

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

    private void OnUsageUpdated(UsageSnapshot? snap)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => RenderPreview());
            return;
        }
        RenderPreview();
    }

    private void UpdateLabels()
    {
        LblOpacity.Text = Math.Round(SldOpacity.Value * 100) + "%";
        LblScale.Text = Math.Round(SldScale.Value * 100) + "%";
        LblRefresh.Text = FormatInterval((int)Math.Round(SldRefresh.Value));
        LblWarn.Text = Math.Round(SldWarn.Value) + "%";
        LblAlert.Text = Math.Round(SldAlert.Value) + "%";
        LblRetention.Text = Math.Round(SldRetention.Value) + " dias";
        UpdateRetentionUi();
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

        sb.Append("Cada consulta grava uma linha: no intervalo atual dá cerca de ")
          .Append(EstimateKbPerMonth()).Append(" KB por mês. ")
          .Append(keepDays
              ? "Registros mais antigos que o limite são apagados."
              : "Nada é apagado automaticamente.");

        HistoryInfo.Text = sb.ToString();
    }

    private int EstimateKbPerMonth()
    {
        var seconds = Math.Max(15, (int)Math.Round(SldRefresh.Value));
        var pointsPerMonth = 30.0 * 24 * 3600 / Math.Max(60, seconds); // 1 ponto por minuto no máximo
        return (int)Math.Round(pointsPerMonth * 62 / 1024.0);          // ~62 bytes por linha
    }

    private static string FormatInterval(int seconds) =>
        seconds < 60 ? seconds + " s" : (seconds / 60) + " min";

    /// <summary>Aceita vírgula ou ponto como separador decimal; valor inválido mantém o anterior.</summary>
    private static double ParseWeight(string text, double fallback)
    {
        var t = (text ?? "").Trim().Replace(',', '.');
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0 && !double.IsNaN(v)
            ? v
            : fallback;
    }

    // ------------------------------------------------------------------
    // Eventos
    // ------------------------------------------------------------------

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        LblOpacity.Text = Math.Round(e.NewValue * 100) + "%";
    }

    private void OnScaleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        LblScale.Text = Math.Round(e.NewValue * 100) + "%";
    }

    private void OnRefreshIntervalChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        LblRefresh.Text = FormatInterval((int)Math.Round(e.NewValue));
    }

    private void OnWarnChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        LblWarn.Text = Math.Round(e.NewValue) + "%";
        if (SldAlert.Value <= e.NewValue) SldAlert.Value = Math.Min(100, e.NewValue + 5);
        RenderPreview();
    }

    private void OnAlertChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        LblAlert.Text = Math.Round(e.NewValue) + "%";
        RenderPreview();
    }

    private void OnRetentionChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        UpdateRetentionUi();
    }

    private void OnRetentionDaysChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        LblRetention.Text = Math.Round(e.NewValue) + " dias";
    }

    private void OnSourceChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        TxtToken.IsEnabled = SrcManual.IsChecked == true;
    }

    private void OnToggleDiagClick(object sender, RoutedEventArgs e)
    {
        DiagPanel.Visibility = DiagPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void OnResetPositionClick(object sender, RoutedEventArgs e)
    {
        _resetPosition = true;
        MessageBox.Show(this, "O gadget voltará ao canto inferior direito ao salvar.",
            "Claude Indicator", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnHistoryClick(object sender, RoutedEventArgs e) => _host.ShowHistory();

    private void OnProjectsClick(object sender, RoutedEventArgs e) => _host.ShowProjects();

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        HeaderStatus.Text = "Atualizando…";
        await _host.RefreshAsync(true);
        RenderPreview();
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
                DiagPanel.Visibility = Visibility.Visible;
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
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnExitAppClick(object sender, RoutedEventArgs e)
    {
        var r = MessageBox.Show(this, "Fechar o Claude Indicator?", "Claude Indicator",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r == MessageBoxResult.Yes) _host.Exit();
    }

    private void OnClosed(object sender, EventArgs e)
    {
        _host.Updated -= OnUsageUpdated;
    }
}
