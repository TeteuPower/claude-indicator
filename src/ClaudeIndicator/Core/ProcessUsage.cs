using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace ClaudeIndicator.Core;

/// <summary>Um programa e o quanto ele está consumindo. A unidade depende da lista: %, % ou GB.</summary>
public sealed record ProcessUse(string Name, double Value);

/// <summary>
/// Quem está consumindo mais agora, por componente. Cada lista vem do maior para o menor, já
/// somada por nome de programa: o Chrome abre dezenas de processos, e vinte linhas de "chrome.exe"
/// não responderiam a pergunta que se faz ao passar o mouse.
/// </summary>
public sealed class ProcessTops
{
    public static readonly ProcessTops Empty = new();

    public IReadOnlyList<ProcessUse> Cpu { get; init; } = Array.Empty<ProcessUse>();
    public IReadOnlyList<ProcessUse> Ram { get; init; } = Array.Empty<ProcessUse>();
    public IReadOnlyList<ProcessUse> Gpu { get; init; } = Array.Empty<ProcessUse>();

    /// <summary>
    /// Os contadores de GPU por processo responderam? Sem isso, lista vazia significaria
    /// "ninguém usando a GPU", quando na verdade é "não sei medir nesta máquina".
    /// </summary>
    public bool GpuOk { get; init; }
}

/// <summary>
/// Mede o consumo por processo: memória, CPU e GPU.
///
/// A memória e o tempo de CPU vêm de uma única chamada ao kernel
/// (<c>NtQuerySystemInformation</c>), e não da classe <c>Process</c>. O motivo é permissão: ler
/// <c>TotalProcessorTime</c> abre um handle para cada processo, e sem elevação metade deles nega
/// acesso — numa medição aqui, 187 de 377 —, justamente os do sistema, que às vezes são os que
/// mais consomem. O kernel entrega todos de uma vez, com nome, memória e tempo de CPU, em ~7 ms.
///
/// A GPU sai dos contadores "GPU Engine", os mesmos que o Gerenciador de Tarefas usa. Pela classe
/// PerformanceCounter isso custava mais de seis segundos (773 instâncias, uma consulta por
/// instância); por uma consulta PDH com curinga, aberta uma vez e reaproveitada, cada leitura sai
/// em ~2 ms.
///
/// Uso de CPU é diferença entre duas leituras, então a primeira amostra sai sem a lista de CPU —
/// não há de onde tirar diferença ainda.
/// </summary>
public sealed class ProcessSampler : IDisposable
{
    /// <summary>Quantos programas cada lista mostra.</summary>
    public const int Quantos = 5;

    private Dictionary<int, long>? _cpuAnterior;
    private DateTimeOffset _quandoAnterior;

    private IntPtr _pdhQuery = IntPtr.Zero;
    private IntPtr _pdhCounter = IntPtr.Zero;
    private bool _pdhTentado;

    public ProcessTops Sample()
    {
        var agora = DateTimeOffset.UtcNow;
        var processos = LerProcessos();
        if (processos.Count == 0) return ProcessTops.Empty;

        var ram = processos
            .Where(p => p.WorkingSet > 0)
            .GroupBy(p => p.Nome, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProcessUse(g.Key, g.Sum(p => p.WorkingSet) / 1024.0 / 1024 / 1024))
            .OrderByDescending(u => u.Value)
            .Take(Quantos)
            .ToList();

        var cpu = new List<ProcessUse>();
        var anterior = _cpuAnterior;
        var decorridoMs = (agora - _quandoAnterior).TotalMilliseconds;
        if (anterior != null && decorridoMs > 200)
        {
            var fatia = decorridoMs * Environment.ProcessorCount;
            cpu = processos
                .Where(p => anterior.ContainsKey(p.Pid))
                .Select(p => new ProcessUse(p.Nome, (p.CpuTicks - anterior[p.Pid]) / 10000.0 / fatia * 100))
                .Where(u => u.Value > 0.05)
                .GroupBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => new ProcessUse(g.Key, Math.Min(g.Sum(u => u.Value), 100)))
                .OrderByDescending(u => u.Value)
                .Take(Quantos)
                .ToList();
        }

        _cpuAnterior = processos.ToDictionary(p => p.Pid, p => p.CpuTicks);
        _quandoAnterior = agora;

        var (gpu, gpuOk) = LerGpu(processos);

        return new ProcessTops { Cpu = cpu, Ram = ram, Gpu = gpu, GpuOk = gpuOk };
    }

    // ------------------------------------------------------------------
    // Processos, memória e tempo de CPU — direto do kernel
    // ------------------------------------------------------------------

    private readonly record struct Bruto(int Pid, string Nome, long WorkingSet, long CpuTicks);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int classe, IntPtr buffer, int tamanho, out int necessario);

    /// <summary>SystemProcessInformation.</summary>
    private const int SystemProcessInformation = 5;

    // Deslocamentos dentro de SYSTEM_PROCESS_INFORMATION em 64 bits. Conferidos contra a classe
    // Process: nome e memória batem em 371 de 376 processos, e os cinco restantes são os que não
    // têm arquivo de imagem (System, Registry, Secure System, Memory Compression) — nesses o nome
    // vem sem ".exe" e a memória bate igual.
    private const int OffProximo = 0;
    private const int OffTempoUsuario = 40;
    private const int OffTempoKernel = 48;
    private const int OffNomeTamanho = 56;
    private const int OffNomePonteiro = 64;
    private const int OffPid = 80;
    private const int OffWorkingSet = 144;

    private static List<Bruto> LerProcessos()
    {
        var lista = new List<Bruto>(400);
        var tamanho = 1 << 20;

        for (var tentativa = 0; tentativa < 6; tentativa++)
        {
            var buffer = Marshal.AllocHGlobal(tamanho);
            try
            {
                var status = NtQuerySystemInformation(SystemProcessInformation, buffer, tamanho, out var necessario);
                if (status != 0)
                {
                    // STATUS_INFO_LENGTH_MISMATCH e companhia: cresce e tenta de novo. A lista muda
                    // entre a pergunta do tamanho e a leitura, então o buffer leva folga.
                    tamanho = Math.Max(necessario + 128 * 1024, tamanho * 2);
                    continue;
                }

                var p = buffer;
                while (true)
                {
                    var pid = (int)Marshal.ReadIntPtr(p, OffPid).ToInt64();
                    var proximo = Marshal.ReadInt32(p, OffProximo);

                    // pid 0 é o processo Ocioso: ele "consome" todo o tempo que ninguém usou, e
                    // apareceria sempre no topo da lista de CPU dizendo o contrário do que parece
                    if (pid != 0)
                    {
                        var ticks = Marshal.ReadInt64(p, OffTempoUsuario) + Marshal.ReadInt64(p, OffTempoKernel);
                        var ws = Marshal.ReadIntPtr(p, OffWorkingSet).ToInt64();

                        var tam = (ushort)Marshal.ReadInt16(p, OffNomeTamanho);
                        var ptr = Marshal.ReadIntPtr(p, OffNomePonteiro);
                        var nome = ptr != IntPtr.Zero && tam > 0
                            ? Marshal.PtrToStringUni(ptr, tam / 2) ?? ""
                            : "";

                        if (nome.Length > 0) lista.Add(new Bruto(pid, nome, ws, ticks));
                    }

                    if (proximo == 0) break;
                    p = IntPtr.Add(p, proximo);
                }

                return lista;
            }
            catch
            {
                return lista; // leitura inválida: melhor lista vazia que número inventado
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return lista;
    }

    // ------------------------------------------------------------------
    // GPU por processo — contadores "GPU Engine" por consulta PDH
    // ------------------------------------------------------------------

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? fonte, IntPtr dados, out IntPtr consulta);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr consulta, string caminho, IntPtr dados, out IntPtr contador);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddCounterW(IntPtr consulta, string caminho, IntPtr dados, out IntPtr contador);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr consulta);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr consulta);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static extern uint PdhGetFormattedCounterArray(IntPtr contador, uint formato, ref uint tamanho,
                                                           out uint quantos, IntPtr itens);

    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const string CaminhoGpu = @"\GPU Engine(*)\Utilization Percentage";

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhItem
    {
        public IntPtr Nome;
        public uint Status;
        public uint Alinhamento;
        public double Valor;
    }

    private (IReadOnlyList<ProcessUse>, bool) LerGpu(List<Bruto> processos)
    {
        if (!AbrirGpu()) return (Array.Empty<ProcessUse>(), false);

        try
        {
            if (PdhCollectQueryData(_pdhQuery) != 0) return (Array.Empty<ProcessUse>(), false);

            uint tamanho = 0;
            PdhGetFormattedCounterArray(_pdhCounter, PDH_FMT_DOUBLE, ref tamanho, out _, IntPtr.Zero);
            if (tamanho == 0) return (Array.Empty<ProcessUse>(), true);

            var buffer = Marshal.AllocHGlobal((int)tamanho);
            try
            {
                if (PdhGetFormattedCounterArray(_pdhCounter, PDH_FMT_DOUBLE, ref tamanho, out var quantos, buffer) != 0)
                    return (Array.Empty<ProcessUse>(), false);

                var porPid = new Dictionary<int, double>();
                var passo = Marshal.SizeOf<PdhItem>();

                for (var i = 0; i < quantos; i++)
                {
                    var item = Marshal.PtrToStructure<PdhItem>(IntPtr.Add(buffer, i * passo));
                    if (item.Valor <= 0) continue;

                    var pid = PidDaInstancia(Marshal.PtrToStringUni(item.Nome));
                    if (pid == null) continue;

                    // um processo usa várias engines (3D, cópia, vídeo): o total é a soma delas,
                    // como o Gerenciador de Tarefas faz
                    porPid[pid.Value] = porPid.TryGetValue(pid.Value, out var atual) ? atual + item.Valor : item.Valor;
                }

                var nomes = new Dictionary<int, string>(processos.Count);
                foreach (var p in processos) nomes[p.Pid] = p.Nome;

                var lista = porPid
                    .Where(kv => kv.Value >= 0.5)
                    .Select(kv => new ProcessUse(nomes.TryGetValue(kv.Key, out var n) ? n : $"pid {kv.Key}", kv.Value))
                    .GroupBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new ProcessUse(g.Key, Math.Min(g.Sum(u => u.Value), 100)))
                    .OrderByDescending(u => u.Value)
                    .Take(Quantos)
                    .ToList();

                return (lista, true);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return (Array.Empty<ProcessUse>(), false);
        }
    }

    /// <summary>
    /// O nome da instância é "pid_1234_luid_0x...__phys_0_eng_0_engtype_3D". O que interessa é o
    /// número entre o primeiro e o segundo sublinhado.
    /// </summary>
    private static int? PidDaInstancia(string? instancia)
    {
        if (instancia == null || !instancia.StartsWith("pid_", StringComparison.Ordinal)) return null;
        var fim = instancia.IndexOf('_', 4);
        if (fim < 0) return null;
        return int.TryParse(instancia.AsSpan(4, fim - 4), out var pid) ? pid : null;
    }

    /// <summary>
    /// Abre a consulta uma única vez. Registrar o contador custa uns 260 ms (o Windows carrega o
    /// catálogo de contadores); mantida aberta, cada leitura depois sai em milissegundos. A
    /// primeira coleta também serve de referência: contador de taxa precisa de duas.
    /// </summary>
    private bool AbrirGpu()
    {
        if (_pdhCounter != IntPtr.Zero) return true;
        if (_pdhTentado) return false;
        _pdhTentado = true;

        try
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out var consulta) != 0) return false;

            if (PdhAddEnglishCounterW(consulta, CaminhoGpu, IntPtr.Zero, out var contador) != 0
                && PdhAddCounterW(consulta, CaminhoGpu, IntPtr.Zero, out contador) != 0)
            {
                PdhCloseQuery(consulta);
                return false;   // máquina sem os contadores de GPU por processo
            }

            PdhCollectQueryData(consulta);
            _pdhQuery = consulta;
            _pdhCounter = contador;
            return true;
        }
        catch
        {
            return false;   // sem pdh.dll não há lista de GPU, e o resto segue
        }
    }

    public void Dispose()
    {
        if (_pdhQuery == IntPtr.Zero) return;
        try { PdhCloseQuery(_pdhQuery); } catch { }
        _pdhQuery = IntPtr.Zero;
        _pdhCounter = IntPtr.Zero;
        _pdhTentado = false;
    }
}
