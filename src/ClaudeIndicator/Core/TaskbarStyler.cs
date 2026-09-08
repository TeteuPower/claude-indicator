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
/// Aparência da barra de tarefas do <b>Windows</b>, pela política de acento do compositor
/// (<c>SetWindowCompositionAttribute</c>) — a API que ficou conhecida justamente por isso.
///
/// <b>Aviso medido:</b> no Windows 11 build 26200 desta máquina ela <b>não muda a barra</b>. O teste
/// que decidiu: com a área de trabalho à vista (janelas minimizadas), fotos da faixa da barra mais
/// 60 px de papel de parede acima, comparando o nativo com os quatro estados (tom, transparente,
/// desfoque, acrílico), tom de 0 a 65%, aplicados na <c>Shell_TrayWnd</c> <b>e</b> em cada
/// janela-filha grande dela — a ilha XAML (<c>DesktopWindowContentBridge</c>), a
/// <c>CoreWindow</c>, a <c>ReBarWindow32</c>, a lista de tarefas. Todas as fotos saíram iguais.
///
/// Antes disso eu havia concluído o contrário, comparando a <i>cor média</i> da faixa: a média
/// mudava, mas por causa do que passava atrás da barra, não do efeito. Métrica cega leva a
/// conclusão errada com toda a aparência de rigor.
///
/// Programas que conseguem hoje, como o TranslucentTB, chegam lá por outro caminho — mexendo na
/// árvore de composição da barra, não pela política de acento. O tipo fica porque a API continua
/// valendo onde funciona (Windows 10 e builds anteriores do 11) e porque é a base pronta se o
/// caminho mais profundo for implementado. A tela avisa que aqui pode não mudar nada.
///
/// Dois cuidados que o tipo garante:
///
/// 1. <b>Toda barra, não só a principal.</b> Com "mostrar a barra em todas as telas" ligado existe
///    uma janela por monitor, e estilizar só a principal deixaria as outras destoando.
/// 2. <b>Devolver.</b> Sair do app devolve a barra ao estado do sistema, e tirar o acento pode não
///    bastar: por precaução, o shell também é avisado para se redesenhar.
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
    /// Além de tirar o acento, pede ao shell que se redesenhe: mensagem de tema nas janelas da barra
    /// e a difusão de "ImmersiveColorSet", que é o que o Windows manda quando o tema muda. Por
    /// precaução: onde o acento funciona, removê-lo pode deixar a barra sem o material do sistema
    /// até algo forçar o redesenho, e o app não deve devolver a barra pior do que a pegou.
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
