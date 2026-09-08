using System;
using System.Runtime.InteropServices;

namespace ClaudeIndicator.Core;

/// <summary>Borda da tela onde a barra própria mora. O rodapé fica de fora: lá já está a do Windows.</summary>
public enum DockEdge
{
    /// <summary>Encostada no topo, deitada.</summary>
    Top,

    /// <summary>Em pé, na lateral esquerda.</summary>
    Left,

    /// <summary>Em pé, na lateral direita.</summary>
    Right
}

/// <summary>
/// Registra uma janela como <b>barra de aplicativo</b> do Windows (appbar), a mesma API
/// (<c>SHAppBarMessage</c>) que os docks antigos usavam e que a própria barra de tarefas usa.
///
/// Registrar é o que diferencia uma barra de verdade de uma janela encostada na borda: o shell
/// tira aquela faixa da área útil, então janela maximizada para no limite dela e o Windows nem
/// desenha por baixo. Sem registro, a janela só flutua por cima — que é o outro modo oferecido.
///
/// Duas regras que este tipo existe para garantir:
///
/// 1. Toda medida aqui é em <b>pixels de tela</b>, não em unidades do WPF. O shell fala em pixels,
///    e converter na hora errada põe a barra a centenas de pixels do lugar em tela de 150%.
/// 2. Quem registra <b>tem</b> que remover. Uma faixa reservada por uma janela que morreu fica
///    presa até o Explorer reiniciar — por isso a remoção acontece no fechamento, na saída do app
///    e ainda no encerramento do processo.
/// </summary>
public sealed class DesktopAppBar
{
    private const uint ABM_NEW = 0x00000000;
    private const uint ABM_REMOVE = 0x00000001;
    private const uint ABM_QUERYPOS = 0x00000002;
    private const uint ABM_SETPOS = 0x00000003;
    private const uint ABM_WINDOWPOSCHANGED = 0x00000009;

    /// <summary>A faixa reservada mudou (outra barra entrou, resolução mudou): reposicionar.</summary>
    public const int ABN_POSCHANGED = 0x0000001;

    /// <summary>Um aplicativo em tela cheia entrou (wParam != 0) ou saiu (wParam == 0).</summary>
    public const int ABN_FULLSCREENAPP = 0x0000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public TaskbarInfo.RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);

    /// <summary>
    /// Mensagem que o shell usa para avisar esta barra. Registrada uma vez por processo: pedir a
    /// mesma string de novo devolve o mesmo número, então o valor é estável entre janelas.
    /// </summary>
    public static uint CallbackMessage { get; } = RegisterWindowMessage("ClaudeIndicator.AppBar.Callback");

    private IntPtr _hwnd;

    public bool Registered => _hwnd != IntPtr.Zero;

    /// <summary>Entra na lista de barras do shell. Repetir com a mesma janela é inofensivo.</summary>
    public bool Register(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || _hwnd == hwnd) return Registered;
        Remove();

        var data = new APPBARDATA
        {
            cbSize = Marshal.SizeOf<APPBARDATA>(),
            hWnd = hwnd,
            uCallbackMessage = CallbackMessage
        };

        try
        {
            if (SHAppBarMessage(ABM_NEW, ref data) == UIntPtr.Zero) return false;
        }
        catch
        {
            return false;
        }

        _hwnd = hwnd;
        return true;
    }

    /// <summary>Devolve a faixa para a área útil. Silencioso e idempotente de propósito.</summary>
    public void Remove()
    {
        if (_hwnd == IntPtr.Zero) return;

        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd };
        try
        {
            SHAppBarMessage(ABM_REMOVE, ref data);
        }
        catch
        {
            // shell fora do ar: nada a fazer além de esquecer o registro
        }
        _hwnd = IntPtr.Zero;
    }

    /// <summary>
    /// Pede a faixa e devolve onde ela de fato ficou, em pixels de tela.
    ///
    /// São duas conversas com o shell, e é isso que faz a barra conviver com a do Windows: no
    /// <c>ABM_QUERYPOS</c> ele empurra o retângulo pedido para fora do que já está reservado; aí
    /// a espessura é recolocada a partir da borda aprovada e o <c>ABM_SETPOS</c> confirma. Sem o
    /// segundo passo a faixa é aprovada mas não reservada, e as janelas passam por cima.
    /// </summary>
    public TaskbarInfo.RECT Reserve(DockEdge edge, TaskbarInfo.RECT desired)
    {
        if (_hwnd == IntPtr.Zero) return desired;

        var data = new APPBARDATA
        {
            cbSize = Marshal.SizeOf<APPBARDATA>(),
            hWnd = _hwnd,
            uEdge = EdgeCode(edge),
            rc = desired
        };

        var thickness = edge == DockEdge.Top
            ? desired.Bottom - desired.Top
            : desired.Right - desired.Left;

        try
        {
            SHAppBarMessage(ABM_QUERYPOS, ref data);

            // o shell mexe na borda de fora; a espessura volta a ser a nossa
            switch (edge)
            {
                case DockEdge.Top:
                    data.rc.Bottom = data.rc.Top + thickness;
                    break;
                case DockEdge.Left:
                    data.rc.Right = data.rc.Left + thickness;
                    break;
                default:
                    data.rc.Left = data.rc.Right - thickness;
                    break;
            }

            SHAppBarMessage(ABM_SETPOS, ref data);
        }
        catch
        {
            return desired;
        }

        return data.rc;
    }

    /// <summary>Avisa o shell que a janela se moveu — mantém a ordem entre barras coerente.</summary>
    public void NotifyMoved()
    {
        if (_hwnd == IntPtr.Zero) return;

        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd };
        try
        {
            SHAppBarMessage(ABM_WINDOWPOSCHANGED, ref data);
        }
        catch
        {
            // sem consequência: a posição já foi aplicada pelo SetWindowPos
        }
    }

    private static uint EdgeCode(DockEdge edge) => edge switch
    {
        DockEdge.Left => 0,   // ABE_LEFT
        DockEdge.Top => 1,    // ABE_TOP
        _ => 2                // ABE_RIGHT
    };

    /// <summary>
    /// A faixa que a barra quer, em pixels de tela: a borda inteira do monitor com a espessura
    /// pedida. A espessura chega em unidades de tela do WPF e é convertida pela escala <b>daquele</b>
    /// monitor — 46 unidades viram 46 px na tela de 100% e 69 px na de 150%, que é o que mantém a
    /// barra com a mesma altura aparente nas duas.
    /// </summary>
    public static TaskbarInfo.RECT EdgeRect(DockEdge edge, TaskbarInfo.MonitorGeometry monitor, double thicknessDip)
    {
        var m = monitor.Monitor;
        var px = Math.Max(8, (int)Math.Round(thicknessDip * monitor.Scale));

        return edge switch
        {
            DockEdge.Top => new TaskbarInfo.RECT { Left = m.Left, Top = m.Top, Right = m.Right, Bottom = m.Top + px },
            DockEdge.Left => new TaskbarInfo.RECT { Left = m.Left, Top = m.Top, Right = m.Left + px, Bottom = m.Bottom },
            _ => new TaskbarInfo.RECT { Left = m.Right - px, Top = m.Top, Right = m.Right, Bottom = m.Bottom }
        };
    }
}
