using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ClaudeIndicator.Core;

/// <summary>Ritmo de quadros de um processo.</summary>
public readonly struct FrameStats
{
    public static readonly FrameStats None = new();

    public FrameStats(double fps, double frameTimeMs, double onePercentLowFps, int samples)
    {
        Fps = fps;
        FrameTimeMs = frameTimeMs;
        OnePercentLowFps = onePercentLowFps;
        Samples = samples;
    }

    /// <summary>Quadros por segundo na última janela de medição.</summary>
    public double Fps { get; }

    /// <summary>Tempo médio de quadro, em milissegundos.</summary>
    public double FrameTimeMs { get; }

    /// <summary>
    /// O "1% low": o ritmo nos 1% de quadros mais lentos. É o número que revela engasgo — a média
    /// pode estar ótima e a experiência ser ruim, e essa diferença é justamente o que se procura.
    /// </summary>
    public double OnePercentLowFps { get; }

    /// <summary>Quantos quadros entraram na conta.</summary>
    public int Samples { get; }

    public bool HasValue => Samples > 1 && Fps > 0;
}

/// <summary>
/// Conta quadros por processo a partir dos eventos de apresentação do Windows.
///
/// Os eventos chegam pela thread do ETW e são lidos pela interface, então tudo aqui é protegido
/// por um cadeado simples. Cada processo guarda só os carimbos da última janela de interesse; o
/// que envelhece é descartado, e processo que para de apresentar some sozinho.
/// </summary>
public sealed class FrameRateMonitor : IDisposable
{
    /// <summary>Janela do FPS instantâneo. Curta o bastante para reagir, longa o bastante para não tremer.</summary>
    private const double WindowSeconds = 1.0;

    /// <summary>Janela do 1% low: engasgo raro precisa de história para aparecer.</summary>
    private const double HistorySeconds = 8.0;

    private readonly object _lock = new();
    private readonly Dictionary<int, Queue<long>> _frames = new();
    private readonly double _qpcFrequency;
    private EtwSession? _session;

    public FrameRateMonitor()
    {
        QueryPerformanceFrequency(out var f);
        _qpcFrequency = f > 0 ? f : 10_000_000.0;
    }

    /// <summary>Por que a medição não está funcionando, quando não está.</summary>
    public string? Error { get; private set; }

    /// <summary>A medição está de pé?</summary>
    public bool Running => _session?.Running == true;

    public bool Start()
    {
        if (_session != null) return Running;

        var session = new EtwSession("ClaudeIndicatorFrames", Record);
        if (!session.Start())
        {
            Error = session.Error;
            session.Dispose();
            return false;
        }

        Error = null;
        _session = session;
        return true;
    }

    public void Stop()
    {
        _session?.Dispose();
        _session = null;
        lock (_lock) _frames.Clear();
    }

    /// <summary>
    /// Registra um quadro apresentado. É a porta de entrada do medidor: a sessão ETW é quem a
    /// alimenta em produção, mas quem alimenta não faz diferença para a conta.
    /// </summary>
    public void Record(PresentEvent e)
    {
        lock (_lock)
        {
            if (!_frames.TryGetValue(e.ProcessId, out var fila))
            {
                // Um limite de processos acompanhados evita que o dicionário cresça com toda janela
                // do sistema que desenha alguma coisa. Quem apresenta de verdade é sempre pouco.
                if (_frames.Count >= 32) PruneLocked(e.Timestamp);
                if (_frames.Count >= 32) return;

                fila = new Queue<long>(512);
                _frames[e.ProcessId] = fila;
            }

            fila.Enqueue(e.Timestamp);

            var corte = e.Timestamp - (long)(HistorySeconds * _qpcFrequency);
            while (fila.Count > 0 && fila.Peek() < corte) fila.Dequeue();
        }
    }

    private void PruneLocked(long agora)
    {
        var corte = agora - (long)(HistorySeconds * _qpcFrequency);
        var mortos = new List<int>();
        foreach (var (pid, fila) in _frames)
            if (fila.Count == 0 || fila.ToArray()[^1] < corte) mortos.Add(pid);
        foreach (var pid in mortos) _frames.Remove(pid);
    }

    /// <summary>Ritmo de quadros deste processo, ou <see cref="FrameStats.None"/> se ele não apresenta.</summary>
    public FrameStats StatsFor(int processId)
    {
        QueryPerformanceCounter(out var agora);

        long[] carimbos;
        lock (_lock)
        {
            if (!_frames.TryGetValue(processId, out var fila) || fila.Count < 2) return FrameStats.None;
            carimbos = fila.ToArray();
        }

        var janela = agora - (long)(WindowSeconds * _qpcFrequency);
        var recentes = 0;
        for (var i = carimbos.Length - 1; i >= 0 && carimbos[i] >= janela; i--) recentes++;

        // Nenhum quadro no último segundo: o processo parou de apresentar (minimizado, alt-tab,
        // fechando). Dizer "0 FPS" seria mais honesto que repetir o último número.
        if (recentes == 0) return new FrameStats(0, 0, 0, carimbos.Length);

        var decorrido = (agora - Math.Max(carimbos[0], janela)) / _qpcFrequency;
        var fps = decorrido > 0 ? recentes / decorrido : 0;

        // tempos entre quadros consecutivos, em ms, sobre toda a história guardada
        var intervalos = new double[carimbos.Length - 1];
        for (var i = 1; i < carimbos.Length; i++)
            intervalos[i - 1] = (carimbos[i] - carimbos[i - 1]) * 1000.0 / _qpcFrequency;

        var media = 0.0;
        foreach (var v in intervalos) media += v;
        media /= intervalos.Length;

        // O 1% low é a MÉDIA do 1% de quadros mais lentos, e não o quadro que cai no percentil 99.
        // A diferença importa: um engasgo isolado numa janela de ~180 quadros é 0,5% da amostra, e
        // o percentil o descartaria justamente por ser raro — quando é exatamente ele que se quer
        // ver. Pelo menos um quadro sempre entra na conta.
        Array.Sort(intervalos);
        var piores = Math.Max(1, intervalos.Length / 100);
        var soma = 0.0;
        for (var i = intervalos.Length - piores; i < intervalos.Length; i++) soma += intervalos[i];
        var piorMedio = soma / piores;
        var low = piorMedio > 0 ? 1000.0 / piorMedio : 0;

        return new FrameStats(fps, media, low, carimbos.Length);
    }

    /// <summary>
    /// Processos que estão apresentando quadros agora, do mais ativo para o menos. É por aqui que a
    /// detecção de jogo começa: quem desenha muitos quadros por segundo é candidato, o resto não.
    /// </summary>
    public List<(int ProcessId, double Fps)> ActivePresenters(double minimumFps = 5)
    {
        QueryPerformanceCounter(out var agora);
        var janela = agora - (long)(WindowSeconds * _qpcFrequency);

        var lista = new List<(int, double)>();
        lock (_lock)
        {
            foreach (var (pid, fila) in _frames)
            {
                if (fila.Count < 2) continue;

                var carimbos = fila.ToArray();
                var recentes = 0;
                for (var i = carimbos.Length - 1; i >= 0 && carimbos[i] >= janela; i--) recentes++;
                if (recentes == 0) continue;

                var decorrido = (agora - Math.Max(carimbos[0], janela)) / _qpcFrequency;
                var fps = decorrido > 0 ? recentes / decorrido : 0;
                if (fps >= minimumFps) lista.Add((pid, fps));
            }
        }

        lista.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return lista;
    }

    public void Dispose() => Stop();

    [DllImport("kernel32.dll")]
    private static extern bool QueryPerformanceFrequency(out long frequency);

    [DllImport("kernel32.dll")]
    private static extern bool QueryPerformanceCounter(out long counter);
}
