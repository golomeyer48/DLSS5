using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Dlss5Optimizer.App.Services;
using Dlss5Optimizer.App.ViewModels;
using Dlss5Optimizer.Core.Models;

namespace Dlss5Optimizer.App;

public partial class App : Application
{
    private AppServices? _services;
    private bool _screenshotMode;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        // Nur für den Build: --screenshot <Ordner> [--folder <Spielordner>] speichert alle Reiter als PNG und beendet sich.
        string? screenshotDir = null;
        var folders = new List<string>();
        for (int i = 0; i + 1 < e.Args.Length; i++)
        {
            if (e.Args[i] == "--screenshot")
                screenshotDir = e.Args[++i];
            else if (e.Args[i] == "--folder")
                folders.Add(e.Args[++i]);
        }
        _screenshotMode = screenshotDir is not null;

        _services = new AppServices();
        if (_screenshotMode)
        {
            _services.Settings.ManualFolders.Clear();
            _services.Settings.ManualFolders.AddRange(folders);
            _services.SystemOverride = new SystemInfo(
                new GpuInfo("NVIDIA GeForce RTX 5070 Ti", new Version(617, 14), 16L << 30), new DisplayInfo(3840, 2160, 60), HardwareSchedulingEnabled: true);
            _services.RefreshSystem();
        }

        var window = new MainWindow();
        var vm = new MainViewModel(_services, new Dialogs(() => window));
        window.DataContext = vm;
        window.Show();
        if (_screenshotMode)
            _ = TakeScreenshotsAsync(window, vm, screenshotDir!);
        else
            _ = vm.ScanCommand.ExecuteAsync(null);
    }

    private async Task TakeScreenshotsAsync(MainWindow window, MainViewModel vm, string dir)
    {
        try
        {
            await vm.ScanCommand.ExecuteAsync(null);
            vm.SelectedGame = vm.Games.FirstOrDefault();
            Directory.CreateDirectory(dir);
            string[] names = ["1-spiele", "2-komponenten", "3-system"];
            for (int i = 0; i < names.Length; i++)
            {
                window.MainTabs.SelectedIndex = i;
                window.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                await Task.Delay(300);
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(bitmap));
                await using var fs = File.Create(Path.Combine(dir, names[i] + ".png"));
                png.Save(fs);
            }
            Shutdown(vm.Games.Count > 0 ? 0 : 2);
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "fehler.txt"), ex.ToString());
            Shutdown(1);
        }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _services?.Log("Unbehandelter Fehler: " + e.Exception);
        if (_screenshotMode)
        {
            // Im Build darf kein Dialog hängen bleiben.
            Console.Error.WriteLine(e.Exception);
            e.Handled = true;
            Shutdown(1);
            return;
        }
        MessageBox.Show(e.Exception.Message, "Unerwarteter Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
