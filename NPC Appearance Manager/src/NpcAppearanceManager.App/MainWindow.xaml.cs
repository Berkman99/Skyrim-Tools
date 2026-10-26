using System.ComponentModel;
using System.Windows;
using NpcAppearanceManager.App.ViewModels;

namespace NpcAppearanceManager.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        RestorePlacement();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        Loaded += async (_, _) => await vm.InitializeAsync();
        SizeChanged += (_, _) => RememberPlacement();
        LocationChanged += (_, _) => RememberPlacement();
        StateChanged += (_, _) => RememberPlacement();
    }

    /// <summary>Opens where it was last closed (if that spot is still on a screen).</summary>
    private void RestorePlacement()
    {
        var s = _vm.Settings;
        if (s.WindowWidth is > 300 && s.WindowHeight is > 200)
        {
            Width = s.WindowWidth.Value;
            Height = s.WindowHeight.Value;
        }
        if (s.WindowLeft is { } left && s.WindowTop is { } top &&
            left > SystemParameters.VirtualScreenLeft - 50 && top > SystemParameters.VirtualScreenTop - 50 &&
            left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
            top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void RememberPlacement()
    {
        if (!IsLoaded) return;
        var s = _vm.Settings;
        s.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            s.WindowLeft = Left;
            s.WindowTop = Top;
            s.WindowWidth = Width;
            s.WindowHeight = Height;
        }
        _vm.SaveSettingsSoon();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _vm.Shutdown();
        base.OnClosing(e);
    }
}
