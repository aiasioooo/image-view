using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace ImageViewerAutoscale;

public sealed class CircularProgressRing : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress),
        typeof(double),
        typeof(CircularProgressRing),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly DispatcherTimer _timer;
    private double _angle;

    public CircularProgressRing()
    {
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _timer.Tick += (_, _) =>
        {
            _angle = (_angle + 12) % 360;
            InvalidateVisual();
        };

        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _timer.Start();
            }
            else
            {
                _timer.Stop();
            }
        };
    }

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, Math.Clamp(value, 0, 100));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
        {
            return;
        }

        var outerThickness = Math.Max(3, size * 0.09);
        var innerThickness = Math.Max(2, size * 0.075);
        var outerRadius = (size - outerThickness) / 2.0;
        var innerRadius = outerRadius - outerThickness - 4;
        var center = new Point(ActualWidth / 2.0, ActualHeight / 2.0);
        var outerTrackPen = new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), outerThickness);
        var outerArcPen = new Pen(new SolidColorBrush(Color.FromRgb(120, 224, 255)), outerThickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        var innerTrackPen = new Pen(new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)), innerThickness);
        var innerArcPen = new Pen(new SolidColorBrush(Color.FromRgb(255, 222, 92)), innerThickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };

        drawingContext.DrawEllipse(null, outerTrackPen, center, outerRadius, outerRadius);
        drawingContext.DrawEllipse(null, innerTrackPen, center, innerRadius, innerRadius);

        var start = PointOnCircle(center, outerRadius, _angle);
        var end = PointOnCircle(center, outerRadius, _angle + 110);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, isFilled: false, isClosed: false);
            context.ArcTo(
                end,
                new Size(outerRadius, outerRadius),
                rotationAngle: 0,
                isLargeArc: false,
                sweepDirection: SweepDirection.Clockwise,
                isStroked: true,
                isSmoothJoin: true);
        }
        geometry.Freeze();

        drawingContext.DrawGeometry(null, outerArcPen, geometry);

        if (Progress <= 0)
        {
            return;
        }

        var sweep = Math.Min(359.9, 360.0 * Progress / 100.0);
        var progressStart = PointOnCircle(center, innerRadius, 0);
        var progressEnd = PointOnCircle(center, innerRadius, sweep);
        var progressGeometry = new StreamGeometry();
        using (var context = progressGeometry.Open())
        {
            context.BeginFigure(progressStart, isFilled: false, isClosed: false);
            context.ArcTo(
                progressEnd,
                new Size(innerRadius, innerRadius),
                rotationAngle: 0,
                isLargeArc: sweep > 180,
                sweepDirection: SweepDirection.Clockwise,
                isStroked: true,
                isSmoothJoin: true);
        }
        progressGeometry.Freeze();

        drawingContext.DrawGeometry(null, innerArcPen, progressGeometry);
    }

    private static Point PointOnCircle(Point center, double radius, double angleDegrees)
    {
        var radians = (angleDegrees - 90) * Math.PI / 180.0;
        return new Point(
            center.X + Math.Cos(radians) * radius,
            center.Y + Math.Sin(radians) * radius);
    }
}
