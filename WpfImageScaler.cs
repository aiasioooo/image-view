using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageViewerAutoscale;

public static class WpfImageScaler
{
    public static Task ScaleAndSavePngAsync(
        string inputPath,
        string outputPath,
        int scale,
        CancellationToken cancellationToken)
    {
        var source = ImageLoader.LoadFrozen(inputPath);
        return ScaleAndSavePngAsync(source, outputPath, scale, cancellationToken);
    }

    public static Task ScaleAndSavePngAsync(
        BitmapSource source,
        string outputPath,
        int scale,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var width = checked(source.PixelWidth * scale);
                var height = checked(source.PixelHeight * scale);

                var visual = new DrawingVisual();
                RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);

                using (var context = visual.RenderOpen())
                {
                    context.DrawImage(source, new Rect(0, 0, width, height));
                }

                var target = new RenderTargetBitmap(
                    width,
                    height,
                    96,
                    96,
                    PixelFormats.Pbgra32);
                target.Render(visual);
                target.Freeze();

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                using var stream = File.Create(outputPath);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(target));
                encoder.Save(stream);
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        return completion.Task.WaitAsync(cancellationToken);
    }
}
