using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using LibreHardwareMonitor.Hardware;

namespace ClaudeIndicator.Core;

/// <summary>
/// Lê uso, temperatura e consumo da CPU e da GPU, e a memória do sistema.
///
/// A GPU NVIDIA responde sem privilégio nenhum. A CPU é outra história: temperatura e watts saem
/// de registradores do processador, acessíveis só por um driver de kernel — sem executar como
/// administrador os sensores aparecem, mas sem valor. Por isso o retrato carrega o estado de
/// elevação: a interface precisa distinguir "não tem leitura" de "não tenho permissão".
///
/// Abrir o monitor leva alguns segundos e cada atualização dezenas de milissegundos, então tudo
/// acontece numa thread própria e a interface só lê o último retrato pronto.
/// </summary>
public sealed class HardwareMonitor : IDisposable
{
    private readonly object _lock = new();
    private Computer? _computer;
    private Thread? _worker;
    private volatile bool _running;
    private volatile int _intervalMs = 1000;
    private volatile bool _cpuSensors;
    private PerformanceCounter? _cpuLoad;
    private PerformanceCounter? _thermalZone;

    public HardwareSnapshot Current { get; private set; } = HardwareSnapshot.Empty;

    /// <summary>Disparado na thread de leitura sempre que há um retrato novo.</summary>
    public event Action<HardwareSnapshot>? Updated;

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <param name="cpuSensors">
    /// Ligar os sensores profundos da CPU. Isso faz a biblioteca extrair e carregar um driver de
    /// kernel, o que exige elevação, dispara alerta de antivírus e é barrado pela Integridade de
    /// Memória. Desligado, o uso da CPU ainda é lido — por contador de desempenho, sem driver.
    /// </param>
    public void Start(int intervalSeconds, bool cpuSensors)
    {
        _intervalMs = Math.Clamp(intervalSeconds, 1, 60) * 1000;
        _cpuSensors = cpuSensors;
        if (_running) return;

        _running = true;
        _worker = new Thread(Loop)
        {
            IsBackground = true,
            Name = "ClaudeIndicator.Hardware",
            Priority = ThreadPriority.BelowNormal
        };
        _worker.Start();
    }

    public void SetInterval(int intervalSeconds) =>
        _intervalMs = Math.Clamp(intervalSeconds, 1, 60) * 1000;

    public void Stop()
    {
        _running = false;
        _worker = null;

        lock (_lock)
        {
            try
            {
                _computer?.Close();
            }
            catch
            {
                // fechar o driver pode falhar se ele já saiu; não há o que fazer
            }
            _computer = null;
        }
        Current = HardwareSnapshot.Empty;
    }

    public void Dispose() => Stop();

    // ------------------------------------------------------------------

    private void Loop()
    {
        var elevado = IsElevated;

        try
        {
            // uso da CPU sem driver nenhum: o contador do Windows basta
            _cpuLoad = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total");
            _cpuLoad.NextValue();
        }
        catch
        {
            _cpuLoad = null; // sem contador, o uso da CPU fica sem leitura
        }

        try
        {
            // Zona térmica ACPI: temperatura sem driver e sem elevação, a ~2 ms por leitura.
            // Não é o sensor interno do processador, mas acompanha o aquecimento dele de perto.
            var cat = new PerformanceCounterCategory("Thermal Zone Information");
            var zona = cat.GetInstanceNames().FirstOrDefault();
            if (zona != null)
            {
                _thermalZone = new PerformanceCounter("Thermal Zone Information", "Temperature", zona);
                _thermalZone.NextValue();
            }
        }
        catch
        {
            _thermalZone = null; // nem toda máquina expõe zona térmica
        }

        try
        {
            lock (_lock)
            {
                _computer = new Computer
                {
                    // só liga a CPU quando os sensores profundos foram pedidos: é o que faz a
                    // biblioteca extrair o driver de kernel
                    IsCpuEnabled = _cpuSensors,
                    IsGpuEnabled = true,
                    IsMemoryEnabled = true
                };
                _computer.Open();
            }
        }
        catch (Exception ex)
        {
            Publish(new HardwareSnapshot
            {
                At = DateTimeOffset.Now,
                Elevated = elevado,
                Error = "Não foi possível iniciar a leitura de hardware: " + ex.Message
            });
            _running = false;
            return;
        }

        while (_running)
        {
            try
            {
                Publish(Read(elevado));
            }
            catch (Exception ex)
            {
                Publish(new HardwareSnapshot
                {
                    At = DateTimeOffset.Now,
                    Elevated = elevado,
                    Error = "Falha ao ler os sensores: " + ex.Message
                });
            }

            Thread.Sleep(_intervalMs);
        }
    }

    /// <summary>Quantas leituras o traçado guarda. A 2 s por leitura, cobre uns dois minutos.</summary>
    private const int TrailLength = 60;

    private readonly Queue<double> _trilhaCpu = new(TrailLength);
    private readonly Queue<double> _trilhaGpu = new(TrailLength);
    private readonly Queue<double> _trilhaRam = new(TrailLength);
    private readonly object _trilhaLock = new();

    /// <summary>Cópia das últimas leituras, da mais antiga para a mais recente.</summary>
    public HardwareTrail Trail()
    {
        lock (_trilhaLock)
        {
            return new HardwareTrail
            {
                Cpu = _trilhaCpu.ToArray(),
                Gpu = _trilhaGpu.ToArray(),
                Ram = _trilhaRam.ToArray()
            };
        }
    }

    private static void Empilhar(Queue<double> fila, Reading leitura)
    {
        // leitura ausente não vira zero: zero é um valor, e desenhar um vale que não aconteceu
        // seria pior que não desenhar nada. Repete o último, ou começa em zero se não há nenhum.
        var valor = leitura.HasValue ? leitura.Value!.Value : (fila.Count > 0 ? fila.ToArray()[^1] : 0);
        fila.Enqueue(Math.Clamp(valor, 0, 100));
        while (fila.Count > TrailLength) fila.Dequeue();
    }

    private void Publish(HardwareSnapshot snap)
    {
        lock (_trilhaLock)
        {
            Empilhar(_trilhaCpu, snap.Cpu.Load);
            Empilhar(_trilhaGpu, snap.Gpu.Load);
            Empilhar(_trilhaRam, snap.Ram.Load);
        }

        Current = snap;
        Updated?.Invoke(snap);
    }

    private HardwareSnapshot Read(bool elevado)
    {
        var cpu = new ComponentReading();
        var gpu = new ComponentReading();
        var ram = new ComponentReading();

        lock (_lock)
        {
            if (_computer == null) return HardwareSnapshot.Empty;

            foreach (var hw in _computer.Hardware)
            {
                hw.Update();
                foreach (var sub in hw.SubHardware) sub.Update();

                switch (hw.HardwareType)
                {
                    case HardwareType.Cpu:
                        cpu = ReadCpu(hw);
                        break;


                    // a dedicada é a que interessa; a integrada entra só se não houver outra
                    case HardwareType.GpuNvidia:
                    case HardwareType.GpuAmd:
                        gpu = ReadGpu(hw);
                        break;

                    case HardwareType.GpuIntel:
                        if (!gpu.HasAnything) gpu = ReadGpu(hw);
                        break;

                    case HardwareType.Memory:
                        ram = ReadMemory(hw);
                        break;
                }
            }
        }

        // O uso vem do contador, que funciona sempre. A temperatura tem duas fontes: o sensor
        // interno do processador (só com driver) e, na falta dele, a zona térmica ACPI.
        var doDriver = cpu.Temperature;
        var daZona = doDriver.HasValue ? Reading.None : ReadThermalZone();

        cpu = new ComponentReading
        {
            Name = cpu.Name.Length > 0 ? cpu.Name : "CPU",
            Load = ReadCpuLoad(),
            Temperature = doDriver.HasValue ? doDriver : daZona,
            Power = cpu.Power
        };

        return new HardwareSnapshot
        {
            At = DateTimeOffset.Now,
            Cpu = cpu,
            Gpu = gpu,
            Ram = ram,
            Elevated = elevado,
            CpuSensorsEnabled = _cpuSensors,
            CpuTemperatureFromThermalZone = daZona.HasValue
        };
    }

    /// <summary>Zona térmica ACPI, em Kelvin no contador.</summary>
    private Reading ReadThermalZone()
    {
        try
        {
            if (_thermalZone == null) return Reading.None;
            var kelvin = _thermalZone.NextValue();
            if (kelvin <= 0 || float.IsNaN(kelvin)) return Reading.None;

            var celsius = kelvin - 273.15;
            // leitura fora de qualquer faixa plausível é ruído, não temperatura
            return celsius is > 0 and < 125 ? new Reading(celsius) : Reading.None;
        }
        catch
        {
            return Reading.None;
        }
    }

    private Reading ReadCpuLoad()
    {
        try
        {
            if (_cpuLoad == null) return Reading.None;
            var v = _cpuLoad.NextValue();
            return double.IsNaN(v) ? Reading.None : new Reading(Math.Clamp(v, 0, 100));
        }
        catch
        {
            return Reading.None;
        }
    }

    private static ComponentReading ReadCpu(IHardware hw) => new()
    {
        Name = hw.Name,
        Load = Find(hw, SensorType.Load, "CPU Total", "CPU Usage"),
        // "Core Max" é o pico entre os núcleos, que é o número honesto de temperatura
        Temperature = Find(hw, SensorType.Temperature, "Core Max", "Core Average", "CPU Package", "Core (Tctl/Tdie)"),
        Power = Find(hw, SensorType.Power, "CPU Package", "Package", "CPU Cores")
    };

    private static ComponentReading ReadGpu(IHardware hw)
    {
        // os sensores de memória da GPU vêm em MB
        var usada = Scale(Find(hw, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used"), 1.0 / 1024);
        var total = Scale(Find(hw, SensorType.SmallData, "GPU Memory Total"), 1.0 / 1024);

        return new ComponentReading
        {
            Name = hw.Name,
            Load = Find(hw, SensorType.Load, "GPU Core", "D3D 3D"),
            Temperature = Find(hw, SensorType.Temperature, "GPU Core", "GPU Hot Spot"),
            Power = Find(hw, SensorType.Power, "GPU Package", "GPU Power"),
            MemoryUsed = usada,
            MemoryTotal = total
        };
    }

    private static ComponentReading ReadMemory(IHardware hw)
    {
        var usada = Find(hw, SensorType.Data, "Memory Used");
        var livre = Find(hw, SensorType.Data, "Memory Available");
        var total = usada.HasValue && livre.HasValue
            ? new Reading(usada.Value!.Value + livre.Value!.Value)
            : Reading.None;

        return new ComponentReading
        {
            Name = "Memória",
            Load = Find(hw, SensorType.Load, "Memory"),
            MemoryUsed = usada,
            MemoryTotal = total
        };
    }

    /// <summary>
    /// Procura o sensor pelos nomes candidatos, em ordem de preferência. Os nomes variam entre
    /// fabricantes e versões da biblioteca, então tentar vários é mais seguro que fixar um.
    /// </summary>
    private static Reading Find(IHardware hw, SensorType type, params string[] names)
    {
        foreach (var name in names)
        {
            var sensor = hw.Sensors.FirstOrDefault(s =>
                s.SensorType == type && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (sensor?.Value is { } v && !double.IsNaN(v)) return new Reading(v);
        }
        return Reading.None;
    }

    private static Reading Scale(Reading r, double factor) =>
        r.HasValue ? new Reading(r.Value!.Value * factor) : Reading.None;
}
