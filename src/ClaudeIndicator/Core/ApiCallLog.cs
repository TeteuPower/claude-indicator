using System;
using System.Collections.Generic;

namespace ClaudeIndicator.Core;

/// <summary>Como terminou uma consulta à API.</summary>
public enum ApiOutcome
{
    /// <summary>Respondeu com os dados de consumo.</summary>
    Ok,

    /// <summary>Respondeu, mas recusou por limite de consultas (HTTP 429).</summary>
    RateLimited,

    /// <summary>Não deu para obter o consumo: rede, credencial, formato inesperado.</summary>
    Failed
}

/// <summary>Uma consulta registrada.</summary>
public sealed class ApiCall
{
    public DateTimeOffset At { get; init; }
    public ApiOutcome Outcome { get; init; }
    public string Detail { get; init; } = "";

    public string Describe()
    {
        var hora = At.ToLocalTime().ToString("HH:mm:ss");
        var texto = Outcome switch
        {
            ApiOutcome.Ok => "respondeu",
            ApiOutcome.RateLimited => "limite de consultas",
            _ => "falhou"
        };
        return string.IsNullOrEmpty(Detail) ? $"{hora} — {texto}" : $"{hora} — {texto}: {Detail}";
    }
}

/// <summary>
/// As últimas consultas à API, em ordem cronológica. Guarda um número fixo: a mais nova entra
/// pela direita e empurra a mais antiga para fora, que é como a linha do tempo é lida.
///
/// Só entram consultas que de fato aconteceram — quando o app está esperando o fim de uma pausa
/// por limite, nenhuma chamada é feita e nada é registrado, senão a linha mostraria falhas que
/// não ocorreram.
/// </summary>
public sealed class ApiCallLog
{
    public const int Capacity = 10;

    private readonly Queue<ApiCall> _calls = new(Capacity);
    private readonly object _lock = new();

    public void Record(ApiOutcome outcome, string detail = "")
    {
        lock (_lock)
        {
            _calls.Enqueue(new ApiCall { At = DateTimeOffset.Now, Outcome = outcome, Detail = detail });
            while (_calls.Count > Capacity) _calls.Dequeue();
        }
    }

    /// <summary>Da mais antiga para a mais recente.</summary>
    public List<ApiCall> Recent()
    {
        lock (_lock) return new List<ApiCall>(_calls);
    }

    /// <summary>Quantas das registradas falharam (inclui limite de consultas).</summary>
    public int FailureCount()
    {
        lock (_lock)
        {
            var n = 0;
            foreach (var c in _calls)
            {
                if (c.Outcome != ApiOutcome.Ok) n++;
            }
            return n;
        }
    }
}
