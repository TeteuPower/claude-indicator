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
    private static DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    public static string FilePath => Path.Combine(AppSettings.DataDir, "history.jsonl");

    /// <summary>Espaço mínimo entre pontos; abaixo disso a consulta não vira registro.</summary>
    private static readonly TimeSpan MinSpacing = TimeSpan.FromSeconds(55);

    /// <summary>Com retenção ligada, a limpeza roda no máximo uma vez a cada 6 horas.</summary>
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(6);

    public static void Append(UsageSnapshot snap, AppSettings settings)
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
                Prune(settings.HistoryRetentionDays);
            }
            catch
            {
                // histórico é acessório: nunca derruba a atualização
            }
        }
    }

    /// <summary>Data do ponto mais antigo guardado, ou null se não há histórico.</summary>
    public static DateTimeOffset? OldestPoint()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            foreach (var line in File.ReadLines(FilePath))
            {
                var p = ParseLine(line);
                if (p != null) return p.At;
            }
        }
        catch
        {
            // arquivo em uso: sem data conhecida
        }
        return null;
    }

    /// <summary>Tamanho do arquivo de histórico em bytes (0 se ainda não existe).</summary>
    public static long FileSizeBytes()
    {
        try
        {
            var info = new FileInfo(FilePath);
            return info.Exists ? info.Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Pontos gravados na janela pedida. Use <see cref="TimeSpan.MaxValue"/> para tudo.</summary>
    public static List<HistoryPoint> Load(TimeSpan window)
    {
        // janelas muito grandes estourariam a subtração: nesse caso não há corte
        var cutoff = window >= TimeSpan.FromDays(36500)
            ? DateTimeOffset.MinValue
            : DateTimeOffset.Now - window;
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

    /// <summary>
    /// Descarta pontos mais antigos que <paramref name="retentionDays"/>.
    /// Com 0 (padrão) nada é apagado: o arquivo nunca é reescrito.
    /// </summary>
    public static void Prune(int retentionDays, bool force = false)
    {
        if (retentionDays <= 0) return;
        if (!force && DateTimeOffset.Now - _lastPrune < PruneInterval) return;
        _lastPrune = DateTimeOffset.Now;

        try
        {
            if (!File.Exists(FilePath)) return;

            var cutoff = DateTimeOffset.Now - TimeSpan.FromDays(retentionDays);
            var kept = new StringBuilder();
            var dropped = false;
            foreach (var line in File.ReadLines(FilePath))
            {
                var p = ParseLine(line);
                if (p != null && p.At >= cutoff) kept.Append(line).Append(Environment.NewLine);
                else if (p != null) dropped = true;
            }
            if (dropped) File.WriteAllText(FilePath, kept.ToString());
        }
        catch
        {
            // sem espaço/permissão/arquivo em uso: tenta de novo no próximo ciclo
        }
    }
}
