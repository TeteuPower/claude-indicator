using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ClaudeIndicator.Core;

/// <summary>
/// Leitura de disco: quanto o disco está ocupado, na mesma régua do Gerenciador de Tarefas.
///
/// <b>Por que "tempo ativo" e não "porcentagem da velocidade máxima".</b> Não existe 0 a 100% da
/// capacidade de um disco. O Windows não sabe o teto do aparelho, e esse teto nem é um número
/// fixo: o mesmo NVMe entrega alguns GB/s em leitura sequencial e algumas dezenas de MB/s em
/// aleatória de 4 KB, e ainda desacelera quando o cache SLC enche no meio de uma gravação longa.
/// Não há régua contra a qual dividir.
///
/// O que existe e é porcentagem de verdade é o <b>tempo ativo</b>: a fração do tempo em que o
/// disco teve pelo menos um pedido em andamento. É o número que o Gerenciador mostra como "Tempo
/// de atividade", e sai de <c>% Idle Time</c> invertido.
///
/// O contador que <i>parece</i> ser o certo, <c>% Disk Time</c>, não serve: ele conta fila, não
/// tempo, e passa de 100% sem esforço. Medido neste projeto, num instante em que o tempo ativo era
/// 27,9%, o <c>% Disk Time</c> marcou 392,3%.
///
/// <b>"Todos os discos" é o mais ocupado, e não a média.</b> A instância <c>_Total</c> do Windows
/// reparte entre os discos, e isso apaga exatamente o que se quer ver. Medido nesta máquina com o
/// disco do sistema saturado:
///
/// <code>
/// _Total          25,0%
/// Disco 0 (C:)   100,0%
/// Disco 1 (D:)     0,0%
/// Disco 2 (E:)     0,0%
/// </code>
///
/// Um disco travado virava 25% na barra, que é o mesmo que não avisar. Quem olha o indicador quer
/// saber se <b>algum</b> disco está no limite, então o número é o do disco mais ocupado, e o balão
/// diz qual é.
///
/// Os nomes dos contadores ficam em inglês de propósito, mesmo num Windows em português: é assim
/// que o .NET os resolve, e é o que o resto do projeto já faz com "% Processor Utility".
/// </summary>
public sealed class DiskMonitor : IDisposable
{
    /// <summary>Valor guardado nas configurações para "acompanhe o disco mais ocupado".</summary>
    public const string Todos = "";

    private readonly object _trava = new();
    private readonly List<Alvo> _alvos = new();
    private string _escolha = Todos;

    /// <summary>Qual disco está sendo acompanhado, ou vazio para o mais ocupado.</summary>
    public string Instancia
    {
        get { lock (_trava) return _escolha; }
    }

    /// <summary>
    /// Aponta para um disco, ou para todos. Instância desconhecida cai em "todos", que é a resposta
    /// menos surpreendente quando o disco escolhido foi removido ou renomeado.
    /// </summary>
    public void Apontar(string instancia)
    {
        var querida = Resolver(instancia);

        lock (_trava)
        {
            if (_escolha == querida && _alvos.Count > 0) return;

            Soltar();
            _escolha = querida;

            var quais = querida.Length > 0
                ? new[] { querida }
                : Instancias().Where(n => n != "_Total").ToArray();

            foreach (var nome in quais)
            {
                var alvo = Alvo.Abrir(nome);
                if (alvo != null) _alvos.Add(alvo);
            }
        }
    }

    /// <summary>
    /// A leitura atual. Com "todos", devolve o disco <b>mais ocupado</b> — é ele que denuncia
    /// gargalo, e a média entre discos esconderia um deles no limite.
    /// </summary>
    public DiskReading Ler()
    {
        lock (_trava)
        {
            if (_alvos.Count == 0) return new DiskReading { Instance = _escolha, Name = Apelido(_escolha) };

            DiskReading? melhor = null;
            var quantos = 0;

            foreach (var alvo in _alvos)
            {
                var leitura = alvo.Ler(_alvos.Count);
                if (leitura == null) continue;

                quantos++;
                if (melhor == null || Ocupacao(leitura) > Ocupacao(melhor)) melhor = leitura;
            }

            if (melhor == null)
            {
                Soltar();
                return new DiskReading { Instance = _escolha, Name = Apelido(_escolha) };
            }

            return quantos == melhor.Total ? melhor : new DiskReading
            {
                Name = melhor.Name,
                Instance = melhor.Instance,
                Busy = melhor.Busy,
                ReadBytes = melhor.ReadBytes,
                WriteBytes = melhor.WriteBytes,
                Total = quantos
            };
        }
    }

    private static double Ocupacao(DiskReading d) => d.Busy.HasValue ? d.Busy.Value!.Value : -1;

    // ------------------------------------------------------------------ instâncias

    /// <summary>
    /// Os discos que o Windows tem para oferecer, na ordem do Gerenciador de Tarefas. A opção de
    /// acompanhar o mais ocupado vem primeiro, porque é o padrão.
    /// </summary>
    public static List<(string Instancia, string Apelido)> Discos()
    {
        var lista = new List<(string, string)> { (Todos, Apelido(Todos)) };

        foreach (var nome in Instancias())
        {
            if (nome == "_Total") continue;
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
        return achada != null && achada != "_Total" ? achada : Todos;
    }

    /// <summary>
    /// O nome como o Gerenciador de Tarefas escreve. A instância vem no formato "1 D:", e chamar
    /// isso de "Disco 1 (D:)" é o que permite reconhecer na hora qual dos discos é qual.
    /// </summary>
    public static string Apelido(string instancia)
    {
        if (string.IsNullOrWhiteSpace(instancia)) return "O disco mais ocupado";

        var espaco = instancia.IndexOf(' ');
        if (espaco <= 0) return "Disco " + instancia;

        var numero = instancia[..espaco];
        var letras = instancia[(espaco + 1)..].Trim();
        return letras.Length > 0 ? $"Disco {numero} ({letras})" : "Disco " + numero;
    }

    // ------------------------------------------------------------------ um disco

    private sealed class Alvo : IDisposable
    {
        private readonly string _instancia;
        private readonly PerformanceCounter _ocioso;
        private readonly PerformanceCounter _bytesLeitura;
        private readonly PerformanceCounter _bytesGravacao;

        private Alvo(string instancia, PerformanceCounter ocioso, PerformanceCounter leitura, PerformanceCounter gravacao)
        {
            _instancia = instancia;
            _ocioso = ocioso;
            _bytesLeitura = leitura;
            _bytesGravacao = gravacao;
        }

        public static Alvo? Abrir(string instancia)
        {
            try
            {
                var ocioso = new PerformanceCounter("PhysicalDisk", "% Idle Time", instancia, true);
                var leitura = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", instancia, true);
                var gravacao = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", instancia, true);

                // contador de taxa precisa de uma primeira amostra para ter de onde derivar
                ocioso.NextValue();
                leitura.NextValue();
                gravacao.NextValue();

                return new Alvo(instancia, ocioso, leitura, gravacao);
            }
            catch
            {
                return null;
            }
        }

        public DiskReading? Ler(int total)
        {
            try
            {
                return new DiskReading
                {
                    Instance = _instancia,
                    Name = Apelido(_instancia),
                    Busy = new Reading(Math.Clamp(100 - _ocioso.NextValue(), 0, 100)),
                    ReadBytes = new Reading(Math.Max(0, _bytesLeitura.NextValue())),
                    WriteBytes = new Reading(Math.Max(0, _bytesGravacao.NextValue())),
                    Total = total
                };
            }
            catch
            {
                // disco removido no meio da leitura: melhor nada que número inventado
                return null;
            }
        }

        public void Dispose()
        {
            try { _ocioso.Dispose(); } catch { }
            try { _bytesLeitura.Dispose(); } catch { }
            try { _bytesGravacao.Dispose(); } catch { }
        }
    }

    private void Soltar()
    {
        foreach (var alvo in _alvos) alvo.Dispose();
        _alvos.Clear();
    }

    public void Dispose()
    {
        lock (_trava) Soltar();
    }
}
