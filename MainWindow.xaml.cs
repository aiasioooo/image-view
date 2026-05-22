using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ImageViewerAutoscale;

public partial class MainWindow : Window
{
    private const double TitleBarHeight = 30;
    private const int WmSizing = 0x0214;
    private const int WmszLeft = 1;
    private const int WmszRight = 2;
    private const int WmszTop = 3;
    private const int WmszTopLeft = 4;
    private const int WmszTopRight = 5;
    private const int WmszBottom = 6;
    private const int WmszBottomLeft = 7;
    private const int WmszBottomRight = 8;
    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;
    private static readonly TimeSpan PinnedOwnerIdleInterval = TimeSpan.FromMilliseconds(33);
    private static readonly TimeSpan PinnedOwnerActiveInterval = TimeSpan.FromMilliseconds(2);
    private static readonly TimeSpan PinnedOwnerRecentActivityWindow = TimeSpan.FromMilliseconds(300);
    private readonly ImageCacheService _cacheService;
    private readonly ZoomUsageTracker _zoomUsage = new();
    private readonly Dictionary<DisplayMode, BitmapSource> _loadedModes = new();
    private readonly CancellationTokenSource _windowLifetime = new();
    private readonly DispatcherTimer _pinnedWindowTimer;
    private readonly DispatcherTimer _gifTimer;
    private HwndSource? _hwndSource;
    private string _imagePath;
    private DisplayMode _mode = DisplayMode.Original;
    private BitmapSource? _original;
    private AnimatedImage? _animatedImage;
    private int _gifFrameIndex;
    private bool _gifPaused;
    private double _gifSpeed = 1.0;
    private DisplayMode[]? _gifFrameModes;
    private readonly Dictionary<(int FrameIndex, DisplayMode Mode), BitmapSource> _gifGeneratedFrames = new();
    private string? _gifTempRoot;
    private long _imageLoadVersion;
    private long _foregroundGenerationVersion;
    private string _status = "loading";
    private bool _windowPlacementLoaded;
    private bool _isLightBackground;
    private bool _keepAspectMode;
    private bool _windowBorderEnabled = true;
    private double? _keepAspectRatio;
    private double? _activeResizeAspect;
    private bool _isWindowMoveMode;
    private bool _hasEnteredWindowMoveMode;
    private bool _isWindowPinMode;
    private bool _restoreTopmostOnNextMoveMode;
    private bool _restoreWindowPinModeOnNextMoveMode;
    private nint _pinnedOwnerHandle;
    private Vector _pinnedOwnerOffset;
    private NativeWindowInterop.NativeRect _lastPinnedOwnerRect;
    private DateTime _lastPinnedOwnerMoveUtc;
    private readonly Dictionary<BorderSampleKey, Color> _borderColorCache = new();

    public MainWindow(string imagePath, ImageCacheService cacheService)
    {
        InitializeComponent();
        _imagePath = Path.GetFullPath(imagePath);
        _cacheService = cacheService;
        WindowPlacementStore.Apply(this);
        Viewport.SetBoundsLock(WindowPlacementStore.LoadBoundsLocked());
        _keepAspectMode = WindowPlacementStore.LoadKeepAspectMode();
        _keepAspectRatio = WindowPlacementStore.LoadKeepAspectRatio();
        Viewport.SetScaleWithResize(_keepAspectMode);
        _windowPlacementLoaded = true;
        _isLightBackground = false;
        ApplyBackground();
        ApplyWindowBorder(null);

        _pinnedWindowTimer = new DispatcherTimer
        {
            Interval = PinnedOwnerIdleInterval
        };
        _pinnedWindowTimer.Tick += (_, _) => FollowPinnedOwner();
        _gifTimer = new DispatcherTimer();
        _gifTimer.Tick += (_, _) => AdvanceGifFrame(1, fromTimer: true);

        Viewport.InteractionSettled += OnViewportInteractionSettled;
        Viewport.ZoomChanged += (_, zoom) =>
        {
            _zoomUsage.RecordZoom(zoom, _mode);
            UpdateTitle();
        };

        SourceInitialized += OnSourceInitialized;
        Loaded += async (_, _) => await LoadImageAsync(_imagePath);
        Loaded += (_, _) => SystemThemeService.ApplyWindowFrame(this);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Closed += (_, _) =>
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _hwndSource?.RemoveHook(WndProc);
            _gifTimer.Stop();
            ClearGifTempFiles();
            _windowLifetime.Cancel();
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_windowPlacementLoaded)
        {
            base.OnClosing(e);
            return;
        }

        if (_hasEnteredWindowMoveMode)
        {
            WindowPlacementStore.SaveSettings(Viewport.BoundsLocked, _keepAspectMode, _keepAspectRatio);
        }
        else
        {
            WindowPlacementStore.Save(this, Viewport.BoundsLocked, _keepAspectMode, _keepAspectRatio);
        }

        base.OnClosing(e);
    }

    private async Task LoadImageAsync(string imagePath)
    {
        var fullPath = Path.GetFullPath(imagePath);
        var imageVersion = Interlocked.Increment(ref _imageLoadVersion);

        try
        {
            _imagePath = fullPath;
            _mode = DisplayMode.Original;
            _original = null;
            _animatedImage = null;
            _gifFrameIndex = 0;
            _gifPaused = false;
            _gifSpeed = 1.0;
            _gifFrameModes = null;
            _gifGeneratedFrames.Clear();
            ClearGifTempFiles();
            _gifTimer.Stop();
            _loadedModes.Clear();
            _borderColorCache.Clear();
            InvalidateForegroundGeneration();
            _status = "loading";
            UpdateTitle();

            var animated = await Task.Run(() => ImageLoader.LoadAnimatedGif(fullPath));
            var bitmap = animated?.Frames[0].Bitmap ?? await Task.Run(() => ImageLoader.LoadFrozen(fullPath));
            if (imageVersion != _imageLoadVersion)
            {
                return;
            }

            _animatedImage = animated;
            _gifFrameModes = animated is null
                ? null
                : Enumerable.Repeat(DisplayMode.Original, animated.Frames.Count).ToArray();
            _original = bitmap;
            _loadedModes[DisplayMode.Original] = bitmap;
            _loadedModes[DisplayMode.PixelInspect] = bitmap;
            Viewport.SetSource(bitmap, useNearestNeighbor: false, preserveView: false, sourcePixelScale: 1);
            ApplyWindowBorder(bitmap);
            _status = "ready";
            UpdateTitle();
            StartGifPlaybackIfNeeded();
            await RequestLikelyGenerationsAsync();
        }
        catch (Exception ex)
        {
            _status = ex.Message;
            UpdateTitle();
            MessageBox.Show(this, ex.Message, "Could not open image", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private async void OnViewportInteractionSettled(object? sender, EventArgs e)
    {
        ApplyWindowBorder(GetCurrentModeBitmap());
        await RequestLikelyGenerationsAsync();
    }

    private async Task RequestLikelyGenerationsAsync()
    {
        if (_original is null || _animatedImage is not null)
        {
            return;
        }

        var desired = _zoomUsage.GetDesiredModes(_mode)
            .Where(mode => !mode.IsExternalMl())
            .ToArray();
        foreach (var mode in desired)
        {
            if (!mode.IsGenerated() || _loadedModes.ContainsKey(mode))
            {
                continue;
            }

            ObserveBackground(LoadGeneratedModeAsync(mode, switchWhenReady: mode == _mode, CancellationToken.None));
        }

        await _cacheService.PregenerateNeighborsAsync(
            _imagePath,
            _zoomUsage.GetNeighborModes(_mode).Where(mode => !mode.IsExternalMl()),
            _windowLifetime.Token);
    }

    private async Task LoadGeneratedModeAsync(DisplayMode mode, bool switchWhenReady, CancellationToken cancellationToken)
    {
        if (_original is null)
        {
            return;
        }

        var version = switchWhenReady
            ? Interlocked.Increment(ref _foregroundGenerationVersion)
            : Volatile.Read(ref _foregroundGenerationVersion);
        var imageVersion = _imageLoadVersion;
        var imagePath = _imagePath;
        if (switchWhenReady)
        {
            SetProgressVisible(true);
            SetStatus($"generating {mode.Label()}");
        }

        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _windowLifetime.Token,
                cancellationToken);
            var progress = switchWhenReady
                ? new Progress<double>(value =>
                {
                    if (IsCurrentForegroundGeneration(version, mode, imagePath))
                    {
                        SetGenerationProgress(value);
                    }
                })
                : null;
            var generatedPath = await _cacheService.GetOrCreateAsync(imagePath, mode, linkedCancellation.Token, progress);
            var bitmap = await Task.Run(() => ImageLoader.LoadFrozen(generatedPath), linkedCancellation.Token);
            if (imageVersion != _imageLoadVersion || !string.Equals(imagePath, _imagePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _loadedModes[mode] = bitmap;

            if (switchWhenReady && IsCurrentForegroundGeneration(version, mode, imagePath))
            {
                Viewport.SetSource(bitmap, useNearestNeighbor: false, preserveView: true, sourcePixelScale: mode.Scale());
                ApplyWindowBorder(bitmap);
            }

            if (switchWhenReady && IsCurrentForegroundGeneration(version, mode, imagePath))
            {
                SetStatus("ready");
            }
        }
        catch (OperationCanceledException)
        {
            if (switchWhenReady && IsCurrentForegroundGeneration(version, mode, imagePath))
            {
                SetStatus("ready");
            }
        }
        catch (ExternalUpscalerNotConfiguredException ex)
        {
            if (switchWhenReady && IsCurrentForegroundGeneration(version, mode, imagePath))
            {
                SetStatus(ex.Message);
            }
        }
        catch (Exception ex)
        {
            if (switchWhenReady && IsCurrentForegroundGeneration(version, mode, imagePath))
            {
                SetStatus($"generation failed: {ex.Message}");
            }
        }
        finally
        {
            if (switchWhenReady && IsCurrentForegroundGeneration(version, mode, imagePath))
            {
                SetProgressVisible(false);
            }
        }
    }

    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.T)
        {
            ToggleWindowMoveMode();
            e.Handled = true;
            return;
        }

        if (_isWindowMoveMode)
        {
            if (e.Key == Key.P)
            {
                ToggleAlwaysOnTop();
            }
            else if (e.Key == Key.W)
            {
                ToggleWindowPinMode();
            }

            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.O)
        {
            OpenImages();
            return;
        }

        if (_animatedImage is not null)
        {
            switch (e.Key)
            {
                case Key.Space:
                    ToggleGifPause();
                    e.Handled = true;
                    UpdateTitle();
                    return;
                case Key.Up when _gifPaused:
                    AdvanceGifFrame(-1, fromTimer: false);
                    e.Handled = true;
                    UpdateTitle();
                    return;
                case Key.Down when _gifPaused:
                    AdvanceGifFrame(1, fromTimer: false);
                    e.Handled = true;
                    UpdateTitle();
                    return;
                case Key.OemPlus:
                case Key.Add:
                    AdjustGifSpeed(1.25);
                    e.Handled = true;
                    UpdateTitle();
                    return;
                case Key.OemMinus:
                case Key.Subtract:
                    AdjustGifSpeed(0.8);
                    e.Handled = true;
                    UpdateTitle();
                    return;
            }
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && (e.Key == Key.D1 || e.Key == Key.NumPad1))
        {
            Viewport.SetOneToOne();
            UpdateTitle();
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.F)
        {
            Viewport.FitToWindow();
            UpdateTitle();
            return;
        }

        switch (e.Key)
        {
            case Key.D0:
            case Key.NumPad0:
                if (_animatedImage is not null)
                {
                    SwitchGifFrameMode(DisplayMode.PixelInspect);
                    break;
                }

                SwitchMode(DisplayMode.PixelInspect);
                break;
            case Key.D1:
            case Key.NumPad1:
                if (_animatedImage is not null)
                {
                    SwitchGifFrameMode(DisplayMode.Original);
                    break;
                }

                SwitchMode(DisplayMode.Original);
                break;
            case Key.D2:
            case Key.NumPad2:
                if (_animatedImage is not null)
                {
                    SwitchGifFrameMode(DisplayMode.AnimeMl2x);
                    break;
                }

                SwitchGeneratedMode(DisplayMode.AnimeMl2x);
                break;
            case Key.D3:
            case Key.NumPad3:
                if (_animatedImage is not null)
                {
                    SwitchGifFrameMode(DisplayMode.AnimeMl4x);
                    break;
                }

                SwitchGeneratedMode(DisplayMode.AnimeMl4x);
                break;
            case Key.D8:
            case Key.NumPad8:
                if (_animatedImage is not null)
                {
                    SwitchGifFrameMode(DisplayMode.HighQuality2x);
                    break;
                }

                SwitchGeneratedMode(DisplayMode.HighQuality2x);
                break;
            case Key.D9:
            case Key.NumPad9:
                if (_animatedImage is not null)
                {
                    SwitchGifFrameMode(DisplayMode.HighQuality4x);
                    break;
                }

                SwitchGeneratedMode(DisplayMode.HighQuality4x);
                break;
            case Key.F:
                OpenImageDirectory();
                break;
            case Key.B:
                ToggleBackground();
                break;
            case Key.H:
                ShowHelp();
                break;
            case Key.K:
                ToggleKeepAspectMode();
                break;
            case Key.L:
                ToggleBoundsLock();
                break;
            case Key.R:
                ToggleWindowBorder();
                break;
            case Key.Right:
            case Key.D:
                await NavigateInDirectoryAsync(1);
                break;
            case Key.Left:
            case Key.A:
                await NavigateInDirectoryAsync(-1);
                break;
        }

        UpdateTitle();
    }

    private async Task NavigateInDirectoryAsync(int direction)
    {
        var next = FindAdjacentImage(direction);
        if (next is null)
        {
            return;
        }

        await LoadImageAsync(next);
    }

    private string? FindAdjacentImage(int direction)
    {
        var directory = Path.GetDirectoryName(_imagePath);
        if (directory is null || !Directory.Exists(directory))
        {
            return null;
        }

        var files = ImageDirectoryIndex.GetImages(directory);

        if (files.Length == 0)
        {
            return null;
        }

        var index = Array.FindIndex(files, path => string.Equals(path, _imagePath, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return null;
        }

        var nextIndex = (index + direction + files.Length) % files.Length;
        return files[nextIndex];
    }

    private void ShowHelp()
    {
        const string help = """
Mouse wheel: zoom
Ctrl + mouse wheel: smooth zoom
Left drag: pan
Space: pause/unpause animated GIF
Up / Down while paused: previous / next GIF frame
+ / -: speed up / slow down animated GIF

Left / Right / A / D: previous / next image in folder
F: open image folder
L: lock zoom to larger image dimension and clamp image borders
0: pixel inspect
1: original
2: fast ML 2x
3: quality ML 4x
8: high-quality 2x
9: high-quality 4x
B: black/white background
K: toggle keep-aspect window resize
R: toggle sampled 1px window border
T: toggle move-window mode
P: toggle always on top while in move-window mode
W: toggle pin-to-window mode while in move-window mode
Ctrl + F: fit to window
Ctrl + 1: 100%
Ctrl + O: open images
H: help
""";

        MessageBox.Show(this, help, "Image Viewer Autoscale Help", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenImageDirectory()
    {
        if (!File.Exists(_imagePath))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{_imagePath}\"",
            UseShellExecute = true
        });
    }

    private void ToggleBoundsLock()
    {
        var locked = Viewport.ToggleBoundsLock();
        _status = locked ? "bounds locked" : "bounds unlocked";
        UpdateTitle();
    }

    private void ToggleBackground()
    {
        _isLightBackground = !_isLightBackground;
        ApplyBackground();
    }

    private void ToggleGifPause()
    {
        if (_animatedImage is null)
        {
            return;
        }

        _gifPaused = !_gifPaused;
        if (_gifPaused)
        {
            _gifTimer.Stop();
        }
        else
        {
            ScheduleGifTimer();
        }
    }

    private void AdjustGifSpeed(double factor)
    {
        if (_animatedImage is null)
        {
            return;
        }

        _gifSpeed = Math.Clamp(_gifSpeed * factor, 0.1, 8.0);
        if (!_gifPaused)
        {
            ScheduleGifTimer();
        }
    }

    private void ToggleKeepAspectMode()
    {
        _keepAspectMode = !_keepAspectMode;
        _keepAspectRatio = _keepAspectMode ? CaptureCurrentDisplayAreaAspect() : null;
        Viewport.SetScaleWithResize(_keepAspectMode);
        _status = _keepAspectMode ? "keep aspect on" : "keep aspect off";
        UpdateTitle();
    }

    private void ToggleWindowBorder()
    {
        _windowBorderEnabled = !_windowBorderEnabled;
        ApplyWindowBorder(GetCurrentModeBitmap());
        UpdateTitle();
    }

    private void ToggleWindowMoveMode()
    {
        if (!_isWindowMoveMode && WindowState == WindowState.Maximized)
        {
            return;
        }

        _isWindowMoveMode = !_isWindowMoveMode;
        _hasEnteredWindowMoveMode |= _isWindowMoveMode;
        if (!_isWindowMoveMode)
        {
            _restoreTopmostOnNextMoveMode = Topmost;
            _restoreWindowPinModeOnNextMoveMode = _isWindowPinMode;
            Topmost = false;
            _isWindowPinMode = false;
            ClearPinnedOwner();
        }

        Top += _isWindowMoveMode ? TitleBarHeight : -TitleBarHeight;
        Height += _isWindowMoveMode ? -TitleBarHeight : TitleBarHeight;
        TitleBar.Visibility = _isWindowMoveMode ? Visibility.Collapsed : Visibility.Visible;
        TitleBarRow.Height = _isWindowMoveMode ? new GridLength(0) : new GridLength(TitleBarHeight);
        ResizeMode = _isWindowMoveMode ? ResizeMode.NoResize : ResizeMode.CanResize;
        Viewport.SetInteractionEnabled(!_isWindowMoveMode);

        if (_isWindowMoveMode)
        {
            Topmost = _restoreTopmostOnNextMoveMode;
            _isWindowPinMode = _restoreWindowPinModeOnNextMoveMode;
            if (_isWindowPinMode)
            {
                PinToWindowBelowCurrentWindow();
            }

            Focus();
        }
    }

    private void ToggleAlwaysOnTop()
    {
        if (!Topmost)
        {
            _isWindowPinMode = false;
            _restoreWindowPinModeOnNextMoveMode = false;
            ClearPinnedOwner();
        }

        Topmost = !Topmost;
        _restoreTopmostOnNextMoveMode = Topmost;
        UpdateTitle();
    }

    private void ToggleWindowPinMode()
    {
        _isWindowPinMode = !_isWindowPinMode;
        if (_isWindowPinMode)
        {
            Topmost = false;
            _restoreTopmostOnNextMoveMode = false;
            PinToWindowBelowCurrentWindow();
        }
        else
        {
            ClearPinnedOwner();
        }

        _restoreWindowPinModeOnNextMoveMode = _isWindowPinMode;
        UpdateTitle();
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
        {
            Dispatcher.Invoke(() =>
            {
                SystemThemeService.ApplyWindowFrame(this);
            });
        }
    }

    private void ApplyBackground()
    {
        var background = _isLightBackground ? Brushes.White : Brushes.Black;

        Background = background;
        Viewport.SetBackground(background);
    }

    private void SwitchMode(DisplayMode mode)
    {
        if (_original is null || !_loadedModes.TryGetValue(mode, out var bitmap))
        {
            return;
        }

        InvalidateForegroundGeneration();
        _mode = mode;
        _zoomUsage.RecordMode(mode);
        Viewport.SetSource(bitmap, useNearestNeighbor: mode == DisplayMode.PixelInspect, preserveView: true, sourcePixelScale: mode.Scale());
        ApplyWindowBorder(bitmap);
        SetStatus("ready");
    }

    private void StartGifPlaybackIfNeeded()
    {
        if (_animatedImage is null || !_animatedImage.IsAnimated)
        {
            return;
        }

        _gifPaused = false;
        ScheduleGifTimer();
    }

    private void SwitchGifFrameMode(DisplayMode mode)
    {
        if (_animatedImage is null || _gifFrameModes is null)
        {
            return;
        }

        if (mode.IsGenerated() && !_gifPaused)
        {
            SetStatus("pause GIF to scale frames");
            return;
        }

        _gifFrameModes[_gifFrameIndex] = mode;
        _mode = mode;
        DisplayCurrentGifFrame();
    }

    private void ScheduleGifTimer()
    {
        if (_animatedImage is null || _gifPaused)
        {
            _gifTimer.Stop();
            return;
        }

        var interval = TimeSpan.FromMilliseconds(Math.Max(5, _animatedImage.Frames[_gifFrameIndex].Delay.TotalMilliseconds / _gifSpeed));
        _gifTimer.Interval = interval;
        _gifTimer.Start();
    }

    private void AdvanceGifFrame(int direction, bool fromTimer)
    {
        if (_animatedImage is null || _animatedImage.Frames.Count == 0)
        {
            return;
        }

        var count = _animatedImage.Frames.Count;
        var nextIndex = (_gifFrameIndex + direction + count) % count;
        SetGifFrame(nextIndex);

        if (fromTimer)
        {
            ScheduleGifTimer();
        }
    }

    private void SetGifFrame(int frameIndex)
    {
        if (_animatedImage is null)
        {
            return;
        }

        _gifFrameIndex = Math.Clamp(frameIndex, 0, _animatedImage.Frames.Count - 1);
        _mode = _gifFrameModes?[_gifFrameIndex] ?? DisplayMode.Original;
        DisplayCurrentGifFrame();
    }

    private void DisplayCurrentGifFrame()
    {
        if (_animatedImage is null)
        {
            return;
        }

        var bitmap = _animatedImage.Frames[_gifFrameIndex].Bitmap;
        _original = bitmap;
        _loadedModes[DisplayMode.Original] = bitmap;
        _loadedModes[DisplayMode.PixelInspect] = bitmap;

        if (_mode is DisplayMode.Original or DisplayMode.PixelInspect)
        {
            Viewport.SetSource(
                bitmap,
                useNearestNeighbor: _mode == DisplayMode.PixelInspect,
                preserveView: true,
                sourcePixelScale: 1);
            ApplyWindowBorder(bitmap);
        }
        else if (_gifGeneratedFrames.TryGetValue((_gifFrameIndex, _mode), out var generated))
        {
            Viewport.SetSource(generated, useNearestNeighbor: false, preserveView: true, sourcePixelScale: _mode.Scale());
            ApplyWindowBorder(generated);
            SetStatus("ready");
        }
        else if (_gifPaused)
        {
            Viewport.SetSource(bitmap, useNearestNeighbor: false, preserveView: true, sourcePixelScale: 1);
            ApplyWindowBorder(bitmap);
            ObserveBackground(LoadGifGeneratedFrameAsync(_gifFrameIndex, _mode));
        }
        else
        {
            Viewport.SetSource(bitmap, useNearestNeighbor: false, preserveView: true, sourcePixelScale: 1);
            ApplyWindowBorder(bitmap);
        }

        UpdateTitle();
    }

    private async Task LoadGifGeneratedFrameAsync(int frameIndex, DisplayMode mode)
    {
        if (_animatedImage is null
            || frameIndex < 0
            || frameIndex >= _animatedImage.Frames.Count
            || !mode.IsGenerated())
        {
            return;
        }

        if (_gifGeneratedFrames.ContainsKey((frameIndex, mode)))
        {
            return;
        }

        var imageVersion = _imageLoadVersion;
        var inputPath = GetGifTempPath(frameIndex, DisplayMode.Original, "input");
        var outputPath = GetGifTempPath(frameIndex, mode, "output");

        try
        {
            SetProgressVisible(true);
            SetStatus($"generating frame {frameIndex + 1} {mode.Label()}");

            if (!File.Exists(outputPath))
            {
                if (mode.IsExternalMl())
                {
                    if (!File.Exists(inputPath))
                    {
                        await SaveBitmapPngAsync(_animatedImage.Frames[frameIndex].Bitmap, inputPath, _windowLifetime.Token);
                    }

                    var progress = new Progress<double>(SetGenerationProgress);
                    await _cacheService.CreateExternalUpscaleAsync(inputPath, outputPath, mode, _windowLifetime.Token, progress);
                }
                else
                {
                    SetGenerationProgress(5);
                    await WpfImageScaler.ScaleAndSavePngAsync(
                        _animatedImage.Frames[frameIndex].Bitmap,
                        outputPath,
                        mode.Scale(),
                        _windowLifetime.Token);
                    SetGenerationProgress(100);
                }
            }

            var bitmap = await Task.Run(() => ImageLoader.LoadFrozen(outputPath), _windowLifetime.Token);
            if (imageVersion != _imageLoadVersion || _animatedImage is null)
            {
                return;
            }

            _gifGeneratedFrames[(frameIndex, mode)] = bitmap;
            if (_gifFrameIndex == frameIndex && _mode == mode)
            {
                Viewport.SetSource(bitmap, useNearestNeighbor: false, preserveView: true, sourcePixelScale: mode.Scale());
                ApplyWindowBorder(bitmap);
                SetStatus("ready");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ExternalUpscalerNotConfiguredException ex)
        {
            if (_gifFrameIndex == frameIndex && _mode == mode)
            {
                SetStatus(ex.Message);
            }
        }
        catch (Exception ex)
        {
            if (_gifFrameIndex == frameIndex && _mode == mode)
            {
                SetStatus($"frame generation failed: {ex.Message}");
            }
        }
        finally
        {
            SetProgressVisible(false);
        }
    }

    private string GetGifTempPath(int frameIndex, DisplayMode mode, string kind)
    {
        _gifTempRoot ??= _cacheService.CreateTemporaryFrameCacheDirectory();
        Directory.CreateDirectory(_gifTempRoot);
        return Path.Combine(_gifTempRoot, $"frame-{frameIndex:D5}-{kind}-{mode.Label()}.png");
    }

    private static async Task SaveBitmapPngAsync(BitmapSource bitmap, string path, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
        await stream.FlushAsync(cancellationToken);
    }

    private void ClearGifTempFiles()
    {
        if (string.IsNullOrWhiteSpace(_gifTempRoot))
        {
            return;
        }

        try
        {
            if (Directory.Exists(_gifTempRoot))
            {
                Directory.Delete(_gifTempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        finally
        {
            _gifTempRoot = null;
        }
    }

    private void SwitchGeneratedMode(DisplayMode mode)
    {
        InvalidateForegroundGeneration();
        _mode = mode;
        _zoomUsage.RecordMode(mode);

        if (_loadedModes.TryGetValue(mode, out var bitmap))
        {
            Viewport.SetSource(bitmap, useNearestNeighbor: false, preserveView: true, sourcePixelScale: mode.Scale());
            ApplyWindowBorder(bitmap);
            SetStatus("ready");
            return;
        }

        if (_original is not null)
        {
            Viewport.SetSource(_original, useNearestNeighbor: false, preserveView: true, sourcePixelScale: 1);
            ApplyWindowBorder(_original);
        }

        ObserveBackground(LoadGeneratedModeAsync(mode, switchWhenReady: true, CancellationToken.None));
    }

    private void InvalidateForegroundGeneration()
    {
        Interlocked.Increment(ref _foregroundGenerationVersion);
        SetProgressVisible(false);
    }

    private bool IsCurrentForegroundGeneration(long version, DisplayMode mode, string imagePath)
    {
        return version == Volatile.Read(ref _foregroundGenerationVersion)
            && _mode == mode
            && string.Equals(imagePath, _imagePath, StringComparison.OrdinalIgnoreCase);
    }

    private static void ObserveBackground(Task task)
    {
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        var files = ((string[])e.Data.GetData(DataFormats.FileDrop))
            .Where(App.IsSupportedImage)
            .ToArray();

        foreach (var file in files)
        {
            ((App)Application.Current).OpenImageWindow(file);
        }
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isWindowMoveMode || WindowState == WindowState.Maximized)
        {
            return;
        }

        try
        {
            _pinnedWindowTimer.Stop();
            DragMove();
            if (_isWindowPinMode)
            {
                PinToWindowBelowCursor();
            }

            e.Handled = true;
        }
        catch (InvalidOperationException)
        {
            // Ignore drag attempts that arrive after the mouse button was released.
        }
    }

    private void OpenImages()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open image",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.tif;*.tiff|All files|*.*",
            Multiselect = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        foreach (var file in dialog.FileNames.Where(App.IsSupportedImage))
        {
            ((App)Application.Current).OpenImageWindow(file);
        }
    }

    private void SetStatus(string status)
    {
        _status = status;
        Dispatcher.Invoke(UpdateTitle);
    }

    private void SetProgressVisible(bool isVisible)
    {
        Dispatcher.Invoke(() =>
        {
            if (isVisible)
            {
                ProgressRing.Progress = 0;
            }

            ProgressRing.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    private void SetGenerationProgress(double progress)
    {
        Dispatcher.Invoke(() =>
        {
            ProgressRing.Progress = progress;
        });
    }

    private void UpdateTitle()
    {
        var lockLabel = Viewport.BoundsLocked ? " | locked" : "";
        var keepAspectLabel = _keepAspectMode ? " | keep-aspect" : "";
        var borderLabel = _windowBorderEnabled ? " | border" : "";
        var gifLabel = _animatedImage is null
            ? ""
            : $" | gif {_gifFrameIndex + 1}/{_animatedImage.Frames.Count} {_gifSpeed:0.##}x{(_gifPaused ? " paused" : "")}";
        var topmostLabel = Topmost ? " | topmost" : "";
        var pinModeLabel = _isWindowPinMode ? " | pin-mode" : "";
        var pinnedLabel = _pinnedOwnerHandle != nint.Zero ? " | pinned" : "";
        Title = $"{Path.GetFileName(_imagePath)} | {_mode.Label()} | {Viewport.Zoom:P0}{lockLabel}{keepAspectLabel}{borderLabel}{gifLabel}{topmostLabel}{pinModeLabel}{pinnedLabel} | {_status}";
        TitleText.Text = Title;
    }

    private BitmapSource? GetCurrentModeBitmap()
    {
        return _loadedModes.TryGetValue(_mode, out var bitmap) ? bitmap : _original;
    }

    private void ApplyWindowBorder(BitmapSource? bitmap)
    {
        if (!_windowBorderEnabled)
        {
            RootBorder.BorderThickness = new Thickness(0);
            return;
        }

        RootBorder.BorderThickness = new Thickness(1);
        var visibleRect = Viewport.GetVisibleSourcePixelRect();
        var key = BorderSampleKey.Create(bitmap, visibleRect);
        if (!_borderColorCache.TryGetValue(key, out var color))
        {
            color = SampleBorderColor(bitmap, visibleRect);
            _borderColorCache[key] = color;
        }

        RootBorder.BorderBrush = new SolidColorBrush(color);
    }

    private static Color SampleBorderColor(BitmapSource? bitmap, Int32Rect? visibleRect)
    {
        if (bitmap is null || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
        {
            return Color.FromRgb(32, 32, 32);
        }

        var source = bitmap.Format == PixelFormats.Bgra32 || bitmap.Format == PixelFormats.Pbgra32
            ? bitmap
            : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var rect = NormalizeSampleRect(visibleRect, width, height);
        var perimeterSamples = Math.Max(64, Math.Min(1024, 2 * (rect.Width + rect.Height)));
        var edgeSamples = Math.Max(1, perimeterSamples / 4);
        var dynamicSamples = Math.Max(16, Math.Min(128, rect.Width * rect.Height / 4096));
        long red = 0;
        long green = 0;
        long blue = 0;
        var count = 0;

        for (var i = 0; i < edgeSamples; i++)
        {
            var x = rect.X + (edgeSamples <= 1 ? 0 : (int)Math.Round(i * (rect.Width - 1) / (double)(edgeSamples - 1)));
            AddSample(source, x, rect.Y, ref red, ref green, ref blue, ref count);
            if (rect.Height > 1)
            {
                AddSample(source, x, rect.Y + rect.Height - 1, ref red, ref green, ref blue, ref count);
            }

            var y = rect.Y + (edgeSamples <= 1 ? 0 : (int)Math.Round(i * (rect.Height - 1) / (double)(edgeSamples - 1)));
            AddSample(source, rect.X, y, ref red, ref green, ref blue, ref count);
            if (rect.Width > 1)
            {
                AddSample(source, rect.X + rect.Width - 1, y, ref red, ref green, ref blue, ref count);
            }
        }

        var seed = HashCode.Combine(rect.X, rect.Y, rect.Width, rect.Height, width, height);
        for (var i = 0; i < dynamicSamples; i++)
        {
            seed = unchecked(seed * 1664525 + 1013904223);
            var x = rect.X + Math.Abs(seed % rect.Width);
            seed = unchecked(seed * 1664525 + 1013904223);
            var y = rect.Y + Math.Abs(seed % rect.Height);
            AddSample(source, x, y, ref red, ref green, ref blue, ref count);
        }

        if (count == 0)
        {
            return Color.FromRgb(32, 32, 32);
        }

        return Color.FromRgb(
            (byte)Math.Clamp(red / count, 0, 255),
            (byte)Math.Clamp(green / count, 0, 255),
            (byte)Math.Clamp(blue / count, 0, 255));
    }

    private static Int32Rect NormalizeSampleRect(Int32Rect? visibleRect, int sourceWidth, int sourceHeight)
    {
        if (visibleRect is not { } rect)
        {
            return new Int32Rect(0, 0, sourceWidth, sourceHeight);
        }

        var x = Math.Clamp(rect.X, 0, sourceWidth - 1);
        var y = Math.Clamp(rect.Y, 0, sourceHeight - 1);
        var right = Math.Clamp(rect.X + rect.Width, x + 1, sourceWidth);
        var bottom = Math.Clamp(rect.Y + rect.Height, y + 1, sourceHeight);
        return new Int32Rect(x, y, right - x, bottom - y);
    }

    private static void AddSample(
        BitmapSource source,
        int x,
        int y,
        ref long red,
        ref long green,
        ref long blue,
        ref int count)
    {
        var pixel = new byte[4];
        source.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        var a = pixel[3];
        if (a == 0)
        {
            return;
        }

        blue += pixel[0];
        green += pixel[1];
        red += pixel[2];
        count++;
    }

    private readonly record struct BorderSampleKey(int BitmapId, int X, int Y, int Width, int Height)
    {
        private const int BucketSize = 16;

        public static BorderSampleKey Create(BitmapSource? bitmap, Int32Rect? rect)
        {
            var normalized = rect ?? new Int32Rect(0, 0, bitmap?.PixelWidth ?? 0, bitmap?.PixelHeight ?? 0);
            return new BorderSampleKey(
                bitmap is null ? 0 : RuntimeHelpers.GetHashCode(bitmap),
                normalized.X / BucketSize,
                normalized.Y / BucketSize,
                Math.Max(1, normalized.Width / BucketSize),
                Math.Max(1, normalized.Height / BucketSize));
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwndSource = (HwndSource?)PresentationSource.FromVisual(this);
        _hwndSource?.AddHook(WndProc);
        if (_keepAspectMode)
        {
            _keepAspectRatio ??= CaptureCurrentDisplayAreaAspect();
        }
    }

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmEnterSizeMove)
        {
            _activeResizeAspect = GetStableKeepAspectRatio(hwnd);
        }
        else if (message == WmExitSizeMove)
        {
            _activeResizeAspect = null;
        }
        else if (message == WmSizing && _keepAspectMode && !_isWindowMoveMode && WindowState == WindowState.Normal)
        {
            PreserveSizingAspect(hwnd, (int)wParam, lParam);
            handled = true;
        }

        return nint.Zero;
    }

    private void PreserveSizingAspect(nint hwnd, int edge, nint rectPointer)
    {
        if (rectPointer == nint.Zero)
        {
            return;
        }

        var aspect = _activeResizeAspect ?? GetStableKeepAspectRatio(hwnd);
        if (double.IsNaN(aspect) || double.IsInfinity(aspect) || aspect <= 0)
        {
            return;
        }

        _activeResizeAspect = aspect;

        var rect = Marshal.PtrToStructure<SizingRect>(rectPointer);
        var inset = GetDisplayAreaInsetPixels(hwnd);
        switch (edge)
        {
            case WmszLeft:
            case WmszRight:
                ResizeSizingRectHeightFromWidth(ref rect, aspect, inset, keepCenter: true, anchorTop: false);
                break;
            case WmszTop:
            case WmszBottom:
                ResizeSizingRectWidthFromHeight(ref rect, aspect, inset);
                break;
            case WmszTopLeft:
            case WmszTopRight:
                ResizeSizingRectHeightFromWidth(ref rect, aspect, inset, keepCenter: false, anchorTop: false);
                break;
            case WmszBottomLeft:
            case WmszBottomRight:
                ResizeSizingRectHeightFromWidth(ref rect, aspect, inset, keepCenter: false, anchorTop: true);
                break;
        }

        Marshal.StructureToPtr(rect, rectPointer, false);
    }

    private double GetStableKeepAspectRatio(nint hwnd)
    {
        if (_keepAspectRatio is { } ratio && ratio > 0)
        {
            return ratio;
        }

        var captured = CaptureCurrentDisplayAreaAspect() ?? GetCurrentDisplayAreaAspect(hwnd);
        if (!double.IsNaN(captured) && !double.IsInfinity(captured) && captured > 0)
        {
            _keepAspectRatio = captured;
        }

        return captured;
    }

    private double? CaptureCurrentDisplayAreaAspect()
    {
        if (Viewport.ActualWidth > 1 && Viewport.ActualHeight > 1)
        {
            return Viewport.ActualWidth / Viewport.ActualHeight;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero)
        {
            return null;
        }

        var aspect = GetCurrentDisplayAreaAspect(handle);
        return !double.IsNaN(aspect) && !double.IsInfinity(aspect) && aspect > 0 ? aspect : null;
    }

    private double GetCurrentDisplayAreaAspect(nint hwnd)
    {
        if (!NativeWindowInterop.TryGetWindowRect(hwnd, out var rect))
        {
            return double.NaN;
        }

        var inset = GetDisplayAreaInsetPixels(hwnd);
        var displayWidth = rect.Right - rect.Left - inset.Width;
        var displayHeight = rect.Bottom - rect.Top - inset.Height;
        return displayWidth > 0 && displayHeight > 0 ? displayWidth / displayHeight : double.NaN;
    }

    private (double Width, double Height) GetDisplayAreaInsetPixels(nint hwnd)
    {
        if (!NativeWindowInterop.TryGetWindowRect(hwnd, out var rect))
        {
            return (0, 0);
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var displayWidthPixels = Math.Max(0, Viewport.ActualWidth * dpi.DpiScaleX);
        var displayHeightPixels = Math.Max(0, Viewport.ActualHeight * dpi.DpiScaleY);
        return (
            Math.Max(0, rect.Right - rect.Left - displayWidthPixels),
            Math.Max(0, rect.Bottom - rect.Top - displayHeightPixels));
    }

    private static void ResizeSizingRectHeightFromWidth(ref SizingRect rect, double aspect, (double Width, double Height) inset, bool keepCenter, bool anchorTop)
    {
        var width = rect.Right - rect.Left;
        var displayWidth = Math.Max(1, width - inset.Width);
        var height = Math.Max(1, (int)Math.Round(displayWidth / aspect + inset.Height));
        if (keepCenter)
        {
            var center = (rect.Top + rect.Bottom) / 2;
            rect.Top = center - height / 2;
            rect.Bottom = rect.Top + height;
        }
        else if (anchorTop)
        {
            rect.Bottom = rect.Top + height;
        }
        else
        {
            rect.Top = rect.Bottom - height;
        }
    }

    private static void ResizeSizingRectWidthFromHeight(ref SizingRect rect, double aspect, (double Width, double Height) inset)
    {
        var height = rect.Bottom - rect.Top;
        var displayHeight = Math.Max(1, height - inset.Height);
        var width = Math.Max(1, (int)Math.Round(displayHeight * aspect + inset.Width));
        var center = (rect.Left + rect.Right) / 2;
        rect.Left = center - width / 2;
        rect.Right = rect.Left + width;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SizingRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private void PinToWindowBelowCursor()
    {
        var ownHandle = new WindowInteropHelper(this).Handle;
        if (ownHandle == nint.Zero || !NativeWindowInterop.TryGetCursorPosition(out var cursor))
        {
            ClearPinnedOwner();
            return;
        }

        var targetHandle = NativeWindowInterop.FindWindowBelow(ownHandle, cursor);
        if (targetHandle == nint.Zero)
        {
            ClearPinnedOwner();
            UpdateTitle();
            return;
        }

        SetPinnedOwner(targetHandle);
    }

    private void PinToWindowBelowCurrentWindow()
    {
        var ownHandle = new WindowInteropHelper(this).Handle;
        if (ownHandle == nint.Zero)
        {
            ClearPinnedOwner();
            return;
        }

        var targetHandle = NativeWindowInterop.FindOverlappingWindowBelow(ownHandle);
        if (targetHandle == nint.Zero)
        {
            ClearPinnedOwner();
            UpdateTitle();
            return;
        }

        SetPinnedOwner(targetHandle);
    }

    private void SetPinnedOwner(nint ownerHandle)
    {
        if (!NativeWindowInterop.TryGetWindowRect(ownerHandle, out var ownerRect))
        {
            ClearPinnedOwner();
            return;
        }

        _pinnedOwnerHandle = ownerHandle;
        new WindowInteropHelper(this).Owner = ownerHandle;
        var ownerTopLeft = DeviceToDip(ownerRect.Left, ownerRect.Top);
        _pinnedOwnerOffset = new Vector(Left - ownerTopLeft.X, Top - ownerTopLeft.Y);
        _lastPinnedOwnerRect = ownerRect;
        _lastPinnedOwnerMoveUtc = DateTime.UtcNow;
        _pinnedWindowTimer.Interval = PinnedOwnerActiveInterval;
        _pinnedWindowTimer.Start();
        UpdateTitle();
    }

    private void FollowPinnedOwner()
    {
        if (!NativeWindowInterop.IsValidWindow(_pinnedOwnerHandle)
            || !NativeWindowInterop.TryGetWindowRect(_pinnedOwnerHandle, out var ownerRect))
        {
            ClearPinnedOwner();
            UpdateTitle();
            return;
        }

        if (!ownerRect.Equals(_lastPinnedOwnerRect))
        {
            _lastPinnedOwnerRect = ownerRect;
            _lastPinnedOwnerMoveUtc = DateTime.UtcNow;
            _pinnedWindowTimer.Interval = PinnedOwnerActiveInterval;
        }
        else if (DateTime.UtcNow - _lastPinnedOwnerMoveUtc > PinnedOwnerRecentActivityWindow)
        {
            _pinnedWindowTimer.Interval = PinnedOwnerIdleInterval;
        }

        var ownerTopLeft = DeviceToDip(ownerRect.Left, ownerRect.Top);
        Left = ownerTopLeft.X + _pinnedOwnerOffset.X;
        Top = ownerTopLeft.Y + _pinnedOwnerOffset.Y;
    }

    private void ClearPinnedOwner()
    {
        _pinnedWindowTimer.Stop();
        _pinnedWindowTimer.Interval = PinnedOwnerIdleInterval;
        _pinnedOwnerHandle = nint.Zero;
        new WindowInteropHelper(this).Owner = nint.Zero;
    }

    private Point DeviceToDip(double x, double y)
    {
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return transform.Transform(new Point(x, y));
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
