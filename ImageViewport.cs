using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ImageViewerAutoscale;

public sealed class ImageViewport : FrameworkElement
{
    private readonly DispatcherTimer _interactionTimer;
    private BitmapSource? _source;
    private bool _isDragging;
    private bool _renderQueued;
    private Point _lastMouse;
    private Vector _offset;
    private double _zoom = 1.0;
    private double _sourcePixelScale = 1.0;
    private bool _useNearestNeighbor;
    private bool _boundsLocked;
    private bool _interactionEnabled = true;
    private bool _scaleWithResize;
    private Brush _backgroundBrush = Brushes.Black;

    public ImageViewport()
    {
        ClipToBounds = true;
        Focusable = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        _interactionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(260)
        };
        _interactionTimer.Tick += (_, _) =>
        {
            _interactionTimer.Stop();
            RequestRender();
            InteractionSettled?.Invoke(this, EventArgs.Empty);
        };

    }

    public event EventHandler? InteractionSettled;
    public event EventHandler<double>? ZoomChanged;

    public double Zoom => _zoom;
    public bool BoundsLocked => _boundsLocked;
    public bool InteractionEnabled => _interactionEnabled;
    public bool ScaleWithResize => _scaleWithResize;
    public int SourcePixelWidth => _source?.PixelWidth ?? 0;
    public int SourcePixelHeight => _source?.PixelHeight ?? 0;

    public bool ToggleBoundsLock()
    {
        SetBoundsLock(!_boundsLocked);
        return _boundsLocked;
    }

    public void SetBoundsLock(bool isLocked)
    {
        _boundsLocked = isLocked;
        ClampView();
        RequestRender();
    }

    public void SetBackground(Brush brush)
    {
        _backgroundBrush = brush;
        RequestRender();
    }

    public void SetInteractionEnabled(bool isEnabled)
    {
        _interactionEnabled = isEnabled;
        if (isEnabled || !_isDragging)
        {
            return;
        }

        _isDragging = false;
        ReleaseMouseCapture();
    }

    public void SetScaleWithResize(bool isEnabled)
    {
        _scaleWithResize = isEnabled;
    }

    public Int32Rect? GetVisibleSourcePixelRect()
    {
        if (_source is null || ActualWidth <= 1 || ActualHeight <= 1 || _zoom <= 0)
        {
            return null;
        }

        var (oneToOneWidth, oneToOneHeight) = GetOneToOneDipSize();
        if (oneToOneWidth <= 0 || oneToOneHeight <= 0)
        {
            return null;
        }

        var visibleLeftDip = Math.Max(0, -_offset.X / _zoom);
        var visibleTopDip = Math.Max(0, -_offset.Y / _zoom);
        var visibleRightDip = Math.Min(oneToOneWidth, (ActualWidth - _offset.X) / _zoom);
        var visibleBottomDip = Math.Min(oneToOneHeight, (ActualHeight - _offset.Y) / _zoom);
        if (visibleRightDip <= visibleLeftDip || visibleBottomDip <= visibleTopDip)
        {
            return null;
        }

        var scaleX = _source.PixelWidth / oneToOneWidth;
        var scaleY = _source.PixelHeight / oneToOneHeight;
        var x = Math.Clamp((int)Math.Floor(visibleLeftDip * scaleX), 0, _source.PixelWidth - 1);
        var y = Math.Clamp((int)Math.Floor(visibleTopDip * scaleY), 0, _source.PixelHeight - 1);
        var right = Math.Clamp((int)Math.Ceiling(visibleRightDip * scaleX), x + 1, _source.PixelWidth);
        var bottom = Math.Clamp((int)Math.Ceiling(visibleBottomDip * scaleY), y + 1, _source.PixelHeight);
        return new Int32Rect(x, y, right - x, bottom - y);
    }

    public void SetSource(BitmapSource source, bool useNearestNeighbor, bool preserveView, double sourcePixelScale)
    {
        _source = source;
        _useNearestNeighbor = useNearestNeighbor;
        _sourcePixelScale = Math.Max(1.0, sourcePixelScale);

        if (!preserveView)
        {
            SetOneToOneCentered();
        }
        else
        {
            ClampView();
        }

        RequestRender();
    }

    public void FitToWindow()
    {
        if (_source is null || ActualWidth <= 1 || ActualHeight <= 1)
        {
            return;
        }

        var (visualWidth, visualHeight) = GetOneToOneDipSize();
        var scaleX = ActualWidth / visualWidth;
        var scaleY = ActualHeight / visualHeight;
        _zoom = Math.Max(GetMinimumZoom(), Math.Min(scaleX, scaleY));
        _offset = new Vector(
            (ActualWidth - visualWidth * _zoom) / 2.0,
            (ActualHeight - visualHeight * _zoom) / 2.0);
        ClampView();
        ZoomChanged?.Invoke(this, _zoom);
        RequestRender();
    }

    public void SetOneToOne()
    {
        if (_source is null)
        {
            return;
        }

        var center = new Point(ActualWidth / 2.0, ActualHeight / 2.0);
        ZoomAt(center, 1.0 / _zoom);
    }

    public void SetOneToOneCentered()
    {
        if (_source is null || ActualWidth <= 1 || ActualHeight <= 1)
        {
            return;
        }

        var (visualWidth, visualHeight) = GetOneToOneDipSize();
        _zoom = 1.0;
        _offset = new Vector(
            (ActualWidth - visualWidth) / 2.0,
            (ActualHeight - visualHeight) / 2.0);
        ClampView();
        ZoomChanged?.Invoke(this, _zoom);
        RequestRender();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        drawingContext.DrawRectangle(_backgroundBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));

        if (_source is null)
        {
            return;
        }

        var scalingMode = _useNearestNeighbor
            ? BitmapScalingMode.NearestNeighbor
            : BitmapScalingMode.HighQuality;

        RenderOptions.SetBitmapScalingMode(this, scalingMode);

        var oneToOneSize = GetOneToOneDipSize();
        var destination = new Rect(
            _offset.X,
            _offset.Y,
            oneToOneSize.Width * _zoom,
            oneToOneSize.Height * _zoom);

        drawingContext.DrawImage(_source, ExpandFlushEdges(destination));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (!_interactionEnabled)
        {
            e.Handled = true;
            return;
        }

        Focus();
        var factor = Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            ? Math.Pow(1.0018, e.Delta)
            : e.Delta > 0 ? 1.25 : 0.8;

        ZoomAt(e.GetPosition(this), factor);
        MarkInteracting();
        e.Handled = true;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);

        if (_source is null)
        {
            return;
        }

        if (_scaleWithResize
            && sizeInfo.PreviousSize.Width > 1
            && sizeInfo.PreviousSize.Height > 1
            && sizeInfo.NewSize.Width > 1
            && sizeInfo.NewSize.Height > 1)
        {
            ScaleViewWithResize(sizeInfo.PreviousSize, sizeInfo.NewSize);
        }
        else
        {
            var deltaWidth = sizeInfo.NewSize.Width - sizeInfo.PreviousSize.Width;
            var deltaHeight = sizeInfo.NewSize.Height - sizeInfo.PreviousSize.Height;
            _offset += new Vector(deltaWidth / 2.0, deltaHeight / 2.0);
        }

        ClampView();
        RequestRender();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!_interactionEnabled)
        {
            e.Handled = true;
            return;
        }

        Focus();
        _isDragging = true;
        _lastMouse = e.GetPosition(this);
        CaptureMouse();
        MarkInteracting();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_interactionEnabled || !_isDragging)
        {
            return;
        }

        var current = e.GetPosition(this);
        _offset += current - _lastMouse;
        ClampView();
        _lastMouse = current;
        MarkInteracting();
        RequestRender();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_interactionEnabled || !_isDragging)
        {
            return;
        }

        _isDragging = false;
        ReleaseMouseCapture();
        MarkInteracting();
        e.Handled = true;
    }

    private void ZoomAt(Point point, double factor)
    {
        if (_source is null)
        {
            return;
        }

        var oldZoom = _zoom;
        var newZoom = Math.Clamp(oldZoom * factor, GetMinimumZoom(), 64.0);
        if (Math.Abs(newZoom - oldZoom) < 0.0001)
        {
            return;
        }

        var imagePoint = new Point(
            (point.X - _offset.X) / oldZoom,
            (point.Y - _offset.Y) / oldZoom);

        _zoom = newZoom;
        _offset = new Vector(
            point.X - imagePoint.X * newZoom,
            point.Y - imagePoint.Y * newZoom);
        ClampView();

        ZoomChanged?.Invoke(this, _zoom);
        RequestRender();
    }

    private void ClampView()
    {
        if (!_boundsLocked || _source is null || ActualWidth <= 1 || ActualHeight <= 1)
        {
            return;
        }

        var minZoom = GetMinimumZoom();
        if (_zoom < minZoom)
        {
            var center = new Point(ActualWidth / 2.0, ActualHeight / 2.0);
            var oldZoom = _zoom <= 0 ? minZoom : _zoom;
            var imagePoint = new Point(
                (center.X - _offset.X) / oldZoom,
                (center.Y - _offset.Y) / oldZoom);

            _zoom = minZoom;
            _offset = new Vector(
                center.X - imagePoint.X * _zoom,
                center.Y - imagePoint.Y * _zoom);
        }

        var (oneToOneWidth, oneToOneHeight) = GetOneToOneDipSize();
        var displayWidth = oneToOneWidth * _zoom;
        var displayHeight = oneToOneHeight * _zoom;

        _offset = new Vector(
            ClampAxis(_offset.X, displayWidth, ActualWidth),
            ClampAxis(_offset.Y, displayHeight, ActualHeight));
    }

    private double ClampAxis(double offset, double contentLength, double viewportLength)
    {
        if (contentLength <= viewportLength)
        {
            return (viewportLength - contentLength) / 2.0;
        }

        return Math.Clamp(offset, viewportLength - contentLength, 0);
    }

    private double GetMinimumZoom()
    {
        if (!_boundsLocked || _source is null || ActualWidth <= 1 || ActualHeight <= 1)
        {
            return 0.02;
        }

        var (oneToOneWidth, oneToOneHeight) = GetOneToOneDipSize();
        if (oneToOneWidth <= 0 || oneToOneHeight <= 0)
        {
            return 0.02;
        }

        return oneToOneWidth >= oneToOneHeight
            ? ActualWidth / oneToOneWidth
            : ActualHeight / oneToOneHeight;
    }

    private void ScaleViewWithResize(Size previousSize, Size newSize)
    {
        var previousArea = previousSize.Width * previousSize.Height;
        var newArea = newSize.Width * newSize.Height;
        var scale = Math.Sqrt(newArea / previousArea);
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
        {
            return;
        }

        var oldZoom = _zoom;
        var newZoom = Math.Clamp(oldZoom * scale, GetMinimumZoom(), 64.0);
        var oldCenter = new Point(previousSize.Width / 2.0, previousSize.Height / 2.0);
        var newCenter = new Point(newSize.Width / 2.0, newSize.Height / 2.0);
        var imagePoint = new Point(
            (oldCenter.X - _offset.X) / oldZoom,
            (oldCenter.Y - _offset.Y) / oldZoom);

        _zoom = newZoom;
        _offset = new Vector(
            newCenter.X - imagePoint.X * newZoom,
            newCenter.Y - imagePoint.Y * newZoom);

        if (Math.Abs(newZoom - oldZoom) >= 0.0001)
        {
            ZoomChanged?.Invoke(this, _zoom);
        }
    }

    private void MarkInteracting()
    {
        _interactionTimer.Stop();
        _interactionTimer.Start();
    }

    private void RequestRender()
    {
        if (_renderQueued)
        {
            return;
        }

        _renderQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _renderQueued = false;
            InvalidateVisual();
        }, DispatcherPriority.Render);
    }

    private (double Width, double Height) GetOneToOneDipSize()
    {
        if (_source is null)
        {
            return (0, 0);
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        return (
            _source.PixelWidth / _sourcePixelScale / dpi.DpiScaleX,
            _source.PixelHeight / _sourcePixelScale / dpi.DpiScaleY);
    }

    private Rect ExpandFlushEdges(Rect rect)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var bleedX = 1.0 / dpi.DpiScaleX;
        var bleedY = 1.0 / dpi.DpiScaleY;
        var toleranceX = bleedX / 2.0;
        var toleranceY = bleedY / 2.0;

        var left = rect.Left;
        var top = rect.Top;
        var right = rect.Right;
        var bottom = rect.Bottom;

        if (Math.Abs(left) <= toleranceX)
        {
            left -= bleedX;
        }

        if (Math.Abs(top) <= toleranceY)
        {
            top -= bleedY;
        }

        if (Math.Abs(ActualWidth - right) <= toleranceX)
        {
            right += bleedX;
        }

        if (Math.Abs(ActualHeight - bottom) <= toleranceY)
        {
            bottom += bleedY;
        }

        return new Rect(new Point(left, top), new Point(right, bottom));
    }
}
