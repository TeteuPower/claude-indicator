using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeIndicator.Core;

/// <summary>Lado da barra de tarefas onde o painel é ancorado.</summary>
public enum TaskbarAnchor
{
    /// <summary>Espaço livre à esquerda, antes do botão Iniciar.</summary>
    Left,

    /// <summary>Encostado na área de notificação, à direita.</summary>
    Right
}

/// <summary>
/// Uma barra de tarefas concreta, já resolvida para o monitor onde ela está.
///
/// O Windows desenha uma barra por monitor quando "mostrar a barra de tarefas em todas as telas"
/// está ligado: a do monitor principal é a <c>Shell_TrayWnd</c>, e cada secundária é uma
/// <c>Shell_SecondaryTrayWnd</c>. Tudo o que o painel precisa saber sobre "onde me coloco" sai
/// daqui, e não de uma consulta global — era isso que prendia os dois painéis à tela principal.
/// </summary>
public sealed class TaskbarBar
{
    public IntPtr Handle { get; init; }
    public IntPtr Monitor { get; init; }

    /// <summary>Nome do dispositivo do monitor, no formato \\.\DISPLAY1. É a chave guardada nas configurações.</summary>
    public string Device { get; init; } = "";

    public bool Primary { get; init; }

    /// <summary>Área da barra, em pixels de tela.</summary>
    public TaskbarInfo.RECT Bar { get; init; }

    /// <summary>Área do monitor inteiro, em pixels de tela.</summary>
    public TaskbarInfo.RECT MonitorRect { get; init; }

    /// <summary>Está no rodapé (o caso comum)? Se estiver na lateral, o modo barra não se aplica.</summary>
    public bool IsHorizontal => Bar.Width >= Bar.Height * 3;
}

/// <summary>Um monitor oferecido na escolha das configurações.</summary>
public sealed class MonitorOption
{
    /// <summary>Chave guardada nas configurações. Vazio significa "onde o Windows puser a barra principal".</summary>
    public string Device { get; init; } = "";

    /// <summary>Texto do botão: "Tela 2 (principal)".</summary>
    public string Label { get; init; } = "";

    /// <summary>Detalhe para o tooltip.</summary>
    public string Detail { get; init; } = "";

    /// <summary>Tem barra de tarefas agora? Sem barra, o painel não tem onde se encaixar.</summary>
    public bool HasTaskbar { get; init; }

    /// <summary>O monitor está ligado neste momento?</summary>
    public bool Present { get; init; } = true;
}

/// <summary>
/// Geometria da barra de tarefas do Windows.
///
/// O Windows 11 removeu o suporte a deskbands, então não há como embutir um controle na barra
/// pela API. O que dá para fazer — e é o que o modo "barra de tarefas" usa — é posicionar uma
/// janela sem borda sobre o espaço livre dela e acompanhar as mudanças de tamanho e posição.
/// </summary>
public static class TaskbarInfo
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr child, string? cls, string? win);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref RECT rect, IntPtr data);

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint MONITORINFOF_PRIMARY = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private static MONITORINFOEX? InfoOf(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero) return null;
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = "" };
        return GetMonitorInfo(monitor, ref info) ? info : null;
    }

    // ------------------------------------------------------------------
    // As barras
    // ------------------------------------------------------------------

    /// <summary>
    /// Todas as barras de tarefas visíveis: a principal primeiro, depois as secundárias na ordem
    /// da esquerda para a direita. Vazia se o shell não estiver respondendo.
    /// </summary>
    public static List<TaskbarBar> Bars()
    {
        var found = new List<TaskbarBar>();

        var primary = Describe(FindWindow("Shell_TrayWnd", null), true);
        if (primary != null) found.Add(primary);

        // FindWindowEx com pai nulo percorre as janelas de topo: é assim que se enumeram as
        // secundárias, uma por monitor extra.
        var h = IntPtr.Zero;
        while ((h = FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
        {
            var bar = Describe(h, false);
            if (bar != null && found.All(b => b.Monitor != bar.Monitor)) found.Add(bar);
        }

        return found
            .OrderByDescending(b => b.Primary)
            .ThenBy(b => b.MonitorRect.Left)
            .ToList();
    }

    private static TaskbarBar? Describe(IntPtr hwnd, bool primary)
    {
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd) || !GetWindowRect(hwnd, out var rect)) return null;

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = InfoOf(monitor);

        return new TaskbarBar
        {
            Handle = hwnd,
            Monitor = monitor,
            Device = info?.szDevice ?? "",
            Primary = primary || (info != null && (info.Value.dwFlags & MONITORINFOF_PRIMARY) == MONITORINFOF_PRIMARY),
            Bar = rect,
            MonitorRect = info?.rcMonitor ?? rect
        };
    }

    /// <summary>
    /// A barra do monitor escolhido. Se o nome estiver vazio, desconhecido ou o monitor tiver sido
    /// desconectado, cai na barra principal — melhor o painel aparecer no lugar errado que
    /// desaparecer sem explicação quando alguém desliga um cabo.
    /// </summary>
    public static TaskbarBar? Resolve(string? device)
    {
        var bars = Bars();
        if (bars.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(device)) return bars.FirstOrDefault(b => b.Primary) ?? bars[0];

        return bars.FirstOrDefault(b => string.Equals(b.Device, device, StringComparison.OrdinalIgnoreCase))
            ?? bars.FirstOrDefault(b => b.Primary)
            ?? bars[0];
    }

    /// <summary>Está no rodapé (o caso comum)? Se estiver na lateral, o modo barra não se aplica.</summary>
    public static bool IsHorizontal(TaskbarBar? bar) => bar == null || bar.IsHorizontal;

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);

    /// <summary>
    /// Escala do monitor desta barra (1,0 a 100%, 1,5 a 150%). Serve para converter medidas em
    /// unidades independentes de dispositivo — a "Distância da borda", por exemplo — nos pixels
    /// daquela tela, que é o que o posicionamento usa. Com telas de escalas diferentes, usar a
    /// escala da tela errada põe o painel a centenas de pixels do lugar.
    /// </summary>
    public static double ScaleOf(TaskbarBar? bar)
    {
        if (bar == null || bar.Monitor == IntPtr.Zero) return 1.0;
        try
        {
            // 0 = MDT_EFFECTIVE_DPI, a escala que o usuário escolheu para aquela tela
            if (GetDpiForMonitor(bar.Monitor, 0, out var dpi, out _) == 0 && dpi > 0) return dpi / 96.0;
        }
        catch
        {
            // shcore ausente: 100% é o palpite seguro
        }
        return 1.0;
    }

    /// <summary>
    /// Faixa livre da barra: entre a borda esquerda e o botão Iniciar, ou entre os ícones de
    /// aplicativos e a área de notificação. Devolve (início, fim) em pixels de tela.
    ///
    /// Nas barras secundárias do Windows 11 esses filhos não existem — a barra toda é XAML —, e aí
    /// o limite passa a ser a borda do monitor. Nesses casos é a "Distância da borda" que afasta o
    /// painel do relógio.
    /// </summary>
    public static (int From, int To)? FreeSpan(TaskbarBar? bar, TaskbarAnchor anchor)
    {
        if (bar == null) return null;
        var rect = bar.Bar;

        var start = ChildRect(bar.Handle, "Start");
        var apps = ChildRect(bar.Handle, "ReBarWindow32") ?? ChildRect(bar.Handle, "WorkerW");
        var notify = ChildRect(bar.Handle, "TrayNotifyWnd") ?? ChildRect(bar.Handle, "ClockButton");

        if (anchor == TaskbarAnchor.Left)
        {
            // até onde começa o primeiro elemento (Iniciar, ou os ícones se o Iniciar estiver à esquerda)
            var limit = start?.Left ?? apps?.Left ?? rect.Right;
            return limit > rect.Left ? (rect.Left, limit) : null;
        }

        var from = apps?.Right ?? rect.Left;

        // Sem área de notificação: é o caso das barras secundárias do Windows 11, onde o relógio é
        // desenhado pela camada XAML e não tem janela própria para medir. Aí se reserva uma faixa
        // proporcional à altura da barra — cabe o relógio de duas linhas — e o ajuste fino fica
        // com a "Distância da borda".
        var to = notify?.Left ?? rect.Right - rect.Height * 3;
        return to > from ? (from, to) : null;
    }

    private static RECT? ChildRect(IntPtr parent, string className)
    {
        var h = FindWindowEx(parent, IntPtr.Zero, className, null);
        if (h == IntPtr.Zero) return null;
        return GetWindowRect(h, out var r) ? r : null;
    }

    /// <summary>
    /// Uma janela em tela cheia está na frente (jogo, vídeo, apresentação) no monitor desta barra?
    /// Nesse caso o painel se esconde: ficar por cima de um jogo em tela cheia seria pior que não
    /// aparecer.
    ///
    /// A comparação é sempre com o monitor da barra recebida. Medir contra o monitor principal
    /// escondia o painel toda vez que uma janela era maximizada em outro monitor maior — ela é
    /// maior que o principal sem estar em tela cheia coisa nenhuma.
    /// </summary>
    public static bool FullscreenAppInFront(TaskbarBar? bar)
    {
        if (bar == null) return false;

        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;

        var sb = new StringBuilder(128);
        GetClassName(fg, sb, sb.Capacity);
        var cls = sb.ToString();
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false; // área de trabalho

        // outro monitor: o que acontece lá não cobre o nosso painel
        if (MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST) != bar.Monitor) return false;
        if (!GetWindowRect(fg, out var r)) return false;

        var m = bar.MonitorRect;

        // cobre o monitor inteiro, inclusive onde a barra fica: aí é tela cheia de verdade
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    // ------------------------------------------------------------------
    // Escolha do monitor, nas configurações
    // ------------------------------------------------------------------

    /// <summary>
    /// Os monitores oferecidos na escolha, da esquerda para a direita, com a opção automática na
    /// frente. Um monitor guardado nas configurações que não esteja mais ligado continua na lista,
    /// marcado como ausente, para não apagar a escolha de quem só desconectou o cabo.
    /// </summary>
    public static List<MonitorOption> MonitorOptions(params string?[] keepDevices)
    {
        var bars = Bars();
        var list = new List<MonitorOption>
        {
            new()
            {
                Device = "",
                Label = "Automático",
                Detail = "Segue a tela principal do Windows.",
                HasTaskbar = bars.Count > 0
            }
        };

        var monitors = new List<MONITORINFOEX>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = InfoOf(h);
            if (info != null) monitors.Add(info.Value);
            return true;
        }, IntPtr.Zero);

        var ordered = monitors.OrderBy(m => m.rcMonitor.Left).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var m = ordered[i];
            var primary = (m.dwFlags & MONITORINFOF_PRIMARY) == MONITORINFOF_PRIMARY;
            var hasBar = bars.Any(b => string.Equals(b.Device, m.szDevice, StringComparison.OrdinalIgnoreCase));

            var detail = $"{m.szDevice} · {m.rcMonitor.Width}×{m.rcMonitor.Height}";
            if (primary) detail += " · tela principal";
            detail += hasBar ? " · com barra de tarefas" : " · sem barra de tarefas nesta tela";

            list.Add(new MonitorOption
            {
                Device = m.szDevice,
                Label = $"Tela {i + 1}" + (primary ? " (principal)" : ""),
                Detail = detail,
                HasTaskbar = hasBar
            });
        }

        foreach (var wanted in keepDevices)
        {
            if (string.IsNullOrWhiteSpace(wanted)) continue;
            if (list.Any(o => string.Equals(o.Device, wanted, StringComparison.OrdinalIgnoreCase))) continue;

            list.Add(new MonitorOption
            {
                Device = wanted!,
                Label = "Tela desconectada",
                Detail = $"{wanted} não está ligada agora. Enquanto isso o painel usa a tela principal.",
                HasTaskbar = false,
                Present = false
            });
        }

        return list;
    }
}
