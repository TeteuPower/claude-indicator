using System;
using System.Runtime.InteropServices;

namespace ClaudeIndicator.Core;

/// <summary>Aparência pedida para a barra de tarefas do Windows.</summary>
public enum TaskbarLook
{
    /// <summary>Não mexe: a barra fica como o Windows a desenha.</summary>
    Sistema,

    /// <summary>Cor cheia, sem nada do fundo aparecendo.</summary>
    Opaca,

    /// <summary>O fundo aparece nítido, com um tom por cima.</summary>
    Transparente,

    /// <summary>Desfoque clássico, com tom.</summary>
    Desfocada,

    /// <summary>Acrílico: desfoque com granulado, o vidro do Windows 11.</summary>
    Fosca
}

/// <summary>
/// Aparência da barra de tarefas do <b>Windows</b>, pela mesma API de composição que o app usa na
/// barra própria — é o que o TranslucentTB faz, e é o que permite este app substituí-lo em vez de
/// os dois brigarem pelo mesmo efeito.
///
/// Três cuidados que o tipo existe para garantir:
///
/// 1. <b>Toda barra, não só a principal.</b> Com "mostrar a barra em todas as telas" ligado existe
///    uma janela por monitor, e estilizar só a principal deixa as outras destoando.
/// 2. <b>Reaplicar.</b> O Explorer recria as janelas da barra ao reiniciar (e ele reinicia sozinho
///    mais do que se imagina), e o efeito não sobrevive à janela antiga. Sem reaplicar, a barra
///    volta ao normal e parece que o app parou de funcionar.
/// 3. <b>Devolver.</b> Sair do app tem que devolver a barra ao estado do sistema — e tirar o efeito
///    <b>não é suficiente</b>: medido, a barra fica escura e a translucidez nativa do Windows 11 não
///    volta sozinha. Precisa de um empurrão para o shell se redesenhar.
/// </summary>
public static class TaskbarStyler
{
    private static TaskbarLook _aplicado = TaskbarLook.Sistema;

    /// <summary>Alguma coisa foi aplicada e ainda não devolvida?</summary>
    public static bool Ativo => _aplicado != TaskbarLook.Sistema;

    /// <summary>
    /// Aplica a aparência em todas as barras. <paramref name="tint"/> é a opacidade do tom da cor
    /// do app sobre o efeito, de 0 a 1.
    /// </summary>
    public static void Apply(TaskbarLook look, double tint)
    {
        var bars = TaskbarInfo.Bars();
        if (bars.Count == 0) return;

        if (look == TaskbarLook.Sistema)
        {
            Restore();
            return;
        }

        var efeito = look switch
        {
            TaskbarLook.Opaca => WindowBackdrop.Efeito.Tom,
            TaskbarLook.Transparente => WindowBackdrop.Efeito.Transparente,
            TaskbarLook.Desfocada => WindowBackdrop.Efeito.Desfoque,
            _ => WindowBackdrop.Efeito.Acrilico
        };

        // Opaca ignora o controle de tom: "opaca" com tom pela metade não seria opaca. Nos outros
        // o tom é o que separa "dá para ler os ícones" de "vidro lavado".
        var alfa = look == TaskbarLook.Opaca
            ? (byte)255
            : (byte)Math.Clamp(Math.Round(tint * 255), 0, 255);

        foreach (var bar in bars)
            WindowBackdrop.Apply(bar.Handle, efeito, alfa, 0x1B, 0x1A, 0x19);

        _aplicado = look;
    }

    /// <summary>
    /// Devolve as barras ao desenho do sistema.
    ///
    /// Tirar o acento sozinho não devolve nada: medindo a cor média da faixa, o nativo é #35303F, com
    /// desfoque vira #323332, e ao remover o acento fica #313331 — escuro, sem a translucidez do
    /// Windows. O que traz de volta é avisar o shell para se redesenhar: mensagem de tema na própria
    /// barra e, principalmente, a difusão de "ImmersiveColorSet", que devolveu exatamente o #35303F
    /// nativo. Sem isso, quem desligasse a opção ficaria com a barra pior do que antes de instalar o
    /// app — e sem saber por quê.
    /// </summary>
    public static void Restore()
    {
        var bars = TaskbarInfo.Bars();

        foreach (var bar in bars)
            WindowBackdrop.Apply(bar.Handle, WindowBackdrop.Efeito.Nenhum, 0, 0, 0, 0);

        try
        {
            foreach (var bar in bars)
            {
                SendMessageTimeout(bar.Handle, WM_THEMECHANGED, IntPtr.Zero, null, SMTO_ABORTIFHUNG, 500, out _);
                SendMessageTimeout(bar.Handle, WM_DWMCOMPOSITIONCHANGED, IntPtr.Zero, null, SMTO_ABORTIFHUNG, 500, out _);
            }

            SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, "ImmersiveColorSet",
                SMTO_ABORTIFHUNG, 1000, out _);
        }
        catch
        {
            // sem o empurrão a barra fica escura até o próximo reinício do Explorer, mas o app não
            // tem o que fazer a respeito
        }

        _aplicado = TaskbarLook.Sistema;
    }

    private const int WM_SETTINGCHANGE = 0x001A;
    private const int WM_THEMECHANGED = 0x031A;
    private const int WM_DWMCOMPOSITIONCHANGED = 0x031E;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, int msg, IntPtr wParam, string? lParam,
        uint flags, uint timeout, out IntPtr result);
}
