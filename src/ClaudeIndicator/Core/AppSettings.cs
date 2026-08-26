using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeIndicator.Core;

public enum DisplayMode
{
    Tray,
    Gadget,
    Both
}

/// <summary>Como as barras são organizadas (ícone da bandeja e gadget).</summary>
public enum BarOrientation
{
    /// <summary>Empilhadas: no ícone, colunas lado a lado; no gadget, uma barra por linha.</summary>
    Vertical,

    /// <summary>Lado a lado na horizontal: no ícone, linhas; no gadget, uma célula por barra na mesma linha.</summary>
    Horizontal
}

/// <summary>
/// Todas as preferências do usuário. Persistido em %APPDATA%\ClaudeIndicator\settings.json
/// </summary>
public class AppSettings
{
    // ---- Exibição ----
    /// <summary>Mantido para ler configurações antigas; as três opções abaixo é que valem.</summary>
    public DisplayMode DisplayMode { get; set; } = DisplayMode.Tray;

    // nulos até o Sanitize resolver: assim uma configuração antiga (que só tinha DisplayMode)
    // é migrada sem perder a escolha do usuário
    public bool? ShowTrayIcon { get; set; }
    public bool? ShowGadget { get; set; }

    /// <summary>Painel desenhado no espaço livre da barra de tarefas.</summary>
    public bool ShowTaskbarBar { get; set; }

    public BarOrientation TrayOrientation { get; set; } = BarOrientation.Vertical;

    // ---- Painel da barra de tarefas ----
    public TaskbarAnchor TaskbarBarAnchor { get; set; } = TaskbarAnchor.Left;
    public double TaskbarBarOpacity { get; set; } = 0.0;
    public double TaskbarBarScale { get; set; } = 1.0;
    public double TaskbarBarOffset { get; set; } = 8;

    // ---- Painel do PC (CPU, GPU, memória) ----
    /// <summary>Segundo painel na barra de tarefas, com os sensores do computador.</summary>
    public bool ShowPcPanel { get; set; }

    /// <summary>
    /// Lado do painel do PC. O padrão é o oposto do painel da IA: a ideia é justamente ter um
    /// bloco de cada lado, para nunca haver dúvida sobre qual indicador é qual.
    /// </summary>
    public TaskbarAnchor PcPanelAnchor { get; set; } = TaskbarAnchor.Right;

    public bool PcShowCpu { get; set; } = true;
    public bool PcShowGpu { get; set; } = true;
    public bool PcShowRam { get; set; } = true;

    /// <summary>
    /// Ler temperatura e watts da CPU. Desligado por padrão de propósito: isso carrega um driver
    /// de kernel, exige elevação, dispara alerta de antivírus e é barrado quando a Integridade de
    /// Memória está ligada. O uso da CPU é lido sem nada disso.
    /// </summary>
    public bool PcCpuSensors { get; set; }

    /// <summary>Intervalo de leitura dos sensores, em segundos.</summary>
    public int PcIntervalSeconds { get; set; } = 2;

    [JsonIgnore] public bool TrayEnabled => ShowTrayIcon ?? true;
    [JsonIgnore] public bool GadgetEnabled => ShowGadget ?? false;

    // ---- Barras ----
    public bool ShowSession { get; set; } = true;
    public bool ShowWeekly { get; set; } = true;
    public bool ShowFable { get; set; } = true;

    public string SessionLabel { get; set; } = "Sessão";
    public string WeeklyLabel { get; set; } = "Semanal";
    public string FableLabel { get; set; } = "Fable 5";

    // ---- Atualização ----
    public int RefreshSeconds { get; set; } = 120;

    // ---- Gadget ----
    /// <summary>
    /// Posição do gadget. Nulo = nunca posicionado. Antes o "nunca posicionado" era -1, o que
    /// confundia com coordenada negativa de verdade — quem tem monitor à esquerda do principal
    /// perdia a posição a cada atualização.
    /// </summary>
    public double? GadgetLeft { get; set; }
    public double? GadgetTop { get; set; }
    public double GadgetOpacity { get; set; } = 0.95;
    public double GadgetScale { get; set; } = 1.0;
    public bool GadgetTopmost { get; set; } = true;
    public bool GadgetLocked { get; set; } = false;
    public bool GadgetShowReset { get; set; } = true;
    public BarOrientation GadgetOrientation { get; set; } = BarOrientation.Vertical;

    // ---- Histórico ----
    /// <summary>Dias de histórico mantidos. 0 (padrão) = guardar para sempre, nada é apagado.</summary>
    public int HistoryRetentionDays { get; set; } = 0;

    // ---- Velocímetro (ritmo de consumo) ----
    /// <summary>Mostra o ritmo de consumo no gadget.</summary>
    public bool ShowRateGadget { get; set; } = true;

    /// <summary>Mostra o ritmo de consumo no painel da barra de tarefas.</summary>
    public bool ShowRateTaskbar { get; set; } = true;

    /// <summary>Qual limite o ritmo acompanha.</summary>
    public BarKind RateKind { get; set; } = BarKind.Weekly;

    /// <summary>
    /// Linha do tempo dos últimos ciclos de comunicação com a API, no gadget e no painel da
    /// barra de tarefas.
    /// </summary>
    public bool ShowCallTimeline { get; set; } = true;

    /// <summary>Janela de medição do ritmo, em minutos (5, 20, 60 ou 1440).</summary>
    public int RateWindowMinutes { get; set; } = 20;

    /// <summary>
    /// Barra do tempo decorrido na janela do limite, junto da barra de consumo. Serve para
    /// comparar os dois: gastar mais rápido que o relógio significa acabar antes de renovar.
    /// </summary>
    public bool ShowTimeProgress { get; set; } = true;

    // ---- Atualização ----
    /// <summary>Procurar versão nova no GitHub (uma vez a cada 6 h, no máximo).</summary>
    public bool CheckUpdates { get; set; } = true;

    /// <summary>Repositório consultado, no formato dono/nome.</summary>
    public string UpdateRepository { get; set; } = "TeteuPower/claude-indicator";

    /// <summary>
    /// Aceitar a pré-release "latest", que é a gerada a cada push na main. Desligado, só as
    /// versões marcadas com tag contam como atualização.
    /// </summary>
    public bool IncludePrereleases { get; set; } = true;

    /// <summary>Versão que o usuário mandou ignorar; não avisa de novo até sair uma posterior.</summary>
    public string SkippedVersion { get; set; } = "";

    // ---- Sistema ----
    public bool StartWithWindows { get; set; } = false;
    public bool StartHidden { get; set; } = true;

    // ---- Conta ----
    /// <summary>"ClaudeCode" = lê %USERPROFILE%\.claude\.credentials.json | "Manual" = token colado</summary>
    public string CredentialSource { get; set; } = "ClaudeCode";
    public string ManualAccessToken { get; set; } = "";

    // ---- Avançado ----
    public List<string> UsageEndpoints { get; set; } = new()
    {
        "https://api.anthropic.com/api/oauth/usage",
        "https://api.anthropic.com/api/claude_cli/usage"
    };

    // ---- Consumo por projeto (transcrições do Claude Code) ----
    /// <summary>
    /// Pesos do custo aproximado de cada tipo de token. A API não publica a fórmula do limite,
    /// então isto é uma aproximação: serve para repartir o consumo entre projetos, não para
    /// calcular valor absoluto. Entrada é a referência (peso 1).
    /// </summary>
    public double WeightOutput { get; set; } = 5.0;
    public double WeightCacheWrite { get; set; } = 1.25;
    public double WeightCacheRead { get; set; } = 0.1;

    /// <summary>Trechos de id de modelo que contam no limite próprio do Fable (separados por vírgula).</summary>
    public string FableModelIds { get; set; } = "fable";

    public string SessionKeywords { get; set; } = "five_hour,fivehour,5h,session,current";
    public string WeeklyKeywords { get; set; } = "seven_day,sevenday,7d,week,weekly";
    public string FableKeywords { get; set; } = "opus,fable";

    public int WarnThreshold { get; set; } = 75;
    public int AlertThreshold { get; set; } = 90;
    public bool NotifyOnThreshold { get; set; } = true;

    // ---------------------------------------------------------------

    [JsonIgnore]
    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeIndicator");

    [JsonIgnore]
    public static string FilePath => Path.Combine(DataDir, "settings.json");

    [JsonIgnore]
    public static bool Exists => File.Exists(FilePath);

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<AppSettings>(json, Opts);
                if (s != null)
                {
                    s.Sanitize();
                    return s;
                }
            }
        }
        catch
        {
            // configuração corrompida: volta ao padrão
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts));
        }
        catch
        {
            // sem permissão de escrita: ignora
        }
    }

    /// <summary>JSON das preferências: usado para comparar rascunho com o que está salvo.</summary>
    public string Serialize() => JsonSerializer.Serialize(this, Opts);

    public AppSettings Clone()
    {
        var json = JsonSerializer.Serialize(this, Opts);
        return JsonSerializer.Deserialize<AppSettings>(json, Opts) ?? new AppSettings();
    }

    public void Sanitize()
    {
        // migração do sentinela antigo
        if (GadgetLeft == -1 && GadgetTop == -1) { GadgetLeft = null; GadgetTop = null; }

        // migração do DisplayMode antigo para as três opções independentes
        ShowTrayIcon ??= DisplayMode is DisplayMode.Tray or DisplayMode.Both;
        ShowGadget ??= DisplayMode is DisplayMode.Gadget or DisplayMode.Both;
        if (!ShowTrayIcon.Value && !ShowGadget.Value && !ShowTaskbarBar) ShowTrayIcon = true;

        if (TaskbarBarOpacity < 0) TaskbarBarOpacity = 0;
        if (TaskbarBarOpacity > 1) TaskbarBarOpacity = 1;
        if (TaskbarBarScale < 0.8) TaskbarBarScale = 0.8;
        if (TaskbarBarScale > 1.6) TaskbarBarScale = 1.6;
        if (TaskbarBarOffset < 0) TaskbarBarOffset = 0;
        if (TaskbarBarOffset > 600) TaskbarBarOffset = 600;

        // piso de 60s: o limite de consultas é da conta e cada sessão do Claude Code
        // aberta consulta o mesmo endpoint — abaixo disso o HTTP 429 é questão de tempo
        if (RefreshSeconds < 60) RefreshSeconds = 60;
        if (RefreshSeconds > 3600) RefreshSeconds = 3600;
        if (GadgetOpacity < 0.25) GadgetOpacity = 0.25;
        if (GadgetOpacity > 1.0) GadgetOpacity = 1.0;
        if (GadgetScale < 0.7) GadgetScale = 0.7;
        if (GadgetScale > 2.0) GadgetScale = 2.0;
        if (Array.IndexOf(ConsumptionRate.WindowChoices, RateWindowMinutes) < 0) RateWindowMinutes = 20;
        if (PcIntervalSeconds < 1) PcIntervalSeconds = 1;
        if (PcIntervalSeconds > 30) PcIntervalSeconds = 30;
        if (!PcShowCpu && !PcShowGpu && !PcShowRam) PcShowCpu = true;
        if (WeightOutput <= 0 || double.IsNaN(WeightOutput)) WeightOutput = 5.0;
        if (WeightCacheWrite < 0 || double.IsNaN(WeightCacheWrite)) WeightCacheWrite = 1.25;
        if (WeightCacheRead < 0 || double.IsNaN(WeightCacheRead)) WeightCacheRead = 0.1;
        if (string.IsNullOrWhiteSpace(FableModelIds)) FableModelIds = "fable";
        if (HistoryRetentionDays < 0) HistoryRetentionDays = 0;
        if (HistoryRetentionDays > 3650) HistoryRetentionDays = 3650;
        if (WarnThreshold < 1) WarnThreshold = 1;
        if (WarnThreshold > 99) WarnThreshold = 99;
        if (AlertThreshold <= WarnThreshold) AlertThreshold = Math.Min(100, WarnThreshold + 5);
        if (!ShowSession && !ShowWeekly && !ShowFable) ShowSession = true;
        if (UsageEndpoints == null || UsageEndpoints.Count == 0)
            UsageEndpoints = new List<string> { "https://api.anthropic.com/api/oauth/usage" };
        if (string.IsNullOrWhiteSpace(SessionLabel)) SessionLabel = "Sessão";
        if (string.IsNullOrWhiteSpace(WeeklyLabel)) WeeklyLabel = "Semanal";
        if (string.IsNullOrWhiteSpace(FableLabel)) FableLabel = "Fable 5";
        if (CredentialSource != "Manual") CredentialSource = "ClaudeCode";
    }

    public IEnumerable<string> EndpointList()
    {
        foreach (var e in UsageEndpoints)
        {
            if (!string.IsNullOrWhiteSpace(e)) yield return e.Trim();
        }
    }

    public bool IsEnabled(BarKind kind) => kind switch
    {
        BarKind.Session => ShowSession,
        BarKind.Weekly => ShowWeekly,
        BarKind.Fable => ShowFable,
        _ => false
    };

    public string LabelFor(BarKind kind) => kind switch
    {
        BarKind.Session => SessionLabel,
        BarKind.Weekly => WeeklyLabel,
        BarKind.Fable => FableLabel,
        _ => kind.ToString()
    };

    public IEnumerable<BarKind> EnabledKinds()
    {
        if (ShowSession) yield return BarKind.Session;
        if (ShowWeekly) yield return BarKind.Weekly;
        if (ShowFable) yield return BarKind.Fable;
    }
}
