using System.IO;

namespace ImageViewerAutoscale;

public static class ImageDirectoryIndex
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);

    public static string[] GetImages(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        var now = DateTime.UtcNow;
        var lastWrite = Directory.GetLastWriteTimeUtc(fullPath);

        lock (Gate)
        {
            if (Entries.TryGetValue(fullPath, out var entry)
                && entry.LastWriteUtc == lastWrite
                && now - entry.LoadedUtc <= CacheTtl)
            {
                return entry.Files;
            }
        }

        var files = Directory.EnumerateFiles(fullPath)
            .Where(App.IsSupportedImage)
            .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        lock (Gate)
        {
            Entries[fullPath] = new Entry(files, lastWrite, now);
        }

        return files;
    }

    private sealed record Entry(string[] Files, DateTime LastWriteUtc, DateTime LoadedUtc);
}
