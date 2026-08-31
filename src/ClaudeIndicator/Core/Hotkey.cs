using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace ClaudeIndicator.Core;

/// <summary>
/// Uma combinação de teclas, guardada como texto legível ("Ctrl+Alt+O").
///
/// Texto e não números porque o arquivo de configuração é para ser lido por gente: um
/// "Ctrl+Alt+O" se entende de relance, um par de inteiros com máscara de modificadores não.
/// A conversão para o que o Windows espera acontece aqui, num lugar só.
/// </summary>
public readonly struct Hotkey
{
    public Hotkey(ModifierKeys modifiers, Key key)
    {
        Modifiers = modifiers;
        Key = key;
    }

    public ModifierKeys Modifiers { get; }
    public Key Key { get; }

    public bool IsValid => Key != Key.None && Modifiers != ModifierKeys.None;

    /// <summary>Modificadores no formato do RegisterHotKey.</summary>
    public uint NativeModifiers
    {
        get
        {
            uint m = 0;
            if (Modifiers.HasFlag(ModifierKeys.Alt)) m |= 0x0001;
            if (Modifiers.HasFlag(ModifierKeys.Control)) m |= 0x0002;
            if (Modifiers.HasFlag(ModifierKeys.Shift)) m |= 0x0004;
            if (Modifiers.HasFlag(ModifierKeys.Windows)) m |= 0x0008;

            // sem repetição enquanto a tecla fica presa: um atalho de alternar não deve piscar
            m |= 0x4000;
            return m;
        }
    }

    public uint NativeKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    public override string ToString()
    {
        if (!IsValid) return "";

        var partes = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) partes.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) partes.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) partes.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) partes.Add("Win");
        partes.Add(Key.ToString());
        return string.Join("+", partes);
    }

    public static Hotkey Parse(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return default;

        var mods = ModifierKeys.None;
        var tecla = Key.None;

        foreach (var bruto in texto.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var parte = bruto.Trim();
            switch (parte.ToLowerInvariant())
            {
                case "ctrl":
                case "control": mods |= ModifierKeys.Control; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "win":
                case "windows": mods |= ModifierKeys.Windows; break;
                default:
                    if (Enum.TryParse<Key>(parte, ignoreCase: true, out var k)) tecla = k;
                    break;
            }
        }

        return new Hotkey(mods, tecla);
    }

    /// <summary>
    /// A combinação que uma tecla pressionada representa, ou inválida enquanto só há modificadores
    /// pressionados. Serve para a captura na tela de configurações.
    ///
    /// Quem chama precisa resolver <see cref="Key.System"/> antes: com Alt segurado o WPF põe a
    /// tecla de verdade em SystemKey e deixa Key.System no lugar dela.
    /// </summary>
    public static Hotkey FromKeyPress(Key key, ModifierKeys modifiers) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
            or Key.System or Key.None
            ? default
            : new Hotkey(modifiers, key);
}
