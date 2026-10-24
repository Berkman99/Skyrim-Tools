using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using NpcFaceManager.App.ViewModels;

namespace NpcFaceManager.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        RestorePlacement();
        SourceInitialized += (_, _) => UseDarkTitleBar();
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

    /// <summary>Asks Windows for a dark, teal title bar to match the window.</summary>
    private void UseDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int on = 1;
        _ = DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        int caption = 0x00292613; // #132629 (PanelBrush) as 0x00BBGGRR; ignored before Windows 11
        _ = DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int)); // DWMWA_CAPTION_COLOR
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnClosing(CancelEventArgs e)
    {
        _vm.Shutdown();
        base.OnClosing(e);
    }
}
