using System;
using System.Runtime.InteropServices;

namespace ClaudeIndicator.Core;

/// <summary>
/// Fundo fosco de janela pelo compositor do Windows: o desfoque acrílico que a barra de tarefas e
/// os menus do sistema usam.
///
/// Não dá para fazer isso pintando: o desfoque precisa do que está <b>atrás</b> da janela, e isso
/// só o compositor tem. O que o app faz é pedir o efeito e deixar a área do conteúdo transparente
/// para ele aparecer.
///
/// A pegadinha é que o fosco e a transparência por pixel do WPF são exclusivos entre si. Com
/// <c>AllowsTransparency=True</c> o WPF transforma a janela em <i>layered</i> e desenha ela inteira
/// por conta própria — aí o compositor não tem onde compor o desfoque, e o resultado é um retângulo
/// opaco. Por isso a janela da barra nasce de um jeito ou de outro conforme a preferência, e trocar
/// a preferência recria a janela: não é capricho, é a única ordem que o Windows aceita.
/// </summary>
public static class WindowBackdrop
{
    // ---- Via principal: política de acento -----------------------------------------------------
    // É a mesma que a barra de tarefas do sistema usa. Não é documentada, mas é estável desde o
    // Windows 10 e, ao contrário do caminho oficial (DWMWA_SYSTEMBACKDROP_TYPE), não impõe a
    // variante CLARA do acrílico: numa janela sem barra de título o atributo de tema escuro não
    // alcança, e vidro claro sob texto claro é texto ilegível. Testado nesta máquina.

    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
    private const int ACCENT_ENABLE_BLURBEHIND = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;

        /// <summary>Tom por cima do desfoque, em 0xAABBGGRR.</summary>
        public uint GradientColor;

        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    // ---- Via de reserva: o atributo oficial do DWM (Windows 11) -------------------------------

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_TRANSIENTWINDOW = 3;

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int Left, Right, Top, Bottom;
    }

    /// <summary>
    /// Liga o fosco na janela, <b>sem tom próprio</b>: o desfoque vem do compositor e quem tinge é
    /// a janela, pintando por cima com a cor e a opacidade do app.
    ///
    /// A divisão é assim porque o acrílico do Windows 11 ignora a cor que se pede aqui — testado:
    /// pedir tom escuro em 25% e em 55% dava o mesmo resultado, com o papel de parede dominando.
    /// Como o efeito respeita o alfa do que a janela desenha, o tom feito no WPF funciona, é exato,
    /// e usa a mesma régua do modo sem fosco: um controle de fundo, dois modos.
    ///
    /// Devolve false quando o Windows não oferece efeito nenhum — aí quem chamou precisa voltar
    /// para um fundo opaco, senão a janela fica preta: sem o compositor pintando atrás,
    /// "transparente" não tem nada para revelar.
    /// </summary>
    /// <summary>
    /// Fosco com o tom da barra: acrílico quando o Windows tem, desfoque clássico como reserva.
    /// O <paramref name="tom"/> é o alfa da cor do app sobre o vidro — ele vai no PEDIDO, e não
    /// pintado pela janela: janela não-layered não tem alfa próprio para compor sobre o efeito, e
    /// pintar por cima resulta em preto.
    /// </summary>
    public static bool ApplyFrosted(IntPtr hwnd, byte tom) =>
        Apply(hwnd, Efeito.Acrilico, tom, 0x1B, 0x1A, 0x19)
        || Apply(hwnd, Efeito.Desfoque, tom, 0x1B, 0x1A, 0x19);

    /// <summary>Efeitos que o compositor oferece, do mais leve ao mais forte.</summary>
    public enum Efeito
    {
        /// <summary>Sem efeito: a janela volta a ser o que o Windows desenha por padrão.</summary>
        Nenhum = 0,

        /// <summary>Só o tom, sem desfoque.</summary>
        Tom = 1,

        /// <summary>Tom com o fundo revelado, sem desfoque.</summary>
        Transparente = 2,

        /// <summary>Desfoque clássico, que aceita o tom.</summary>
        Desfoque = 3,

        /// <summary>Acrílico do Windows 10/11.</summary>
        Acrilico = 4
    }

    /// <summary>
    /// Pede um efeito ao compositor, com o tom em cima dele. Devolve false quando o Windows não
    /// aceita o pedido.
    /// </summary>
    public static bool Apply(IntPtr hwnd, Efeito efeito, byte a, byte r, byte g, byte b)
    {
        if (hwnd == IntPtr.Zero) return false;

        // 0xAABBGGRR — a ordem dos canais aqui é a do Win32, não a do WPF
        var cor = (uint)(a << 24 | b << 16 | g << 8 | r);
        return TrySetAccent(hwnd, (int)efeito, cor);
    }

    private static bool TrySetAccent(IntPtr hwnd, int estado, uint cor)
    {
        var politica = new AccentPolicy
        {
            AccentState = estado,
            AccentFlags = 2,          // aplica o efeito nas bordas todas
            GradientColor = cor
        };

        var tamanho = Marshal.SizeOf<AccentPolicy>();
        var buffer = Marshal.AllocHGlobal(tamanho);
        try
        {
            Marshal.StructureToPtr(politica, buffer, false);
            var dados = new WindowCompositionAttributeData
            {
                Attribute = WCA_ACCENT_POLICY,
                Data = buffer,
                SizeOfData = tamanho
            };
            return SetWindowCompositionAttribute(hwnd, ref dados) != 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TrySystemBackdrop(IntPtr hwnd)
    {
        try
        {
            // o efeito só alcança a área do conteúdo se a moldura for estendida sobre ela
            var margens = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref margens);

            var escuro = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref escuro, sizeof(int));

            var tipo = DWMSBT_TRANSIENTWINDOW;
            return DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref tipo, sizeof(int)) == 0;
        }
        catch
        {
            return false;
        }
    }
}
