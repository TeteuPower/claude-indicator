using System;
using System.Collections.Generic;

namespace ClaudeIndicator.Core;

public enum BarKind
{
    Session,
    Weekly,
    Fable
}

public class UsageBar
{
    public BarKind Kind { get; set; }
    public double Percent { get; set; }
    public DateTimeOffset? ResetsAt { get; set; }
    public string? SourcePath { get; set; }

    public double Fraction => Math.Clamp(Percent / 100.0, 0, 1);

    /// <summary>
    /// Duração da janela de cada limite. A sessão é de 5 horas e os semanais de 7 dias — é o que
    /// permite dizer quanto da janela já passou, e não só quando ela renova.
    /// </summary>
    public static TimeSpan WindowFor(BarKind kind) =>
        kind == BarKind.Session ? TimeSpan.FromHours(5) : TimeSpan.FromDays(7);

    /// <summary>
    /// Quanto da janela já passou (0..1), pelo horário de renovação. Null quando a API não
    /// informou o reset.
    /// </summary>
    public double? TimeFraction()
    {
        if (ResetsAt == null) return null;
        var window = WindowFor(Kind);
        var left = ResetsAt.Value - DateTimeOffset.Now;
        if (left <= TimeSpan.Zero) return 1;
        if (left >= window) return 0;
        return Math.Clamp(1 - left.TotalSeconds / window.TotalSeconds, 0, 1);
    }

    /// <summary>Texto curto do tempo decorrido, para tooltip.</summary>
    public string TimeProgressText()
    {
        var f = TimeFraction();
        if (f == null) return "";
        var window = WindowFor(Kind);
        var elapsed = TimeSpan.FromSeconds(window.TotalSeconds * f.Value);
        var label = window >= TimeSpan.FromDays(1)
            ? $"{elapsed.Days}d {elapsed.Hours}h de {window.Days}d"
            : $"{(int)elapsed.TotalHours}h {elapsed.Minutes:00}m de {(int)window.TotalHours}h";
        return $"{Math.Round(f.Value * 100)}% da janela decorrida ({label})";
    }

    public string ResetText()
    {
        if (ResetsAt == null) return "";
        var span = ResetsAt.Value.ToLocalTime() - DateTimeOffset.Now;
        if (span.TotalSeconds <= 0) return "renovando…";
        if (span.TotalDays >= 1)
        {
            var days = (int)span.TotalDays;
            var hrs = span.Hours;
            return hrs > 0 ? $"reseta em {days}d {hrs}h" : $"reseta em {days}d";
        }
        if (span.TotalHours >= 1) return $"reseta em {(int)span.TotalHours}h {span.Minutes:00}m";
        return $"reseta em {Math.Max(1, (int)span.TotalMinutes)}m";
    }

    public string ResetClock()
    {
        if (ResetsAt == null) return "";
        var local = ResetsAt.Value.ToLocalTime();
        return local.Date == DateTimeOffset.Now.Date
            ? local.ToString("HH:mm")
            : local.ToString("dd/MM HH:mm");
    }
}

public class UsageSnapshot
{
    public List<UsageBar> Bars { get; set; } = new();
    public DateTimeOffset FetchedAt { get; set; }
    public string? Error { get; set; }
    public string? RawJson { get; set; }
    public string? EndpointUsed { get; set; }
    public string? Account { get; set; }

    /// <summary>Quando as barras foram de fato obtidas da API (difere de FetchedAt quando são reaproveitadas).</summary>
    public DateTimeOffset? DataAt { get; set; }

    /// <summary>As barras vieram de uma consulta anterior porque a atual falhou.</summary>
    public bool Stale { get; set; }

    /// <summary>A API devolveu HTTP 429 (limite de consultas).</summary>
    public bool RateLimited { get; set; }

    /// <summary>Segundos sugeridos pelo cabeçalho Retry-After, quando presente.</summary>
    public int? RetryAfterSeconds { get; set; }

    public bool Ok => string.IsNullOrEmpty(Error);

    public UsageBar? Get(BarKind kind)
    {
        foreach (var b in Bars)
        {
            if (b.Kind == kind) return b;
        }
        return null;
    }

    /// <summary>Barras habilitadas nas configurações, na ordem sessão → semanal → fable.</summary>
    public List<UsageBar> Visible(AppSettings s)
    {
        var list = new List<UsageBar>();
        foreach (var kind in s.EnabledKinds())
        {
            var bar = Get(kind);
            if (bar != null) list.Add(bar);
        }
        return list;
    }
}
