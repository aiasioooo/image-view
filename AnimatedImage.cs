using System.Windows.Media.Imaging;

namespace ImageViewerAutoscale;

public sealed record AnimatedImage(IReadOnlyList<AnimatedFrame> Frames)
{
    public bool IsAnimated => Frames.Count > 1;
}

public sealed record AnimatedFrame(BitmapSource Bitmap, TimeSpan Delay);
