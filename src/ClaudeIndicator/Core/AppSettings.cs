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

/// <summary>Como as barras são desenhadas no ícone da bandeja.</summary>
public enum TrayOrientation
{
    /// <summary>Uma coluna por barra, preenchendo de baixo para cima.</summary>
    Vertical,

    /// <summary>Uma linha por barra, preenchendo da esquerda para a direita.</summary>
    Horizontal
}

/// <summary>
/// Todas as preferências do usuário. Persistido em %APPDATA%\ClaudeIndicator\settings.json
/// </summary>
public class AppSettings
{
    // ---- Exibição ----
    public DisplayMode DisplayMode { get; set; } = DisplayMode.Tray;
    public TrayOrientation TrayOrientation { get; set; } = TrayOrientation.Vertical;

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
    public double GadgetLeft { get; set; } = -1;
    public double GadgetTop { get; set; } = -1;
    public double GadgetOpacity { get; set; } = 0.95;
    public double GadgetScale { get; set; } = 1.0;
    public bool GadgetTopmost { get; set; } = true;
    public bool GadgetLocked { get; set; } = false;
    public bool GadgetShowReset { get; set; } = true;

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

    public AppSettings Clone()
    {
        var json = JsonSerializer.Serialize(this, Opts);
        return JsonSerializer.Deserialize<AppSettings>(json, Opts) ?? new AppSettings();
    }

    public void Sanitize()
    {
        if (RefreshSeconds < 15) RefreshSeconds = 15;
        if (RefreshSeconds > 3600) RefreshSeconds = 3600;
        if (GadgetOpacity < 0.25) GadgetOpacity = 0.25;
        if (GadgetOpacity > 1.0) GadgetOpacity = 1.0;
        if (GadgetScale < 0.7) GadgetScale = 0.7;
        if (GadgetScale > 2.0) GadgetScale = 2.0;
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
