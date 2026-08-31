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
        "conhost", "ShellHost", "PhoneExperienceHost", "msteams", "Teams", "slack", "zoom",
        "EXCEL", "WINWORD", "POWERPNT", "OUTLOOK", "ONENOTE", "MSACCESS", "MSPUB", "VISIO",
        "acrobat", "AcroRd32", "SumatraPDF", "Photoshop", "Illustrator"
    };

    private readonly FrameRateMonitor _frames;

    public GameDetector(FrameRateMonitor frames) => _frames = frames;

    /// <summary>
    /// Processo escolhido à mão. Com ele definido, adivinhação nenhuma acontece: o indicador vai
    /// para a janela desse processo e ponto. É o modo padrão, porque adivinhar erra — em janela
    /// sem bordas um jogo é indistinguível de qualquer outra janela, e sem a medição de quadros,
    /// que exige administrador, não sobra sinal forte para separar um do outro.
    /// </summary>
    public string? TargetProcess { get; set; }

    /// <summary>
    /// Mostrar mesmo quando o jogo não está em primeiro plano.
    ///
    /// Vale nos dois modos. Na adivinhação isso exige lembrar qual era o jogo: sem foco não há o
    /// que olhar em primeiro plano, e sem memória a opção simplesmente não faria efeito — que foi
    /// exatamente como ela nasceu, funcionando só para quem escolhia o processo à mão.
    /// </summary>
    public bool ShowWithoutFocus { get; set; }

    /// <summary>
    /// Exceções escolhidas pelo usuário, somadas à lista embutida. Só valem para a adivinhação:
    /// um processo apontado à mão é uma decisão explícita, e vetá-la seria desobedecer.
    /// </summary>
    public HashSet<string> Excluded { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Por que o alvo escolhido não está sendo mostrado agora, quando não está.</summary>
    public string? TargetStatus { get; private set; }

    /// <summary>
    /// O alvo escolhido, se ele estiver aberto. Sem processo com esse nome, ou com o jogo atrás de
    /// outra janela, devolve null e explica o motivo em <see cref="TargetStatus"/>.
    /// </summary>
    private GameInfo? DetectTarget(string alvo)
    {
        var janela = WindowScanner.MainWindowOf(alvo);
        if (janela == null)
        {
            TargetStatus = $"{alvo} não está aberto";
            return null;
        }

        if (!ShowWithoutFocus)
        {
            var frente = GetForegroundWindow();
            GetWindowThreadProcessId(frente, out var pidFrente);
            if (pidFrente != janela.ProcessId)
            {
                TargetStatus = $"{alvo} está aberto, mas não está em primeiro plano";
                return null;
            }
        }

        TargetStatus = null;
        return new GameInfo
        {
            ProcessId = janela.ProcessId,
            ProcessName = janela.ProcessName,
            Title = janela.Title,
            Window = janela.Handle,
            Bounds = janela.Bounds,
            Monitor = janela.Monitor,
            Mode = janela.CoversMonitor ? GameWindowMode.Fullscreen : GameWindowMode.Windowed,
            Frames = _frames.StatsFor(janela.ProcessId)
        };
    }

    /// <summary>
    /// O jogo que deve receber o indicador agora, ou null.
    ///
    /// Três caminhos, nesta ordem: o processo escolhido à mão; o que está em primeiro plano; e,
    /// com "mostrar sem foco" ligado, o último jogo reconhecido, enquanto a janela dele existir.
    /// </summary>
    public GameInfo? Detect()
    {
        if (!string.IsNullOrWhiteSpace(TargetProcess)) return DetectTarget(TargetProcess!);

        TargetStatus = null;

        var naFrente = DetectForeground();
        if (naFrente != null)
        {
            _ultimoJogo = naFrente.ProcessId;
            return naFrente;
        }

        return ShowWithoutFocus ? DetectUltimo() : null;
    }

    /// <summary>O último jogo reconhecido, se a janela dele ainda estiver aberta.</summary>
    private GameInfo? DetectUltimo()
    {
        if (_ultimoJogo == 0) return null;

        var janela = WindowScanner.MainWindowOf(_ultimoJogo);
        if (janela == null)
        {
            _ultimoJogo = 0;   // fechou: esquece, senão o bloco ficaria preso a um fantasma
            return null;
        }

        return new GameInfo
        {
            ProcessId = janela.ProcessId,
            ProcessName = janela.ProcessName,
            Title = janela.Title,
            Window = janela.Handle,
            Bounds = janela.Bounds,
            Monitor = janela.Monitor,
            Mode = janela.CoversMonitor ? GameWindowMode.Fullscreen : GameWindowMode.Windowed,
            Frames = _frames.StatsFor(janela.ProcessId)
        };
    }

    private int _ultimoJogo;

    /// <summary>O que está em primeiro plano, se parecer um jogo.</summary>
    private GameInfo? DetectForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;

        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == Environment.ProcessId) return null;

        var nome = ProcessName((int)pid);
        if (nome == null || NaoSaoJogo.Contains(nome) || Excluded.Contains(nome)) return null;

        if (!GetWindowRect(hwnd, out var r)) return null;
        var bounds = new GameInfo.Rect(r.Left, r.Top, r.Right, r.Bottom);
        if (bounds.Width < 320 || bounds.Height < 240) return null;

        var monitor = MonitorBounds(hwnd);
        var modo = bounds.Area >= monitor.Area * FullscreenCoverage
            ? GameWindowMode.Fullscreen
            : GameWindowMode.Windowed;

        var frames = _frames.StatsFor((int)pid);

        // A medição é um sinal POSITIVO, nunca um veto. A versão anterior exigia quadros quando a
        // medição estava de pé, e com isso um jogo em Vulkan ou OpenGL — que não passa pelos
        // provedores DXGI e D3D9 — era ativamente recusado, enquanto sem medição ele apareceria.
        // Perder o FPS de um jogo é aceitável; esconder o indicador por causa disso, não.
        var apresentando = frames.HasValue && frames.Fps >= MinimumFps;
        if (!apresentando && modo != GameWindowMode.Fullscreen) return null;

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
