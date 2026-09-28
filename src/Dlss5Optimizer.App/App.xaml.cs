using System.Windows;
using System.Windows.Threading;
using Dlss5Optimizer.App.Services;
using Dlss5Optimizer.App.ViewModels;

namespace Dlss5Optimizer.App;

public partial class App : Application
{
    private AppServices? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        _services = new AppServices();
        var window = new MainWindow();
        var vm = new MainViewModel(_services, new Dialogs(() => window));
        window.DataContext = vm;
        window.Show();
        _ = vm.ScanCommand.ExecuteAsync(null);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _services?.Log("Unbehandelter Fehler: " + e.Exception);
        MessageBox.Show(e.Exception.Message, "Unerwarteter Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
