using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace ClaudeIndicator.Core;

/// <summary>Um ponto do histórico de desempenho.</summary>
public sealed class HardwarePoint
{
    public DateTimeOffset At { get; init; }
    public double? CpuLoad { get; init; }
    public double? CpuTemp { get; init; }
    public double? CpuWatts { get; init; }
    public double? GpuLoad { get; init; }
    public double? GpuTemp { get; init; }
    public double? GpuWatts { get; init; }
    public double? RamLoad { get; init; }
    public double? RamGb { get; init; }
}

/// <summary>
/// Grava o desempenho do PC em disco, um ponto a cada dez segundos, para a página de desempenho
/// poder olhar para trás.
///
/// Os sensores são lidos a cada 2 s, mas gravar nesse passo daria 43 mil linhas por dia sem
/// acrescentar leitura nenhuma — para análise, dez segundos é resolução de sobra. O formato é o
/// mesmo jsonl do histórico de consumo: uma linha por ponto, legível, e um arquivo que pode ser
/// truncado no meio sem corromper o resto.
/// </summary>
public static class HardwareHistory
{
    public static string FilePath => Path.Combine(AppSettings.DataDir, "hardware-history.jsonl");

    /// <summary>Espaço mínimo entre pontos gravados.</summary>
    private static readonly TimeSpan MinSpacing = TimeSpan.FromSeconds(10);

    /// <summary>O histórico não passa de 14 dias: análise de desempenho olha horas, não meses.</summary>
    private const int RetentionDays = 14;

    private static DateTimeOffset _lastAppend = DateTimeOffset.MinValue;
    private static DateTimeOffset _lastPrune = DateTimeOffset.MinValue;
    private static readonly object Lock = new();

    public static void Append(HardwareSnapshot snap)
    {
        if (!snap.Ok && !snap.Cpu.HasAnything && !snap.Gpu.HasAnything) return;

        lock (Lock)
        {
            if (snap.At - _lastAppend < MinSpacing) return;

            try
            {
                Directory.CreateDirectory(AppSettings.DataDir);

                var obj = new Dictionary<string, object?> { ["t"] = snap.At.ToString("o") };
                void Por(string chave, Reading r, int casas = 0)
                {
                    if (r.HasValue) obj[chave] = Math.Round(r.Value!.Value, casas);
                }

                Por("cl", snap.Cpu.Load);
                Por("ct", snap.Cpu.Temperature);
                Por("cw", snap.Cpu.Power);
                Por("gl", snap.Gpu.Load);
                Por("gt", snap.Gpu.Temperature);
                Por("gw", snap.Gpu.Power);
                Por("rl", snap.Ram.Load);
                Por("rg", snap.Ram.MemoryUsed, 1);

                File.AppendAllText(FilePath, JsonSerializer.Serialize(obj) + Environment.NewLine);
                _lastAppend = snap.At;

                // a poda anda junto com a gravação, mas só de hora em hora: reescrever o arquivo
                // a cada ponto seria pagar o custo da limpeza 360 vezes mais que o necessário
                if (snap.At - _lastPrune > TimeSpan.FromHours(1))
                {
                    _lastPrune = snap.At;
                    Prune();
                }
            }
            catch
            {
                // histórico é acessório: nunca derruba a leitura dos sensores
            }
        }
    }

    /// <summary>Os pontos dentro da janela pedida, do mais antigo para o mais recente.</summary>
    public static List<HardwarePoint> Load(TimeSpan window)
    {
        var pontos = new List<HardwarePoint>();
        var corte = DateTimeOffset.Now - window;

        try
        {
            if (!File.Exists(FilePath)) return pontos;

            foreach (var linha in File.ReadLines(FilePath))
            {
                if (linha.Length == 0) continue;

                try
                {
                    using var doc = JsonDocument.Parse(linha);
                    var raiz = doc.RootElement;
                    if (!raiz.TryGetProperty("t", out var t)) continue;

                    var quando = DateTimeOffset.Parse(t.GetString()!, CultureInfo.InvariantCulture);
                    if (quando < corte) continue;

                    double? Ler(string chave) =>
                        raiz.TryGetProperty(chave, out var v) && v.ValueKind == JsonValueKind.Number
                            ? v.GetDouble()
                            : null;

                    pontos.Add(new HardwarePoint
                    {
                        At = quando,
                        CpuLoad = Ler("cl"),
                        CpuTemp = Ler("ct"),
                        CpuWatts = Ler("cw"),
                        GpuLoad = Ler("gl"),
                        GpuTemp = Ler("gt"),
                        GpuWatts = Ler("gw"),
                        RamLoad = Ler("rl"),
                        RamGb = Ler("rg")
                    });
                }
                catch
                {
                    // linha truncada por queda de energia: pula e segue
                }
            }
        }
        catch
        {
            // arquivo em uso ou ilegível: a página mostra o que tiver
        }

        return pontos;
    }

    private static void Prune()
    {
        var corte = DateTimeOffset.Now.AddDays(-RetentionDays);
        var manter = new List<string>();
        var mudou = false;

        foreach (var linha in File.ReadLines(FilePath))
        {
            if (linha.Length == 0) { mudou = true; continue; }

            var i = linha.IndexOf("\"t\":\"", StringComparison.Ordinal);
            if (i < 0) { mudou = true; continue; }

            var inicio = i + 5;
            var fim = linha.IndexOf('"', inicio);
            if (fim < 0 || !DateTimeOffset.TryParse(linha[inicio..fim], CultureInfo.InvariantCulture,
                                                    DateTimeStyles.None, out var quando)
                || quando < corte)
            {
                mudou = true;
                continue;
            }

            manter.Add(linha);
        }

        if (mudou) File.WriteAllLines(FilePath, manter);
    }
}
