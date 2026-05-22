using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace ImageViewerAutoscale;

public static class SystemThemeService
{
    private const int Windows11Build = 22000;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20h1 = 19;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    public static bool IsSystemDarkMode()
    {
        var value = Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme",
            1);

        return value is int intValue && intValue == 0;
    }

    public static void ApplyWindowFrame(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        if (Environment.OSVersion.Version.Build >= Windows11Build)
        {
            var caption = Rgb(32, 32, 32);
            var text = Rgb(255, 255, 255);
            var border = Rgb(64, 64, 64);

            _ = DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref caption, sizeof(int));
            _ = DwmSetWindowAttribute(handle, DwmwaTextColor, ref text, sizeof(int));
            _ = DwmSetWindowAttribute(handle, DwmwaBorderColor, ref border, sizeof(int));
            return;
        }

        // Windows 10's dark caption flag can paint only the title text background on
        // some builds, leaving the artifact seen at startup. Keep the native frame.
    }

    private static int Rgb(byte red, byte green, byte blue)
    {
        return red | (green << 8) | (blue << 16);
    }

    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static extern int DwmSetWindowAttributeInt(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);

    private static int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size)
    {
        try
        {
            return DwmSetWindowAttributeInt(hwnd, attribute, ref value, size);
        }
        catch
        {
            return -1;
        }
    }
}
