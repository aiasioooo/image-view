using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ImageViewerAutoscale;

public sealed class ImageCacheService
{
    private static readonly TimeSpan CacheUnusedLifetime = TimeSpan.FromHours(3);
    private static readonly TimeSpan CacheCleanupInterval = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, Lazy<CacheJob>> _jobs = new();
    private readonly ExternalUpscalerConfig _upscalerConfig;
    private readonly string _appRoot;
    private readonly string _cacheRoot;
    private DateTime _lastCacheCleanupUtc = DateTime.MinValue;
    private int _cacheCleanupRunning;

    public ImageCacheService()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _appRoot = Path.Combine(localAppData, "ImageViewerAutoscale");
        _cacheRoot = Path.Combine(_appRoot, "cache");
        Directory.CreateDirectory(_cacheRoot);
        _upscalerConfig = ExternalUpscalerConfig.LoadOrCreate(Path.Combine(_appRoot, "upscalers.json"), _appRoot);
        ObserveBackground(CleanupUnusedCacheAsync(force: false));
    }

    public Task<string> GetOrCreateAsync(
        string imagePath,
        DisplayMode mode,
        CancellationToken cancellationToken,
        IProgress<double>? progress = null)
    {
        if (!mode.IsGenerated())
        {
            return Task.FromResult(imagePath);
        }

        var outputPath = GetCachePath(imagePath, mode);
        if (File.Exists(outputPath))
        {
            TouchCacheFile(outputPath);
            ObserveBackground(CleanupUnusedCacheAsync(force: false));
            progress?.Report(100);
            return Task.FromResult(outputPath);
        }

        var jobKey = $"{outputPath}|{mode}";
        var lazy = _jobs.GetOrAdd(jobKey, _ => new Lazy<CacheJob>(
            () =>
            {
                var progressBroadcaster = new ProgressBroadcaster();
                var task = CreateAsync(imagePath, outputPath, mode, cancellationToken, progressBroadcaster);
                return new CacheJob(task, progressBroadcaster);
            },
            LazyThreadSafetyMode.ExecutionAndPublication));
        var job = lazy.Value;
        job.Progress.Add(progress);

        return AwaitAndCleanAsync(jobKey, lazy, job);
    }

    public async Task PregenerateNeighborsAsync(
        string imagePath,
        IEnumerable<DisplayMode> modes,
        CancellationToken cancellationToken)
    {
        var modesToCreate = modes.Where(mode => mode.IsGenerated()).Distinct().ToArray();
        if (modesToCreate.Length == 0)
        {
            return;
        }

        foreach (var neighbor in GetNeighborImages(imagePath).Take(2))
        {
            foreach (var mode in modesToCreate)
            {
                ObserveBackground(GetOrCreateAsync(neighbor, mode, cancellationToken));
            }
        }

        await Task.CompletedTask;
    }

    private static void ObserveBackground(Task task)
    {
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private async Task<string> AwaitAndCleanAsync(string jobKey, Lazy<CacheJob> lazy, CacheJob job)
    {
        try
        {
            return await job.Task.ConfigureAwait(false);
        }
        finally
        {
            if (_jobs.TryGetValue(jobKey, out var current) && ReferenceEquals(current, lazy))
            {
                _jobs.TryRemove(jobKey, out _);
            }
        }
    }

    private async Task<string> CreateAsync(
        string imagePath,
        string outputPath,
        DisplayMode mode,
        CancellationToken cancellationToken,
        IProgress<double>? progress)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        if (mode.IsExternalMl())
        {
            await RunExternalUpscalerAsync(imagePath, outputPath, mode, cancellationToken, progress).ConfigureAwait(false);
        }
        else
        {
            progress?.Report(5);
            await WpfImageScaler.ScaleAndSavePngAsync(imagePath, outputPath, mode.Scale(), cancellationToken)
                .ConfigureAwait(false);
            progress?.Report(100);
        }

        TouchCacheFile(outputPath);
        ObserveBackground(CleanupUnusedCacheAsync(force: false));
        return outputPath;
    }

    private async Task CleanupUnusedCacheAsync(bool force)
    {
        var now = DateTime.UtcNow;
        if (!force && now - _lastCacheCleanupUtc < CacheCleanupInterval)
        {
            return;
        }

        if (Interlocked.Exchange(ref _cacheCleanupRunning, 1) == 1)
        {
            return;
        }

        try
        {
            _lastCacheCleanupUtc = now;
            await Task.Run(() => CleanupUnusedCacheFiles(now)).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _cacheCleanupRunning, 0);
        }
    }

    private void CleanupUnusedCacheFiles(DateTime nowUtc)
    {
        if (!Directory.Exists(_cacheRoot))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(_cacheRoot, "*.png", SearchOption.AllDirectories))
        {
            try
            {
                var lastAccessUtc = File.GetLastAccessTimeUtc(path);
                if (nowUtc - lastAccessUtc <= CacheUnusedLifetime)
                {
                    continue;
                }

                File.Delete(path);
                DeleteEmptyParents(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void DeleteEmptyParents(string path)
    {
        var directory = Path.GetDirectoryName(path);
        while (!string.IsNullOrWhiteSpace(directory)
            && !string.Equals(directory, _cacheRoot, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(directory))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    return;
                }

                Directory.Delete(directory);
                directory = Path.GetDirectoryName(directory);
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }
    }

    private static void TouchCacheFile(string path)
    {
        try
        {
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task RunExternalUpscalerAsync(
        string imagePath,
        string outputPath,
        DisplayMode mode,
        CancellationToken cancellationToken,
        IProgress<double>? progress)
    {
        var command = _upscalerConfig.Get(mode);
        if (command is null || string.IsNullOrWhiteSpace(command.Executable))
        {
            throw new ExternalUpscalerNotConfiguredException(
                $"{mode.Label()} not configured; edit {_upscalerConfig.ConfigPath}");
        }

        var args = command.Arguments
            .Replace("{input}", imagePath)
            .Replace("{output}", outputPath)
            .Replace("{scale}", mode.Scale().ToString());

        var startInfo = new ProcessStartInfo
        {
            FileName = command.Executable,
            Arguments = args,
            WorkingDirectory = Path.GetDirectoryName(command.Executable) ?? _appRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {command.Executable}");

        progress?.Report(0);
        var stdout = DrainOutputAsync(process.StandardOutput, progress, cancellationToken);
        var stderr = DrainOutputAsync(process.StandardError, progress, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        if (process.ExitCode != 0 || !File.Exists(outputPath))
        {
            throw new InvalidOperationException($"{mode.Label()} failed with exit code {process.ExitCode}");
        }

        progress?.Report(100);
    }

    private static async Task DrainOutputAsync(
        TextReader reader,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new char[512];
        var tail = "";

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            if (progress is null)
            {
                continue;
            }

            var text = tail + new string(buffer, 0, read);
            foreach (Match match in Regex.Matches(text, @"(?<!\d)(\d{1,3}(?:\.\d+)?)%"))
            {
                if (double.TryParse(match.Groups[1].Value, out var value))
                {
                    progress.Report(Math.Clamp(value, 0, 100));
                }
            }

            tail = text.Length > 16 ? text[^16..] : text;
        }
    }

    private string GetCachePath(string imagePath, DisplayMode mode)
    {
        var info = new FileInfo(imagePath);
        var keySource = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{mode.Label()}|v1";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keySource))).ToLowerInvariant();
        return Path.Combine(_cacheRoot, hash[..2], hash[2..4], $"{hash}-{mode.Label()}.png");
    }

    private static IEnumerable<string> GetNeighborImages(string imagePath)
    {
        var directory = Path.GetDirectoryName(imagePath);
        if (directory is null || !Directory.Exists(directory))
        {
            yield break;
        }

        var files = Directory.EnumerateFiles(directory)
            .Where(App.IsSupportedImage)
            .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        var index = Array.FindIndex(files, path => string.Equals(path, imagePath, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            yield break;
        }

        if (index + 1 < files.Length)
        {
            yield return files[index + 1];
        }

        if (index - 1 >= 0)
        {
            yield return files[index - 1];
        }
    }

    private sealed record CacheJob(Task<string> Task, ProgressBroadcaster Progress);

    private sealed class ProgressBroadcaster : IProgress<double>
    {
        private readonly object _gate = new();
        private readonly List<IProgress<double>> _targets = new();
        private double _lastProgress;

        public void Add(IProgress<double>? progress)
        {
            if (progress is null)
            {
                return;
            }

            lock (_gate)
            {
                _targets.Add(progress);
                progress.Report(_lastProgress);
            }
        }

        public void Report(double value)
        {
            IProgress<double>[] targets;
            lock (_gate)
            {
                _lastProgress = value;
                targets = _targets.ToArray();
            }

            foreach (var target in targets)
            {
                target.Report(value);
            }
        }
    }
}

public sealed record ExternalUpscalerConfig
{
    public required string ConfigPath { get; init; }
    public UpscalerCommand AnimeMl2x { get; init; } = new();
    public UpscalerCommand AnimeMl4x { get; init; } = new();

    public UpscalerCommand? Get(DisplayMode mode)
    {
        return mode switch
        {
            DisplayMode.AnimeMl2x => AnimeMl2x,
            DisplayMode.AnimeMl4x => AnimeMl4x,
            _ => null
        };
    }

    public static ExternalUpscalerConfig LoadOrCreate(string path, string appRoot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (!File.Exists(path))
        {
            var sample = CreateDefault(path, appRoot);
            File.WriteAllText(path, JsonSerializer.Serialize(sample, JsonOptions()));
            return sample;
        }

        var config = JsonSerializer.Deserialize<ExternalUpscalerConfig>(File.ReadAllText(path), JsonOptions())
            ?? new ExternalUpscalerConfig { ConfigPath = path };
        config = config with { ConfigPath = path };

        if (NeedsAutoDetectedExecutable(config))
        {
            var detected = CreateDefault(path, appRoot);
            if (!string.IsNullOrWhiteSpace(detected.AnimeMl2x.Executable))
            {
                config = detected;
                File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions()));
            }
        }
        else if (config.AnimeMl4x.Arguments.Contains("realesr-animevideov3", StringComparison.OrdinalIgnoreCase))
        {
            config = config with
            {
                AnimeMl4x = new UpscalerCommand
                {
                    Executable = config.AnimeMl4x.Executable,
                    Arguments = "-i \"{input}\" -o \"{output}\" -n realesrgan-x4plus-anime -s 4 -f png"
                }
            };
            File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions()));
        }

        return config;
    }

    private static ExternalUpscalerConfig CreateDefault(string path, string appRoot)
    {
        var executable = FindRealEsrganExecutable(appRoot);
        return new ExternalUpscalerConfig
        {
            ConfigPath = path,
            AnimeMl2x = new UpscalerCommand
            {
                Executable = executable,
                Arguments = "-i \"{input}\" -o \"{output}\" -n realesr-animevideov3 -s 2 -f png"
            },
            AnimeMl4x = new UpscalerCommand
            {
                Executable = executable,
                Arguments = "-i \"{input}\" -o \"{output}\" -n realesrgan-x4plus-anime -s 4 -f png"
            }
        };
    }

    private static bool NeedsAutoDetectedExecutable(ExternalUpscalerConfig config)
    {
        return string.IsNullOrWhiteSpace(config.AnimeMl2x.Executable)
            || string.IsNullOrWhiteSpace(config.AnimeMl4x.Executable)
            || !File.Exists(config.AnimeMl2x.Executable)
            || !File.Exists(config.AnimeMl4x.Executable);
    }

    private static string FindRealEsrganExecutable(string appRoot)
    {
        var toolsRoot = Path.Combine(appRoot, "tools");
        if (!Directory.Exists(toolsRoot))
        {
            return "";
        }

        return Directory.EnumerateFiles(toolsRoot, "realesrgan-ncnn-vulkan.exe", SearchOption.AllDirectories)
            .OrderBy(path => path.Length)
            .FirstOrDefault() ?? "";
    }

    private static JsonSerializerOptions JsonOptions()
    {
        return new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };
    }
}

public sealed class UpscalerCommand
{
    public string Executable { get; init; } = "";
    public string Arguments { get; init; } = "";
}

public sealed class ExternalUpscalerNotConfiguredException(string message) : Exception(message);
