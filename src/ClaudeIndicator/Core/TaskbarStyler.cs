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
/// Aparência da barra de tarefas do <b>Windows</b>. São <b>duas metades</b> que só juntas funcionam
/// no Windows 11, e descobrir isso custou várias rodadas:
///
/// 1. O <b>acento</b> na janela da barra (<c>SetWindowCompositionAttribute</c> — transparente,
///    desfoque ou acrílico). Sozinho não muda nada: o XAML da barra pinta o próprio fundo por cima.
/// 2. O <b>tap</b> (<see cref="ExplorerTap"/>), que entra no Explorer e deixa esse fundo XAML
///    transparente. Sozinho, deixa a barra <b>preta</b> — sem fundo, o compositor mostra preto.
///
/// Com as duas, o fundo XAML sai da frente e o acento aparece: vidro de verdade, com o papel de
/// parede atravessando. Medido nesta máquina (build 26200), faixa da barra contra o papel logo
/// acima: nativo (34,36,36); só tap (3,4,5) preto; tap + acrílico na raiz (48,41,90) vidro; tap +
/// transparente na raiz (89,79,189), ainda mais aberto. O acento tem que ir na <b>raiz</b>
/// <c>Shell_TrayWnd</c>, não na ilha XAML filha — na filha o resultado volta a ser preto.
///
/// É o caminho do TranslucentTB, e é por isso que ele funciona onde a política de acento sozinha
/// não muda nada.
///
/// Dois cuidados que o tipo garante:
///
/// 1. <b>Toda barra, não só a principal.</b> Com "mostrar a barra em todas as telas" ligado existe
///    uma janela por monitor, e estilizar só a principal deixaria as outras destoando.
/// 2. <b>Devolver.</b> Sair do app devolve o fundo XAML original (pelo tap) e tira o acento, e
///    ainda avisa o shell para se redesenhar — a barra não pode ficar pior do que estava.
/// </summary>
public static class TaskbarStyler
{
    private static TaskbarLook _aplicado = TaskbarLook.Sistema;

    /// <summary>
    /// O tap dentro do Explorer. Sem ele, no Windows 11 a política de acento não muda nada: o XAML
    /// da barra pinta o próprio fundo por cima do efeito. O tap deixa esse fundo transparente e aí
    /// o efeito pedido à janela aparece — é a combinação que o TranslucentTB usa.
    /// </summary>
    private static ExplorerTap? _tap;

    /// <summary>Alguma coisa foi aplicada e ainda não devolvida?</summary>
    public static bool Ativo => _aplicado != TaskbarLook.Sistema;

    /// <summary>O que deu errado ao colocar o tap no Explorer, para a tela dizer. Nulo quando está tudo bem.</summary>
    public static string? ErroDoTap => _tap?.Erro;

    /// <summary>O tap está dentro do Explorer agora?</summary>
    public static bool TapInstalado => _tap?.Instalado == true;

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

        // Ordem importa e foi medida. O acento entra PRIMEIRO, na raiz de cada barra; só depois o
        // tap deixa o fundo XAML transparente. Ao contrário — XAML transparente antes do acento —
        // há um instante em que a barra não tem fundo nenhum e o compositor a mostra PRETA (medido:
        // (3,4,5) contra o (48,41,90) do vidro pronto). Começar pelo acento nunca deixa a barra sem
        // fundo: enquanto o tap não agiu, o XAML opaco cobre; quando age, o acento já está lá.
        foreach (var bar in bars)
            WindowBackdrop.Apply(bar.Handle, efeito, alfa, 0x1B, 0x1A, 0x19);

        _tap ??= new ExplorerTap();
        if (_tap.Instalado || _tap.Instalar())
        {
            // opaca: o XAML fica na cor cheia (nada do fundo aparece). Nos outros, transparente,
            // e o vidro vem do acento na janela logo abaixo.
            _tap.Aplicar(look == TaskbarLook.Opaca ? 0xFF1B1A19u : 0x00000000u);
        }

        _aplicado = look;
    }

    /// <summary>
    /// O shell recriou a barra (Explorer reiniciou, tela entrou): o gancho da barra antiga morreu
    /// com ela e a nova precisa do seu. Reengancha e reaplica.
    /// </summary>
    public static void Reaplicar(TaskbarLook look, double tint)
    {
        if (look == TaskbarLook.Sistema) return;
        _tap?.Reinstalar();
        Apply(look, tint);
    }

    /// <summary>Solta os ganchos ao sair. A DLL continua no Explorer até ele reiniciar, de propósito (ver ExplorerTap).</summary>
    public static void Encerrar()
    {
        _tap?.Dispose();
        _tap = null;
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

        // o fundo do XAML volta ao original antes do efeito da janela sair
        _tap?.Devolver();

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
