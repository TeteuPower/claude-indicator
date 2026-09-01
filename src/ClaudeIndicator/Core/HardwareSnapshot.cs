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

/// <summary>Retrato do hardware num instante.</summary>
public sealed class HardwareSnapshot
{
    public DateTimeOffset At { get; init; }
    public ComponentReading Cpu { get; init; } = new();
    public ComponentReading Gpu { get; init; } = new();
    public ComponentReading Ram { get; init; } = new();

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
