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
    Failed,

    /// <summary>
    /// A consulta do ciclo não respondeu antes de o ciclo seguinte começar — rede lenta, quase
    /// sempre. Não é falha de conexão, e por isso não é vermelho.
    /// </summary>
    Idle
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
            ApiOutcome.Idle => "ciclo sem resposta",
            _ => "falhou"
        };
        return string.IsNullOrEmpty(Detail) ? $"{hora} — {texto}" : $"{hora} — {texto}: {Detail}";
    }
}

/// <summary>
/// Os últimos ciclos de comunicação com a API, em ordem cronológica. Guarda um número fixo: o
/// mais novo entra pela direita e empurra o mais antigo para fora.
///
/// O registro é por CICLO, não por chamada: um ponto é anotado a cada intervalo configurado,
/// tenha havido consulta ou não. É isso que faz a faixa mostrar a saúde da conexão — durante uma
/// pausa por limite os pontos continuam avançando em vermelho, em vez de a faixa congelar.
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

    /// <summary>Quantos ciclos não conseguiram falar com a API (falha ou limite).</summary>
    public int FailureCount()
    {
        lock (_lock)
        {
            var n = 0;
            foreach (var c in _calls)
            {
                if (c.Outcome is ApiOutcome.Failed or ApiOutcome.RateLimited) n++;
            }
            return n;
        }
    }
}
