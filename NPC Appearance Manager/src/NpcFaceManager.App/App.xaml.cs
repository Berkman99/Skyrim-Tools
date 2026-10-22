using System.Windows;
using System.Windows.Threading;
using NpcFaceManager.App.ViewModels;
using NpcFaceManager.Core.Settings;

namespace NpcFaceManager.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        var window = new MainWindow(new MainViewModel(e.Args));
        MainWindow = window;
        window.Show();
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "NpcAppearanceManager.errors.log"),
                $"[{DateTime.Now:u}] {e.Exception}{System.Environment.NewLine}");
        }
        catch { }
        MessageBox.Show("Something went wrong:\n\n" + e.Exception.Message + "\n\nDetails were saved to NpcAppearanceManager.errors.log.",
            AppSettings.AppName, MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
