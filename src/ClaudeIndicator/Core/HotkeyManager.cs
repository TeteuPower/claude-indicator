using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ClaudeIndicator.Core;

/// <summary>
/// Atalhos que funcionam com o jogo em primeiro plano.
///
/// Dentro de uma partida não dá para alternar até as configurações, e é justamente ali que se quer
/// tirar o indicador da frente ou mudá-lo de canto. Atalho global do Windows resolve: o sistema
/// entrega a tecla mesmo com outro programa em foco.
///
/// Uma janela invisível recebe as mensagens. Não dá para usar as janelas do indicador para isso:
/// elas aparecem e somem conforme o jogo, e o atalho precisa existir o tempo todo.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly Dictionary<int, Action> _acoes = new();
    private readonly List<string> _falhas = new();
    private HwndSource? _janela;
    private int _proximoId = 1;

    /// <summary>Combinações que o Windows recusou, normalmente por já estarem em uso.</summary>
    public IReadOnlyList<string> Failures => _falhas;

    /// <summary>Registra uma combinação. Devolve false quando outra aplicação já a tomou.</summary>
    public bool Register(Hotkey hotkey, Action acao)
    {
        if (!hotkey.IsValid) return false;

        EnsureWindow();
        if (_janela == null) return false;

        var id = _proximoId++;
        if (!RegisterHotKey(_janela.Handle, id, hotkey.NativeModifiers, hotkey.NativeKey))
        {
            _falhas.Add(hotkey.ToString());
            return false;
        }

        _acoes[id] = acao;
        return true;
    }

    /// <summary>Solta todas as combinações. Chamado antes de registrar o conjunto novo.</summary>
    public void UnregisterAll()
    {
        if (_janela != null)
        {
            foreach (var id in _acoes.Keys) UnregisterHotKey(_janela.Handle, id);
        }
        _acoes.Clear();
        _falhas.Clear();
    }

    private void EnsureWindow()
    {
        if (_janela != null) return;

        var parametros = new HwndSourceParameters("ClaudeIndicatorHotkeys")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = WsExToolWindow
        };
        _janela = new HwndSource(parametros);
        _janela.AddHook(Processar);
    }

    private IntPtr Processar(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey) return IntPtr.Zero;

        if (_acoes.TryGetValue(wParam.ToInt32(), out var acao))
        {
            handled = true;
            try
            {
                acao();
            }
            catch
            {
                // um atalho com defeito não pode derrubar a bomba de mensagens
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _janela?.Dispose();
        _janela = null;
    }

    private const int WmHotkey = 0x0312;
    private const int WsExToolWindow = 0x00000080;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
