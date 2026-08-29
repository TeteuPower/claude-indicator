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

    /// <summary>
    /// Quanto de atraso na entrega é normal antes de considerar que o processo parou de apresentar.
    ///
    /// O ETW não entrega evento a evento: ele enche um buffer e despeja de tempo em tempo, e na
    /// prática o quadro chega aqui com cerca de dois segundos de idade. Era exatamente isso que
    /// mantinha o FPS em branco com o jogo rodando — a janela de medição olhava o último segundo,
    /// e o quadro mais novo que existia já era mais velho que isso.
    /// </summary>
    private const double DeliveryGraceSeconds = 4.0;

    /// <summary>Janela do 1% low: engasgo raro precisa de história para aparecer.</summary>
    private const double HistorySeconds = 8.0;

    /// <summary>Em que unidade os carimbos dos eventos chegam.</summary>
    private enum Relogio
    {
        /// <summary>Ainda não se sabe: o primeiro evento decide.</summary>
        Indefinido,

        /// <summary>Contador de alta resolução, na frequência da máquina.</summary>
        Qpc,

        /// <summary>Hora do sistema: 100 ns desde 1601.</summary>
        FileTime
    }

    private readonly object _lock = new();
    private readonly Dictionary<int, Queue<long>> _frames = new();
    private readonly double _qpcFrequency;
    private Relogio _relogio = Relogio.Indefinido;
    private EtwSession? _session;

    public FrameRateMonitor()
    {
        QueryPerformanceFrequency(out var f);
        _qpcFrequency = f > 0 ? f : 10_000_000.0;
    }

    /// <summary>Agora, na mesma unidade em que os carimbos chegam.</summary>
    private long Agora() =>
        _relogio == Relogio.FileTime ? DateTimeOffset.UtcNow.ToFileTime() : QpcAgora();

    /// <summary>Quantas unidades do carimbo cabem em um segundo.</summary>
    private double PorSegundo() =>
        _relogio == Relogio.FileTime ? 10_000_000.0 : _qpcFrequency;

    /// <summary>
    /// Descobre a unidade dos carimbos olhando o primeiro evento: o relógio certo é aquele em que
    /// ele cai a poucos segundos de agora.
    ///
    /// Poderia ser fixo — a sessão pede hora do sistema —, mas essa suposição já custou caro uma
    /// vez: o código pedia contador de alta resolução, recebia hora do sistema e comparava os dois
    /// como se fossem a mesma coisa. Toda conta dava tempo negativo, nenhum processo aparecia
    /// como ativo, e nada nisso dava erro. Conferir com o primeiro evento custa uma comparação.
    /// </summary>
    private void ResolverRelogio(long carimbo)
    {
        var margem = 60L;

        var filetime = DateTimeOffset.UtcNow.ToFileTime();
        if (Math.Abs(filetime - carimbo) < margem * 10_000_000L)
        {
            _relogio = Relogio.FileTime;
            return;
        }

        QueryPerformanceCounter(out var qpc);
        if (Math.Abs(qpc - carimbo) < margem * (long)_qpcFrequency)
        {
            _relogio = Relogio.Qpc;
            return;
        }

        // nenhum dos dois bate: fica no de hora do sistema, que é o que a sessão pede
        _relogio = Relogio.FileTime;
    }

    private long QpcAgora()
    {
        QueryPerformanceCounter(out var v);
        return v;
    }

    /// <summary>Por que a medição não está funcionando, quando não está.</summary>
    public string? Error { get; private set; }

    /// <summary>A medição está de pé?</summary>
    public bool Running => _session?.Running == true;

    /// <summary>
    /// Uma frase sobre a saúde da captura. Sessão no ar sem evento nenhum chegando é um estado
    /// possível e silencioso — foi exatamente assim que ela ficou quebrada sem ninguém perceber.
    /// </summary>
    public string Health()
    {
        if (_session == null) return "desligada";
        if (!_session.Running) return "não subiu: " + (Error ?? "motivo desconhecido");
        if (_session.EnableErrors.Count > 0) return "provedor recusado: " + string.Join("; ", _session.EnableErrors);
        if (_session.EventsSeen == 0) return "no ar, mas sem receber evento nenhum";
        return $"no ar: {_session.EventsSeen} eventos, {_session.PresentsSeen} quadros";
    }

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
            if (_relogio == Relogio.Indefinido) ResolverRelogio(e.Timestamp);

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

            var corte = e.Timestamp - (long)(HistorySeconds * PorSegundo());
            while (fila.Count > 0 && fila.Peek() < corte) fila.Dequeue();
        }
    }

    private void PruneLocked(long agora)
    {
        var corte = agora - (long)(HistorySeconds * PorSegundo());
        var mortos = new List<int>();
        foreach (var (pid, fila) in _frames)
            if (fila.Count == 0 || fila.ToArray()[^1] < corte) mortos.Add(pid);
        foreach (var pid in mortos) _frames.Remove(pid);
    }

    /// <summary>Ritmo de quadros deste processo, ou <see cref="FrameStats.None"/> se ele não apresenta.</summary>
    public FrameStats StatsFor(int processId)
    {
        long[] carimbos;
        long agora;
        double porSegundo;
        lock (_lock)
        {
            if (!_frames.TryGetValue(processId, out var fila) || fila.Count < 2) return FrameStats.None;
            carimbos = fila.ToArray();
            agora = Agora();
            porSegundo = PorSegundo();
        }

        // A medição é feita sobre os CARIMBOS, e não contra o relógio de parede: os eventos chegam
        // com segundos de atraso, então "quantos quadros no último segundo de agora" daria sempre
        // zero. O relógio serve só para uma pergunta — o processo ainda está apresentando?
        var fim = carimbos[^1];
        if (agora - fim > DeliveryGraceSeconds * porSegundo)
            return new FrameStats(0, 0, 0, carimbos.Length);

        var janela = fim - (long)(WindowSeconds * porSegundo);
        var recentes = 0;
        for (var i = carimbos.Length - 1; i >= 0 && carimbos[i] >= janela; i--) recentes++;
        if (recentes < 2) return new FrameStats(0, 0, 0, carimbos.Length);

        // n carimbos delimitam n-1 intervalos: é a contagem dos intervalos que dá o ritmo
        var decorrido = (fim - Math.Max(carimbos[0], janela)) / porSegundo;
        var fps = decorrido > 0 ? (recentes - 1) / decorrido : 0;

        // tempos entre quadros consecutivos, em ms, sobre toda a história guardada
        var intervalos = new double[carimbos.Length - 1];
        for (var i = 1; i < carimbos.Length; i++)
            intervalos[i - 1] = (carimbos[i] - carimbos[i - 1]) * 1000.0 / porSegundo;

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
        var lista = new List<(int, double)>();
        lock (_lock)
        {
            var agora = Agora();
            var porSegundo = PorSegundo();

            foreach (var (pid, fila) in _frames)
            {
                if (fila.Count < 2) continue;

                var carimbos = fila.ToArray();
                var fim = carimbos[^1];
                if (agora - fim > DeliveryGraceSeconds * porSegundo) continue;

                var janela = fim - (long)(WindowSeconds * porSegundo);
                var recentes = 0;
                for (var i = carimbos.Length - 1; i >= 0 && carimbos[i] >= janela; i--) recentes++;
                if (recentes < 2) continue;

                var decorrido = (fim - Math.Max(carimbos[0], janela)) / porSegundo;
                var fps = decorrido > 0 ? (recentes - 1) / decorrido : 0;
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
