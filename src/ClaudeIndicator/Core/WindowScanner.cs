using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeIndicator.Core;

/// <summary>Uma janela candidata a receber os indicadores.</summary>
public sealed class WindowCandidate
{
    public required IntPtr Handle { get; init; }
    public required int ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public required string Title { get; init; }
    public required GameInfo.Rect Bounds { get; init; }
    public required GameInfo.Rect Monitor { get; init; }

    /// <summary>Quanto da área do monitor esta janela cobre, de 0 a 1.</summary>
    public double Coverage => Monitor.Area > 0 ? Math.Min(1, (double)Bounds.Area / Monitor.Area) : 0;

    public bool CoversMonitor => Coverage >= 0.92;

    /// <summary>Quadros por segundo, quando a medição está de pé. Zero se não há leitura.</summary>
    public double Fps { get; set; }

    /// <summary>Uma linha descrevendo a janela, para a lista de escolha.</summary>
    public string Describe()
    {
        var partes = new List<string> { $"{Bounds.Width}×{Bounds.Height}" };
        if (CoversMonitor) partes.Add("ocupa o monitor");
        if (Fps > 0) partes.Add($"{Fps:0} fps");
        return string.Join(" · ", partes);
    }
}

/// <summary>
/// Lista as janelas abertas que fazem sentido como alvo do indicador.
///
/// Existe porque adivinhar o que é um jogo erra: em janela sem bordas o jogo se parece com
/// qualquer outra janela, e sem a medição de quadros (que exige administrador) não sobra sinal
/// forte. Escolher na mão sempre funciona, e é o caminho que o app oferece primeiro.
/// </summary>
public static class WindowScanner
{
    /// <summary>Processos do próprio sistema que nunca são o alvo.</summary>
    private static readonly HashSet<string> Ignorar = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost",
        "TextInputHost", "ApplicationFrameHost", "LockApp", "WidgetService", "Widgets",
        "PhoneExperienceHost", "ShellHost", "ClaudeIndicator"
    };

    /// <summary>
    /// Janelas visíveis com título e tamanho de gente, da que mais cobre a tela para a que menos
    /// cobre — um jogo em tela cheia ou sem bordas aparece no topo da lista.
    /// </summary>
    public static List<WindowCandidate> Scan()
    {
        var lista = new List<WindowCandidate>();
        var meu = Environment.ProcessId;

        EnumWindows((h, _) =>
        {
            try
            {
                if (!IsWindowVisible(h)) return true;
                if (GetWindow(h, GwOwner) != IntPtr.Zero) return true;  // janela de diálogo

                var ex = GetWindowLongPtrW(h, GwlExStyle).ToInt64();
                if ((ex & WsExToolWindow) != 0) return true;

                // janela de aplicativo de loja suspensa: existe, mas não desenha nada
                if (DwmGetWindowAttribute(h, DwmaCloaked, out var oculta, sizeof(int)) == 0 && oculta != 0)
                    return true;

                if (!GetWindowRect(h, out var r)) return true;
                var bounds = new GameInfo.Rect(r.Left, r.Top, r.Right, r.Bottom);
                if (bounds.Width < 400 || bounds.Height < 300) return true;

                GetWindowThreadProcessId(h, out var pid);
                if (pid == 0 || pid == meu) return true;

                string nome;
                try
                {
                    using var p = Process.GetProcessById((int)pid);
                    nome = p.ProcessName;
                }
                catch
                {
                    return true;
                }
                if (Ignorar.Contains(nome)) return true;

                var titulo = Title(h);
                if (titulo.Length == 0) return true;

                lista.Add(new WindowCandidate
                {
                    Handle = h,
                    ProcessId = (int)pid,
                    ProcessName = nome,
                    Title = titulo,
                    Bounds = bounds,
                    Monitor = MonitorBounds(h)
                });
            }
            catch
            {
                // janela fechou no meio da varredura: segue para a próxima
            }
            return true;
        }, IntPtr.Zero);

        lista.Sort((a, b) => b.Coverage.CompareTo(a.Coverage));
        return lista;
    }

    /// <summary>
    /// A janela principal deste processo: a maior visível que ele tem. Um jogo costuma ter janelas
    /// auxiliares (splash, mensagem), e a que interessa é a que ocupa a tela.
    /// </summary>
    public static WindowCandidate? MainWindowOf(int processId)
    {
        WindowCandidate? melhor = null;
        foreach (var c in Scan())
        {
            if (c.ProcessId != processId) continue;
            if (melhor == null || c.Bounds.Area > melhor.Bounds.Area) melhor = c;
        }
        return melhor;
    }

    /// <summary>A janela principal do primeiro processo com este nome que estiver rodando.</summary>
    public static WindowCandidate? MainWindowOf(string processName)
    {
        WindowCandidate? melhor = null;
        foreach (var c in Scan())
        {
            if (!string.Equals(c.ProcessName, processName, StringComparison.OrdinalIgnoreCase)) continue;
            if (melhor == null || c.Bounds.Area > melhor.Bounds.Area) melhor = c;
        }
        return melhor;
    }

    private static string Title(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString().Trim();
    }

    private static GameInfo.Rect MonitorBounds(IntPtr hwnd)
    {
        var h = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (h != IntPtr.Zero && GetMonitorInfoW(h, ref info))
            return new GameInfo.Rect(info.rcMonitor.Left, info.rcMonitor.Top,
                                     info.rcMonitor.Right, info.rcMonitor.Bottom);
        return new GameInfo.Rect(0, 0, 1920, 1080);
    }

    // ------------------------------------------------------------------

    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080;
    private const uint GwOwner = 4;
    private const uint MonitorDefaultToNearest = 2;
    private const int DwmaCloaked = 14;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);
}
