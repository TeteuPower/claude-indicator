using System;
using System.Reflection;

namespace ClaudeIndicator.Core;

/// <summary>Identificação da build, para a interface exibir qual versão está rodando.</summary>
public static class AppInfo
{
    public const string Name = "Claude Indicator";

    private static string? _version;

    /// <summary>Versão no formato 1.2.0, lida dos metadados do próprio executável.</summary>
    public static string Version
    {
        get
        {
            if (_version != null) return _version;

            var asm = Assembly.GetExecutingAssembly();

            // InformationalVersion pode vir com sufixo de build (+sha): fica só a parte numérica
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                var plus = info.IndexOf('+');
                _version = plus > 0 ? info.Substring(0, plus) : info;
                return _version;
            }

            var v = asm.GetName().Version;
            _version = v == null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}";
            return _version;
        }
    }

    public static string NameWithVersion => $"{Name} {Version}";
}
