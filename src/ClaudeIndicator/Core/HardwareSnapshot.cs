using System;

namespace ClaudeIndicator.Core;

/// <summary>Uma medida de hardware, que pode não ter leitura disponível.</summary>
public readonly struct Reading
{
    public double? Value { get; }
    public bool HasValue => Value.HasValue;

    public Reading(double? value) => Value = value;

    public static Reading None => new(null);

    /// <summary>Texto curto com a unidade, ou "—" quando não há leitura.</summary>
    public string Format(string unit, int decimals = 0)
    {
        if (!HasValue) return "—";
        var v = Value!.Value;
        return decimals <= 0
            ? Math.Round(v).ToString("0") + unit
            : v.ToString("0." + new string('#', decimals)) + unit;
    }
}

/// <summary>
/// As últimas leituras de uso de cada componente, para desenhar o traçado.
///
/// Um número sozinho diz onde está; o traçado diz para onde vai. "GPU 78%" pode ser um pico
/// passageiro ou um platô de dez minutos, e a diferença muda o que se faz a respeito.
/// </summary>
public sealed class HardwareTrail
{
    public static readonly HardwareTrail Empty = new();

    public double[] Cpu { get; init; } = Array.Empty<double>();
    public double[] Gpu { get; init; } = Array.Empty<double>();
    public double[] Ram { get; init; } = Array.Empty<double>();
}

/// <summary>Leituras de um componente.</summary>
public sealed class ComponentReading
{
    public string Name { get; init; } = "";

    /// <summary>Uso, em porcentagem.</summary>
    public Reading Load { get; init; } = Reading.None;

    /// <summary>Temperatura, em graus Celsius.</summary>
    public Reading Temperature { get; init; } = Reading.None;

    /// <summary>Consumo, em watts.</summary>
    public Reading Power { get; init; } = Reading.None;

    /// <summary>Memória usada, em GB (VRAM na GPU, RAM no sistema).</summary>
    public Reading MemoryUsed { get; init; } = Reading.None;

    /// <summary>Memória total, em GB.</summary>
    public Reading MemoryTotal { get; init; } = Reading.None;

    public bool HasAnything => Load.HasValue || Temperature.HasValue || Power.HasValue || MemoryUsed.HasValue;
}

/// <summary>
/// Leituras de um disco físico.
///
/// Tipo próprio em vez de mais um <see cref="ComponentReading"/> porque as perguntas são outras.
/// Um disco não tem "uso" de um recurso finito como memória ou núcleos: tem tempo ocupado e uma
/// direção. E a direção é o que interessa ver — ler e gravar são trabalhos diferentes, e uma
/// leitura de 400 MB/s não significa a mesma coisa que uma gravação de 400 MB/s.
/// </summary>
public sealed class DiskReading
{
    /// <summary>Como o Gerenciador de Tarefas escreveria: "Disco 1 (D:)".</summary>
    public string Name { get; init; } = "";

    /// <summary>A instância do contador, no formato "1 D:".</summary>
    public string Instance { get; init; } = "";

    /// <summary>
    /// Fração do tempo em que o disco teve pelo menos um pedido em andamento, de 0 a 100. É o
    /// "Tempo de atividade" do Gerenciador — e é a única porcentagem honesta que um disco tem.
    /// </summary>
    public Reading Busy { get; init; } = Reading.None;

    /// <summary>Quanto do tempo ocupado é leitura, de 0 a 1. O resto é gravação.</summary>
    public Reading ReadShare { get; init; } = Reading.None;

    /// <summary>Bytes lidos por segundo.</summary>
    public Reading ReadBytes { get; init; } = Reading.None;

    /// <summary>Bytes gravados por segundo.</summary>
    public Reading WriteBytes { get; init; } = Reading.None;

    public bool HasAnything => Busy.HasValue;

    /// <summary>A parcela do tempo ocupado que é leitura, de 0 a 100.</summary>
    public double ReadPercent => Fatia(true);

    /// <summary>A parcela do tempo ocupado que é gravação, de 0 a 100.</summary>
    public double WritePercent => Fatia(false);

    private double Fatia(bool leitura)
    {
        if (!Busy.HasValue) return 0;

        var ocupado = Math.Clamp(Busy.Value!.Value, 0, 100);
        var parte = ReadShare.HasValue ? Math.Clamp(ReadShare.Value!.Value, 0, 1) : 0.5;
        return ocupado * (leitura ? parte : 1 - parte);
    }

    /// <summary>Taxa em texto curto, na maior unidade que ainda mostra um número legível.</summary>
    public static string Taxa(Reading bytesPorSegundo)
    {
        if (!bytesPorSegundo.HasValue) return "—";

        var v = Math.Max(0, bytesPorSegundo.Value!.Value);
        if (v >= 1024d * 1024 * 1024) return (v / (1024d * 1024 * 1024)).ToString("0.0") + " GB/s";
        if (v >= 1024d * 1024) return (v / (1024d * 1024)).ToString("0") + " MB/s";
        if (v >= 1024) return (v / 1024).ToString("0") + " KB/s";
        return v > 0 ? "<1 KB/s" : "0";
    }
}

/// <summary>Retrato do hardware num instante.</summary>
public sealed class HardwareSnapshot
{
    public DateTimeOffset At { get; init; }
    public ComponentReading Cpu { get; init; } = new();
    public ComponentReading Gpu { get; init; } = new();
    public ComponentReading Ram { get; init; } = new();

    /// <summary>O disco escolhido nas configurações.</summary>
    public DiskReading Disk { get; init; } = new();

    /// <summary>
    /// O app está elevado? Sem elevação, temperatura e potência da CPU não têm leitura — os
    /// sensores existem, mas o driver que acessa os registradores do processador não carrega.
    /// </summary>
    public bool Elevated { get; init; }

    /// <summary>
    /// A temperatura da CPU veio da zona térmica ACPI, e não do sensor interno do processador.
    /// A zona é do conjunto ao redor do processador: acompanha bem, mas não é o mesmo número que
    /// o Afterburner mostra, e a interface não deve fingir que é.
    /// </summary>
    public bool CpuTemperatureFromThermalZone { get; init; }

    /// <summary>Os sensores profundos da CPU foram pedidos nesta execução?</summary>
    public bool CpuSensorsEnabled { get; init; }

    /// <summary>
    /// Quem está consumindo mais agora, por componente — o que os balões dos indicadores mostram
    /// quando o mouse para sobre CPU, GPU ou memória. Vem junto do retrato porque é medido na
    /// mesma passada, na thread de leitura, e não no meio do desenho.
    /// </summary>
    public ProcessTops Processes { get; init; } = ProcessTops.Empty;

    /// <summary>Mensagem de falha da abertura do monitor, se houve.</summary>
    public string? Error { get; init; }

    public bool Ok => Error == null;

    public static HardwareSnapshot Empty => new() { At = DateTimeOffset.MinValue };
}
