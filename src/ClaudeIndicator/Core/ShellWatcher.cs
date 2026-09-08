using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ClaudeIndicator.Core;

/// <summary>
/// Avisa quando o shell mexeu nas janelas da barra de tarefas — reinício do Explorer, tela que
/// entra ou sai, compositor reiniciado, tema trocado.
///
/// Existe para o app <b>não ficar perguntando</b>. A aparência da barra do Windows precisa ser
/// reaplicada quando o Explorer recria as janelas dela, e a primeira versão descobria isso com um
/// relógio de três segundos. Custo real era irrisório — achar três janelas e mandar um atributo —,
/// mas perguntar de novo a cada três segundos por algo que o Windows <b>avisa</b> é desperdício de
/// princípio: o `TaskbarCreated` é uma mensagem de difusão feita exatamente para isto.
///
/// A janela daqui é de topo, e não "só para mensagens": mensagem de difusão não chega em janela
/// filha de <c>HWND_MESSAGE</c>. Ela tem tamanho zero, é de ferramenta e nunca aparece.
/// </summary>
public sealed class ShellWatcher : IDisposable
{
    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int WM_THEMECHANGED = 0x031A;
    private const int WM_DWMCOMPOSITIONCHANGED = 0x031E;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);

    /// <summary>Difusão do shell quando a barra de tarefas é (re)criada.</summary>
    private static readonly uint TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    private HwndSource? _janela;

    /// <summary>Algo mudou no shell: hora de reaplicar o que o app tinha aplicado.</summary>
    public event Action? Changed;

    public ShellWatcher()
    {
        try
        {
            _janela = new HwndSource(new HwndSourceParameters("ClaudeIndicator.ShellWatcher")
            {
                Width = 0,
                Height = 0,
                PositionX = -10000,
                PositionY = -10000,
                WindowStyle = 0,                      // sem WS_VISIBLE: nasce e fica escondida
                ExtendedWindowStyle = 0x00000080      // WS_EX_TOOLWINDOW: fora do Alt+Tab
            });
            _janela.AddHook(OnMessage);
        }
        catch
        {
            // sem a janela o app perde o aviso, não a função: a reaplicação de segurança do ciclo
            // de consulta continua valendo
            _janela = null;
        }
    }

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == TaskbarCreated
            || msg == WM_DISPLAYCHANGE
            || msg == WM_SETTINGCHANGE
            || msg == WM_THEMECHANGED
            || msg == WM_DWMCOMPOSITIONCHANGED)
        {
            Changed?.Invoke();
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        try
        {
            _janela?.RemoveHook(OnMessage);
            _janela?.Dispose();
        }
        catch
        {
            // encerrando: nada a fazer
        }
        _janela = null;
    }
}
