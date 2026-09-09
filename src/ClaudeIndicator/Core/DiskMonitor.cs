using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ClaudeIndicator.Core;

/// <summary>
/// Leitura de um disco físico: quanto ele está ocupado e em que direção.
///
/// <b>Por que "tempo ativo" e não "porcentagem da velocidade máxima".</b> Não existe 0 a 100% da
/// capacidade de um disco. O Windows não sabe o teto do aparelho, e esse teto nem é um número
/// fixo: o mesmo NVMe entrega alguns GB/s em leitura sequencial e algumas dezenas de MB/s em
/// aleatória de 4 KB, e ainda desacelera quando o cache SLC enche no meio de uma gravação longa.
/// Não há régua contra a qual dividir.
///
/// O que existe e é porcentagem de verdade é o <b>tempo ativo</b>: a fração do tempo em que o
/// disco teve pelo menos um pedido em andamento. É o que o Gerenciador de Tarefas mostra como
/// "Tempo de atividade", e sai de <c>% Idle Time</c> invertido.
///
/// O contador que <i>parece</i> ser o certo, <c>% Disk Time</c>, não serve: ele conta fila, não
/// tempo, e passa de 100% sem esforço. Medido neste projeto, num instante em que o tempo ativo era
/// 27,9%, o <c>% Disk Time</c> marcou 392,3%. Uma barra alimentada por ele estaria cheia quase
/// sempre e não diria nada. O mesmo vale para os irmãos dele por direção.
///
/// A <b>direção</b> vem da proporção entre <c>% Disk Read Time</c> e <c>% Disk Write Time</c>.
/// Individualmente eles estouram os 100% como o pai, mas a razão entre os dois continua honesta —
/// e é só disso que se precisa para repartir o tempo ativo entre leitura e gravação.
///
/// Os nomes dos contadores ficam em inglês de propósito, mesmo num Windows em português: é assim
/// que o .NET os resolve, e é o que o resto do projeto já faz com "% Processor Utility".
/// </summary>
public sealed class DiskMonitor : IDisposable
{
    /// <summary>A instância que soma todos os discos.</summary>
    public const string Todos = "_Total";

    private readonly object _trava = new();

    private string _instancia = "";
    private PerformanceCounter? _ocioso;
    private PerformanceCounter? _tempoLeitura;
    private PerformanceCounter? _tempoGravacao;
    private PerformanceCounter? _bytesLeitura;
    private PerformanceCounter? _bytesGravacao;

    /// <summary>Qual disco está sendo lido agora.</summary>
    public string Instancia
    {
        get { lock (_trava) return _instancia; }
    }

    /// <summary>
    /// Aponta para um disco. Instância vazia ou desconhecida cai no somatório de todos, que é a
    /// resposta menos surpreendente quando o disco escolhido foi removido ou renomeado.
    /// </summary>
    public void Apontar(string instancia)
    {
        var querida = Resolver(instancia);

        lock (_trava)
        {
            if (_instancia == querida && _ocioso != null) return;

            Soltar();
            _instancia = querida;

            try
            {
                _ocioso = new PerformanceCounter("PhysicalDisk", "% Idle Time", querida, true);
                _tempoLeitura = new PerformanceCounter("PhysicalDisk", "% Disk Read Time", querida, true);
                _tempoGravacao = new PerformanceCounter("PhysicalDisk", "% Disk Write Time", querida, true);
                _bytesLeitura = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", querida, true);
                _bytesGravacao = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", querida, true);

                // contador de taxa precisa de uma primeira amostra para ter de onde derivar
                foreach (var c in Contadores()) c.NextValue();
            }
            catch
            {
                Soltar();
            }
        }
    }

    /// <summary>A leitura atual, ou uma leitura vazia se os contadores não responderam.</summary>
    public DiskReading Ler()
    {
        lock (_trava)
        {
            if (_ocioso == null) return new DiskReading { Instance = _instancia, Name = Apelido(_instancia) };

            try
            {
                var ativo = Math.Clamp(100 - _ocioso.NextValue(), 0, 100);
                var tLer = Math.Max(0, _tempoLeitura!.NextValue());
                var tGrav = Math.Max(0, _tempoGravacao!.NextValue());
                var bLer = Math.Max(0, _bytesLeitura!.NextValue());
                var bGrav = Math.Max(0, _bytesGravacao!.NextValue());

                // A razão entre os tempos é a repartição natural. Quando os dois zeram mas há bytes
                // andando — acontece em rajada curta, que o contador de tempo arredonda para zero —
                // os bytes decidem. Sem nenhum dos dois a barra fica no centro, que é o certo: não
                // há direção quando não há movimento.
                var somaTempo = tLer + tGrav;
                var somaBytes = bLer + bGrav;
                var fatiaLeitura =
                    somaTempo > 0 ? tLer / somaTempo :
                    somaBytes > 0 ? bLer / somaBytes : 0.5;

                return new DiskReading
                {
                    Instance = _instancia,
                    Name = Apelido(_instancia),
                    Busy = new Reading(ativo),
                    ReadShare = new Reading(Math.Clamp(fatiaLeitura, 0, 1)),
                    ReadBytes = new Reading(bLer),
                    WriteBytes = new Reading(bGrav)
                };
            }
            catch
            {
                // disco removido no meio da leitura: melhor leitura vazia que número inventado
                Soltar();
                return new DiskReading { Instance = _instancia, Name = Apelido(_instancia) };
            }
        }
    }

    // ------------------------------------------------------------------ instâncias

    /// <summary>
    /// Os discos que o Windows tem para oferecer, na ordem em que aparecem no Gerenciador de
    /// Tarefas. O somatório vem primeiro, porque é o padrão.
    /// </summary>
    public static List<(string Instancia, string Apelido)> Discos()
    {
        var lista = new List<(string, string)> { (Todos, Apelido(Todos)) };

        foreach (var nome in Instancias())
        {
            if (nome == Todos) continue;
            lista.Add((nome, Apelido(nome)));
        }

        return lista;
    }

    private static string[] Instancias()
    {
        try
        {
            return new PerformanceCounterCategory("PhysicalDisk")
                .GetInstanceNames()
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string Resolver(string instancia)
    {
        if (string.IsNullOrWhiteSpace(instancia)) return Todos;

        var achada = Instancias().FirstOrDefault(n => string.Equals(n, instancia, StringComparison.OrdinalIgnoreCase));
        return achada ?? Todos;
    }

    /// <summary>
    /// O nome como o Gerenciador de Tarefas escreve. A instância vem no formato "1 D:", e chamar
    /// isso de "Disco 1 (D:)" é o que permite reconhecer na hora qual dos discos é qual.
    /// </summary>
    public static string Apelido(string instancia)
    {
        if (string.IsNullOrWhiteSpace(instancia) || instancia == Todos) return "Todos os discos";

        var espaco = instancia.IndexOf(' ');
        if (espaco <= 0) return "Disco " + instancia;

        var numero = instancia[..espaco];
        var letras = instancia[(espaco + 1)..].Trim();
        return letras.Length > 0 ? $"Disco {numero} ({letras})" : "Disco " + numero;
    }

    // ------------------------------------------------------------------

    private IEnumerable<PerformanceCounter> Contadores()
    {
        if (_ocioso != null) yield return _ocioso;
        if (_tempoLeitura != null) yield return _tempoLeitura;
        if (_tempoGravacao != null) yield return _tempoGravacao;
        if (_bytesLeitura != null) yield return _bytesLeitura;
        if (_bytesGravacao != null) yield return _bytesGravacao;
    }

    private void Soltar()
    {
        foreach (var c in Contadores())
        {
            try { c.Dispose(); } catch { }
        }

        _ocioso = null;
        _tempoLeitura = null;
        _tempoGravacao = null;
        _bytesLeitura = null;
        _bytesGravacao = null;
    }

    public void Dispose()
    {
        lock (_trava) Soltar();
    }
}
