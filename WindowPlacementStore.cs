using System.IO;
using System.Text.Json;
using System.Windows;

namespace ImageViewerAutoscale;

public static class WindowPlacementStore
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ImageViewerAutoscale",
        "window.json");

    public static void Apply(Window window)
    {
        var placement = Load();
        if (placement is null)
        {
            return;
        }

        if (placement.HasWindowPlacement == false)
        {
            return;
        }

        var left = placement.Left;
        var top = placement.Top;
        var width = Math.Max(320, placement.Width);
        var height = Math.Max(240, placement.Height);

        if (!IntersectsVirtualScreen(left, top, width, height))
        {
            return;
        }

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = left;
        window.Top = top;
        window.Width = width;
        window.Height = height;
        window.WindowState = placement.IsMaximized ? WindowState.Maximized : WindowState.Normal;
    }

    public static bool LoadBoundsLocked()
    {
        return Load()?.BoundsLocked ?? false;
    }

    public static bool LoadKeepAspectMode()
    {
        return Load()?.KeepAspectMode ?? false;
    }

    public static double? LoadKeepAspectRatio()
    {
        var ratio = Load()?.KeepAspectDisplayRatio;
        return ratio is > 0 ? ratio : null;
    }

    public static void Save(Window window, bool boundsLocked, bool keepAspectMode, double? keepAspectRatio)
    {
        var bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : window.RestoreBounds;

        var placement = new WindowPlacement
        {
            HasWindowPlacement = true,
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            IsMaximized = window.WindowState == WindowState.Maximized,
            BoundsLocked = boundsLocked,
            KeepAspectMode = keepAspectMode,
            KeepAspectDisplayRatio = keepAspectRatio
        };

        Write(placement);
    }

    public static void SaveSettings(bool boundsLocked, bool keepAspectMode, double? keepAspectRatio)
    {
        var placement = Load() ?? new WindowPlacement
        {
            HasWindowPlacement = false
        };

        placement.BoundsLocked = boundsLocked;
        placement.KeepAspectMode = keepAspectMode;
        placement.KeepAspectDisplayRatio = keepAspectRatio;
        Write(placement);
    }

    private static void Write(WindowPlacement placement)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(placement, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private static WindowPlacement? Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(SettingsPath));
        }
        catch
        {
            return null;
        }
    }

    private static bool IntersectsVirtualScreen(double left, double top, double width, double height)
    {
        var right = left + width;
        var bottom = top + height;
        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenTop = SystemParameters.VirtualScreenTop;
        var screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
        var screenBottom = screenTop + SystemParameters.VirtualScreenHeight;

        return right > screenLeft
            && left < screenRight
            && bottom > screenTop
            && top < screenBottom;
    }

    private sealed class WindowPlacement
    {
        public bool? HasWindowPlacement { get; set; }
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool IsMaximized { get; set; }
        public bool BoundsLocked { get; set; }
        public bool KeepAspectMode { get; set; }
        public double? KeepAspectDisplayRatio { get; set; }
    }
}
