using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MinecraftHost;

/// <summary>Makes the Windows title bar match the theme (Windows 10 20H1+ / Windows 11).</summary>
static class DarkTitleBar
{
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    const int DWMWA_CAPTION_COLOR = 35;     // Windows 11 only; ignored elsewhere

    public static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;   // not shown yet; SourceInitialized applies it
        int dark = Themes.Current.Light ? 0 : 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        var bg = Themes.Get("Bg");
        int caption = bg.R | bg.G << 8 | bg.B << 16;   // COLORREF 0x00BBGGRR, the window background
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
    }
}
