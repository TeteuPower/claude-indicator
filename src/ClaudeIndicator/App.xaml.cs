using System;
using System.Threading;
using System.Windows;
using ClaudeIndicator.Core;

namespace ClaudeIndicator;

public partial class App : System.Windows.Application
{
    private static Mutex? _instanceMutex;
    private AppHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Instância única
        _instanceMutex = new Mutex(true, @"Global\ClaudeIndicator.SingleInstance", out var created);
        if (!created)
        {
            MessageBox.Show("O Claude Indicator já está em execução (veja a bandeja do sistema).",
                "Claude Indicator", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            MessageBox.Show("Ocorreu um erro inesperado:\n\n" + args.Exception.Message,
                "Claude Indicator", MessageBoxButton.OK, MessageBoxImage.Warning);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, _) => { };

        _host = new AppHost();
        _host.Start(e.Args);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
        }
        catch
        {
            // ignora
        }
        base.OnExit(e);
    }
}
