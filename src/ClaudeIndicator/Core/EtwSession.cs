using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace ClaudeIndicator.Core;

/// <summary>Um evento de apresentação de quadro, reduzido ao que interessa.</summary>
public readonly struct PresentEvent
{
    public PresentEvent(int processId, long timestamp)
    {
        ProcessId = processId;
        Timestamp = timestamp;
    }

    /// <summary>Processo que apresentou o quadro.</summary>
    public int ProcessId { get; }

    /// <summary>Carimbo do evento, em unidades de QueryPerformanceCounter.</summary>
    public long Timestamp { get; }
}

/// <summary>
/// Sessão de Event Tracing for Windows que escuta os eventos de apresentação de quadro do Windows.
///
/// É assim que o PresentMon (Intel) e o FrameView (NVIDIA) medem FPS: o runtime gráfico do Windows
/// já anuncia cada quadro apresentado, e basta escutar. Nada é injetado no jogo, nada é lido da
/// memória dele — o caminho oposto ao do RTSS, que injeta uma DLL e engancha o Present para
/// desenhar dentro do jogo. Passivo assim, não há o que um anticheat possa confundir com trapaça.
///
/// Só o cabeçalho de cada evento é lido: quem apresentou e quando. O corpo do evento é ignorado,
/// o que dispensa decodificar manifesto e mantém o custo em quase nada.
/// </summary>
public sealed class EtwSession : IDisposable
{
    // Microsoft-Windows-DXGI: todo jogo Direct3D 10/11/12 passa por aqui.
    private static readonly Guid DxgiProvider = new("ca11c036-0102-4a2d-a6ad-f03cfed5d3c9");
    private const int DxgiPresentStart = 42;

    // Microsoft-Windows-D3D9: jogos antigos, que não usam DXGI.
    private static readonly Guid D3d9Provider = new("783aca0a-790e-4d7f-8451-aa850511c6b9");
    private const int D3d9PresentStart = 1;

    /// <summary>Os provedores que anunciam quadro apresentado, e o evento de cada um.</summary>
    public static readonly (Guid Provider, int EventId)[] PresentProviders =
    {
        (DxgiProvider, DxgiPresentStart),
        (D3d9Provider, D3d9PresentStart)
    };

    private readonly string _sessionName;
    private readonly Action<PresentEvent> _onPresent;
    private readonly (Guid Provider, int EventId)[] _providers;

    private long _handle;          // TRACEHANDLE da sessão de controle
    private long _traceHandle;     // TRACEHANDLE do consumidor
    private Thread? _pump;
    private IntPtr _properties;
    private bool _stopping;

    // O delegate precisa ficar vivo enquanto o ETW o chama: sem esta referência o coletor de lixo
    // o remove e o callback nativo cai no vazio.
    private readonly EventRecordCallback _callback;

    /// <param name="providers">
    /// Quais provedores escutar e qual evento de cada um conta como quadro apresentado. O padrão
    /// cobre Direct3D 9 e tudo que passa por DXGI, que é onde os jogos estão.
    /// </param>
    public EtwSession(string sessionName, Action<PresentEvent> onPresent,
                      (Guid Provider, int EventId)[]? providers = null)
    {
        _sessionName = sessionName;
        _onPresent = onPresent;
        _providers = providers ?? PresentProviders;
        _callback = OnEventRecord;
    }

    /// <summary>
    /// Onde cada campo cai dentro da estrutura que o Windows lê. É a aferição que importa de
    /// verdade: se o ponteiro do callback não estiver no deslocamento certo, a bomba de eventos
    /// chama endereço errado — e falha assim é silenciosa, não dá exceção.
    /// </summary>
    public static int LogfileOffset(string field) => (int)Marshal.OffsetOf<EventTraceLogfile>(field);

    /// <summary>Tamanhos que as estruturas nativas precisam ter em x64. Um erro aqui corromperia tudo.</summary>
    public static (int Properties, int Record, int Header, int Logfile) NativeSizes() =>
        (Marshal.SizeOf<EventTraceProperties>(), Marshal.SizeOf<EventRecord>(),
         Marshal.SizeOf<EventHeader>(), Marshal.SizeOf<EventTraceLogfile>());

    /// <summary>Por que a sessão não subiu, quando não subiu.</summary>
    public string? Error { get; private set; }

    /// <summary>Eventos recebidos, de qualquer provedor. Zero com a sessão no ar significa problema.</summary>
    public long EventsSeen => _eventsSeen;

    /// <summary>Desses, quantos eram quadro apresentado.</summary>
    public long PresentsSeen => _presentsSeen;

    /// <summary>Provedores que recusaram ser ligados, se houve algum.</summary>
    public IReadOnlyList<string> EnableErrors => _enableErrors;

    private long _eventsSeen;
    private long _presentsSeen;
    private readonly List<string> _enableErrors = new();

    /// <summary>Está escutando?</summary>
    public bool Running => _traceHandle != 0 && _traceHandle != InvalidHandle;

    /// <summary>
    /// Liga a sessão. Devolve false com <see cref="Error"/> preenchido quando não dá — o caso
    /// comum é falta de privilégio: criar sessão ETW exige administrador ou pertencer ao grupo
    /// "Usuários do log de desempenho".
    /// </summary>
    public bool Start()
    {
        try
        {
            StopStaleSession();

            var nameBytes = (_sessionName.Length + 1) * 2;
            var size = Marshal.SizeOf<EventTraceProperties>() + nameBytes;
            _properties = Marshal.AllocHGlobal(size);
            for (var i = 0; i < size; i++) Marshal.WriteByte(_properties, i, 0);

            var props = new EventTraceProperties
            {
                Wnode = new WnodeHeader
                {
                    BufferSize = (uint)size,
                    Flags = WnodeFlagTracedGuid,
                    // Carimbos em hora do sistema (FILETIME, 100 ns desde 1601). Pedir
                    // QueryPerformanceCounter aqui não é atendido — a sessão entrega FILETIME de
                    // qualquer jeito —, e pedir uma coisa recebendo outra é como o medidor ficou
                    // comparando carimbo de um relógio com "agora" de outro.
                    ClientContext = 2
                },
                LogFileMode = ProcessTraceModeRealTime,
                BufferSize = 64,
                MinimumBuffers = 8,
                MaximumBuffers = 32,
                FlushTimer = 1,
                LoggerNameOffset = (uint)Marshal.SizeOf<EventTraceProperties>()
            };
            Marshal.StructureToPtr(props, _properties, false);
            WriteSessionName();

            var status = StartTraceW(out _handle, _sessionName, _properties);
            if (status != 0)
            {
                Error = status == ErrorAccessDenied
                    ? "criar uma sessão de rastreamento exige administrador"
                    : $"não foi possível criar a sessão de rastreamento (erro {status})";
                Cleanup();
                return false;
            }

            foreach (var (provider, _) in _providers) EnableProvider(provider);

            var logfile = new EventTraceLogfile
            {
                LoggerName = _sessionName,
                ProcessTraceMode = ProcessTraceModeRealTime | ProcessTraceModeEventRecord,
                EventRecordCallback = _callback
            };

            _traceHandle = OpenTraceW(ref logfile);
            if (_traceHandle == InvalidHandle)
            {
                Error = $"não foi possível abrir o rastreamento (erro {Marshal.GetLastWin32Error()})";
                Cleanup();
                return false;
            }

            _pump = new Thread(Pump)
            {
                IsBackground = true,
                Name = "etw-frames",
                Priority = ThreadPriority.AboveNormal
            };
            _pump.Start();
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Cleanup();
            return false;
        }
    }

    private void EnableProvider(Guid provider)
    {
        // O NÍVEL é um filtro, não uma etiqueta: o ETW entrega apenas eventos de nível menor ou
        // igual ao pedido. "Informativo" (4) parecia razoável e descartava calado tudo que é
        // detalhado (5) — que é justamente onde o Present mora. Pedindo o máximo, nada é filtrado.
        //
        // A palavra-chave fica em zero de propósito, e não em "todas": zero significa sem filtro
        // por palavra-chave, enquanto todas-ligadas exigiria que o evento tivesse ao menos uma —
        // e evento sem palavra-chave nenhuma nunca chegaria.
        var status = EnableTraceEx2(_handle, ref provider, EventControlCodeEnableProvider,
                                    TraceLevelAll, 0, 0, 0, IntPtr.Zero);
        if (status != 0) _enableErrors.Add($"{provider}: erro {status}");
    }

    /// <summary>
    /// Sessão ETW sobrevive ao processo que a criou. Um fechamento anormal deixaria o nome ocupado
    /// e todo Start seguinte falharia com "já existe" — então derruba a antiga antes de criar.
    /// </summary>
    private void StopStaleSession()
    {
        var nameBytes = (_sessionName.Length + 1) * 2;
        var size = Marshal.SizeOf<EventTraceProperties>() + nameBytes;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            for (var i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0);
            var props = new EventTraceProperties
            {
                Wnode = new WnodeHeader { BufferSize = (uint)size },
                LoggerNameOffset = (uint)Marshal.SizeOf<EventTraceProperties>()
            };
            Marshal.StructureToPtr(props, buffer, false);
            ControlTraceW(0, _sessionName, buffer, ControlCodeStop);
        }
        catch
        {
            // não havia sessão pendurada, que é o caso normal
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void WriteSessionName()
    {
        var offset = Marshal.SizeOf<EventTraceProperties>();
        var bytes = System.Text.Encoding.Unicode.GetBytes(_sessionName + "\0");
        for (var i = 0; i < bytes.Length; i++)
            Marshal.WriteByte(_properties, offset + i, bytes[i]);
    }

    private void Pump()
    {
        var handles = new[] { _traceHandle };
        try
        {
            // ProcessTrace só volta quando a sessão é fechada — daí a thread dedicada
            ProcessTrace(handles, 1, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // sessão derrubada por fora: o Stop já cuidou do resto
        }
    }

    private void OnEventRecord(ref EventRecord record)
    {
        if (_stopping) return;

        System.Threading.Interlocked.Increment(ref _eventsSeen);

        var id = record.EventHeader.EventDescriptor.Id;
        var provider = record.EventHeader.ProviderId;

        var isPresent = false;
        foreach (var (p, evento) in _providers)
        {
            if (p == provider && evento == id) { isPresent = true; break; }
        }
        if (!isPresent) return;
        System.Threading.Interlocked.Increment(ref _presentsSeen);

        try
        {
            _onPresent(new PresentEvent((int)record.EventHeader.ProcessId,
                                        record.EventHeader.TimeStamp));
        }
        catch
        {
            // um consumidor com defeito não pode derrubar a bomba de eventos
        }
    }

    public void Dispose()
    {
        _stopping = true;

        if (_traceHandle != 0 && _traceHandle != InvalidHandle)
        {
            CloseTrace(_traceHandle);
            _traceHandle = 0;
        }

        if (_handle != 0 && _properties != IntPtr.Zero)
        {
            ControlTraceW(_handle, _sessionName, _properties, ControlCodeStop);
            _handle = 0;
        }

        // ProcessTrace devolve o controle quando a sessão fecha; espera pouco e segue
        _pump?.Join(2000);
        _pump = null;

        Cleanup();
    }

    private void Cleanup()
    {
        if (_properties != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_properties);
            _properties = IntPtr.Zero;
        }
    }

    // ------------------------------------------------------------------
    // Interop
    // ------------------------------------------------------------------

    private const long InvalidHandle = -1;
    private const uint WnodeFlagTracedGuid = 0x00020000;
    private const uint ProcessTraceModeRealTime = 0x00000100;
    private const uint ProcessTraceModeEventRecord = 0x10000000;
    private const uint EventControlCodeEnableProvider = 1;
    private const byte TraceLevelAll = 0xFF;
    private const uint ControlCodeStop = 1;
    private const int ErrorAccessDenied = 5;

    private delegate void EventRecordCallback(ref EventRecord record);

    [StructLayout(LayoutKind.Sequential)]
    private struct WnodeHeader
    {
        public uint BufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;
        public long TimeStamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTraceProperties
    {
        public WnodeHeader Wnode;
        public uint BufferSize;
        public uint MinimumBuffers;
        public uint MaximumBuffers;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint FlushTimer;
        public uint EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers;
        public uint FreeBuffers;
        public uint EventsLost;
        public uint BuffersWritten;
        public uint LogBuffersLost;
        public uint RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset;
        public uint LoggerNameOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventDescriptor
    {
        public ushort Id;
        public byte Version;
        public byte Channel;
        public byte Level;
        public byte Opcode;
        public ushort Task;
        public ulong Keyword;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventHeader
    {
        public ushort Size;
        public ushort HeaderType;
        public ushort Flags;
        public ushort EventProperty;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;
        public Guid ProviderId;
        public EventDescriptor EventDescriptor;
        public ulong ProcessorTime;
        public Guid ActivityId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EtwBufferContext
    {
        public byte ProcessorNumber;
        public byte Alignment;
        public ushort LoggerId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventRecord
    {
        public EventHeader EventHeader;
        public EtwBufferContext BufferContext;
        public ushort ExtendedDataCount;
        public ushort UserDataLength;
        public IntPtr ExtendedData;
        public IntPtr UserData;
        public IntPtr UserContext;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct EventTraceHeader
    {
        public ushort Size;
        public ushort FieldTypeFlags;
        public uint Version;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct EventTrace
    {
        public EventTraceHeader Header;
        public uint InstanceId;
        public uint ParentInstanceId;
        public Guid ParentGuid;
        public IntPtr MofData;
        public uint MofLength;
        public EtwBufferContext BufferContext;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TraceLogfileHeader
    {
        public uint BufferSize;
        public uint Version;
        public uint ProviderVersion;
        public uint NumberOfProcessors;
        public long EndTime;
        public uint TimerResolution;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint BuffersWritten;
        public Guid LogInstanceGuid;
        public IntPtr LoggerName;
        public IntPtr LogFileName;
        public TimeZoneInformation TimeZone;
        public long BootTime;
        public long PerfFreq;
        public long StartTime;
        public uint ReservedFlags;
        public uint BuffersLost;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TimeZoneInformation
    {
        public int Bias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string StandardName;
        public SystemTime StandardDate;
        public int StandardBias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DaylightName;
        public SystemTime DaylightDate;
        public int DaylightBias;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct EventTraceLogfile
    {
        public string? LogFileName;
        public string? LoggerName;
        public long CurrentTime;
        public uint BuffersRead;
        public uint ProcessTraceMode;
        public EventTrace CurrentEvent;
        public TraceLogfileHeader LogfileHeader;
        public IntPtr BufferCallback;
        public uint BufferSize;
        public uint Filled;
        public uint EventsLost;
        public EventRecordCallback? EventRecordCallback;
        public uint IsKernelTrace;
        public IntPtr Context;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int StartTraceW(out long handle, string name, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int ControlTraceW(long handle, string name, IntPtr properties, uint code);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int EnableTraceEx2(long handle, ref Guid provider, uint controlCode,
                                             byte level, ulong matchAny, ulong matchAll,
                                             uint timeout, IntPtr parameters);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern long OpenTraceW(ref EventTraceLogfile logfile);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int ProcessTrace(long[] handles, uint count, IntPtr start, IntPtr end);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int CloseTrace(long handle);
}
