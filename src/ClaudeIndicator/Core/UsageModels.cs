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
