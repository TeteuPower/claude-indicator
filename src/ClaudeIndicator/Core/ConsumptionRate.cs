using System;
using System.Collections.Generic;

namespace ClaudeIndicator.Core;

/// <summary>Ritmo de consumo agora, e como ele se compara ao que o limite aguenta.</summary>
public sealed class RateReading
{
    /// <summary>Pontos percentuais do limite consumidos por minuto.</summary>
    public double PerMinute { get; init; }

    /// <summary>
    /// Ritmo que gastaria exatamente o que resta até a renovação. É a referência do velocímetro:
    /// abaixo dela o limite dura até renovar, acima dela acaba antes.
    /// </summary>
    public double Sustainable { get; init; }

    /// <summary>Minutos até o limite acabar no ritmo atual, ou null se o ritmo é zero.</summary>
    public double? MinutesToEmpty { get; init; }

    /// <summary>Havia medição suficiente para calcular.</summary>
    public bool HasData { get; init; }

    /// <summary>Janela pedida, em minutos.</summary>
    public int WindowMinutes { get; init; }

    /// <summary>Minutos de histórico que a janela realmente cobriu (pode ser menos, com o app fechado).</summary>
    public double MeasuredMinutes { get; init; }

    /// <summary>0 = parado, 1 = exatamente no ritmo sustentável, acima disso está gastando rápido demais.</summary>
    public double Ratio => Sustainable > 0 ? PerMinute / Sustainable : 0;

    public static RateReading Empty => new() { HasData = false };
}

/// <summary>
/// Calcula o ritmo a partir do histórico local: a barra é uma porcentagem acumulada, então a
/// velocidade é a subida dela dividida pelo tempo. Só as subidas contam — as quedas são
/// renovações da janela, não consumo negativo.
/// </summary>
public static class ConsumptionRate
{
    /// <summary>
    /// Janela de medição do ritmo. Curta reage rápido e oscila; longa é estável e demora a
    /// perceber mudança. 20 min é o meio-termo padrão.
    /// </summary>
    public static readonly int[] WindowChoices = { 5, 20, 60, 1440 };

    public static string DescribeWindow(int minutes) => minutes switch
    {
        < 60 => minutes + " min",
        60 => "1 hora",
        1440 => "24 horas",
        _ => (minutes / 60) + " horas"
    };

    /// <summary>
    /// Buracos maiores que isso são app fechado e não entram na conta do tempo. Em janelas longas
    /// o limite é mais frouxo, senão qualquer pausa normal de uso zeraria a medição.
    /// </summary>
    private static TimeSpan MaxGapFor(TimeSpan window) =>
        window <= TimeSpan.FromHours(1) ? TimeSpan.FromMinutes(30) : TimeSpan.FromMinutes(120);

    public static RateReading Measure(IReadOnlyList<HistoryPoint> history, UsageBar? bar, BarKind kind,
        int windowMinutes = 20)
    {
        if (bar == null) return RateReading.Empty;

        var window = TimeSpan.FromMinutes(Math.Clamp(windowMinutes, 1, 1440));
        var maxGap = MaxGapFor(window);
        var cutoff = DateTimeOffset.Now - window;
        double consumed = 0;
        double minutes = 0;
        HistoryPoint? prev = null;

        foreach (var p in history)
        {
            var v = p.Get(kind);
            if (v == null) continue;

            if (prev != null && p.At >= cutoff)
            {
                var gap = p.At - prev.At;
                var pv = prev.Get(kind);
                if (pv != null && gap <= maxGap && gap > TimeSpan.Zero)
                {
                    var d = v.Value - pv.Value;
                    if (d > 0) consumed += d;
                    minutes += gap.TotalMinutes;
                }
            }
            prev = p;
        }

        // sem tempo medido não há velocidade; com tempo medido e nada consumido, a velocidade é zero
        if (minutes < 1) return RateReading.Empty;

        var perMinute = consumed / minutes;
        var remaining = Math.Max(0, 100 - bar.Percent);

        double sustainable = 0;
        if (bar.ResetsAt != null)
        {
            var minutesLeft = (bar.ResetsAt.Value - DateTimeOffset.Now).TotalMinutes;
            if (minutesLeft > 0) sustainable = remaining / minutesLeft;
        }

        return new RateReading
        {
            HasData = true,
            PerMinute = perMinute,
            Sustainable = sustainable,
            MinutesToEmpty = perMinute > 0 ? remaining / perMinute : null,
            WindowMinutes = (int)window.TotalMinutes,
            MeasuredMinutes = minutes
        };
    }

    /// <summary>"0,12% p/min" — precisa de casas decimais porque o normal é bem abaixo de 1%.</summary>
    public static string Format(RateReading r)
    {
        if (!r.HasData) return "—";
        var v = r.PerMinute;
        if (v <= 0) return "0% p/min";
        if (v < 0.01) return v.ToString("0.###") + "% p/min";
        if (v < 1) return v.ToString("0.##") + "% p/min";
        return v.ToString("0.#") + "% p/min";
    }

    /// <summary>Tempo até acabar, em linguagem curta.</summary>
    public static string FormatTimeLeft(RateReading r)
    {
        if (!r.HasData || r.MinutesToEmpty == null) return "";
        var m = r.MinutesToEmpty.Value;
        if (double.IsInfinity(m) || m > 60 * 24 * 30) return "";
        if (m < 60) return $"acaba em {Math.Max(1, (int)m)} min";
        if (m < 60 * 24) return $"acaba em {(int)(m / 60)}h {((int)m % 60):00}m";
        return $"acaba em {(int)(m / 1440)}d";
    }
}
