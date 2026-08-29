using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ClaudeIndicator.Core;

/// <summary>
/// O tema claro/escuro do Windows, lido e trocado direto.
///
/// A página de Personalização › Cores não faz mais que isto: escreve duas chaves em
/// HKCU e avisa o sistema. São duas porque o Windows separa o que é aplicativo do que é sistema —
/// a barra de tarefas e o Iniciar seguem a segunda. A caixa "Escolher seu modo" muda as duas
/// juntas, e é esse comportamento que o botão reproduz.
/// </summary>
public static class WindowsTheme
{
    private const string Chave = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string Aplicativos = "AppsUseLightTheme";
    private const string Sistema = "SystemUsesLightTheme";

    /// <summary>
    /// O tema atual é o claro? Vale o valor dos aplicativos: é ele que existe em toda instalação
    /// (o do sistema só aparece a partir do Windows 10 1903).
    /// </summary>
    public static bool IsLight()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Chave);
            return k?.GetValue(Aplicativos) is int v ? v != 0 : false;
        }
        catch
        {
            return false; // sem leitura, assume escuro, que é o padrão do Windows 11
        }
    }

    /// <summary>Troca para o outro tema e devolve o que ficou valendo.</summary>
    public static bool Toggle()
    {
        var alvo = !IsLight();
        Set(alvo);
        return alvo;
    }

    public static void Set(bool light)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(Chave, writable: true);
            if (k == null) return;

            var v = light ? 1 : 0;
            k.SetValue(Aplicativos, v, RegistryValueKind.DWord);
            k.SetValue(Sistema, v, RegistryValueKind.DWord);
        }
        catch
        {
            return; // política de grupo pode bloquear a chave; sem aviso não há o que fazer
        }

        Anunciar();
    }

    /// <summary>
    /// Avisa as janelas abertas que a paleta mudou. A barra de tarefas percebe a chave sozinha,
    /// mas os aplicativos já abertos só repintam com este aviso — sem ele, metade da tela troca
    /// de tema e a outra metade fica como estava até ser reiniciada.
    /// </summary>
    private static void Anunciar()
    {
        try
        {
            SendMessageTimeout(HwndBroadcast, WmSettingChange, IntPtr.Zero, "ImmersiveColorSet",
                               SmtoAbortIfHung, 200, out _);
        }
        catch
        {
            // o aviso é cortesia: a chave já está gravada e o tema muda de qualquer jeito
        }
    }

    private static readonly IntPtr HwndBroadcast = new(0xFFFF);
    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam,
                                                    string lParam, uint flags, uint timeout,
                                                    out IntPtr result);
}
