using System;
using System.Runtime.InteropServices;

namespace ClaudeIndicator.Core;

/// <summary>
/// Avisa quando outra janela vai para a frente.
///
/// Existe por causa da ordem-Z: a barra de tarefas também é "sempre por cima", e toda ativação de
/// janela — clicar na barra, fechar um diálogo, alternar de aplicativo — refaz essa ordem e pode
/// deixar os painéis atrás dela. Descobrir isso por varredura significa esperar o próximo tique;
/// o Windows avisa de graça, e o aviso chega no instante em que acontece.
///
/// O gancho é fora de processo (<c>WINEVENT_OUTOFCONTEXT</c>), então a chamada chega pela fila de
/// mensagens desta thread — é seguro mexer na interface direto do callback, sem despachar.
/// </summary>
public static class ForegroundWatcher
{
    /// <summary>Alguma janela acabou de ir para a frente.</summary>
    public static event Action? Changed;

    private static IntPtr _hook;
    private static WinEventProc? _callback;   // precisa viver enquanto o gancho existir

    public static void Start()
    {
        if (_hook != IntPtr.Zero) return;

        _callback = OnWinEvent;
        _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground,
                                IntPtr.Zero, _callback, 0, 0,
                                WineventOutofcontext | WineventSkipown);
    }

    public static void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
        _callback = null;
    }

    private static void OnWinEvent(IntPtr hook, uint evento, IntPtr hwnd,
                                   int idObject, int idChild, uint thread, uint tempo)
    {
        try
        {
            Changed?.Invoke();
        }
        catch
        {
            // um assinante com defeito não pode derrubar o gancho do sistema
        }
    }

    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutofcontext = 0x0000;
    private const uint WineventSkipown = 0x0001;

    private delegate void WinEventProc(IntPtr hook, uint evento, IntPtr hwnd,
                                       int idObject, int idChild, uint thread, uint tempo);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module,
                                                 WinEventProc callback, uint process,
                                                 uint thread, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);
}
