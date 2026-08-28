using System;
using System.Runtime.InteropServices;

namespace ClaudeIndicator.Core;

internal static class NativeMethods
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int newLong);

    /// <summary>
    /// Faz a janela não roubar o foco nem aparecer no Alt+Tab: o painel da barra de tarefas é
    /// um indicador, clicar nele não deve tirar o foco do que a pessoa está fazendo.
    /// </summary>
    public static void MakeNoActivate(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;
        try
        {
            var style = GetWindowLong(hWnd, GWL_EXSTYLE);
            SetWindowLong(hWnd, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        }
        catch
        {
            // sem permissão: a janela continua funcionando, só rouba o foco ao clicar
        }
    }

    /// <summary>
    /// Faz o clique atravessar a janela e chegar em quem está embaixo. É o que separa um indicador
    /// por cima do jogo de um obstáculo no meio da tela: o mouse simplesmente não a enxerga.
    /// </summary>
    public static void MakeClickThrough(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;
        try
        {
            var style = GetWindowLong(hWnd, GWL_EXSTYLE);
            SetWindowLong(hWnd, GWL_EXSTYLE,
                          style | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        }
        catch
        {
            // sem permissão: a janela fica clicável, o que atrapalharia — mas não quebra nada
        }
    }
}
