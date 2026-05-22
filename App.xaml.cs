using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace ImageViewerAutoscale;

public partial class App : Application
{
    private readonly ImageCacheService _cacheService = new();

    private void OnStartup(object sender, StartupEventArgs e)
    {
        var imagePaths = e.Args.Where(IsSupportedImage).ToArray();
        if (imagePaths.Length == 0)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Open image",
                Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.tif;*.tiff|All files|*.*",
                Multiselect = true
            };

            if (dialog.ShowDialog() == true)
            {
                imagePaths = dialog.FileNames.Where(IsSupportedImage).ToArray();
            }
        }

        if (imagePaths.Length == 0)
        {
            Shutdown();
            return;
        }

        foreach (var path in imagePaths)
        {
            OpenImageWindow(path);
        }
    }

    internal void OpenImageWindow(string imagePath)
    {
        var window = new MainWindow(imagePath, _cacheService);
        window.Show();
    }

    internal static bool IsSupportedImage(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif" or ".tif" or ".tiff" => true,
            _ => false
        };
    }
}
