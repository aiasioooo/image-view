using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageViewerAutoscale;

public static class ImageLoader
{
    private static readonly TimeSpan DefaultGifFrameDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MinimumGifFrameDelay = TimeSpan.FromMilliseconds(20);

    public static BitmapSource LoadFrozen(string path)
    {
        using var stream = File.OpenRead(path);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    public static AnimatedImage? LoadAnimatedGif(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        using var stream = File.OpenRead(path);
        var decoder = new GifBitmapDecoder(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count <= 1)
        {
            return null;
        }

        var frames = ComposeGifFrames(decoder);

        return new AnimatedImage(frames);
    }

    private static AnimatedFrame[] ComposeGifFrames(GifBitmapDecoder decoder)
    {
        var frameInfo = decoder.Frames
            .Select(frame => new GifFrameInfo(
                frame,
                GetMetadataInt(frame.Metadata as BitmapMetadata, "/imgdesc/Left") ?? 0,
                GetMetadataInt(frame.Metadata as BitmapMetadata, "/imgdesc/Top") ?? 0,
                GetMetadataInt(frame.Metadata as BitmapMetadata, "/grctlext/Disposal") ?? 0,
                GetGifFrameDelay(frame)))
            .ToArray();
        var width = GetMetadataInt(decoder.Metadata as BitmapMetadata, "/logscrdesc/Width")
            ?? frameInfo.Max(frame => frame.Left + frame.Frame.PixelWidth);
        var height = GetMetadataInt(decoder.Metadata as BitmapMetadata, "/logscrdesc/Height")
            ?? frameInfo.Max(frame => frame.Top + frame.Frame.PixelHeight);
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        var canvas = new byte[width * height * 4];
        var stride = width * 4;
        var dpiX = decoder.Frames[0].DpiX;
        var dpiY = decoder.Frames[0].DpiY;
        var composed = new List<AnimatedFrame>(frameInfo.Length);

        foreach (var info in frameInfo)
        {
            var restorePrevious = info.Disposal == 3 ? (byte[])canvas.Clone() : null;
            CompositeFrame(canvas, width, height, info.Frame, info.Left, info.Top);

            var pixels = (byte[])canvas.Clone();
            var bitmap = BitmapSource.Create(
                width,
                height,
                dpiX,
                dpiY,
                PixelFormats.Pbgra32,
                null,
                pixels,
                stride);
            bitmap.Freeze();
            composed.Add(new AnimatedFrame(bitmap, info.Delay));

            if (info.Disposal == 2)
            {
                ClearRect(canvas, width, height, info.Left, info.Top, info.Frame.PixelWidth, info.Frame.PixelHeight);
            }
            else if (restorePrevious is not null)
            {
                canvas = restorePrevious;
            }
        }

        return composed.ToArray();
    }

    private static void CompositeFrame(byte[] canvas, int canvasWidth, int canvasHeight, BitmapFrame frame, int left, int top)
    {
        if (left >= canvasWidth || top >= canvasHeight)
        {
            return;
        }

        BitmapSource source = frame.Format == PixelFormats.Pbgra32
            ? frame
            : new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
        var copyWidth = Math.Min(source.PixelWidth, canvasWidth - Math.Max(0, left));
        var copyHeight = Math.Min(source.PixelHeight, canvasHeight - Math.Max(0, top));
        if (copyWidth <= 0 || copyHeight <= 0)
        {
            return;
        }

        var sourceStride = source.PixelWidth * 4;
        var sourcePixels = new byte[sourceStride * source.PixelHeight];
        source.CopyPixels(sourcePixels, sourceStride, 0);
        var startX = Math.Max(0, left);
        var startY = Math.Max(0, top);
        var sourceStartX = startX - left;
        var sourceStartY = startY - top;

        for (var y = 0; y < copyHeight; y++)
        {
            var sourceIndex = ((sourceStartY + y) * source.PixelWidth + sourceStartX) * 4;
            var destIndex = ((startY + y) * canvasWidth + startX) * 4;
            for (var x = 0; x < copyWidth; x++)
            {
                var alpha = sourcePixels[sourceIndex + 3];
                if (alpha == 0)
                {
                    sourceIndex += 4;
                    destIndex += 4;
                    continue;
                }

                if (alpha == 255)
                {
                    canvas[destIndex] = sourcePixels[sourceIndex];
                    canvas[destIndex + 1] = sourcePixels[sourceIndex + 1];
                    canvas[destIndex + 2] = sourcePixels[sourceIndex + 2];
                    canvas[destIndex + 3] = 255;
                }
                else
                {
                    var inverseAlpha = 255 - alpha;
                    canvas[destIndex] = (byte)Math.Min(255, sourcePixels[sourceIndex] + canvas[destIndex] * inverseAlpha / 255);
                    canvas[destIndex + 1] = (byte)Math.Min(255, sourcePixels[sourceIndex + 1] + canvas[destIndex + 1] * inverseAlpha / 255);
                    canvas[destIndex + 2] = (byte)Math.Min(255, sourcePixels[sourceIndex + 2] + canvas[destIndex + 2] * inverseAlpha / 255);
                    canvas[destIndex + 3] = (byte)Math.Min(255, alpha + canvas[destIndex + 3] * inverseAlpha / 255);
                }

                sourceIndex += 4;
                destIndex += 4;
            }
        }
    }

    private static void ClearRect(byte[] canvas, int canvasWidth, int canvasHeight, int left, int top, int width, int height)
    {
        var startX = Math.Clamp(left, 0, canvasWidth);
        var startY = Math.Clamp(top, 0, canvasHeight);
        var endX = Math.Clamp(left + width, startX, canvasWidth);
        var endY = Math.Clamp(top + height, startY, canvasHeight);

        for (var y = startY; y < endY; y++)
        {
            Array.Clear(canvas, (y * canvasWidth + startX) * 4, (endX - startX) * 4);
        }
    }

    private static int? GetMetadataInt(BitmapMetadata? metadata, string query)
    {
        try
        {
            var value = metadata?.GetQuery(query);
            return value is null ? null : Convert.ToInt32(value);
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidCastException)
        {
            return null;
        }
    }

    private static TimeSpan GetGifFrameDelay(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata
                && metadata.GetQuery("/grctlext/Delay") is ushort delay
                && delay > 0)
            {
                return TimeSpan.FromMilliseconds(Math.Max(MinimumGifFrameDelay.TotalMilliseconds, delay * 10.0));
            }
        }
        catch (NotSupportedException)
        {
        }
        catch (ArgumentException)
        {
        }

        return DefaultGifFrameDelay;
    }

    private sealed record GifFrameInfo(BitmapFrame Frame, int Left, int Top, int Disposal, TimeSpan Delay);
}
