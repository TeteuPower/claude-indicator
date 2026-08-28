using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeIndicator.Core;

/// <summary>
/// O que o app sabia quando foi fechado: a última leitura boa e os últimos ciclos de comunicação.
///
/// Existe porque fechar e reabrir apagava tudo. As barras voltavam em "carregando…" e uma consulta
/// nova saía na hora — inútil, porque o consumo de trinta segundos atrás continua sendo o consumo,
/// e cara, porque o limite de consultas é da conta inteira. Guardar isto faz o app reabrir sabendo
/// o que já sabia, e esperar o ciclo normal para perguntar de novo.
///
/// É um retrato, não histórico: o histórico de consumo continua em history.jsonl. Aqui fica só o
/// suficiente para a tela nascer preenchida.
/// </summary>
public sealed class SessionState
{
    private static string FilePath => Path.Combine(AppSettings.DataDir, "session.json");

    /// <summary>Última leitura que veio com barras.</summary>
    public StoredSnapshot? Snapshot { get; set; }

    /// <summary>Os últimos ciclos, do mais antigo para o mais recente.</summary>
    public List<StoredCall> Calls { get; set; } = new();

    public sealed class StoredSnapshot
    {
        public DateTimeOffset DataAt { get; set; }
        public string? Account { get; set; }
        public string? EndpointUsed { get; set; }
        public List<StoredBar> Bars { get; set; } = new();
    }

    public sealed class StoredBar
    {
        public BarKind Kind { get; set; }
        public double Percent { get; set; }
        public DateTimeOffset? ResetsAt { get; set; }
    }

    public sealed class StoredCall
    {
        public DateTimeOffset At { get; set; }
        public ApiOutcome Outcome { get; set; }
        public string Detail { get; set; } = "";
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Save(UsageSnapshot? snapshot, IReadOnlyList<ApiCall> calls)
    {
        try
        {
            var estado = new SessionState();

            if (snapshot != null && snapshot.Bars.Count > 0)
            {
                estado.Snapshot = new StoredSnapshot
                {
                    DataAt = snapshot.DataAt ?? snapshot.FetchedAt,
                    Account = snapshot.Account,
                    EndpointUsed = snapshot.EndpointUsed
                };
                foreach (var b in snapshot.Bars)
                {
                    estado.Snapshot.Bars.Add(new StoredBar
                    {
                        Kind = b.Kind,
                        Percent = b.Percent,
                        ResetsAt = b.ResetsAt
                    });
                }
            }

            foreach (var c in calls)
                estado.Calls.Add(new StoredCall { At = c.At, Outcome = c.Outcome, Detail = c.Detail });

            Directory.CreateDirectory(AppSettings.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(estado, Json));
        }
        catch
        {
            // sem permissão, disco cheio, arquivo em uso: reabrir sem estado é só menos bom
        }
    }

    public static SessionState? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return JsonSerializer.Deserialize<SessionState>(File.ReadAllText(FilePath), Json);
        }
        catch
        {
            return null; // arquivo de uma versão anterior ou truncado: começa limpo
        }
    }

    /// <summary>
    /// A leitura guardada, como o resto do app a entende. Volta marcada como <see cref="UsageSnapshot.Stale"/>
    /// porque é isso que ela é: dado de antes, ainda válido, esperando a próxima confirmação — a
    /// mesma marca que uma consulta falha usa ao reaproveitar as barras.
    /// </summary>
    public UsageSnapshot? ToSnapshot(TimeSpan maximumAge)
    {
        if (Snapshot == null || Snapshot.Bars.Count == 0) return null;

        var idade = DateTimeOffset.Now - Snapshot.DataAt;
        if (idade < TimeSpan.Zero || idade > maximumAge) return null;

        var snap = new UsageSnapshot
        {
            FetchedAt = DateTimeOffset.Now,
            DataAt = Snapshot.DataAt,
            Account = Snapshot.Account,
            EndpointUsed = Snapshot.EndpointUsed,
            Stale = true
        };

        foreach (var b in Snapshot.Bars)
        {
            // Limite que já renovou enquanto o app estava fechado não pode voltar com a
            // porcentagem antiga: seria mentira na tela até a primeira consulta responder.
            if (b.ResetsAt != null && b.ResetsAt <= DateTimeOffset.Now) continue;

            snap.Bars.Add(new UsageBar
            {
                Kind = b.Kind,
                Percent = b.Percent,
                ResetsAt = b.ResetsAt
            });
        }

        return snap.Bars.Count > 0 ? snap : null;
    }

    /// <summary>Idade da leitura guardada, ou null quando não há nenhuma aproveitável.</summary>
    public TimeSpan? SnapshotAge =>
        Snapshot == null ? null : DateTimeOffset.Now - Snapshot.DataAt;
}
