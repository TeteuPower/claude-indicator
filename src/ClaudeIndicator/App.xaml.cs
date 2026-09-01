using System;
using System.Threading;
using System.Windows;
using ClaudeIndicator.Core;

namespace ClaudeIndicator;

public partial class App : System.Windows.Application
{
    private const string ActivateSignalName = @"Global\ClaudeIndicator.Activate";

    private static Mutex? _instanceMutex;
    private static EventWaitHandle? _activateSignal;
    private AppHost? _host;

    /// <summary>
    /// Fica ouvindo o aviso de "abriram o app de novo" e traz o painel à frente. Roda numa thread
    /// de fundo porque WaitOne bloqueia; a ação volta para a thread da interface pelo Dispatcher.
    /// </summary>
    private void StartActivationListener()
    {
        try
        {
            _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateSignalName);
        }
        catch
        {
            return; // sem o sinal o app funciona igual, só não reage a uma segunda abertura
        }

        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    _activateSignal.WaitOne();
                }
                catch
                {
                    return;
                }
                Dispatcher.BeginInvoke(new Action(() => AppHost.Current?.ShowDashboard()));
            }
        })
        {
            IsBackground = true,
            Name = "ClaudeIndicator.Activate"
        };
        thread.Start();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Instância única. Abrir de novo não é erro: é alguém querendo ver o app, então a segunda
        // instância avisa a primeira para trazer o painel à frente e sai calada.
        _instanceMutex = new Mutex(true, @"Global\ClaudeIndicator.SingleInstance", out var created);
        if (!created)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ActivateSignalName, out var signal))
                {
                    signal.Set();
                    signal.Dispose();
                }
            }
            catch
            {
                // sem permissão para sinalizar: sair em silêncio ainda é melhor que uma caixa de erro
            }
            Shutdown();
            return;
        }

        StartActivationListener();

        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;

            // no arquivo vai a exceção inteira; na tela, só a mensagem — diagnóstico de verdade
            // precisa da pilha, e ninguém copia pilha de um MessageBox
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(Core.AppSettings.DataDir, "errors.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {args.Exception}\n\n");
            }
            catch { }

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
