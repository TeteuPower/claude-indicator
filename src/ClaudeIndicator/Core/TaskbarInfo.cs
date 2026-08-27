using System;
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
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    /// <summary>Área ocupada pela barra de tarefas, em pixels de tela. Null se não achar.</summary>
    public static RECT? Bounds()
    {
        var tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return null;
        return GetWindowRect(tray, out var r) ? r : null;
    }

    /// <summary>Está no rodapé (o caso comum)? Se estiver na lateral, o modo barra não se aplica.</summary>
    public static bool IsHorizontal()
    {
        var b = Bounds();
        return b == null || b.Value.Width >= b.Value.Height * 3;
    }

    /// <summary>
    /// Faixa livre da barra: entre a borda esquerda e o botão Iniciar, ou entre os ícones de
    /// aplicativos e a área de notificação. Devolve (início, fim) em pixels de tela.
    /// </summary>
    public static (int From, int To)? FreeSpan(TaskbarAnchor anchor)
    {
        var tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero || !GetWindowRect(tray, out var bar)) return null;

        var start = ChildRect(tray, "Start");
        var apps = ChildRect(tray, "ReBarWindow32");
        var notify = ChildRect(tray, "TrayNotifyWnd");

        if (anchor == TaskbarAnchor.Left)
        {
            // até onde começa o primeiro elemento (Iniciar, ou os ícones se o Iniciar estiver à esquerda)
            var limit = start?.Left ?? apps?.Left ?? bar.Right;
            return limit > bar.Left ? (bar.Left, limit) : null;
        }

        var from = apps?.Right ?? bar.Left;
        var to = notify?.Left ?? bar.Right;
        return to > from ? (from, to) : null;
    }

    private static RECT? ChildRect(IntPtr parent, string className)
    {
        var h = FindWindowEx(parent, IntPtr.Zero, className, null);
        if (h == IntPtr.Zero) return null;
        return GetWindowRect(h, out var r) ? r : null;
    }

    /// <summary>
    /// Uma janela em tela cheia está na frente (jogo, vídeo, apresentação) no monitor da barra?
    /// Nesse caso o painel se esconde: ficar por cima de um jogo em tela cheia seria pior que não
    /// aparecer.
    ///
    /// A comparação é sempre com o monitor onde a barra de tarefas está. Medir contra o tamanho do
    /// monitor principal escondia o painel toda vez que uma janela era maximizada em outro monitor
    /// maior — ela é maior que o principal sem estar em tela cheia coisa nenhuma.
    /// </summary>
    public static bool FullscreenAppInFront()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;

        var sb = new StringBuilder(128);
        GetClassName(fg, sb, sb.Capacity);
        var cls = sb.ToString();
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd") return false; // área de trabalho

        var tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return false;

        // outro monitor: o que acontece lá não cobre o nosso painel
        var monitorDaBarra = MonitorFromWindow(tray, MONITOR_DEFAULTTONEAREST);
        if (MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST) != monitorDaBarra) return false;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitorDaBarra, ref info)) return false;
        if (!GetWindowRect(fg, out var r)) return false;

        // cobre o monitor inteiro, inclusive onde a barra fica: aí é tela cheia de verdade
        return r.Left <= info.rcMonitor.Left && r.Top <= info.rcMonitor.Top
            && r.Right >= info.rcMonitor.Right && r.Bottom >= info.rcMonitor.Bottom;
    }
}
