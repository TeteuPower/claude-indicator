using System;
using Microsoft.Win32;

namespace ClaudeIndicator.Core;

/// <summary>Liga/desliga a inicialização automática pelo registro do usuário (não precisa de admin).</summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClaudeIndicator";

    public static string ExecutablePath =>
        Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (key == null) return false;

            if (enabled)
                key.SetValue(ValueName, $"\"{ExecutablePath}\" --minimized", RegistryValueKind.String);
            else if (key.GetValue(ValueName) != null)
                key.DeleteValue(ValueName, false);

            return true;
        }
        catch
        {
            return false;
        }
    }
}
