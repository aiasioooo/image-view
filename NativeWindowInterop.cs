using System.Runtime.InteropServices;

namespace ImageViewerAutoscale;

internal static class NativeWindowInterop
{
    private const uint GwHwndNext = 2;

    public static nint FindWindowBelow(nint windowHandle, NativePoint point)
    {
        for (var current = GetWindow(windowHandle, GwHwndNext);
             current != nint.Zero;
             current = GetWindow(current, GwHwndNext))
        {
            if (!IsWindowVisible(current) || !GetWindowRect(current, out var rect))
            {
                continue;
            }

            if (point.X >= rect.Left
                && point.X < rect.Right
                && point.Y >= rect.Top
                && point.Y < rect.Bottom)
            {
                return current;
            }
        }

        return nint.Zero;
    }

    public static nint FindOverlappingWindowBelow(nint windowHandle)
    {
        if (!GetWindowRect(windowHandle, out var sourceRect))
        {
            return nint.Zero;
        }

        for (var current = GetWindow(windowHandle, GwHwndNext);
             current != nint.Zero;
             current = GetWindow(current, GwHwndNext))
        {
            if (!IsWindowVisible(current) || !GetWindowRect(current, out var rect))
            {
                continue;
            }

            if (RectanglesOverlap(sourceRect, rect))
            {
                return current;
            }
        }

        return nint.Zero;
    }

    public static bool TryGetCursorPosition(out NativePoint point)
    {
        return GetCursorPos(out point);
    }

    public static bool TryGetWindowRect(nint windowHandle, out NativeRect rect)
    {
        return GetWindowRect(windowHandle, out rect);
    }

    public static bool IsValidWindow(nint windowHandle)
    {
        return windowHandle != nint.Zero && IsWindow(windowHandle);
    }

    private static bool RectanglesOverlap(NativeRect left, NativeRect right)
    {
        return left.Left < right.Right
            && left.Right > right.Left
            && left.Top < right.Bottom
            && left.Bottom > right.Top;
    }

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint windowHandle, uint command);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint windowHandle, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint windowHandle);

    [StructLayout(LayoutKind.Sequential)]
    public readonly struct NativePoint
    {
        public readonly int X;
        public readonly int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public readonly struct NativeRect
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
    }
}
