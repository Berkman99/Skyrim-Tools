using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace NpcAppearanceManager.App;

/// <summary>Asks Windows for a dark, teal title bar to match the window.</summary>
public static class DarkTitleBar
{
    public static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        int on = 1;
        _ = DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        int caption = 0x00292613; // #132629 (PanelBrush) as 0x00BBGGRR; ignored before Windows 11
        _ = DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int)); // DWMWA_CAPTION_COLOR
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
