using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeIndicator.Core;

/// <summary>Como a janela do jogo está ocupando a tela.</summary>
public enum GameWindowMode
{
    /// <summary>Janela comum, menor que o monitor.</summary>
    Windowed,

    /// <summary>Ocupa o monitor inteiro: tela cheia exclusiva ou janela sem bordas.</summary>
    Fullscreen
}

/// <summary>O jogo em foco e o que se sabe dele.</summary>
public sealed class GameInfo
{
    public required int ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public required string Title { get; init; }
    public required IntPtr Window { get; init; }

    /// <summary>Retângulo da janela, em pixels de tela.</summary>
    public required Rect Bounds { get; init; }

    /// <summary>Retângulo do monitor onde a janela está.</summary>
    public required Rect Monitor { get; init; }

    public required GameWindowMode Mode { get; init; }

    /// <summary>Ritmo de quadros medido, quando a medição está de pé.</summary>
    public FrameStats Frames { get; init; } = FrameStats.None;

    /// <summary>Retângulo em pixels de tela, sem depender de WPF nem de WinForms.</summary>
    public readonly record struct Rect(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public long Area => (long)Math.Max(0, Width) * Math.Max(0, Height);
    }
}

/// <summary>
/// Descobre se o que está na frente é um jogo.
///
/// A prova forte vem do próprio medidor de quadros: um processo que apresenta dezenas de quadros
/// por segundo, em janela do tamanho do monitor e em primeiro plano, está rodando um jogo. Isso
/// dispensa lista de executáveis conhecidos — que envelhece mal e nunca cobre tudo.
///
/// Sem a medição (que exige administrador) sobra a geometria: janela em primeiro plano cobrindo o
/// monitor, de um processo que não é da lista de exceções. Reconhece menos, e por isso a lista de
/// exceções existe: reprodutor de vídeo em tela cheia e navegador não são jogo.
/// </summary>
public sealed class GameDetector
{
    /// <summary>Abaixo disso não é jogo rodando: é janela redesenhando de vez em quando.</summary>
    private const double MinimumFps = 10;

    /// <summary>Quanto da área do monitor a janela precisa cobrir para contar como tela cheia.</summary>
    private const double FullscreenCoverage = 0.92;

    /// <summary>
    /// Processos que apresentam quadros o tempo todo e não são jogo. Sem isto o navegador rolando
    /// uma página, o Wallpaper Engine e o próprio compositor do Windows virariam "jogo".
    /// </summary>
    private static readonly HashSet<string> NaoSaoJogo = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost",
        "TextInputHost", "ApplicationFrameHost", "SystemSettings", "LockApp", "WidgetService",
        "Widgets", "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "iexplore",
        "Code", "devenv", "rider64", "idea64", "pycharm64", "sublime_text", "notepad",
        "obs64", "obs32", "wallpaper32", "wallpaper64", "Discord", "Spotify", "vlc", "mpc-hc64",
        "ClaudeIndicator", "Taskmgr", "mmc", "WindowsTerminal", "powershell", "pwsh", "cmd",
        "conhost", "ShellHost", "PhoneExperienceHost", "msteams", "Teams", "slack", "zoom"
    };

    private readonly FrameRateMonitor _frames;

    public GameDetector(FrameRateMonitor frames) => _frames = frames;

    /// <summary>
    /// O jogo em primeiro plano agora, ou null. Só devolve algo enquanto o jogo está em foco: um
    /// indicador por cima de um jogo que o usuário deixou para trás só atrapalharia.
    /// </summary>
    public GameInfo? Detect()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;

        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == Environment.ProcessId) return null;

        var nome = ProcessName((int)pid);
        if (nome == null || NaoSaoJogo.Contains(nome)) return null;

        if (!GetWindowRect(hwnd, out var r)) return null;
        var bounds = new GameInfo.Rect(r.Left, r.Top, r.Right, r.Bottom);
        if (bounds.Width < 320 || bounds.Height < 240) return null;

        var monitor = MonitorBounds(hwnd);
        var modo = bounds.Area >= monitor.Area * FullscreenCoverage
            ? GameWindowMode.Fullscreen
            : GameWindowMode.Windowed;

        var frames = _frames.StatsFor((int)pid);

        // Com a medição de pé, ela decide: quem não apresenta quadros não é jogo, mesmo em tela
        // cheia. Sem ela, a geometria decide sozinha e só a tela cheia conta.
        if (_frames.Running)
        {
            if (!frames.HasValue || frames.Fps < MinimumFps) return null;
        }
        else if (modo != GameWindowMode.Fullscreen)
        {
            return null;
        }

        return new GameInfo
        {
            ProcessId = (int)pid,
            ProcessName = nome,
            Title = WindowTitle(hwnd),
            Window = hwnd,
            Bounds = bounds,
            Monitor = monitor,
            Mode = modo,
            Frames = frames
        };
    }

    private static string? ProcessName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch
        {
            return null; // processo morreu entre uma chamada e outra
        }
    }

    private static string WindowTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString();
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

    private const uint MonitorDefaultToNearest = 2;

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
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);
}
