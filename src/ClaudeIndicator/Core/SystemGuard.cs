using System;
using Microsoft.Win32;

namespace ClaudeIndicator.Core;

/// <summary>
/// Estado das proteções do Windows que decidem se um driver de monitoramento consegue carregar.
///
/// Com a Integridade de Memória ligada, o Windows aplica a lista de drivers vulneráveis da
/// Microsoft. O WinRing0 — usado por praticamente todo utilitário que lê temperatura de CPU,
/// inclusive o que a nossa biblioteca extrai — está nessa lista, porque dá acesso direto a
/// memória física e portas de E/S. Nesse cenário nem executar como administrador resolve: o
/// carregamento é barrado antes disso.
/// </summary>
public static class SystemGuard
{
    /// <summary>Integridade de Memória (HVCI) ligada?</summary>
    public static bool MemoryIntegrityEnabled
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity");
                return key?.GetValue("Enabled") is int v && v == 1;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Os sensores profundos da CPU (temperatura e watts) têm chance de funcionar?
    /// Precisa de elevação e da Integridade de Memória desligada.
    /// </summary>
    public static bool CanReadCpuSensors => HardwareMonitor.IsElevated && !MemoryIntegrityEnabled;

    /// <summary>Por que não dá, quando não dá.</summary>
    public static string? CpuSensorsBlockedReason
    {
        get
        {
            if (MemoryIntegrityEnabled)
                return "A Integridade de Memória do Windows está ligada e bloqueia o driver que lê "
                       + "esses sensores — nem como administrador ele carrega. Desligá-la reduz a "
                       + "proteção do sistema contra ataques que abusam justamente desse tipo de driver.";

            if (!HardwareMonitor.IsElevated)
                return "Temperatura e watts da CPU vêm de registradores do processador, alcançáveis "
                       + "só por um driver de kernel: é preciso executar como administrador.";

            return null;
        }
    }
}
