using System;

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
/// 3. <b>Devolver.</b> Sair do app tem que devolver a barra ao estado do sistema. Efeito deixado
///    para trás por um programa que já morreu só sai reiniciando o Explorer — e a culpa fica com
///    o Windows, não com quem deixou.
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

    /// <summary>Devolve as barras ao desenho do sistema.</summary>
    public static void Restore()
    {
        foreach (var bar in TaskbarInfo.Bars())
            WindowBackdrop.Apply(bar.Handle, WindowBackdrop.Efeito.Nenhum, 0, 0, 0, 0);

        _aplicado = TaskbarLook.Sistema;
    }
}
