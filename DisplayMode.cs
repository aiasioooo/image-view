namespace ImageViewerAutoscale;

public enum DisplayMode
{
    PixelInspect = 0,
    Original = 1,
    HighQuality2x = 2,
    HighQuality4x = 3,
    AnimeMl2x = 4,
    AnimeMl4x = 5
}

public static class DisplayModeExtensions
{
    public static string Label(this DisplayMode mode)
    {
        return mode switch
        {
            DisplayMode.Original => "original",
            DisplayMode.HighQuality2x => "hq-2x",
            DisplayMode.HighQuality4x => "hq-4x",
            DisplayMode.AnimeMl2x => "anime-ml-2x",
            DisplayMode.AnimeMl4x => "quality-ml-4x",
            DisplayMode.PixelInspect => "pixel",
            _ => mode.ToString()
        };
    }

    public static int Scale(this DisplayMode mode)
    {
        return mode switch
        {
            DisplayMode.HighQuality2x or DisplayMode.AnimeMl2x => 2,
            DisplayMode.HighQuality4x or DisplayMode.AnimeMl4x => 4,
            _ => 1
        };
    }

    public static bool IsGenerated(this DisplayMode mode)
    {
        return mode is DisplayMode.HighQuality2x
            or DisplayMode.HighQuality4x
            or DisplayMode.AnimeMl2x
            or DisplayMode.AnimeMl4x;
    }

    public static bool IsExternalMl(this DisplayMode mode)
    {
        return mode is DisplayMode.AnimeMl2x or DisplayMode.AnimeMl4x;
    }
}
