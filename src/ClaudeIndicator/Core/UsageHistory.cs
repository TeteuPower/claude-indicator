using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ClaudeIndicator.Core;

public sealed class HistoryPoint
{
    public DateTimeOffset At { get; set; }
    public double? Session { get; set; }
    public double? Weekly { get; set; }
    public double? Fable { get; set; }

    public double? Get(BarKind kind) => kind switch
    {
        BarKind.Session => Session,
        BarKind.Weekly => Weekly,
        BarKind.Fable => Fable,
        _ => null
    };
}

/// <summary>
/// Grava um ponto por consulta bem-sucedida em %APPDATA%\ClaudeIndicator\history.jsonl
/// (uma linha JSON por ponto). É a matéria-prima da janela de histórico. Só acumula
/// enquanto o app está em execução.
/// </summary>
public static class UsageHistory
{
    private static readonly object Lock = new();
    private static DateTimeOffset _lastAppend = DateTimeOffset.MinValue;

    public static string FilePath => Path.Combine(AppSettings.DataDir, "history.jsonl");

    /// <summary>Espaço mínimo entre pontos; abaixo disso a consulta não vira registro.</summary>
    private static readonly TimeSpan MinSpacing = TimeSpan.FromSeconds(55);

    private static readonly TimeSpan Retention = TimeSpan.FromDays(35);

    public static void Append(UsageSnapshot snap)
    {
        if (snap.Bars.Count == 0) return;
        lock (Lock)
        {
            if (snap.FetchedAt - _lastAppend < MinSpacing) return;
            try
            {
                Directory.CreateDirectory(AppSettings.DataDir);
                var obj = new Dictionary<string, object?> { ["t"] = snap.FetchedAt.ToString("o") };
                foreach (var bar in snap.Bars)
                {
                    var key = KeyFor(bar.Kind);
                    if (key != null) obj[key] = Math.Round(bar.Percent, 2);
                }
                File.AppendAllText(FilePath, JsonSerializer.Serialize(obj) + Environment.NewLine);
                _lastAppend = snap.FetchedAt;
                PruneIfLarge();
            }
            catch
            {
                // histórico é acessório: nunca derruba a atualização
            }
        }
    }

    public static List<HistoryPoint> Load(TimeSpan window)
    {
        var cutoff = DateTimeOffset.Now - window;
        var list = new List<HistoryPoint>();
        try
        {
            if (!File.Exists(FilePath)) return list;
            foreach (var line in File.ReadLines(FilePath))
            {
                var p = ParseLine(line);
                if (p != null && p.At >= cutoff) list.Add(p);
            }
            list.Sort((a, b) => a.At.CompareTo(b.At));
        }
        catch
        {
            // arquivo corrompido/em uso: devolve o que deu para ler
        }
        return list;
    }

    private static HistoryPoint? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.String) return null;
            if (!DateTimeOffset.TryParse(t.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var at)) return null;

            var p = new HistoryPoint { At = at };
            if (root.TryGetProperty("s", out var s) && s.ValueKind == JsonValueKind.Number) p.Session = s.GetDouble();
            if (root.TryGetProperty("w", out var w) && w.ValueKind == JsonValueKind.Number) p.Weekly = w.GetDouble();
            if (root.TryGetProperty("f", out var f) && f.ValueKind == JsonValueKind.Number) p.Fable = f.GetDouble();
            return p;
        }
        catch
        {
            return null; // linha truncada (ex.: queda de energia no meio da escrita)
        }
    }

    private static string? KeyFor(BarKind kind) => kind switch
    {
        BarKind.Session => "s",
        BarKind.Weekly => "w",
        BarKind.Fable => "f",
        _ => null
    };

    /// <summary>Reescreve o arquivo sem os pontos além da retenção quando ele passa de ~2 MB.</summary>
    private static void PruneIfLarge()
    {
        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists || info.Length < 2_000_000) return;

            var cutoff = DateTimeOffset.Now - Retention;
            var sb = new StringBuilder();
            foreach (var line in File.ReadLines(FilePath))
            {
                var p = ParseLine(line);
                if (p != null && p.At >= cutoff) sb.AppendLine(line);
            }
            File.WriteAllText(FilePath, sb.ToString());
        }
        catch
        {
            // sem espaço/permissão: tenta de novo no próximo estouro
        }
    }
}
